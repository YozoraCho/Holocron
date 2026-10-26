using System.Globalization;
using Ashla.CombatLog;
using Ashla.SDK;
using TacticalReadout;

namespace Holocron.Tests;

internal static class FakeFight
{
    public const ulong AreaId = 833571547775000;
    public const ulong VeteranId = 836045448953652;
    public const ulong BossId = 4000000000000001;
    public const ulong SlamId = 5000000000000001;
    public const ulong ShockId = 5000000000000002;
    public const ulong SwipeId = 5000000000000003;
    public const ulong RoarId = 5000000000000004;

    public const string Me = "[@Tester#690000000000001|(0.00,0.00,0.00,0.00)|(100/100)]";
    public const string Boss = "[Test Boss {4000000000000001}:1000000000001|(0.00,0.00,0.00,0.00)|(5000000/5000000)]";
    public const string Add = "[Test Add {4000000000000009}:1000000000002|(0.00,0.00,0.00,0.00)|(20000/20000)]";

    private static readonly DateTime Day = new(2026, 10, 26);
    public static readonly DateTime Pull = Day.AddHours(20);

    public static string Line(double seconds, string source, string target, string ability, string effect) =>
        $"[{Pull.AddSeconds(seconds).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {source} {target} {ability} [{effect}]";

    public static string Area(double t, string difficulty = "8 Player Veteran {836045448953652}") =>
        Line(t, Me, "[]", "[]", $"AreaEntered {{836045448953664}}: Test Operation {{{AreaId}}} {difficulty}") + " (he3000) <v7.0.0b>";

    public static string EnterCombat(double t) => Line(t, Me, "[]", "[]", "Event {836045448945472}: EnterCombat {836045448945489}");
    public static string ExitCombat(double t) => Line(t, Me, "[]", "[]", "Event {836045448945472}: ExitCombat {836045448945490}");

    public static string Cast(double t, string name, ulong id, string who = Boss) =>
        Line(t, who, "[=]", $"[{name} {{{id}}}]", "Event {836045448945472}: AbilityActivate {836045448945479}");

    public static string Shock(double t) =>
        Line(t, Boss, Me, $"[Shock {{{ShockId}}}]", $"ApplyEffect {{836045448945477}}: Shock {{{ShockId}}}");

    public static string Hit(double t) =>
        Line(t, Me, Boss, "[Saber Strike {3000000000000001}]", "ApplyEffect {836045448945477}: Damage {836045448945501}") +
        " (1000 energy {836045448940874}) <1000>";

    public static string Death(double t, string who) => Line(t, Me, who, "[]", "Event {836045448945472}: Death {836045448945493}");
    public static string MyDeath(double t) => Line(t, Boss, Me, "[]", "Event {836045448945472}: Death {836045448945493}");

    public static CombatEvent Parse(string line)
    {
        Assert.True(CombatLogParser.TryParse(line, Day, "test.txt", out var parsed), $"Didn't parse: {line}");
        return parsed!;
    }

    public const string File = """
        # A made-up fight for tests.
        encounter "Test Boss"
        area 833571547775000
        difficulty "8 Player Veteran"
        boss 4000000000000001

        hideall "--sync--"

        0 "Pull"
        10 "Big Slam" sync ability 5000000000000001 from boss window 4
        20 "--sync--" sync ability 5000000000000004 from boss
        30 label "loop"
        40 "Swipe" sync ability 5000000000000003 from boss window 5,5 duration 3
        50 "Swipe again" sync ability 5000000000000003 from boss window 3 jump "loop"

        trigger "slam soon"
          before "Big Slam" 3
          alert "Slam in 3"
          speak "slam"
        end

        trigger "shock"
          match apply 5000000000000002 on me
          alarm "{effect} on you, move!"
          suppress 5
        end

        trigger "after swipe"
          match ability 5000000000000003 from boss
          info "Swipe over"
          delay 2
          duration 3
          silent
        end
        """;
}

