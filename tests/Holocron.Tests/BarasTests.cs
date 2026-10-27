using System.Numerics;
using Ashla.SDK;
using TacticalReadout;

namespace Holocron.Tests;

internal static class FakeBaras
{
    public const string File = """
        [area]
        name = "Test Operation"
        area_id = 833571547775000

        [[boss]]
        id = "test_boss"
        name = "Test Boss"

        [[boss.entities]]
        name = "Test Boss"
        ids = [4000000000000001]
        is_boss = true
        is_kill_target = true

        [[boss.entities]]
        name = "Test Add"
        ids = [4000000000000009]

        [[boss.phases]]
        id = "p1"
        name = "Opening"
        start_trigger = { type = "combat_start" }

        [[boss.phases]]
        id = "p2"
        name = "Burn"
        preceded_by = "p1"
        start_trigger = { type = "boss_hp_below", hp_percent = 50.0, selector = ["Test Boss"] }

        [[boss.counters]]
        id = "slams"
        name = "Slams"
        increment_on = { type = "ability_cast", abilities = [5000000000000001], source = "boss" }

        [[boss.timer]]
        id = "enrage"
        name = "Enrage"
        duration_secs = 300.0
        show_at_secs = 60.0
        trigger = { type = "combat_start" }

        [[boss.timer]]
        id = "slam"
        name = "Big Slam"
        display_text = "Next Slam"
        duration_secs = 20.0
        color = [0, 128, 255, 255]
        alert_on = "on_expire"
        alert_text = "Slam now"
        trigger = { type = "ability_cast", abilities = [5000000000000001], source = "boss" }
        cancel_trigger = { type = "phase_entered", phase_id = "p2" }

        [[boss.timer]]
        id = "third_slam"
        name = "Third slam"
        is_alert = true
        alert_text = "Third slam, swap!"
        trigger = { type = "counter_reaches", counter_id = "slams", value = 3 }

        [[boss.timer]]
        id = "shock"
        name = "Shock"
        is_alert = true
        alert_text = "Shock on {target}"
        trigger = { type = "effect_applied", effects = [5000000000000002], target = "local_player" }

        [[boss.timer]]
        id = "burn"
        name = "Burn phase"
        duration_secs = 10.0
        chains_to = "after_burn"
        trigger = { type = "phase_entered", phase_id = "p2" }
        audio = { enabled = true, offset = 3, countdown_start = 2 }

        [[boss.timer]]
        id = "after_burn"
        name = "After burn"
        duration_secs = 5.0
        trigger = { type = "manual" }

        [[boss.timer]]
        id = "master_only"
        name = "Master only"
        duration_secs = 30.0
        difficulties = ["master"]
        trigger = { type = "combat_start" }

        [[boss.timer]]
        id = "swipe_in_burn"
        name = "Swipe (burn)"
        duration_secs = 8.0
        conditions = [{ type = "phase_active", phase_ids = ["p2"] }]
        trigger = { type = "ability_cast", abilities = [5000000000000003] }
        """;

    public static string Boss(long hp) => $"[Test Boss {{4000000000000001}}:1000000000001|(0.00,0.00,0.00,0.00)|({hp}/5000000)]";

    public static string Hit(double t, long bossHp) =>
        FakeFight.Line(t, FakeFight.Me, Boss(bossHp), "[Saber Strike {3000000000000001}]", "ApplyEffect {836045448945477}: Damage {836045448945501}") +
        " (1000 energy {836045448940874}) <1000>";

    public static ReadoutEngine Engine(string toml = File)
    {
        var engine = new ReadoutEngine();
        engine.SetBaras([BarasLoader.Parse("test.toml", toml)]);
        return engine;
    }
}

public class BarasLoaderTests
{
    private static string BundledFolder =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TacticalReadout", "Fights", "BARAS");

