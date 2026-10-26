using System.Numerics;
using Ashla.SDK;

namespace TacticalReadout;

internal sealed record FiredAlert(
    long Id,
    string TriggerId,
    AlertLevel Level,
    string? Text,
    string? Speech,
    bool Silent,
    DateTime At,
    double Duration,
    Vector4? Color = null)
{
    public string? Spoken => Silent ? null : Speech ?? Text;
}

internal sealed record TimelineBar(string Text, double Remaining, double Duration, Vector4? Color = null, bool Depletes = false)
{
    public bool Active => !Depletes && Remaining <= 0;
}

internal sealed record ReadoutView(
    DateTime Now,
    string? Area,
    string? Difficulty,
    IReadOnlyList<FightFile> Ready,
    FightFile? Fight,
    double Position,
    IReadOnlyList<TimelineBar> Bars,
    IReadOnlyList<FiredAlert> Alerts,
    string? LastResult,
    BarasStatus? Boss = null,
    string? BarasArea = null,
    int BarasBosses = 0);

internal sealed class ReadoutEngine
{
    public const double WipeTimeout = 30;
    public const double ResultSeconds = 15;

    private IReadOnlyList<FightFile> _fights = [];
    private List<FightFile> _active = [];
    private CombatEntity? _owner;
    private bool _ownerDead;
    private bool _inCombat;
    private DateTime? _combatStart;
    private GameRef? _area, _difficulty;
    private Encounter? _encounter;
    private readonly List<FiredAlert> _alerts = [];
    private readonly List<(DateTime At, TriggerDef Trigger, CombatEvent? Event)> _pending = [];
    private readonly Dictionary<string, DateTime> _lastFired = new(StringComparer.OrdinalIgnoreCase);
    private long _nextAlert;
    private string? _lastResult;
    private DateTime _lastResultAt;
    private readonly BarasEngine _baras;

    public ReadoutEngine()
    {
        _baras = new BarasEngine(() => ++_nextAlert);
    }

    public bool UseBaras { get; set; } = true;

    internal BarasEngine Baras => _baras;

    public IReadOnlyList<FightFile> Fights => _fights;

    public bool InEncounter => _encounter is not null;

    public void SetBaras(IReadOnlyList<BarasArea> areas) => _baras.SetAreas(areas);

    public void SetFights(IReadOnlyList<FightFile> fights)
    {
        _fights = fights;
        _active = [.. fights.Where(Applies)];
        if (_encounter is not null && !fights.Contains(_encounter.Fight))
        {
            var replacement = fights.FirstOrDefault(f => string.Equals(f.Path, _encounter.Fight.Path, StringComparison.OrdinalIgnoreCase));
            _encounter = replacement is null ? null : _encounter.With(replacement);
        }
    }

    public IReadOnlyList<FiredAlert> Handle(CombatEvent e)
    {
        var fired = new List<FiredAlert>();
        Advance(e.Timestamp, fired);
        Process(e, fired);
        if (_owner is not null && UseBaras && e.Effect.Type.Id != CombatIds.AreaEntered)
        {
            var baras = new List<FiredAlert>();
            _baras.Handle(e, _owner, baras);
            Keep(baras, fired);
        }
        return fired;
    }

    public IReadOnlyList<FiredAlert> Tick(DateTime now)
    {
        var fired = new List<FiredAlert>();
        Advance(now, fired);
        if (UseBaras)
        {
            var baras = new List<FiredAlert>();
            _baras.Tick(now, baras);
            Keep(baras, fired);
        }
        return fired;
    }

    private void Keep(List<FiredAlert> baras, List<FiredAlert> fired)
    {
        foreach (var alert in baras)
        {
            _alerts.Add(alert);
            fired.Add(alert);
        }
    }

    public FiredAlert Test(AlertLevel level, DateTime now)
    {
        var text = level switch
        {
            AlertLevel.Alarm => "Alarm: something dangerous",
            AlertLevel.Alert => "Alert: do something soon",
            _ => "Info: good to know"
        };
        var alert = new FiredAlert(++_nextAlert, "test", level, text, null, false, now, FightParser.DefaultAlertDuration);
        _alerts.Add(alert);
        return alert;
    }