public class FightParserTests
{
    [Fact]
    public void Parses_Header_Timeline_AndTriggers()
    {
        var fight = FightParser.Parse("test.txt", FakeFight.File);

        Assert.Equal("Test Boss", fight.Name);
        Assert.Equal(FakeFight.AreaId, Assert.Single(fight.Areas).Id);
        Assert.Equal("8 Player Veteran", Assert.Single(fight.Difficulties).Name);
        Assert.Equal(FakeFight.BossId, Assert.Single(fight.Bosses).Id);
        Assert.True(fight.IsHidden("--SYNC--"));
        Assert.Equal(5, fight.Timeline.Count);
        Assert.Equal(30, fight.Labels["loop"]);

        var slam = fight.Timeline[1];
        Assert.Equal((10d, "Big Slam", 4d, 4d), (slam.Time, slam.Text, slam.WindowBefore, slam.WindowAfter));
        Assert.Equal(new Matcher(EventKind.Ability, new Ref(FakeFight.SlamId, null), new Who(WhoKind.Boss), Who.Any), slam.Sync);
        Assert.Equal(3, fight.Timeline[3].Duration);
        Assert.Equal(30, fight.JumpTarget(fight.Timeline[4]));

        Assert.Equal(3, fight.Triggers.Count);
        Assert.Equal(("Big Slam", 3d, AlertLevel.Alert, "slam"), (fight.Triggers[0].BeforeEntry, fight.Triggers[0].BeforeSeconds,
            fight.Triggers[0].Level, fight.Triggers[0].Speech));
        Assert.Equal(new Who(WhoKind.Me), fight.Triggers[1].Match!.On);
        Assert.Equal(AlertLevel.Alarm, fight.Triggers[1].Level);
        Assert.True(fight.Triggers[2].Silent);
        Assert.Equal(2, fight.Triggers[2].Delay);
    }

    [Theory]
    [InlineData("area 1", 1, "must start with")]
    [InlineData("encounter \"X\"\nboss 1\nexplode 3", 3, "isn't something")]
    [InlineData("encounter \"X\"\nboss 1\n5 \"A\" jump 0", 3, "needs a sync")]
    [InlineData("encounter \"X\"\nboss 1\n5 \"A\" sync ability 2 jump \"nowhere\"", 3, "no label")]
    [InlineData("encounter \"X\"\nboss 1\ntrigger \"t\"\n  match death boss\n  info \"x\"", 3, "has no end")]
    [InlineData("encounter \"X\"\nboss 1\ntrigger \"t\"\n  info \"x\"\nend", 3, "match line or a before line")]
    [InlineData("encounter \"X\"\nboss 1\ntrigger \"t\"\n  match explode 2\nend", 4, "isn't an event")]
    [InlineData("encounter \"X\"\nboss 1\n5 \"unclosed", 3, "isn't closed")]
    [InlineData("encounter \"X\"\n5 \"A\"", 0, "at least one boss")]
    public void Problems_SayWhereAndWhat(string text, int line, string message)
    {
        var error = Assert.Throws<FightFormatException>(() => FightParser.Parse("x.txt", text));
        Assert.Equal(line, error.Line);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Matchers_RoundTrip()
    {
        var fight = FightParser.Parse("x.txt", "encounter \"X\"\nboss \"Test Boss\"\nstart ability \"Roar\" from \"Test Boss\" on me\nend death boss");
        Assert.Equal("ability \"Roar\" from \"Test Boss\" on me", fight.Start!.ToString());
        Assert.Equal("death boss", fight.End!.ToString());
    }
}

public class ReadoutEngineTests
{
    private readonly ReadoutEngine _engine = new();
    private readonly List<FiredAlert> _fired = [];

    public ReadoutEngineTests() => _engine.SetFights([FightParser.Parse("test.txt", FakeFight.File)]);

    private void Feed(params string[] lines)
    {
        foreach (var line in lines) _fired.AddRange(_engine.Handle(FakeFight.Parse(line)));
    }

    private ReadoutView ViewAt(double seconds)
    {
        _fired.AddRange(_engine.Tick(FakeFight.Pull.AddSeconds(seconds)));
        return _engine.View(FakeFight.Pull.AddSeconds(seconds), 30, 6);
    }

