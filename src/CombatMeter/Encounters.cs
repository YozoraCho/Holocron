using System.Globalization;
using Ashla.SDK;

namespace CombatMeter;

internal enum MeterTab
{
    Damage,
    Healing,
    Tank,
    Threat
}

internal sealed record CombatantRow(
    string Key,
    string Name,
    CombatEntityKind Kind,
    double Amount,
    int Hits,
    int Crits,
    double Max,
    double Raw,
    double Absorbed,
    int Avoided,
    int Shielded,
    int Deaths)
{
    public double CritRate => Hits > 0 ? (double)Crits / Hits : 0;
    public double AvoidRate => Hits > 0 ? (double)Avoided / Hits : 0;
    public double ShieldRate => Hits > 0 ? (double)Shielded / Hits : 0;
    public double Overheal => Raw > Amount ? Raw - Amount : 0;
    public double OverhealRate => Raw > 0 ? Overheal / Raw : 0;
}

internal sealed record AbilityRow(
    string CombatantKey,
    string Name,
    string? From,
    double Amount,
    int Hits,
    int Crits,
    double Max,
    string? DamageType)
{
    public double CritRate => Hits > 0 ? (double)Crits / Hits : 0;
    public double Average => Hits > 0 ? Amount / Hits : 0;
}

internal sealed record TabView(double Total, IReadOnlyList<CombatantRow> Rows, IReadOnlyList<AbilityRow> Abilities)
{
    public static TabView Empty { get; } = new(0, [], []);
}

internal sealed record EncounterView(
    int Number,
    string Title,
    string? Area,
    string Owner,
    string? Discipline,
    DateTime Start,
    TimeSpan Duration,
    bool InProgress,
    TabView Damage,
    TabView Healing,
    TabView Tank,
    TabView Threat)
{
    public TabView Tab(MeterTab tab) => tab switch
    {
        MeterTab.Healing => Healing,
        MeterTab.Tank => Tank,
        MeterTab.Threat => Threat,
        _ => Damage
    };

    public double PerSecond(double amount) => Duration.TotalSeconds < 1 ? amount : amount / Duration.TotalSeconds;

    public string Summary() =>
        $"Fight ended after {Duration.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)}s: " +
        $"{Format(Damage.Total)} damage ({Format(PerSecond(Damage.Total))} DPS), {Format(Healing.Total)} effective healing, " +
        $"{Format(Tank.Total)} damage taken.";

    private static string Format(double value) => value.ToString("N0", CultureInfo.CurrentCulture);
}

internal sealed class EncounterTracker
{
    public const int HistoryLimit = 25;

    private readonly object _gate = new();
    private readonly List<EncounterView> _history = [];
    private CombatEntity? _owner;
    private string? _area;
    private string? _discipline;
    private Encounter? _current;
    private int _count;

    public event Action<EncounterView>? EncounterEnded;

    public void Handle(CombatEvent e)
    {
        EncounterView? ended = null;
        lock (_gate)
        {
            if (e.Source is { IsPlayer: true } player &&
                (_owner is null || e.Effect.Type.Id is CombatIds.AreaEntered or CombatIds.DisciplineChanged))
                _owner = player;
            if (_owner is null) return;
            bool fromOurSide = e.Source?.IsOrBelongsTo(_owner.Id) == true;
            bool toOurSide = e.Target?.IsOrBelongsTo(_owner.Id) == true;

            if (e.Effect.Type.Id == CombatIds.AreaEntered && fromOurSide) _area = e.Effect.Effect.Name;
            if (e.Effect.Type.Id == CombatIds.DisciplineChanged && fromOurSide)
                _discipline = e.Effect.Detail?.Name ?? e.Effect.Effect.Name;

            if (e.IsEvent(CombatIds.EnterCombat) && fromOurSide)
            {
                ended = Close();
                _current = new Encounter(++_count, e.Timestamp, _area, _discipline);
            }
            if (_current is null) return;

            _current.Add(e, fromOurSide, toOurSide);
            if (e.IsEvent(CombatIds.ExitCombat) && fromOurSide) ended = Close();
        }
        if (ended is not null) EncounterEnded?.Invoke(ended);
    }

    public bool Split()
    {
        EncounterView? ended;
        lock (_gate)
        {
            if (_current is not { } current) return false;
            ended = Close();
            _current = new Encounter(++_count, current.Last, _area, _discipline);
        }
        if (ended is not null) EncounterEnded?.Invoke(ended);
        return true;
    }