    [Fact]
    public void EveryBundledEncounterFile_Loads()
    {
        var files = Directory.EnumerateFiles(BundledFolder, "*.toml", SearchOption.AllDirectories).ToList();
        var areas = files.Select(f => BarasLoader.Parse(f, System.IO.File.ReadAllText(f))).ToList();

        Assert.Equal(54, files.Count);
        Assert.Equal("19 operations, 34 flashpoints and 1 other area", BarasArea.Describe(areas));
        Assert.Equal("17 operations", BarasArea.Describe(areas.Where(a => a.TimerCount > 0)));
        Assert.All(areas.Where(a => a.Type == "flashpoint"), a => Assert.Equal(0, a.TimerCount));
        Assert.All(areas, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.True(areas.Sum(a => a.Bosses.Count) >= 200);
        Assert.True(areas.Sum(a => a.TimerCount) >= 900);
        Assert.True(System.IO.File.Exists(Path.Combine(BundledFolder, "LICENSE")));

        var palace = areas.Single(a => a.Name == "The Dread Palace");
        Assert.Equal(137438993410UL, palace.AreaId);
        var bestia = palace.Bosses.Single(b => b.Id == "bestia");
        Assert.Contains(3273941900591104UL, bestia.EncounterIds);
        Assert.Equal(["bestisa_main", "bestia_plus_monsters", "bestia_phase_3_vm"], bestia.Phases.Select(p => p.Id));
        var tentacle = bestia.Timers.Single(t => t.Id == "bestia_tentacle_cs");
        Assert.Equal(("Tentacle Spawn", 16.5), (tentacle.Label, tentacle.Duration));
        Assert.Equal(new Vector4(50, 200, 50, 255) / 255f, tentacle.Color);
    }

    [Fact]
    public void ReadsSubTablesOfAnEarlierList_LikeTheTomlSpecSays()
    {
        const string text = """
            [area]
            name = "A"
            [[boss]]
            id = "b"
            [[boss.counters]]
            id = "stacks"
            [boss.counters.track_effect_stacks]
            effects = [1]
            [[boss.timer]]
            id = "t"
            name = "T"
            [boss.counters.increment_on]
            type = "ability_cast"
            abilities = [2]
            [boss.timer.trigger]
            type = "counter_reaches"
            counter_id = "stacks"
            value = 10
            """;

        var boss = Assert.Single(BarasLoader.Parse("x.toml", text).Bosses);

        var counter = Assert.Single(boss.Counters);
        Assert.Equal("stacks", counter.Id);
        Assert.Equal(BarasTriggerKind.AbilityCast, counter.IncrementOn.Kind);
        Assert.NotNull(counter.Stacks);
        Assert.Equal(BarasTriggerKind.CounterReaches, Assert.Single(boss.Timers).Trigger.Kind);
    }

    [Theory]
    [InlineData("[area]\nname = \"A\"\n[[boss]]\nid = \"b\"\n[[boss.timer]]\nid = \"t\"\nname = \"T\"\ntrigger = { type = \"explode\" }", "Boss \"b\": timer \"t\": Unknown trigger type \"explode\".")]
    [InlineData("[area]\nname = \"A\"\n[[boss]]\nid = \"b\"\n[[boss.timer]]\nid = \"t\"\nname = \"T\"", "Boss \"b\": timer \"t\": It has no trigger.")]
    [InlineData("[area]\nname = \"A\"\n[[boss]]\nid = \"b\"\n[[boss.timer]]\nid = \"t\"\nname = \"T\"\ntrigger = { type = \"ability_cast\", source = \"someone\" }", "Unknown source \"someone\".")]
    [InlineData("name = \"A\"", "There's no [area] table.")]
    [InlineData("[area]\nname = ", "Line 2:")]
    public void Problems_SayWhatIsWrong(string text, string message)
    {
        var error = Assert.Throws<BarasFormatException>(() => BarasLoader.Parse("x.toml", text));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("veteran", "veteran", 8, true)]
    [InlineData("veteran_16", "veteran", 8, false)]
    [InlineData("Master_8", "master", 8, true)]
    [InlineData("story", "master", 16, false)]
    public void DifficultyKeys_MatchLikeBaras(string key, string tier, int size, bool matches) =>
        Assert.Equal(matches, new BarasDifficulty(tier, size).Matches(key));
}

public class BarasEngineTests
{
    private readonly ReadoutEngine _engine = FakeBaras.Engine();
    private readonly List<FiredAlert> _fired = [];

    private void Feed(params string[] lines)
    {
        foreach (var line in lines)
        {
            var e = FakeFight.Parse(line);
            TickTo(e.Timestamp);
            _fired.AddRange(_engine.Handle(e));
        }
    }