    [Fact]
    public void Pull_StartsAtCombat_WhenTheBossShowsUp()
    {
        Feed(FakeFight.Area(-30));
        Assert.Equal("Test Boss", Assert.Single(ViewAt(-30).Ready).Name);
        Feed(FakeFight.EnterCombat(0));
        Assert.Null(ViewAt(0.5).Fight);
        Feed(FakeFight.Hit(1));

        var view = ViewAt(2);
        Assert.Equal("Test Boss", view.Fight?.Name);
        Assert.Equal(2, view.Position, 3);
        Assert.Equal(["Big Slam"], view.Bars.Select(b => b.Text));
        Assert.Equal(8, view.Bars[0].Remaining, 3);
    }

    [Fact]
    public void WrongDifficulty_OrArea_LoadsNothing()
    {
        Feed(FakeFight.Area(-30, "8 Player Story {836045448953651}"), FakeFight.EnterCombat(0), FakeFight.Hit(1));
        Assert.Empty(ViewAt(2).Ready);
        Assert.Null(ViewAt(2).Fight);
    }

    [Fact]
    public void Syncs_MoveTheTimeline_AndJumpsLoop()
    {
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeFight.Hit(1));
        Feed(FakeFight.Cast(12.5, "Big Slam", FakeFight.SlamId));
        Assert.Equal(10, ViewAt(12.5).Position, 3);

        Feed(FakeFight.Cast(25, "Roar", FakeFight.RoarId));
        Assert.Equal(20, ViewAt(25).Position, 3);

        Feed(FakeFight.Cast(45, "Swipe", FakeFight.SwipeId));
        Assert.Equal(40, ViewAt(45).Position, 3);
        Assert.Equal("Swipe", ViewAt(46).Bars[0].Text);
        Assert.True(ViewAt(46).Bars[0].Active);

        Feed(FakeFight.Cast(56, "Swipe", FakeFight.SwipeId));
        Assert.Equal(30, ViewAt(56).Position, 3);
        Assert.Equal("Swipe", ViewAt(56).Bars[0].Text);
        Assert.Equal(10, ViewAt(56).Bars[0].Remaining, 3);
    }

    [Fact]
    public void SyncsOutsideTheirWindow_AreIgnored()
    {
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeFight.Hit(1), FakeFight.Cast(2, "Big Slam", FakeFight.SlamId));
        Assert.Equal(2, ViewAt(2).Position, 3);
    }

    [Fact]
    public void Triggers_FireFromEvents_TimelineAndDelays()
    {
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeFight.Hit(1));
        ViewAt(6.9);
        Assert.Empty(_fired);
        ViewAt(7.1);
        var soon = Assert.Single(_fired);
        Assert.Equal(("Slam in 3", "slam", AlertLevel.Alert), (soon.Text, soon.Spoken, soon.Level));
        ViewAt(8);
        Assert.Single(_fired);

        Feed(FakeFight.Shock(9), FakeFight.Shock(11), FakeFight.Shock(15));
        var shocks = _fired.Where(a => a.TriggerId == "shock").ToList();
        Assert.Equal(2, shocks.Count);
        Assert.Equal("Shock on you, move!", shocks[0].Text);
        Assert.Equal(AlertLevel.Alarm, shocks[0].Level);

        Feed(FakeFight.Cast(40, "Swipe", FakeFight.SwipeId));
        Assert.DoesNotContain(_fired, a => a.TriggerId == "after swipe");
        ViewAt(42.1);
        var after = Assert.Single(_fired, a => a.TriggerId == "after swipe");
        Assert.Null(after.Spoken);
        Assert.Contains(after, ViewAt(43).Alerts);
        Assert.DoesNotContain(after, ViewAt(46).Alerts);
    }

    [Fact]
    public void TimelineTriggers_FireAgainAfterALoop()
    {
        var fight = FightParser.Parse("loop.txt", """
            encounter "Loop"
            boss 4000000000000001
            5 label "top"
            10 "Big Slam" sync ability 5000000000000001 from boss
            20 "Again" sync ability 5000000000000003 from boss jump "top"
            trigger "slam soon"
              before "Big Slam" 2
              info "Slam"
            end
            """);
        _engine.SetFights([fight]);
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeFight.Hit(1));
        ViewAt(8.5);
        Feed(FakeFight.Cast(20, "Swipe", FakeFight.SwipeId));
        ViewAt(23.5);
        Assert.Equal(2, _fired.Count(a => a.TriggerId == "slam soon"));
    }

    [Fact]
    public void BossDeath_IsAVictory()
    {
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeFight.Hit(1), FakeFight.Death(95, FakeFight.Boss));
        var view = ViewAt(96);
        Assert.Null(view.Fight);
        Assert.Equal("Test Boss: Victory after 1:35", view.LastResult);
        Assert.Null(ViewAt(96 + ReadoutEngine.ResultSeconds + 1).LastResult);
    }

    [Fact]
    public void LeavingCombat_EndsTheFight_ButYourDeathDoesNot()
    {
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeFight.Hit(1), FakeFight.MyDeath(20), FakeFight.ExitCombat(20.5));
        Assert.NotNull(ViewAt(25).Fight);
        Feed(FakeFight.EnterCombat(30), FakeFight.Hit(31));
        Assert.Equal(31, ViewAt(31).Position, 3);
        Feed(FakeFight.ExitCombat(60));
        Assert.Equal("Test Boss: Ended after 1:00", ViewAt(60).LastResult);
    }

    [Fact]
    public void AWipe_EndsTheFight_WhenTheBossGoesQuiet()
    {
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeFight.Hit(1), FakeFight.MyDeath(20));
        Assert.NotNull(ViewAt(45).Fight);
        Assert.Equal("Test Boss: Wiped after 0:51", ViewAt(51.5).LastResult);
    }

    [Fact]
    public void TrashInTheSameArea_DoesntStartTheFight()
    {
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0),
            FakeFight.Line(1, FakeFight.Me, FakeFight.Add, "[Saber Strike {3000000000000001}]", "ApplyEffect {836045448945477}: Damage {836045448945501}") + " (500 energy {836045448940874})",
            FakeFight.ExitCombat(10));
        Assert.Null(ViewAt(11).Fight);
        Assert.Null(ViewAt(11).LastResult);
    }
}