    public void ClearHistory()
    {
        lock (_gate) _history.Clear();
    }

    public EncounterView? Live()
    {
        lock (_gate) return _current is not null ? _current.View(Owner, inProgress: true) : _history.FirstOrDefault();
    }

    public IReadOnlyList<EncounterView> History()
    {
        lock (_gate) return [.. _history];
    }

    public bool InProgress
    {
        get { lock (_gate) return _current is not null; }
    }

    private string Owner => _owner?.Name ?? "You";

    private EncounterView? Close()
    {
        if (_current is not { } current) return null;
        _current = null;
        if (!current.HasNumbers) return null;
        var view = current.View(Owner, inProgress: false);
        _history.Insert(0, view);
        if (_history.Count > HistoryLimit) _history.RemoveAt(_history.Count - 1);
        return view;
    }

    private sealed class Encounter(int number, DateTime start, string? area, string? discipline)
    {
        private readonly Dictionary<string, Combatant> _combatants = [];
        private readonly Dictionary<(MeterTab, string, string, string?), AbilityStat> _abilities = [];
        private readonly Dictionary<ulong, Opponent> _opponents = [];

        private readonly DateTime _start = start;

        public DateTime Last { get; private set; } = start;
        public bool HasNumbers { get; private set; }

        public void Add(CombatEvent e, bool fromOurSide, bool toOurSide)
        {
            Last = e.Timestamp;
            var ability = e.Ability?.Name ?? "(unknown)";

            if (e.IsDamage && e.Value is { } damage)
            {
                double amount = damage.Amount ?? 0;
                if (fromOurSide)
                {
                    var who = Get(e.Source!);
                    who.Damage.Add(amount, amount, Counted(damage, amount), damage.IsCritical);
                    Ability(MeterTab.Damage, who, ability, null, damage.Kind?.Name).Add(amount, amount, Counted(damage, amount), damage.IsCritical);
                    if (e.Target is { Kind: CombatEntityKind.Npc } npc) Note(npc, damageDone: amount);
                }
                if (toOurSide)
                {
                    var who = Get(e.Target!);
                    who.Taken.Add(amount, amount, counted: true, damage.IsCritical);
                    who.Absorbed += damage.Absorbed ?? 0;
                    if (IsAvoided(damage)) who.Avoided++;
                    if (damage.Mitigation?.Id == CombatIds.Shield) who.Shielded++;
                    Ability(MeterTab.Tank, who, ability, e.Source?.Name, damage.Kind?.Name).Add(amount, amount, true, damage.IsCritical);
                    if (e.Source is { Kind: CombatEntityKind.Npc } attacker) Note(attacker, damageDone: 0);
                }
            }
            else if (e.IsHeal && fromOurSide && e.Value is { } heal)
            {
                double effective = heal.EffectiveOrAmount ?? 0, raw = heal.Amount ?? effective;
                var who = Get(e.Source!);
                who.Healing.Add(effective, raw, counted: true, heal.IsCritical);
                Ability(MeterTab.Healing, who, ability, null, null).Add(effective, raw, true, heal.IsCritical);
            }

            if (e.Threat is { } threat && threat != 0 && fromOurSide)
            {
                var who = Get(e.Source!);
                who.Threat.Add(threat, threat, counted: true, crit: false);
                Ability(MeterTab.Threat, who, ability, null, null).Add(threat, threat, true, false);
            }

            if (e.IsEvent(CombatIds.Death) && toOurSide) Get(e.Target!).Deaths++;
        }

        public EncounterView View(string owner, bool inProgress) => new(
            number,
            Title(number),
            area,
            owner,
            discipline,
            _start,
            Last - _start,
            inProgress,
            Tab(MeterTab.Damage, c => c.Damage),
            Tab(MeterTab.Healing, c => c.Healing),
            Tab(MeterTab.Tank, c => c.Taken),
            Tab(MeterTab.Threat, c => c.Threat));

        private string Title(int n)
        {
            var hit = _opponents.Values.Where(o => o.DamageDone > 0).ToList();
            var main = hit.Count > 0
                ? hit.MaxBy(o => (o.MaxHealth, o.DamageDone))
                : _opponents.Values.MaxBy(o => o.MaxHealth);
            return main?.Name ?? $"Encounter {n}";
        }