    public ReadoutView View(DateTime now, double lookahead, int maxBars)
    {
        var bars = new List<TimelineBar>();
        double position = 0;
        if (_encounter is { } encounter)
        {
            position = encounter.Position(now);
            foreach (var entry in encounter.Fight.Timeline)
            {
                if (encounter.Fight.IsHidden(entry.Text)) continue;
                double remaining = entry.Time - position;
                if (remaining + entry.Duration < 0 || remaining > lookahead) continue;
                bars.Add(new TimelineBar(entry.Text, remaining, entry.Duration));
                if (bars.Count >= maxBars) break;
            }
        }
        BarasStatus? boss = null;
        var shown = _alerts.Where(a => a.At <= now && now < a.At.AddSeconds(a.Duration));
        if (UseBaras)
        {
            var baras = _baras.View(now);
            if (_encounter is null && baras.Status is { } status)
            {
                boss = status;
                position = (now - status.Start).TotalSeconds;
                bars.AddRange(baras.Bars.Take(maxBars));
            }
            shown = shown.Concat(baras.Countdowns);
        }
        var alerts = shown.OrderByDescending(a => a.Level).ThenByDescending(a => a.Id).ToList();
        string? result = null;
        DateTime resultAt = default;
        if (_lastResult is not null) (result, resultAt) = (_lastResult, _lastResultAt);
        if (UseBaras && _baras.LastResult is { } barasResult && (result is null || _baras.LastResultAt > resultAt))
            (result, resultAt) = (barasResult, _baras.LastResultAt);
        if (result is not null && (now - resultAt).TotalSeconds >= ResultSeconds) result = null;
        var readyBosses = UseBaras ? _baras.Ready.Count : 0;
        return new ReadoutView(now, _area?.Name, _difficulty?.Name, _active, _encounter?.Fight, position, bars, alerts, result,
            boss, readyBosses > 0 ? _baras.AreaName : null, readyBosses);
    }

    private bool Applies(FightFile fight) =>
        (fight.Areas.Count == 0 || fight.Areas.Any(a => a.Matches(_area))) &&
        (fight.Difficulties.Count == 0 || fight.Difficulties.Any(d => d.Matches(_difficulty)));

    private void Process(CombatEvent e, List<FiredAlert> fired)
    {
        if (e.Source is { IsPlayer: true } player &&
            (_owner is null || e.Effect.Type.Id is CombatIds.AreaEntered or CombatIds.DisciplineChanged))
            _owner = player;
        if (_owner is null) return;
        bool fromMe = IsMe(e.Source);

        if (e.Effect.Type.Id == CombatIds.AreaEntered && fromMe)
        {
            End(e.Timestamp, null);
            _area = e.Effect.Effect;
            _difficulty = e.Effect.Detail;
            _baras.EnterArea(e.Effect.Effect, e.Effect.Detail, e.Timestamp);
            _active = [.. _fights.Where(Applies)];
            _inCombat = false;
            _combatStart = null;
            return;
        }
        if (e.IsEvent(CombatIds.EnterCombat) && fromMe)
        {
            _inCombat = true;
            _ownerDead = false;
            _combatStart = e.Timestamp;
        }
        if (e.IsEvent(CombatIds.Death) && IsMe(e.Target)) _ownerDead = true;

        if (_encounter is null) TryStart(e);

        if (_encounter is { } encounter)
        {
            var fight = encounter.Fight;
            if (InvolvesBoss(fight, e)) encounter.LastBossEvent = e.Timestamp;
            NoteBosses(encounter, e);
            Sync(encounter, e);
            foreach (var trigger in fight.Triggers)
                if (trigger.Match is { } match && Matches(match, e, fight)) Fire(trigger, e, e.Timestamp, fired);

            if (fight.End is { } end ? Matches(end, e, fight) : encounter.AllBossesDown)
                End(e.Timestamp, "Victory");
            else if (e.IsEvent(CombatIds.ExitCombat) && fromMe && !_ownerDead)
                End(e.Timestamp, "Ended");
        }

        if (e.IsEvent(CombatIds.ExitCombat) && fromMe)
        {
            _inCombat = false;
            _combatStart = null;
        }
    }