    private DateTime _clock = FakeFight.Pull.AddSeconds(-60);

    private void TickTo(DateTime until)
    {
        for (var t = _clock.AddMilliseconds(100); t < until; t = t.AddMilliseconds(100)) _fired.AddRange(_engine.Tick(t));
        _clock = until;
    }

    private ReadoutView ViewAt(double seconds)
    {
        var at = FakeFight.Pull.AddSeconds(seconds);
        TickTo(at);
        _fired.AddRange(_engine.Tick(at));
        return _engine.View(at, 30, 6);
    }

    private IEnumerable<string?> Texts => _fired.Select(a => a.Text);

    [Fact]
    public void RunsABossFight_PhasesCountersTimersAndAlerts()
    {
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(1));
        Assert.Equal(("Test Operation", 1), (ViewAt(1.5).BarasArea, ViewAt(1.5).BarasBosses));
        Assert.Equal("Test Operation (1 boss)", TimelinePanel.ReadyText(ViewAt(1.5)));
        Assert.Equal("Mandalorian Raiders (5 bosses, no timers)",
            TimelinePanel.ReadyText(ViewAt(1.5) with { BarasArea = "Mandalorian Raiders", BarasBosses = 5, BarasTimers = 0 }));
        Assert.Null(ViewAt(1.5).Boss);

        Feed(FakeBaras.Hit(2, 5000000));
        var view = ViewAt(2);
        Assert.Equal(("Test Boss", "Opening"), (view.Boss?.Boss, view.Boss?.Phase));
        Assert.Equal(1, view.Position, 3);
        Assert.Empty(view.Bars);

        Feed(FakeFight.Cast(5, "Big Slam", FakeFight.SlamId));
        var slam = Assert.Single(ViewAt(5).Bars);
        Assert.Equal(("Next Slam", 20d, true), (slam.Text, slam.Remaining, slam.Depletes));
        Assert.Equal(new Vector4(0, 128, 255, 255) / 255f, slam.Color);
        Assert.Equal(1u, _engine.Baras.Counter("slams"));

        ViewAt(25.05);
        var now = Assert.Single(_fired, a => a.Text == "Slam now");
        Assert.Equal((FakeFight.Pull.AddSeconds(25), "Slam now"), (now.At, now.Spoken));

        Feed(FakeFight.Cast(30, "Big Slam", FakeFight.SlamId), FakeFight.Cast(32, "Big Slam", FakeFight.SlamId));
        Assert.Contains("Third slam, swap!", Texts);
        Assert.Equal(18, ViewAt(32).Bars.Single(b => b.Text == "Next Slam").Remaining, 3);

        Feed(FakeFight.Shock(33));
        Assert.Contains("Shock on Tester", Texts);

        Feed(FakeFight.Cast(34, "Swipe", FakeFight.SwipeId));
        Assert.DoesNotContain(ViewAt(34).Bars, b => b.Text == "Swipe (burn)");

        Feed(FakeBaras.Hit(35, 2000000));
        view = ViewAt(35);
        Assert.Equal("Burn", view.Boss?.Phase);
        Assert.DoesNotContain(view.Bars, b => b.Text == "Next Slam");
        Assert.Equal(10, view.Bars.Single(b => b.Text == "Burn phase").Remaining, 3);

        Feed(FakeFight.Cast(36, "Swipe", FakeFight.SwipeId));
        Assert.Contains(ViewAt(36).Bars, b => b.Text == "Swipe (burn)");

        ViewAt(45.2);
        var spoken = _fired.Where(a => a.TriggerId == "baras:burn").Select(a => (a.Text, a.Spoken)).ToList();
        Assert.Equal([(null, "Burn phase"), (null, "2"), (null, "1")], spoken);
        Assert.Contains(ViewAt(45.2).Bars, b => b.Text == "After burn");

        Feed(FakeFight.Death(50, FakeBaras.Boss(0)));
        view = ViewAt(50);
        Assert.Null(view.Boss);
        Assert.Equal("Test Boss: Victory after 0:49", view.LastResult);

        Feed(FakeBaras.Hit(51, 100));
        Assert.Null(ViewAt(51).Boss);
        Assert.DoesNotContain(_fired, a => a.Text == "Slam now" && a.At > FakeFight.Pull.AddSeconds(26));
    }

