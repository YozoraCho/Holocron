using System.Numerics;

namespace TacticalReadout;

internal readonly record struct BarasSelector(ulong? Id, string? Name)
{
    public bool MatchesGame(ulong id, string? name) =>
        Id is { } expected ? expected == id : name is not null && string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Id?.ToString() ?? $"\"{Name}\"";
}

internal enum BarasFilterKind
{
    Any,
    LocalPlayer,
    OtherPlayers,
    AnyPlayer,
    AnyFriendlyPlayer,
    AnyEnemyPlayer,
    AnyCompanion,
    AnyPlayerOrCompanion,
    AnyExceptLocal,
    CurrentTarget,
    Boss,
    NpcExceptBoss,
    AnyNpc,
    Selector
}

internal sealed record BarasFilter(BarasFilterKind Kind, IReadOnlyList<BarasSelector> Selectors)
{
    public static readonly BarasFilter Any = new(BarasFilterKind.Any, []);
}

internal enum BarasAxis
{
    X,
    Y,
    Z,
    Facing,
    XMinusY,
    XPlusY
}

internal sealed record BarasPosition(bool OnSource, BarasAxis Axis, string Op, double Value, double Min, double Max);

internal enum BarasTriggerKind
{
    CombatStart,
    CombatEnd,
    AbilityCast,
    EffectApplied,
    EffectRemoved,
    DamageTaken,
    HealingTaken,
    ThreatModified,
    BossHpBelow,
    BossHpAbove,
    NpcAppears,
    EntityDeath,
    TargetSet,
    PhaseEntered,
    PhaseEnded,
    AnyPhaseChange,
    CounterReaches,
    CounterChanges,
    TimerExpires,
    TimerStarted,
    TimerCanceled,
    TimeElapsed,
    Never,
    AnyOf
}

internal sealed record BarasTrigger(BarasTriggerKind Kind)
{
    public IReadOnlyList<BarasSelector> Abilities { get; init; } = [];
    public BarasFilter Source { get; init; } = BarasFilter.Any;
    public BarasFilter Target { get; init; } = BarasFilter.Any;
    public IReadOnlyList<BarasSelector> Selector { get; init; } = [];
    public IReadOnlyList<ulong> Mitigation { get; init; } = [];
    public IReadOnlyList<BarasPosition> Position { get; init; } = [];
    public double? Number { get; init; }
    public string? Ref { get; init; }
    public IReadOnlyList<BarasTrigger> Children { get; init; } = [];

    public static readonly BarasTrigger Never = new(BarasTriggerKind.Never);
    public static readonly BarasTrigger CombatEnd = new(BarasTriggerKind.CombatEnd);

    public bool ContainsCombatStart => Kind == BarasTriggerKind.CombatStart || Children.Any(c => c.ContainsCombatStart);

    public double? CombatTimeThreshold => Kind switch
    {
        BarasTriggerKind.CombatStart => 0,
        BarasTriggerKind.TimeElapsed => Number ?? 0,
        _ => null
    };

    public bool IsCombatTime => Kind is BarasTriggerKind.CombatStart or BarasTriggerKind.TimeElapsed;

    public bool CombatTimeMet(double seconds) => Kind switch
    {
        BarasTriggerKind.CombatStart => seconds >= 0,
        BarasTriggerKind.TimeElapsed => seconds >= (Number ?? 0),
        BarasTriggerKind.AnyOf => Children.Any(c => c.CombatTimeMet(seconds)),
        _ => false
    };
}

internal enum BarasConditionKind
{
    PhaseActive,
    CounterCompare,
    CounterCompareCounter,
    TimerTimeRemaining,
    AllOf,
    AnyOf,
    Not
}

internal sealed record BarasCondition(BarasConditionKind Kind)
{
    public IReadOnlyList<string> Phases { get; init; } = [];
    public string? Ref { get; init; }
    public string? Other { get; init; }
    public string Op { get; init; } = "eq";
    public double Value { get; init; }
    public IReadOnlyList<BarasCondition> Children { get; init; } = [];

    public static bool Compare(string op, double value, double against) => op switch
    {
        "ne" => value != against,
        "lt" => value < against,
        "lte" => value <= against,
        "gt" => value > against,
        "gte" => value >= against,
        _ => value == against
    };
}

internal sealed record BarasEntity(string Name, IReadOnlyList<ulong> Ids, bool IsBoss, bool TriggersEncounter, bool IsKillTarget);

internal sealed record BarasPhase(
    string Id,
    string Name,
    string? DisplayText,
    bool Enabled,
    BarasTrigger Start,
    BarasTrigger? End,
    string? PrecededBy,
    IReadOnlyList<BarasCondition> Conditions,
    IReadOnlyList<string> ResetsCounters,
    IReadOnlyList<string> Difficulties);

internal enum BarasAggregation
{
    Max,
    Sum,
    Min
}