public class ReadoutServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class Config(string path) : IPluginConfig
    {
        public string FilePath { get; } = path;
        public object? Saved { get; private set; }
        public T Load<T>() where T : class, new() => Saved as T ?? new T();
        public bool Save<T>(T settings) where T : class
        {
            Saved = settings;
            return true;
        }
    }

    private sealed class Toasts : IToasts
    {
        public List<string> Shown { get; } = [];
        public void Show(string title, string? text = null, ToastKind kind = ToastKind.Info) => Shown.Add($"{kind}: {text}");
    }

    private sealed class Voice : ISpeaker
    {
        public List<(string Text, bool Interrupt)> Said { get; } = [];
        public bool Available => true;
        public void Speak(string text, int volume, int rate, bool interrupt) => Said.Add((text, interrupt));
        public void Dispose() { }
    }

    [Fact]
    public void LoadsFightFiles_ReportsProblems_AndSpeaksAlerts()
    {
        var settingsFile = Path.Combine(_temp.Path, "pluginConfigs", "TacticalReadout.json");
        var builtIn = Directory.CreateDirectory(Path.Combine(_temp.Path, "Fights")).FullName;
        File.WriteAllText(Path.Combine(builtIn, "Test Boss.txt"), "encounter \"Old\"\nboss 1\n");
        File.WriteAllText(Path.Combine(builtIn, "Broken.txt"), "encounter \"Broken\"\nboss 1\nexplode\n");
        var toasts = new Toasts();
        var voice = new Voice();
        using var service = new ReadoutService(new Config(settingsFile), new TestLogger(), toasts, voice, builtIn);
        Directory.CreateDirectory(service.UserFolder!);
        File.WriteAllText(Path.Combine(service.UserFolder!, "Test Boss.txt"), FakeFight.File);

        service.Reload(announce: true);

        var loaded = Assert.Single(service.Loaded);
        Assert.Equal(("Test Boss", false), (loaded.Fight.Name, loaded.BuiltIn));
        Assert.Equal("Broken.txt line 3: \"explode\" isn't something a fight file understands.", Assert.Single(service.Problems).ToString());
        Assert.StartsWith("Warning:", Assert.Single(toasts.Shown));

        service.Settings.Speech = true;
        foreach (var line in new[] { FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeFight.Hit(1), FakeFight.Shock(2) })
            service.Handle(FakeFight.Parse(line));
        var view = service.Frame(1);
        Assert.Equal("Test Boss", view.Fight?.Name);
        Assert.Same(view, service.Frame(1));
        Assert.Equal(("Shock on you, move!", true), Assert.Single(voice.Said));
    }

    [Fact]
    public void MakesTimelines_IntoTheUserFolder_WithoutOverwriting()
    {
        var settingsFile = Path.Combine(_temp.Path, "pluginConfigs", "TacticalReadout.json");
        using var service = new ReadoutService(new Config(settingsFile), new TestLogger(), new Toasts(), new SilentSpeaker(),
            Path.Combine(_temp.Path, "none"));
        var events = new[] { FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeFight.Cast(3, "Big Slam", FakeFight.SlamId) }
            .Select(FakeFight.Parse).ToList();

        var first = service.MakeTimeline(events, "Test", out _);
        var second = service.MakeTimeline(events, "Test", out var message);

        Assert.Equal("Test Boss.txt", Path.GetFileName(first));
        Assert.Equal("Test Boss (2).txt", Path.GetFileName(second));
        Assert.Equal("Saved Test Boss (2).txt with 1 boss ability.", message);
        Assert.Equal(2, service.Loaded.Count);
        Assert.Null(new ReadoutService(new Config("(not saved)"), new TestLogger(), new Toasts(), new SilentSpeaker(), "x").UserFolder);
    }
}