    [Fact]
    public void ShowAtSecs_HidesABarUntilItsLastStretch()
    {
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeBaras.Hit(1, 5000000));
        Assert.DoesNotContain(ViewAt(239).Bars, b => b.Text == "Enrage");
        Assert.Equal(59, ViewAt(241).Bars.Single(b => b.Text == "Enrage").Remaining, 3);
    }

    [Fact]
    public void DifficultyFilters_ComeFromTheAreaLine()
    {
        Feed(FakeFight.Area(-30, "8 Player Master {836045448953655}"), FakeFight.EnterCombat(0), FakeBaras.Hit(1, 5000000));
        Assert.Equal(29, ViewAt(1).Bars.Single(b => b.Text == "Master only").Remaining, 3);
    }

    [Fact]
    public void ZeroHealth_CountsAsADeath()
    {
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeBaras.Hit(1, 5000000), FakeBaras.Hit(61, 0));
        Assert.Equal("Test Boss: Victory after 1:01", ViewAt(62).LastResult);
    }

    [Fact]
    public void LeavingCombatDead_IsAWipe()
    {
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeBaras.Hit(1, 5000000), FakeFight.MyDeath(20), FakeFight.ExitCombat(25));
        Assert.Equal("Test Boss: Wiped after 0:25", ViewAt(26).LastResult);
        Assert.Null(ViewAt(26).Boss);
    }

    [Fact]
    public void OtherAreas_AndTurningItOff_ShowNothing()
    {
        Feed(FakeFight.Line(-30, FakeFight.Me, "[]", "[]", "AreaEntered {836045448953664}: Somewhere Else {999} 8 Player Veteran {836045448953652}"),
            FakeFight.EnterCombat(0), FakeBaras.Hit(1, 5000000));
        Assert.Null(ViewAt(2).Boss);

        var engine = FakeBaras.Engine();
        engine.UseBaras = false;
        foreach (var line in new[] { FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeBaras.Hit(1, 5000000) }) engine.Handle(FakeFight.Parse(line));
        var view = engine.View(FakeFight.Pull.AddSeconds(2), 30, 6);
        Assert.Null(view.Boss);
        Assert.Equal(0, view.BarasBosses);
    }

    [Fact]
    public void AFightFile_ForTheSameBoss_TakesTheTimeline()
    {
        _engine.SetFights([FightParser.Parse("test.txt", FakeFight.File)]);
        Feed(FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeBaras.Hit(1, 5000000), FakeFight.Shock(2));
        var view = ViewAt(2);
        Assert.Equal("Test Boss", view.Fight?.Name);
        Assert.Null(view.Boss);
        Assert.Contains("Shock on Tester", Texts);
        Assert.Contains("Shock on you, move!", Texts);
    }

    [Fact]
    public void RealBarasData_BestiaInTheDreadPalace()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TacticalReadout", "Fights", "BARAS", "operations", "the_dread_palace.toml");
        var engine = new ReadoutEngine();
        engine.SetBaras([BarasLoader.Parse(file, System.IO.File.ReadAllText(file))]);
        var fired = new List<FiredAlert>();
        void Feed(params string[] lines)
        {
            foreach (var line in lines) fired.AddRange(engine.Handle(FakeFight.Parse(line)));
        }
        ReadoutView ViewAt(double seconds)
        {
            var at = FakeFight.Pull.AddSeconds(seconds);
            fired.AddRange(engine.Tick(at));
            return engine.View(at, 30, 6);
        }
        string Bestia(long hp) => $"[Dread Master Bestia {{3273941900591104}}:5320000112163|(137.28,-120.98,-8.85,81.28)|({hp}/19129210)]";
        string Npc(string name, ulong id, int n, int hp) => $"[{name} {{{id}}}:53200002{n:00000}|(150.00,-110.00,-10.00,0.00)|({hp}/500000)]";

        Feed(FakeFight.Line(-30, FakeFight.Me, "[]", "[]", "AreaEntered {836045448953664}: The Dread Palace {137438993410} 8 Player Veteran {836045448953652}"),
            FakeFight.EnterCombat(0),
            FakeFight.Line(1, FakeFight.Me, Npc("Dread Monster", 3291675820556288, 1, 500000), "[Saber Strike {3000000000000001}]",
                "ApplyEffect {836045448945477}: Damage {836045448945501}"));

        var view = ViewAt(1);
        Assert.Equal("Bestia", view.Boss?.Boss);
        Assert.Equal(15.5, view.Bars.Single(b => b.Text == "Tentacle Spawn").Remaining, 3);
        Assert.DoesNotContain(view.Bars, b => b.Text == "Bestia Enters Fight");

        for (int i = 0; i < 10; i++)
            Feed(FakeFight.Death(10 + i, Npc("Dread Larva", 3292079547482112, 100 + i, 0)));
        Assert.Equal(10u, engine.Baras.Counter("bestia_a_d"));
        view = ViewAt(20);
        Assert.Equal("Bestia", view.Boss?.Phase);
        Assert.DoesNotContain("bestia_coming_down", engine.Baras.RunningTimers);

        Feed(FakeFight.Line(30, FakeFight.Me, Bestia(19129210), "[Saber Strike {3000000000000001}]", "ApplyEffect {836045448945477}: Damage {836045448945501}"),
            FakeFight.Line(31, FakeFight.Me, Bestia(9000000), "[Saber Strike {3000000000000001}]", "ApplyEffect {836045448945477}: Damage {836045448945501}"));
        Assert.Equal("Bestia and Monsters", ViewAt(31).Boss?.Phase);

        Feed(FakeFight.Death(200, Bestia(0)));
        Assert.Equal("Bestia: Victory after 3:20", ViewAt(200).LastResult);
    }
}

