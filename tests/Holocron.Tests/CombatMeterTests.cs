using Ashla.SDK;
using CombatMeter;

namespace Holocron.Tests;

public class EncounterTrackerTests
{
    private const ulong PlayerId = 690000000000001;
    private static readonly List<CombatEvent> Events = Fixture.Events();

    private static EncounterTracker Replay(IEnumerable<CombatEvent>? events = null)
    {
        var tracker = new EncounterTracker();
        foreach (var e in events ?? Events) tracker.Handle(e);
        return tracker;
    }

    private static List<CombatEvent> Window(int n)
    {
        var starts = Events.Where(e => e.IsEvent(CombatIds.EnterCombat)).ToList();
        var start = starts[n - 1].Timestamp;
        var end = Events.First(e => e.IsEvent(CombatIds.ExitCombat) && e.Timestamp > start).Timestamp;
        return Events.Where(e => e.Timestamp >= start && e.Timestamp <= end).ToList();
    }

    private static bool Ours(CombatEntity? entity) => entity?.IsOrBelongsTo(PlayerId) == true;

    [Fact]
    public void FiveEncounters_NewestFirst_NamedAfterTheToughestEnemy()
    {
        var tracker = Replay();
        var history = tracker.History();

        Assert.Equal(5, history.Count);
        Assert.Equal([5, 4, 3, 2, 1], history.Select(e => e.Number));
        Assert.Equal("Combat Training Target", history[0].Title);
        Assert.Equal("Separatist Siege Gunner", history[4].Title);
        Assert.False(tracker.InProgress);
        Assert.Same(history[0], tracker.Live());
        Assert.Equal("Concentration", history[0].Discipline);
        Assert.Equal(new TimeSpan(0, 0, 0, 44, 873), history[0].Duration);
    }

    [Fact]
    public void Damage_PerCombatant_MatchesIndependentSums()
    {
        var dummy = Replay().History()[0];
        var hits = Window(5).Where(e => e.IsDamage && Ours(e.Source)).ToList();
        var player = hits.Where(e => e.Source!.IsPlayer).ToList();
        var companion = hits.Where(e => e.Source!.Kind == CombatEntityKind.Companion).ToList();

        Assert.Equal(844208, dummy.Damage.Total);
        var you = dummy.Damage.Rows.Single(r => r.Kind == CombatEntityKind.Player);
        var kira = dummy.Damage.Rows.Single(r => r.Kind == CombatEntityKind.Companion);
        Assert.Equal(player.Sum(e => e.Value!.Amount!.Value), you.Amount);
        Assert.Equal(companion.Sum(e => e.Value!.Amount!.Value), kira.Amount);
        Assert.Equal(player.Count(e => e.Value!.IsCritical), you.Crits);
        Assert.Equal(player.Max(e => e.Value!.Amount!.Value), you.Max);
        Assert.Equal(you, dummy.Damage.Rows[0]);

        Assert.Equal(you.Amount, dummy.Damage.Abilities.Where(a => a.CombatantKey == you.Key).Sum(a => a.Amount));
        var strike = dummy.Damage.Abilities.First(a => a.CombatantKey == you.Key);
        Assert.Equal("Strike", strike.Name);
        Assert.Equal(player.Where(e => e.Ability?.Name == "Strike").Sum(e => e.Value!.Amount!.Value), strike.Amount);
        Assert.NotNull(strike.DamageType);
    }

    [Fact]
    public void Healing_IsEffective_WithOverhealFromTheRawAmount()
    {
        var encounter = Replay().History().Single(e => e.Number == 4);
        var heals = Window(4).Where(e => e.IsHeal && Ours(e.Source)).ToList();

        Assert.Equal(heals.Sum(e => e.Value!.EffectiveOrAmount!.Value), encounter.Healing.Total);
        Assert.Equal(40643, encounter.Healing.Total);
        var raw = encounter.Healing.Rows.Sum(r => r.Raw);
        Assert.Equal(heals.Sum(e => e.Value!.Amount!.Value), raw);
        Assert.All(encounter.Healing.Rows, r => Assert.InRange(r.OverhealRate, 0, 1));
    }