        private TabView Tab(MeterTab tab, Func<Combatant, Stat> pick)
        {
            var rows = _combatants.Values
                .Select(c => (c, stat: pick(c)))
                .Where(x => x.stat.Hits > 0 || x.stat.Amount != 0 || (tab == MeterTab.Tank && x.c.Deaths > 0))
                .OrderByDescending(x => x.stat.Amount)
                .Select(x => new CombatantRow(x.c.Key, x.c.Name, x.c.Kind, x.stat.Amount, x.stat.Hits, x.stat.Crits,
                    x.stat.Max, x.stat.Raw, tab == MeterTab.Tank ? x.c.Absorbed : 0, tab == MeterTab.Tank ? x.c.Avoided : 0,
                    tab == MeterTab.Tank ? x.c.Shielded : 0, x.c.Deaths))
                .ToList();
            var abilities = _abilities
                .Where(p => p.Key.Item1 == tab)
                .OrderByDescending(p => p.Value.Stat.Amount)
                .Select(p => new AbilityRow(p.Key.Item2, p.Key.Item3, p.Key.Item4, p.Value.Stat.Amount, p.Value.Stat.Hits,
                    p.Value.Stat.Crits, p.Value.Stat.Max, p.Value.DamageType))
                .ToList();
            return new TabView(rows.Sum(r => r.Amount), rows, abilities);
        }

        private Combatant Get(CombatEntity entity)
        {
            var key = $"{entity.Kind}:{entity.Id}";
            if (!_combatants.TryGetValue(key, out var combatant))
                _combatants[key] = combatant = new Combatant(key, entity.Name, entity.Kind);
            return combatant;
        }

        private AbilityStat Ability(MeterTab tab, Combatant who, string ability, string? from, string? damageType)
        {
            var key = (tab, who.Key, ability, from);
            if (!_abilities.TryGetValue(key, out var stat)) _abilities[key] = stat = new AbilityStat(damageType);
            HasNumbers = true;
            return stat;
        }

        private void Note(CombatEntity npc, double damageDone)
        {
            var key = npc.InstanceId ?? npc.Id;
            if (!_opponents.TryGetValue(key, out var opponent)) _opponents[key] = opponent = new Opponent(npc.Name);
            opponent.DamageDone += damageDone;
            if (npc.Health is { } health && health.Max > opponent.MaxHealth) opponent.MaxHealth = health.Max;
        }

        private static bool Counted(CombatValue value, double amount) => amount > 0 || value.Mitigation is not null;

        private static bool IsAvoided(CombatValue value)
        {
            if (value.Mitigation is not { } mitigation) return false;
            if (mitigation.Id is CombatIds.Miss or CombatIds.Parry or CombatIds.Deflect) return true;
            return mitigation.Name.Equals("dodge", StringComparison.OrdinalIgnoreCase) ||
                   mitigation.Name.Equals("resist", StringComparison.OrdinalIgnoreCase) ||
                   mitigation.Name.Equals("immune", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class Combatant(string key, string name, CombatEntityKind kind)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public CombatEntityKind Kind { get; } = kind;
        public Stat Damage { get; } = new();
        public Stat Healing { get; } = new();
        public Stat Taken { get; } = new();
        public Stat Threat { get; } = new();
        public double Absorbed { get; set; }
        public int Avoided { get; set; }
        public int Shielded { get; set; }
        public int Deaths { get; set; }
    }

    private sealed class AbilityStat(string? damageType)
    {
        public Stat Stat { get; } = new();
        public string? DamageType { get; } = damageType;

        public void Add(double amount, double raw, bool counted, bool crit) => Stat.Add(amount, raw, counted, crit);
    }

    private sealed class Opponent(string name)
    {
        public string Name { get; } = name;
        public long MaxHealth { get; set; }
        public double DamageDone { get; set; }
    }

    private sealed class Stat
    {
        public double Amount { get; private set; }
        public double Raw { get; private set; }
        public int Hits { get; private set; }
        public int Crits { get; private set; }
        public double Max { get; private set; }

        public void Add(double amount, double raw, bool counted, bool crit)
        {
            Amount += amount;
            Raw += raw;
            if (counted) Hits++;
            if (crit) Crits++;
            if (amount > Max) Max = amount;
        }
    }
}