public class BarasServiceTests : IDisposable
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
        public void Show(string title, string? text = null, ToastKind kind = ToastKind.Info) { }
    }

    private sealed class Voice : ISpeaker
    {
        public List<string> Said { get; } = [];
        public bool Available => true;
        public void Speak(string text, int volume, int rate, bool interrupt) => Said.Add(text);
        public void Dispose() { }
    }

    [Fact]
    public void LoadsTomlFiles_FromBothFolders_YoursReplacingBuiltIn()
    {
        var settingsFile = Path.Combine(_temp.Path, "pluginConfigs", "TacticalReadout.json");
        var builtIn = Directory.CreateDirectory(Path.Combine(_temp.Path, "Fights", "BARAS", "operations")).Parent!.Parent!.FullName;
        System.IO.File.WriteAllText(Path.Combine(builtIn, "BARAS", "operations", "test.toml"), FakeBaras.File.Replace("Test Boss\"\n\n", "Old Boss\"\n\n"));
        System.IO.File.WriteAllText(Path.Combine(builtIn, "BARAS", "operations", "broken.toml"), "[area]\nname = \"Broken\"\n[[boss]]\n");
        var voice = new Voice();
        using var service = new ReadoutService(new Config(settingsFile), new TestLogger(), new Toasts(), voice, builtIn);
        var mine = Directory.CreateDirectory(Path.Combine(service.UserFolder!, "BARAS", "operations")).FullName;
        System.IO.File.WriteAllText(Path.Combine(mine, "test.toml"), FakeBaras.File);

        service.Reload();

        var loaded = Assert.Single(service.Baras);
        Assert.Equal(("BARAS/operations/test.toml", false, "Test Boss"), (loaded.Name, loaded.BuiltIn, loaded.Area.Bosses[0].Name));
        Assert.Equal("broken.toml: A [[boss]] has no id.", Assert.Single(service.Problems).ToString());

        service.Settings.Speech = true;
        foreach (var line in new[] { FakeFight.Area(-30), FakeFight.EnterCombat(0), FakeBaras.Hit(1, 5000000), FakeFight.Shock(2) })
            service.Handle(FakeFight.Parse(line));
        Assert.Equal("Test Boss", service.Frame(1).Boss?.Boss);
        Assert.Equal(["Shock on Tester"], voice.Said);

        var alerts = new AlertPanel(service);
        Assert.True(alerts.Appearance.ClickThrough);
        alerts.ShowArea = true;
        Assert.False(alerts.Appearance.ClickThrough);
        Assert.Equal((560f, 160f), (alerts.Appearance.DefaultWidth, alerts.Appearance.DefaultHeight));

        service.SetUseBaras(false);
        Assert.Null(service.Frame(2).Boss);
        Assert.False(service.Settings.UseBaras);
    }
}