    private void TryStart(CombatEvent e)
    {
        foreach (var fight in _active)
        {
            if (fight.Start is { } start)
            {
                if (!Matches(start, e, fight)) continue;
                StartEncounter(fight, e.Timestamp);
                return;
            }
            if (_inCombat && _combatStart is { } combatStart && InvolvesBoss(fight, e))
            {
                StartEncounter(fight, combatStart);
                return;
            }
        }
    }

    private void StartEncounter(FightFile fight, DateTime start)
    {
        _encounter = new Encounter(fight, start);
        _lastResult = null;
        _pending.Clear();
    }

    private void End(DateTime at, string? result)
    {
        if (_encounter is not { } encounter) return;
        if (result is not null)
        {
            var length = at - encounter.Start;
            _lastResult = $"{encounter.Fight.Name}: {result} after {(int)length.TotalMinutes}:{length.Seconds:00}";
            _lastResultAt = at;
        }
        _encounter = null;
        _pending.Clear();
    }

    private void Advance(DateTime now, List<FiredAlert> fired)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            var (at, trigger, e) = _pending[i];
            if (at > now) continue;
            _pending.RemoveAt(i--);
            Add(trigger, e, at, fired);
        }

        if (_encounter is { } encounter)
        {
            if (_ownerDead && (now - encounter.LastBossEvent).TotalSeconds > WipeTimeout)
            {
                End(now, "Wiped");
            }
            else
            {
                double position = encounter.Position(now);
                var fight = encounter.Fight;
                for (int t = 0; t < fight.Triggers.Count; t++)
                {
                    var trigger = fight.Triggers[t];
                    if (trigger.BeforeEntry is not { } label) continue;
                    for (int i = 0; i < fight.Timeline.Count; i++)
                    {
                        var entry = fight.Timeline[i];
                        if (!string.Equals(entry.Text, label, StringComparison.OrdinalIgnoreCase)) continue;
                        if (position < entry.Time - trigger.BeforeSeconds || position > entry.Time) continue;
                        if (!encounter.Fired.Add((t, i))) continue;
                        var at = encounter.TimeAt(entry.Time - trigger.BeforeSeconds);
                        Fire(trigger, null, at > now ? now : at, fired);
                    }
                }
            }
        }

        _alerts.RemoveAll(a => now >= a.At.AddSeconds(a.Duration));
    }

    private void Sync(Encounter encounter, CombatEvent e)
    {
        double position = encounter.Position(e.Timestamp);
        var fight = encounter.Fight;
        TimelineEntry? match = null;
        foreach (var entry in fight.Timeline)
        {
            if (entry.Sync is not { } sync) continue;
            if (position < entry.Time - entry.WindowBefore || position > entry.Time + entry.WindowAfter) continue;
            if (!Matches(sync, e, fight)) continue;
            match = entry;
            break;
        }
        if (match is null) return;
        encounter.SetPosition(e.Timestamp, match.Time);
        if (fight.JumpTarget(match) is { } target) encounter.SetPosition(e.Timestamp, target);
    }

    private void Fire(TriggerDef trigger, CombatEvent? e, DateTime at, List<FiredAlert> fired)
    {
        if (trigger.Suppress > 0 && _lastFired.TryGetValue(trigger.Id, out var last) && (at - last).TotalSeconds < trigger.Suppress) return;
        _lastFired[trigger.Id] = at;
        if (trigger.Delay > 0) _pending.Add((at.AddSeconds(trigger.Delay), trigger, e));
        else Add(trigger, e, at, fired);
    }

    private void Add(TriggerDef trigger, CombatEvent? e, DateTime at, List<FiredAlert> fired)
    {
        var alert = new FiredAlert(++_nextAlert, trigger.Id, trigger.Level, Fill(trigger.Text, e), Fill(trigger.Speech, e),
            trigger.Silent, at, trigger.Duration);
        _alerts.Add(alert);
        fired.Add(alert);
    }

    internal static string? Fill(string? text, CombatEvent? e)
    {
        if (text is null || e is null || !text.Contains('{')) return text;
        return text
            .Replace("{source}", e.Source?.Name ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{target}", e.Target?.Name ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{ability}", e.Ability?.Name ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{effect}", e.Effect.Effect.Name, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsMe(CombatEntity? entity) => entity is { IsPlayer: true } && _owner is not null && entity.Id == _owner.Id;

    private static bool IsBoss(FightFile fight, CombatEntity? entity) =>
        entity is { Kind: CombatEntityKind.Npc } && fight.Bosses.Any(b => b.Matches(new GameRef(entity.Name, entity.Id)));

    private static bool InvolvesBoss(FightFile fight, CombatEvent e) => IsBoss(fight, e.Source) || IsBoss(fight, e.Target);

    private static void NoteBosses(Encounter encounter, CombatEvent e)
    {
        foreach (var entity in new[] { e.Source, e.Target })
            if (IsBoss(encounter.Fight, entity)) encounter.Seen.Add(entity!.InstanceId ?? entity.Id);
        if (e.IsEvent(CombatIds.Death) && IsBoss(encounter.Fight, e.Target))
            encounter.Dead.Add(e.Target!.InstanceId ?? e.Target.Id);
    }

    private bool Matches(Matcher m, CombatEvent e, FightFile fight)
    {
        bool kind = m.Kind switch
        {
            EventKind.Ability => e.IsEvent(CombatIds.AbilityActivate) && Is(m.What, e.Ability),
            EventKind.Apply => e.Effect.Type.Id == CombatIds.ApplyEffect && !e.IsDamage && !e.IsHeal && Is(m.What, e.Effect.Effect),
            EventKind.Remove => e.Effect.Type.Id == CombatIds.RemoveEffect && Is(m.What, e.Effect.Effect),
            EventKind.Damage => e.IsDamage && Is(m.What, e.Ability),
            EventKind.Heal => e.IsHeal && Is(m.What, e.Ability),
            EventKind.Interrupt => e.IsEvent(CombatIds.AbilityInterrupt) && Is(m.What, e.Ability),
            EventKind.Death => e.IsEvent(CombatIds.Death),
            EventKind.Combat => e.IsEvent(CombatIds.EnterCombat) && IsMe(e.Source),
            _ => false
        };
        return kind && Is(m.From, e.Source, fight) && Is(m.On, e.Target, fight);
    }

    private static bool Is(Ref? what, GameRef? value) => what is null || what.Matches(value);

    private bool Is(Who who, CombatEntity? entity, FightFile fight) => who.Kind switch
    {
        WhoKind.Me => IsMe(entity),
        WhoKind.Boss => IsBoss(fight, entity),
        WhoKind.Named => entity is not null && who.Ref!.Matches(new GameRef(entity.Name, entity.Id)),
        _ => true
    };

    private sealed class Encounter(FightFile fight, DateTime start)
    {
        public FightFile Fight { get; private set; } = fight;
        public DateTime Start { get; } = start;
        public DateTime LastBossEvent { get; set; } = start;
        public HashSet<ulong> Seen { get; } = [];
        public HashSet<ulong> Dead { get; } = [];
        public HashSet<(int Trigger, int Entry)> Fired { get; private set; } = [];
        private DateTime _anchorTime = start;
        private double _anchorPosition;

        public bool AllBossesDown => Seen.Count > 0 && Seen.IsSubsetOf(Dead);

        public double Position(DateTime now) => _anchorPosition + (now - _anchorTime).TotalSeconds;

        public DateTime TimeAt(double position) => _anchorTime.AddSeconds(position - _anchorPosition);

        public void SetPosition(DateTime at, double position)
        {
            if (position < Position(at)) Fired.RemoveWhere(f => Fight.Timeline[f.Entry].Time >= position);
            _anchorTime = at;
            _anchorPosition = position;
        }

        public Encounter With(FightFile replacement)
        {
            var copy = new Encounter(replacement, Start)
            {
                LastBossEvent = LastBossEvent,
                _anchorTime = _anchorTime,
                _anchorPosition = _anchorPosition
            };
            copy.Seen.UnionWith(Seen);
            copy.Dead.UnionWith(Dead);
            return copy;
        }
    }
}