internal sealed record BarasStacks(IReadOnlyList<BarasSelector> Effects, BarasFilter Target, BarasAggregation Aggregation);

internal sealed record BarasCounter(
    string Id,
    string Name,
    bool Enabled,
    BarasTrigger IncrementOn,
    BarasTrigger? DecrementOn,
    BarasTrigger ResetOn,
    uint InitialValue,
    bool Decrement,
    uint? SetValue,
    BarasStacks? Stacks);

internal enum BarasAlertOn
{
    None,
    OnApply,
    OnExpire,
    Countdown
}

internal sealed record BarasAudio(bool Enabled, int Offset, int CountdownStart, string? Text)
{
    public static readonly BarasAudio Off = new(false, 0, 0, null);
}

internal sealed record BarasTimer(
    string Id,
    string Name,
    string? DisplayText,
    BarasTrigger Trigger,
    double Duration,
    bool IsAlert,
    BarasAlertOn AlertOn,
    string? AlertText,
    double? AlertCountdown,
    Vector4 Color,
    IReadOnlyList<BarasCondition> Conditions,
    IReadOnlyList<string> Difficulties,
    int? GroupSize,
    bool Enabled,
    bool CanBeRefreshed,
    int Repeats,
    string? ChainsTo,
    BarasTrigger? Cancel,
    double ShowAt,
    bool Shown,
    BarasAudio Audio)
{
    public string Label => DisplayText ?? Name;
}

internal sealed record BarasBoss(
    string Id,
    string Name,
    bool Enabled,
    IReadOnlyList<BarasEntity> Entities,
    IReadOnlyList<BarasPhase> Phases,
    IReadOnlyList<BarasCounter> Counters,
    IReadOnlyList<BarasTimer> Timers,
    BarasTrigger? EncounterTrigger,
    double? EncounterTriggerFallback,
    bool HasVictoryTrigger,
    BarasTrigger? VictoryTrigger,
    IReadOnlyList<string> VictoryDifficulties)
{
    public IEnumerable<ulong> EncounterIds => Entities.Where(e => e.TriggersEncounter).SelectMany(e => e.Ids);

    public bool IsBossNpc(ulong id) => Entities.Any(e => e.IsBoss && e.Ids.Contains(id));

    public bool IsKillTarget(ulong id) => Entities.Any(e => e.IsKillTarget && e.Ids.Contains(id));

    public bool Knows(ulong id) => Entities.Any(e => e.Ids.Contains(id));

    public BarasPhase? InitialPhase(BarasDifficulty? difficulty) =>
        Phases.FirstOrDefault(p => p.Enabled && BarasDifficulty.Allows(p.Difficulties, difficulty) && p.Start.ContainsCombatStart);
}

internal sealed record BarasArea(string Path, string Name, ulong AreaId, IReadOnlyList<BarasBoss> Bosses, string Type = "")
{
    public int TimerCount => Bosses.Sum(b => b.Timers.Count);

    public static string Describe(IEnumerable<BarasArea> areas)
    {
        var list = areas.ToList();
        int operations = list.Count(a => a.Type == "operation");
        int flashpoints = list.Count(a => a.Type == "flashpoint");
        int other = list.Count - operations - flashpoints;
        var parts = new List<string>();
        if (operations > 0) parts.Add(Count(operations, "operation", "operations"));
        if (flashpoints > 0) parts.Add(Count(flashpoints, "flashpoint", "flashpoints"));
        if (other > 0) parts.Add(Count(other, "other area", "other areas"));
        return parts.Count switch
        {
            0 => "no areas",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1]
        };
    }

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}

internal sealed record BarasDifficulty(string Tier, int GroupSize)
{
    private static readonly Dictionary<ulong, BarasDifficulty> ById = new()
    {
        [836045448953651] = new("story", 8),
        [836045448953652] = new("veteran", 8),
        [836045448953653] = new("story", 16),
        [836045448953654] = new("veteran", 16),
        [836045448953655] = new("master", 8),
        [836045448953656] = new("master", 16),
        [836045448953657] = new("veteran", 4),
        [836045448953659] = new("master", 4)
    };

    public static BarasDifficulty? From(ulong id) => ById.GetValueOrDefault(id);

    public bool Matches(string key)
    {
        key = key.Trim().ToLowerInvariant();
        int split = key.LastIndexOf('_');
        if (split > 0 && int.TryParse(key.AsSpan(split + 1), out int size) && size is 4 or 8 or 16)
            return key[..split] == Tier && size == GroupSize;
        return key == Tier;
    }

    public static bool Allows(IReadOnlyList<string> keys, BarasDifficulty? difficulty) =>
        keys.Count == 0 || (difficulty is not null && keys.Any(difficulty.Matches));
}

internal sealed class BarasFormatException(string message) : Exception(message);
