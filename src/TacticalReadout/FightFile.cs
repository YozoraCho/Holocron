using System.Globalization;
using System.Text;
using Ashla.SDK;

namespace TacticalReadout;

internal sealed record Ref(ulong? Id, string? Name)
{
    public bool Matches(GameRef? value) =>
        value is not null && (Id is { } id ? value.Id == id : string.Equals(value.Name, Name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Id is { } id ? id.ToString(CultureInfo.InvariantCulture) : Quote(Name ?? "");

    internal static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

internal enum WhoKind
{
    Any,
    Me,
    Boss,
    Named
}

internal sealed record Who(WhoKind Kind, Ref? Ref = null)
{
    public static Who Any { get; } = new(WhoKind.Any);

    public override string ToString() => Kind switch
    {
        WhoKind.Me => "me",
        WhoKind.Boss => "boss",
        WhoKind.Named => Ref!.ToString(),
        _ => "any"
    };
}

internal enum EventKind
{
    Ability,
    Apply,
    Remove,
    Damage,
    Heal,
    Interrupt,
    Death,
    Combat
}

internal sealed record Matcher(EventKind Kind, Ref? What, Who From, Who On)
{
    public override string ToString()
    {
        var text = new StringBuilder(Kind.ToString().ToLowerInvariant());
        if (Kind == EventKind.Death) return text.Append(' ').Append(On).ToString();
        if (What is not null) text.Append(' ').Append(What);
        if (From.Kind != WhoKind.Any) text.Append(" from ").Append(From);
        if (On.Kind != WhoKind.Any) text.Append(" on ").Append(On);
        return text.ToString();
    }
}

internal sealed record TimelineEntry(
    int Line,
    double Time,
    string Text,
    Matcher? Sync,
    double WindowBefore,
    double WindowAfter,
    double? JumpTime,
    string? JumpLabel,
    double Duration);

internal enum AlertLevel
{
    Info,
    Alert,
    Alarm
}

internal sealed record TriggerDef(
    int Line,
    string Id,
    Matcher? Match,
    string? BeforeEntry,
    double BeforeSeconds,
    AlertLevel Level,
    string? Text,
    string? Speech,
    bool Silent,
    double Delay,
    double Duration,
    double Suppress);

internal sealed record FightFile(
    string Path,
    string Name,
    IReadOnlyList<Ref> Areas,
    IReadOnlyList<Ref> Difficulties,
    IReadOnlyList<Ref> Bosses,
    Matcher? Start,
    Matcher? End,
    IReadOnlyList<TimelineEntry> Timeline,
    IReadOnlyDictionary<string, double> Labels,
    IReadOnlySet<string> Hidden,
    IReadOnlyList<TriggerDef> Triggers)
{
    public bool IsHidden(string text) => Hidden.Contains(text);

    public double? JumpTarget(TimelineEntry entry) =>
        entry.JumpTime ?? (entry.JumpLabel is { } label && Labels.TryGetValue(label, out var time) ? time : null);
}

internal sealed record FightProblem(string Path, int Line, string Message)
{
    public override string ToString() => Line > 0 ? $"{System.IO.Path.GetFileName(Path)} line {Line}: {Message}" : $"{System.IO.Path.GetFileName(Path)}: {Message}";
}

internal sealed class FightFormatException(int line, string message) : Exception(message)
{
    public int Line { get; } = line;
}

internal static class FightParser
{
    public const double DefaultWindow = 2.5;
    public const double DefaultAlertDuration = 4;

    public static FightFile Parse(string path, string text)
    {
        string? name = null;
        var areas = new List<Ref>();
        var difficulties = new List<Ref>();
        var bosses = new List<Ref>();
        Matcher? start = null, end = null;
        var timeline = new List<TimelineEntry>();
        var labels = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var triggers = new List<TriggerDef>();
        TriggerBuilder? trigger = null;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            int number = index + 1;
            var tokens = new Tokens(Lex(lines[index], number), number);
            if (tokens.AtEnd) continue;

            if (trigger is not null)
            {
                if (tokens.PeekWord("end"))
                {
                    tokens.Next();
                    tokens.ExpectEnd();
                    triggers.Add(trigger.Build());
                    trigger = null;
                }
                else
                {
                    trigger.Read(tokens);
                }
                continue;
            }

            if (tokens.Peek().Kind == TokenKind.Number)
            {
                if (name is null) throw new FightFormatException(number, "The file must start with: encounter \"Name\"");
                double time = tokens.Number();
                if (tokens.PeekWord("label"))
                {
                    tokens.Next();
                    var label = tokens.Text();
                    tokens.ExpectEnd();
                    if (!labels.TryAdd(label, time)) throw new FightFormatException(number, $"The label \"{label}\" is used twice.");
                    continue;
                }
                timeline.Add(ReadEntry(tokens, time, number));
                continue;
            }

            var keyword = tokens.Word();
            if (name is null && keyword != "encounter")
                throw new FightFormatException(number, "The file must start with: encounter \"Name\"");
            switch (keyword)
            {
                case "encounter":
                    if (name is not null) throw new FightFormatException(number, "Only one encounter per file.");
                    name = tokens.Text();
                    if (name.Length == 0) throw new FightFormatException(number, "The encounter needs a name.");
                    break;
                case "area":
                    areas.Add(tokens.Ref());
                    break;
                case "difficulty":
                    difficulties.Add(tokens.Ref());
                    break;
                case "boss":
                    bosses.Add(tokens.Ref());
                    break;
                case "start":
                    start = ReadMatcher(tokens);
                    break;
                case "end":
                    end = ReadMatcher(tokens);
                    break;
                case "hideall":
                    hidden.Add(tokens.Text());
                    break;
                case "trigger":
                    trigger = new TriggerBuilder(number, tokens.Text());
                    break;
                default:
                    throw new FightFormatException(number, $"\"{keyword}\" isn't something a fight file understands.");
            }
            tokens.ExpectEnd();
        }

        if (trigger is not null) throw new FightFormatException(trigger.Line, $"Trigger \"{trigger.Id}\" has no end.");
        if (name is null) throw new FightFormatException(0, "The file is empty. It must start with: encounter \"Name\"");
        if (bosses.Count == 0 && start is null)
            throw new FightFormatException(0, "Add at least one boss line (or a start line) so Tactical Readout knows when the fight begins.");
        foreach (var entry in timeline)
            if (entry.JumpLabel is { } label && !labels.ContainsKey(label))
                throw new FightFormatException(entry.Line, $"There's no label \"{label}\" to jump to.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in triggers)
            if (!ids.Add(definition.Id)) throw new FightFormatException(definition.Line, $"Two triggers are called \"{definition.Id}\".");

        return new FightFile(path, name, areas, difficulties, bosses, start, end,
            [.. timeline.OrderBy(e => e.Time)], labels, hidden, triggers);
    }

    private static TimelineEntry ReadEntry(Tokens tokens, double time, int line)
    {
        var text = tokens.Text();
        Matcher? sync = null;
        double before = DefaultWindow, after = DefaultWindow, duration = 0;
        double? jumpTime = null;
        string? jumpLabel = null;
        while (!tokens.AtEnd)
        {
            switch (tokens.Word())
            {
                case "sync":
                    sync = ReadMatcher(tokens, stopWords: ["window", "jump", "duration"]);
                    break;
                case "window":
                    before = tokens.Number();
                    after = before;
                    if (tokens.PeekKind(TokenKind.Comma))
                    {
                        tokens.Next();
                        after = tokens.Number();
                    }
                    break;
                case "jump":
                    if (tokens.PeekKind(TokenKind.Number)) jumpTime = tokens.Number();
                    else jumpLabel = tokens.Text();
                    break;
                case "duration":
                    duration = tokens.Number();
                    break;
                default:
                    throw new FightFormatException(line, "After the text, a timeline line can have: sync, window, jump, duration.");
            }
        }
        if ((jumpTime is not null || jumpLabel is not null) && sync is null)
            throw new FightFormatException(line, "A jump needs a sync, so it only happens when the boss does it.");
        return new TimelineEntry(line, time, text, sync, before, after, jumpTime, jumpLabel, duration);
    }

    private static Matcher ReadMatcher(Tokens tokens, IReadOnlyCollection<string>? stopWords = null)
    {
        stopWords ??= [];
        int line = tokens.Line;
        var word = tokens.Word();
        var kind = word switch
        {
            "ability" => EventKind.Ability,
            "apply" => EventKind.Apply,
            "remove" => EventKind.Remove,
            "damage" => EventKind.Damage,
            "heal" => EventKind.Heal,
            "interrupt" => EventKind.Interrupt,
            "death" => EventKind.Death,
            "combat" => EventKind.Combat,
            _ => throw new FightFormatException(line,
                $"\"{word}\" isn't an event. Use ability, apply, remove, damage, heal, interrupt, death or combat.")
        };
        if (kind == EventKind.Combat) return new Matcher(kind, null, Who.Any, Who.Any);
        if (kind == EventKind.Death) return new Matcher(kind, null, Who.Any, ReadWho(tokens));

        Ref? what = null;
        if (tokens.PeekWord("any")) tokens.Next();
        else what = tokens.Ref();
        Who from = Who.Any, on = Who.Any;
        while (!tokens.AtEnd && tokens.Peek().Kind == TokenKind.Word && !stopWords.Contains(tokens.Peek().Text))
        {
            switch (tokens.Word())
            {
                case "from":
                    from = ReadWho(tokens);
                    break;
                case "on":
                    on = ReadWho(tokens);
                    break;
                default:
                    throw new FightFormatException(line, "After the event, use from or on.");
            }
        }
        return new Matcher(kind, what, from, on);
    }

    private static Who ReadWho(Tokens tokens)
    {
        if (tokens.PeekWord("me")) { tokens.Next(); return new Who(WhoKind.Me); }
        if (tokens.PeekWord("boss")) { tokens.Next(); return new Who(WhoKind.Boss); }
        if (tokens.PeekWord("any")) { tokens.Next(); return Who.Any; }
        return new Who(WhoKind.Named, tokens.Ref());
    }

    private sealed class TriggerBuilder(int line, string id)
    {
        public int Line { get; } = line;
        public string Id { get; } = id;
        private Matcher? _match;
        private string? _before;
        private double _beforeSeconds;
        private AlertLevel _level = AlertLevel.Info;
        private string? _text, _speech;
        private bool _silent;
        private double _delay, _duration = DefaultAlertDuration, _suppress;

        public void Read(Tokens tokens)
        {
            int number = tokens.Line;
            var word = tokens.Word();
            switch (word)
            {
                case "match":
                    _match = ReadMatcher(tokens);
                    break;
                case "before":
                    _before = tokens.Text();
                    _beforeSeconds = tokens.Number();
                    break;
                case "info" or "alert" or "alarm":
                    if (_text is not null) throw new FightFormatException(number, "A trigger shows one text: info, alert or alarm.");
                    _level = word switch { "alarm" => AlertLevel.Alarm, "alert" => AlertLevel.Alert, _ => AlertLevel.Info };
                    _text = tokens.Text();
                    break;
                case "speak":
                    _speech = tokens.Text();
                    break;
                case "silent":
                    _silent = true;
                    break;
                case "delay":
                    _delay = tokens.Number();
                    break;
                case "duration":
                    _duration = tokens.Number();
                    break;
                case "suppress":
                    _suppress = tokens.Number();
                    break;
                default:
                    throw new FightFormatException(number,
                        $"\"{word}\" isn't a trigger setting. Use match, before, info, alert, alarm, speak, silent, delay, duration, suppress or end.");
            }
            tokens.ExpectEnd();
        }

        public TriggerDef Build()
        {
            if ((_match is null) == (_before is null))
                throw new FightFormatException(Line, $"Trigger \"{Id}\" needs either a match line or a before line.");
            if (_text is null && _speech is null)
                throw new FightFormatException(Line, $"Trigger \"{Id}\" needs an info, alert, alarm or speak line.");
            return new TriggerDef(Line, Id, _match, _before, _beforeSeconds, _level, _text, _speech, _silent, _delay,
                Math.Max(0.5, _duration), _suppress);
        }
    }

    private enum TokenKind
    {
        Word,
        Number,
        Text,
        Comma
    }

    private readonly record struct Token(TokenKind Kind, string Text, ulong? Integer = null, double Value = 0);

    private static List<Token> Lex(string line, int number)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '#') break;
            if (c == ',') { tokens.Add(new Token(TokenKind.Comma, ",")); i++; continue; }
            if (c == '"')
            {
                var text = new StringBuilder();
                i++;
                bool closed = false;
                while (i < line.Length)
                {
                    char d = line[i++];
                    if (d == '\\' && i < line.Length) { text.Append(line[i++]); continue; }
                    if (d == '"') { closed = true; break; }
                    text.Append(d);
                }
                if (!closed) throw new FightFormatException(number, "A quote isn't closed.");
                tokens.Add(new Token(TokenKind.Text, text.ToString()));
                continue;
            }
            int startIndex = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] is not (',' or '"' or '#')) i++;
            var word = line[startIndex..i];
            if (char.IsDigit(word[0]) || (word.Length > 1 && word[0] is '-' or '.'))
            {
                if (!double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    throw new FightFormatException(number, $"\"{word}\" isn't a number.");
                ulong? integer = ulong.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var whole) ? whole : null;
                tokens.Add(new Token(TokenKind.Number, word, integer, value));
            }
            else
            {
                tokens.Add(new Token(TokenKind.Word, word.ToLowerInvariant()));
            }
        }
        return tokens;
    }

    private sealed class Tokens(List<Token> items, int line)
    {
        private int _index;

        public int Line { get; } = line;

        public bool AtEnd => _index >= items.Count;

        public Token Peek() => AtEnd ? throw new FightFormatException(Line, "The line ends too early.") : items[_index];

        public bool PeekKind(TokenKind kind) => !AtEnd && items[_index].Kind == kind;

        public bool PeekWord(string word) => !AtEnd && items[_index].Kind == TokenKind.Word && items[_index].Text == word;

        public Token Next()
        {
            var token = Peek();
            _index++;
            return token;
        }

        public string Word()
        {
            var token = Next();
            return token.Kind == TokenKind.Word ? token.Text : throw new FightFormatException(Line, $"Expected a word, not {token.Text}.");
        }

        public string Text()
        {
            var token = Next();
            return token.Kind == TokenKind.Text ? token.Text : throw new FightFormatException(Line, $"Expected text in quotes, not {token.Text}.");
        }

        public double Number()
        {
            var token = Next();
            if (token.Kind != TokenKind.Number) throw new FightFormatException(Line, $"Expected a number, not {token.Text}.");
            if (token.Value < 0) throw new FightFormatException(Line, "Times and seconds can't be negative.");
            return token.Value;
        }

        public Ref Ref()
        {
            var token = Next();
            return token.Kind switch
            {
                TokenKind.Number when token.Integer is { } id => new Ref(id, null),
                TokenKind.Text when token.Text.Length > 0 => new Ref(null, token.Text),
                _ => throw new FightFormatException(Line, $"Expected an id or a name in quotes, not {token.Text}.")
            };
        }

        public void ExpectEnd()
        {
            if (!AtEnd) throw new FightFormatException(Line, $"Unexpected {items[_index].Text} at the end of the line.");
        }
    }
}