    [Fact]
    public void Tank_TakenAbsorbedAvoided_MatchIndependentCounts()
    {
        var encounter = Replay().History().Single(e => e.Number == 4);
        var incoming = Window(4).Where(e => e.IsDamage && Ours(e.Target)).ToList();
        var player = incoming.Where(e => e.Target!.IsPlayer).ToList();
        var row = encounter.Tank.Rows.Single(r => r.Kind == CombatEntityKind.Player);

        Assert.Equal(34238, encounter.Tank.Total);
        Assert.Equal(player.Sum(e => e.Value!.Amount!.Value), row.Amount);
        Assert.Equal(player.Count, row.Hits);
        Assert.Equal(player.Sum(e => e.Value!.Absorbed ?? 0), row.Absorbed);
        Assert.Equal(player.Count(e => e.Value!.Mitigation?.Id is CombatIds.Miss or CombatIds.Parry or CombatIds.Deflect), row.Avoided);
        Assert.All(encounter.Tank.Abilities, a => Assert.False(string.IsNullOrEmpty(a.From)));
    }

    [Fact]
    public void Threat_IsTheLoggedThreatOfYourSide()
    {
        var dummy = Replay().History()[0];
        var threat = Window(5).Where(e => e.Threat is { } t && t != 0 && Ours(e.Source)).Sum(e => e.Threat!.Value);

        Assert.Equal(threat, dummy.Threat.Total, 3);
        Assert.True(dummy.Threat.Total > 0);
    }

    [Fact]
    public void Summary_KeepsTheFormatTheLogLineUses()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("Fight ended after 44.9s: 844,208 damage (18,813 DPS), 0 effective healing, 0 damage taken.",
                Replay().History()[0].Summary());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Split_EndsTheEncounterHere_AndKeepsCounting()
    {
        var events = Window(5);
        var before = events.Where(e => e.Timestamp < events[0].Timestamp.AddSeconds(20)).ToList();
        var tracker = Replay(Events.TakeWhile(e => e != events[0]).Concat(before));
        Assert.True(tracker.InProgress);
        int ended = 0;
        tracker.EncounterEnded += _ => ended++;

        Assert.True(tracker.Split());
        foreach (var e in events.Skip(before.Count)) tracker.Handle(e);

        Assert.Equal(2, ended);
        var history = tracker.History();
        Assert.Equal(844208, history[0].Damage.Total + history[1].Damage.Total);
        Assert.Equal(history[1].Title, history[0].Title);
        Assert.False(new EncounterTracker().Split());
    }

    [Fact]
    public void LiveEncounter_IsShownWhileInProgress()
    {
        var events = Window(5);
        var tracker = Replay(Events.TakeWhile(e => e != events[0]).Concat(events.Take(100)));

        var live = tracker.Live()!;
        Assert.True(live.InProgress);
        Assert.Equal(5, live.Number);
        Assert.Equal(4, tracker.History().Count);
        Assert.True(live.Damage.Total > 0);
    }

    [Fact]
    public void ClearHistory_And_HistoryLimit()
    {
        var tracker = Replay();
        tracker.ClearHistory();
        Assert.Empty(tracker.History());
        Assert.Null(tracker.Live());

        var fight = Window(2);
        for (int i = 0; i < EncounterTracker.HistoryLimit + 5; i++)
            foreach (var e in fight) tracker.Handle(e);
        Assert.Equal(EncounterTracker.HistoryLimit, tracker.History().Count);
    }

    [Fact]
    public void EncounterWithoutNumbers_IsNotKept()
    {
        var enter = Events.First(e => e.IsEvent(CombatIds.EnterCombat));
        var exit = Events.First(e => e.IsEvent(CombatIds.ExitCombat));
        var owner = Events.First(e => e.Effect.Type.Id == CombatIds.AreaEntered);
        var tracker = Replay([owner, enter, exit]);
        Assert.Empty(tracker.History());
    }
}

public class CombatMeterReplayTests
{
    [Fact]
    public void Replay_ReportsEachFight_FromTheRealLog()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var tracker = new EncounterTracker();
            var fights = new List<string>();
            tracker.EncounterEnded += encounter => fights.Add(encounter.Summary());
            foreach (var e in Fixture.Events()) tracker.Handle(e);

            Assert.Equal(5, fights.Count);
            Assert.Contains("309 damage", fights[0]);
            Assert.Contains("47,189 damage", fights[1]);
            Assert.Contains("45,586 damage", fights[2]);
            Assert.Contains("57,377 damage", fights[3]);
            Assert.Contains("after 44.9s: 844,208 damage (18,813 DPS)", fights[4]);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }
}