public class TimelineMakerTests
{
    private static List<CombatEvent> RecordedPull()
    {
        var lines = new List<string> { FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeFight.Hit(1) };
        for (int i = 0; i < 20; i++) lines.Add(FakeFight.Cast(2 + i * 2, "Melee", 5000000000000099));
        lines.Add(FakeFight.Cast(10.04, "Big Slam", FakeFight.SlamId));
        lines.Add(FakeFight.Shock(12));
        lines.Add(FakeFight.Cast(25, "Roar", FakeFight.RoarId));
        lines.Add(FakeFight.Cast(30, "Add Swing", 5000000000000050, FakeFight.Add));
        lines.Add(FakeFight.Death(60, FakeFight.Boss));
        return [.. lines.Select(FakeFight.Parse).OrderBy(e => e.Timestamp)];
    }

    [Fact]
    public void MakesAStarterFile_ThatLoadsAndSyncs()
    {
        var made = TimelineMaker.Make(RecordedPull(), "Test Boss")!;

        Assert.Equal("Test Boss.txt", made.FileName);
        Assert.Equal((2, 1), (made.Abilities, made.Bosses));
        Assert.Contains("10.0 \"Big Slam\" sync ability 5000000000000001 from boss", made.Text);
        Assert.Contains("Left out Melee, used 20 times", made.Text);
        Assert.DoesNotContain("Add Swing", made.Text);
        Assert.Contains("#   match apply 5000000000000002 on me", made.Text);

        var fight = FightParser.Parse(made.FileName, made.Text);
        Assert.Equal(FakeFight.AreaId, fight.Areas[0].Id);
        Assert.Equal(FakeFight.VeteranId, fight.Difficulties[0].Id);
        Assert.Equal(FakeFight.BossId, fight.Bosses[0].Id);

        var engine = new ReadoutEngine();
        engine.SetFights([fight]);
        foreach (var e in RecordedPull().Take(4)) engine.Handle(e);
        engine.Handle(FakeFight.Parse(FakeFight.Cast(11, "Big Slam", FakeFight.SlamId)));
        Assert.Equal(10, engine.View(FakeFight.Pull.AddSeconds(11), 30, 6).Position, 3);
    }

    [Fact]
    public void NothingToMake_FromAFightWithoutNpcs()
    {
        Assert.Null(TimelineMaker.Make([FakeFight.Parse(FakeFight.Area(0)), FakeFight.Parse(FakeFight.EnterCombat(1))]));
        Assert.Equal("Bad- Name-", TimelineMaker.SafeFileName("Bad: Name?"));
    }
}
