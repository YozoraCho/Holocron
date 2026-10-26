using System.Globalization;
using Ashla.SDK;

namespace TacticalReadout;

internal sealed record BarasStatus(string Boss, string? Phase, DateTime Start);

internal sealed record BarasView(BarasStatus? Status, IReadOnlyList<TimelineBar> Bars, IReadOnlyList<FiredAlert> Countdowns);

internal sealed class BarasEngine(Func<long> nextAlertId)
{
    public const ulong TauntId = 836045448945488;
    private const ulong StoryFourId = 836045448953658;
    private const int MaxPasses = 10;
    private const double CueGrace = 2;

    private IReadOnlyList<BarasArea> _areas = [];
    private List<BarasBoss> _bosses = [];
    private GameRef? _area;
    private BarasDifficulty? _difficulty;
    private ulong? _ownerId;
    private bool _inCombat;
    private bool _ownerDead;
    private DateTime _combatStart;
    private readonly HashSet<ulong> _seenNpcs = [];
    private readonly HashSet<ulong> _dead = [];
    private readonly Dictionary<ulong, ulong> _combatNpcs = [];
    private readonly Dictionary<ulong, double> _hp = [];
    private readonly Dictionary<ulong, CombatEntity> _targets = [];
    private readonly HashSet<BarasBoss> _pending = [];
    private Fight? _fight;

    public string? LastResult { get; private set; }

    public DateTime LastResultAt { get; private set; }

    public IReadOnlyList<BarasBoss> Ready => _bosses;

    public string? AreaName => _bosses.Count > 0 ? _areas.FirstOrDefault(a => Covers(a))?.Name : null;

    public bool Active => _fight is not null;

    public BarasDifficulty? Difficulty => _difficulty;

    public void SetAreas(IReadOnlyList<BarasArea> areas)
    {
        _areas = areas;
        _bosses = [.. areas.Where(Covers).SelectMany(a => a.Bosses)];
    }

    public void EnterArea(GameRef area, GameRef? difficulty, DateTime at)
    {
        Finish(at, null);
        _area = area;
        _difficulty = difficulty is null || difficulty.Id == StoryFourId ? null : BarasDifficulty.From(difficulty.Id) ?? ParseDifficulty(difficulty.Name);
        _bosses = [.. _areas.Where(Covers).SelectMany(a => a.Bosses)];
        _inCombat = false;
        _seenNpcs.Clear();
        _combatNpcs.Clear();
        _targets.Clear();
        _hp.Clear();
        _dead.Clear();
    }

    private bool Covers(BarasArea area) =>
        _area is { } current && (area.AreaId != 0 ? area.AreaId == current.Id : string.Equals(area.Name, current.Name, StringComparison.OrdinalIgnoreCase));

    internal static BarasDifficulty? ParseDifficulty(string name)
    {
        string lower = name.ToLowerInvariant();
        string? tier = lower.Contains("master") || lower.Contains("nightmare") ? "master" : lower.Contains("veteran") || lower.Contains("hard") ? "veteran" : lower.Contains("story") ? "story" : null;
        int size = lower.Contains("16") ? 16 : lower.Contains('8') ? 8 : lower.Contains('4') ? 4 : 0;
        if (tier is null || size == 0 || (tier == "story" && size == 4)) return null;
        return new BarasDifficulty(tier, size);
    }

    public void Handle(CombatEvent e, CombatEntity owner, List<FiredAlert> fired)
    {
        _ownerId = owner.Id;
        Advance(e.Timestamp, fired);
        bool fromMe = IsMe(e.Source);

        if (e.IsEvent(CombatIds.EnterCombat) && fromMe) StartCombat(e.Timestamp);

        if (e.IsEvent(CombatIds.TargetSet) && e.Source is { } setter)
        {
            if (e.Target is { } target) _targets[Uid(setter)] = target;
            else _targets.Remove(Uid(setter));
        }
        else if (e.IsEvent(CombatIds.TargetCleared) && e.Source is { } clearer)
        {
            _targets.Remove(Uid(clearer));
        }

        var signals = new List<Signal>();
        foreach (var entity in new[] { e.Source, e.Target })
            if (entity is { Health.Current: > 0 }) _dead.Remove(Uid(entity));
        foreach (var entity in Deaths(e))
        {
            if (!_dead.Add(Uid(entity))) continue;
            if (IsMe(entity)) _ownerDead = true;
            signals.Add(new Signal(SignalKind.Death, e.Timestamp) { Entity = entity, Event = e });
        }
        foreach (var entity in new[] { e.Source, e.Target })
        {
            if (entity is not { Kind: CombatEntityKind.Npc }) continue;
            ulong uid = Uid(entity);
            if (_inCombat) _combatNpcs[uid] = entity.Id;
            if (_seenNpcs.Add(uid)) signals.Add(new Signal(SignalKind.NpcSeen, e.Timestamp) { Entity = entity, Event = e });
        }

        if (_inCombat)
        {
            var eventSignals = EventSignals(e);
            if (_fight is null) Detect(e, eventSignals, signals);
            if (_fight is { } fight)
            {
                foreach (var entity in new[] { e.Target, e.Source })
                {
                    if (entity is not { Kind: CombatEntityKind.Npc } || !fight.Boss.Knows(entity.Id)) continue;
                    ulong uid = Uid(entity);
                    if (fight.Boss.IsBossNpc(entity.Id)) fight.BossInstances.Add(uid);
                    if (fight.Boss.IsKillTarget(entity.Id)) fight.KillSeen.Add(uid);
                    fight.LastBossEvent = e.Timestamp;
                    if (entity.Health is not { Max: > 0 } health) continue;
                    double percent = health.Current * 100.0 / health.Max;
                    if (_hp.TryGetValue(uid, out double old) && Math.Abs(old - percent) > 1e-9)
                        signals.Add(new Signal(SignalKind.BossHp, e.Timestamp) { Entity = entity, Old = old, New = percent });
                    _hp[uid] = percent;
                }
                foreach (var death in signals.Where(s => s.Kind == SignalKind.Death))
                    if (death.Entity is { Kind: CombatEntityKind.Npc } killed && fight.Boss.IsKillTarget(killed.Id)) fight.KillDead.Add(Uid(killed));
                signals.AddRange(eventSignals);
                Process(signals, e.Timestamp, fired);
                CheckVictory(signals, e.Timestamp);
            }
        }

        if (e.IsEvent(CombatIds.ExitCombat) && fromMe)
        {
            Finish(e.Timestamp, _fight is null ? null : _ownerDead ? "Wiped" : "Ended");
            _inCombat = false;
        }
    }

    private static IEnumerable<CombatEntity> Deaths(CombatEvent e)
    {
        if (e.IsEvent(CombatIds.Death) && e.Target is { } target) yield return target;
        if (e.Source is { Health: { Current: 0, Max: > 0 } } source && source.Kind != CombatEntityKind.Unknown) yield return source;
        if (e.Target is { Health: { Current: 0, Max: > 0 } } zero && zero.Kind != CombatEntityKind.Unknown && !ReferenceEquals(zero, e.Source)) yield return zero;
    }

    public void Tick(DateTime now, List<FiredAlert> fired) => Advance(now, fired);

    public BarasView View(DateTime now)
    {
        if (_fight is not { } fight) return new BarasView(null, [], []);
        var bars = new List<TimelineBar>();
        var countdowns = new List<FiredAlert>();
        foreach (var timer in fight.Timers.Values.OrderBy(t => t.Expires))
        {
            double remaining = (timer.Expires - now).TotalSeconds;
            if (remaining < 0) continue;
            if (timer.Def.Shown && timer.Def.Duration > 0 && (timer.Def.ShowAt <= 0 || remaining <= timer.Def.ShowAt))
                bars.Add(new TimelineBar(timer.Def.Label, remaining, timer.Def.Duration, timer.Def.Color, Depletes: true));
            if (timer.Def.AlertOn == BarasAlertOn.Countdown && timer.Def.AlertCountdown is > 0 and var window && remaining <= window)
            {
                string text = $"{Format(timer.Def.AlertText ?? timer.Def.Name, timer.Source, timer.Target)} ({remaining.ToString("0.0", CultureInfo.InvariantCulture)})";
                countdowns.Add(new FiredAlert(0, "baras:" + timer.Def.Id, AlertLevel.Alert, text, null, true, now, 1, timer.Def.Color));
            }
        }
        var phase = fight.Phase is { } id ? fight.Boss.Phases.FirstOrDefault(p => p.Id == id) : null;
        return new BarasView(new BarasStatus(fight.Boss.Name, phase?.DisplayText ?? phase?.Name, fight.Start), bars, countdowns);
    }

    internal uint Counter(string id) => _fight?.Counters.GetValueOrDefault(id) ?? 0;

    internal string? Phase => _fight?.Phase;

    internal IReadOnlyList<string> RunningTimers => _fight is null ? [] : [.. _fight.Timers.Keys];

    private void StartCombat(DateTime at)
    {
        Finish(at, null);
        _inCombat = true;
        _ownerDead = false;
        _combatStart = at;
        _combatNpcs.Clear();
        _hp.Clear();
        _dead.Clear();
        _pending.Clear();
    }

    private void Finish(DateTime at, string? result)
    {
        if (_fight is not { } fight) return;
        if (fight.Victory) result = "Victory";
        if (result is not null)
        {
            var length = at - fight.Start;
            if (length < TimeSpan.Zero) length = TimeSpan.Zero;
            LastResult = $"{fight.Boss.Name}: {result} after {(int)length.TotalMinutes}:{length.Seconds:00}";
            LastResultAt = at;
        }
        _fight = null;
        _pending.Clear();
    }

    private void CheckVictory(List<Signal> signals, DateTime at)
    {
        if (_fight is not { } fight || fight.Victory) return;
        var boss = fight.Boss;
        if (boss.HasVictoryTrigger && boss.VictoryTrigger is { } trigger)
        {
            if (BarasDifficulty.Allows(boss.VictoryDifficulties, _difficulty) && signals.Any(s => Matches(trigger, s))) fight.Victory = true;
        }
        else if (fight.KillSeen.Count > 0 && fight.KillSeen.IsSubsetOf(fight.KillDead))
        {
            fight.Victory = true;
        }
        if (!fight.Victory) return;
        Finish(at, "Victory");
        _inCombat = false;
    }

    private void Detect(CombatEvent e, List<Signal> eventSignals, List<Signal> signals)
    {
        foreach (var entity in new[] { e.Source, e.Target })
        {
            if (entity is not { Kind: CombatEntityKind.Npc } || !_combatNpcs.ContainsKey(Uid(entity))) continue;
            var boss = _bosses.FirstOrDefault(b => b.Enabled && b.EncounterIds.Contains(entity.Id));
            if (boss is null) continue;
            Activate(boss, e.Timestamp, signals);
            return;
        }
        foreach (var boss in _bosses)
        {
            if (boss.EncounterTrigger is not { } trigger || !boss.Enabled) continue;
            if (!eventSignals.Any(s => Matches(trigger, s))) continue;
            if (boss.EncounterTriggerFallback is null)
            {
                Activate(boss, e.Timestamp, signals);
                return;
            }
            _pending.Add(boss);
        }
        double seconds = (e.Timestamp - _combatStart).TotalSeconds;
        var late = _bosses.FirstOrDefault(b => b.Enabled && _pending.Contains(b) && seconds >= b.EncounterTriggerFallback);
        if (late is not null) Activate(late, e.Timestamp, signals);
    }

    private void Activate(BarasBoss boss, DateTime at, List<Signal> signals)
    {
        var fight = new Fight(boss, _combatStart) { LastBossEvent = at };
        foreach (var counter in boss.Counters) fight.Counters[counter.Id] = counter.InitialValue;
        foreach (var (uid, classId) in _combatNpcs)
        {
            if (boss.IsBossNpc(classId)) fight.BossInstances.Add(uid);
            if (boss.IsKillTarget(classId)) fight.KillSeen.Add(uid);
        }
        _fight = fight;
        _pending.Clear();
        if (boss.InitialPhase(_difficulty) is { } initial)
        {
            EnterPhase(fight, initial);
            signals.Add(new Signal(SignalKind.PhaseChanged, at) { Ref = initial.Id });
        }
    }

    private List<Signal> EventSignals(CombatEvent e)
    {
        var list = new List<Signal>();
        ulong type = e.Effect.Type.Id;
        if (type == CombatIds.ApplyEffect && e.Target is not null)
        {
            list.Add(new Signal(SignalKind.EffectApplied, e.Timestamp) { Event = e, Entity = e.Target });
            if (e.IsDamage) list.Add(new Signal(SignalKind.Damage, e.Timestamp) { Event = e, Entity = e.Target });
            if (e.IsHeal) list.Add(new Signal(SignalKind.Heal, e.Timestamp) { Event = e, Entity = e.Target });
        }
        else if (type == CombatIds.RemoveEffect && e.Target is not null)
        {
            list.Add(new Signal(SignalKind.EffectRemoved, e.Timestamp) { Event = e, Entity = e.Target });
        }
        else if (type == CombatIds.ModifyCharges && e.Target is not null)
        {
            list.Add(new Signal(SignalKind.Charges, e.Timestamp) { Event = e, Entity = e.Target });
        }
        else if (e.IsEvent(CombatIds.AbilityActivate) && e.Source is { } caster)
        {
            var target = e.Target is null || Uid(e.Target) == Uid(caster) ? _targets.GetValueOrDefault(Uid(caster)) ?? caster : e.Target;
            list.Add(new Signal(SignalKind.Ability, e.Timestamp) { Event = e, Entity = target });
        }
        else if (type == CombatIds.Event && (e.Effect.Effect.Id == CombatIds.ModifyThreat || e.Effect.Effect.Id == TauntId) && e.Source is not null && e.Target is not null)
        {
            list.Add(new Signal(SignalKind.Threat, e.Timestamp) { Event = e, Entity = e.Target });
        }
        else if (e.IsEvent(CombatIds.TargetSet) && e.Source is not null)
        {
            list.Add(new Signal(SignalKind.TargetSet, e.Timestamp) { Event = e, Entity = e.Target });
        }
        return list;
    }

    private void Process(List<Signal> batch, DateTime at, List<FiredAlert> fired)
    {
        for (int pass = 0; pass < MaxPasses && batch.Count > 0 && _fight is not null; pass++)
        {
            var all = new List<Signal>(batch);
            UpdateState(all);
            batch = RunTimers(all, at, fired);
        }
    }

    private void UpdateState(List<Signal> all)
    {
        if (_fight is not { } fight) return;
        StackCounters(fight, all);
        var entered = new HashSet<string>(StringComparer.Ordinal);
        int counted = 0;
        for (int round = 0; round < 20; round++)
        {
            int before = all.Count;
            if (counted < all.Count)
            {
                int end = all.Count;
                Counters(fight, all, counted, end);
                counted = end;
            }
            Phases(fight, all, entered);
            PhaseEnd(fight, all);
            if (all.Count == before) break;
        }
    }

    private void Counters(Fight fight, List<Signal> all, int from, int to)
    {
        var slice = all.GetRange(from, to - from);
        DateTime at = slice.Count > 0 ? slice[^1].At : fight.Start;
        foreach (var counter in fight.Boss.Counters)
        {
            if (!counter.Enabled || counter.Stacks is not null) continue;
            if (slice.Any(s => Matches(counter.IncrementOn, s)))
                Change(fight, all, counter.Id, Modify(fight.Counters.GetValueOrDefault(counter.Id), counter.Decrement, counter.SetValue), at, always: true);
            if (counter.DecrementOn is { } decrement && slice.Any(s => Matches(decrement, s)))
                Change(fight, all, counter.Id, Modify(fight.Counters.GetValueOrDefault(counter.Id), true, null), at, always: true);
            if (slice.Any(s => Matches(counter.ResetOn, s)))
                Change(fight, all, counter.Id, counter.InitialValue, at, always: false);
        }
    }

    private static uint Modify(uint value, bool decrement, uint? set) => set ?? (decrement ? (value == 0 ? 0 : value - 1) : value + 1);

    private static void Change(Fight fight, List<Signal> all, string id, uint value, DateTime at, bool always)
    {
        uint old = fight.Counters.GetValueOrDefault(id);
        if (!always && old == value) return;
        fight.Counters[id] = value;
        all.Add(new Signal(SignalKind.CounterChanged, at) { Ref = id, Old = old, New = value });
    }

    private void StackCounters(Fight fight, List<Signal> all)
    {
        var stackCounters = fight.Boss.Counters.Where(c => c.Enabled && c.Stacks is not null).ToList();
        if (stackCounters.Count == 0) return;
        bool changed = false;
        int count = all.Count;
        for (int i = 0; i < count; i++)
        {
            var s = all[i];
            if (s.Event is not { } e || s.Kind is not (SignalKind.EffectApplied or SignalKind.EffectRemoved or SignalKind.Charges) || e.Target is not { } target) continue;
            var effect = e.Effect.Effect;
            if (!stackCounters.Any(c => c.Stacks!.Effects.Any(sel => sel.MatchesGame(effect.Id, effect.Name)))) continue;
            var key = (effect.Id, Uid(target));
            if (s.Kind == SignalKind.EffectRemoved) fight.Stacks.Remove(key);
            else
            {
                int stacks = s.Kind == SignalKind.Charges || IsCharges(e.Value) ? (int)(e.Value?.Amount ?? 1) : 1;
                fight.Stacks[key] = new Stack(Math.Max(0, stacks), target, effect);
            }
            changed = true;
        }
        if (!changed) return;
        DateTime at = all[^1].At;
        foreach (var counter in stackCounters)
        {
            var config = counter.Stacks!;
            var values = fight.Stacks.Values
                .Where(stack => config.Effects.Any(sel => sel.MatchesGame(stack.Effect.Id, stack.Effect.Name)) && FilterOk(config.Target, stack.Entity))
                .Select(stack => (uint)stack.Count)
                .ToList();
            uint value = values.Count == 0 ? 0 : config.Aggregation switch
            {
                BarasAggregation.Sum => (uint)values.Sum(v => v),
                BarasAggregation.Min => values.Min(),
                _ => values.Max()
            };
            Change(fight, all, counter.Id, value, at, always: false);
        }
    }

    private static bool IsCharges(CombatValue? value) => value?.Kind?.Name is "charges";

    private void Phases(Fight fight, List<Signal> all, HashSet<string> entered)
    {
        foreach (var phase in fight.Boss.Phases)
        {
            if (!phase.Enabled || !BarasDifficulty.Allows(phase.Difficulties, _difficulty)) continue;
            if (fight.Phase == phase.Id || entered.Contains(phase.Id)) continue;
            if (phase.PrecededBy is { } required && (fight.Phase ?? fight.PreviousPhase) != required) continue;
            if (!phase.Conditions.All(c => Holds(fight, c, all[^1].At))) continue;
            if (!all.Any(s => Matches(phase.Start, s))) continue;
            string? old = fight.Phase;
            EnterPhase(fight, phase);
            entered.Add(phase.Id);
            DateTime at = all[^1].At;
            if (old is not null && !all.Any(s => s.Kind == SignalKind.PhaseEnded && s.Ref == old))
                all.Add(new Signal(SignalKind.PhaseEnded, at) { Ref = old });
            all.Add(new Signal(SignalKind.PhaseChanged, at) { Ref = phase.Id });
            return;
        }
    }

    private static void EnterPhase(Fight fight, BarasPhase phase)
    {
        fight.PreviousPhase = fight.Phase;
        fight.Phase = phase.Id;
        foreach (var id in phase.ResetsCounters)
            if (fight.Boss.Counters.FirstOrDefault(c => c.Id == id) is { } counter) fight.Counters[id] = counter.InitialValue;
    }

    private void PhaseEnd(Fight fight, List<Signal> all)
    {
        if (fight.Phase is not { } current) return;
        if (all.Any(s => s.Kind == SignalKind.PhaseEnded && s.Ref == current)) return;
        var phase = fight.Boss.Phases.FirstOrDefault(p => p.Id == current);
        if (phase?.End is not { } end || !all.Any(s => Matches(end, s))) return;
        all.Add(new Signal(SignalKind.PhaseEnded, all[^1].At) { Ref = current });
    }

    private List<Signal> RunTimers(List<Signal> all, DateTime at, List<FiredAlert> fired)
    {
        var events = new List<Signal>();
        if (_fight is not { } fight) return events;
        foreach (var signal in all)
        {
            foreach (var timer in fight.Boss.Timers)
                if (Matches(timer.Trigger, signal) && IsActive(fight, timer, signal.At))
                    Start(fight, timer, signal.At, signal.Event?.Source?.Name, signal.Entity?.Name ?? signal.Event?.Target?.Name, events, fired);
            foreach (var running in fight.Timers.Values.ToList())
            {
                if (running.Def.Cancel is not { } cancel || !Matches(cancel, signal)) continue;
                fight.Timers.Remove(running.Def.Id);
                events.Add(new Signal(SignalKind.TimerCanceled, signal.At) { Ref = running.Def.Id });
            }
        }
        CombatTime(fight, at, events, fired);
        return events;
    }

    private void CombatTime(Fight fight, DateTime now, List<Signal> events, List<FiredAlert> fired)
    {
        if (!_inCombat) return;
        double seconds = (now - _combatStart).TotalSeconds;
        foreach (var timer in fight.Boss.Timers)
        {
            if (!timer.Trigger.CombatTimeMet(seconds) || fight.CombatTimeStarted.Contains(timer.Id) || !IsActive(fight, timer, now)) continue;
            fight.CombatTimeStarted.Add(timer.Id);
            Start(fight, timer, _combatStart.AddSeconds(timer.Trigger.CombatTimeThreshold ?? 0), null, null, events, fired);
        }
    }

    private void Start(Fight fight, BarasTimer timer, DateTime at, string? source, string? target, List<Signal> events, List<FiredAlert> fired)
    {
        if (timer.IsAlert || timer.AlertOn == BarasAlertOn.OnApply)
            fired.Add(Alert(timer, Format(timer.AlertText ?? timer.Name, source, target), null, at));
        if (timer.IsAlert)
        {
            events.Add(new Signal(SignalKind.TimerStarted, at) { Ref = timer.Id });
            return;
        }
        if (fight.Timers.TryGetValue(timer.Id, out var existing))
        {
            if (!timer.CanBeRefreshed) return;
            existing.Restart(at);
            foreach (var other in fight.Timers.Values.ToList())
            {
                if (other.Def.Cancel is not { } cancel) continue;
                if (!Matches(cancel, new Signal(SignalKind.TimerStarted, at) { Ref = timer.Id })) continue;
                fight.Timers.Remove(other.Def.Id);
                events.Add(new Signal(SignalKind.TimerCanceled, at) { Ref = other.Def.Id });
            }
            return;
        }
        fight.Timers[timer.Id] = new Running(timer, at, source, target);
        events.Add(new Signal(SignalKind.TimerStarted, at) { Ref = timer.Id });
    }

    private void Advance(DateTime now, List<FiredAlert> fired)
    {
        for (int guard = 0; guard < 1000 && _fight is { } fight; guard++)
        {
            Cues(fight, now, fired);
            var due = fight.Timers.Values.Where(t => t.Expires <= now).OrderBy(t => t.Expires).FirstOrDefault();
            if (due is null) break;
            var at = due.Expires;
            var events = new List<Signal>();
            Expire(fight, due, at, events, fired);
            CombatTime(fight, at, events, fired);
            Process(events, at, fired);
        }
        if (_fight is { } current)
        {
            var events = new List<Signal>();
            CombatTime(current, now, events, fired);
            Process(events, now, fired);
            if (_ownerDead && (now - current.LastBossEvent).TotalSeconds > ReadoutEngine.WipeTimeout)
            {
                Finish(now, "Wiped");
                _inCombat = false;
            }
        }
    }

    private void Cues(Fight fight, DateTime now, List<FiredAlert> fired)
    {
        foreach (var timer in fight.Timers.Values.OrderBy(t => t.Expires))
        {
            var audio = timer.Def.Audio;
            if (!audio.Enabled) continue;
            double remaining = (timer.Expires - now).TotalSeconds;
            if (audio.Offset > 0 && !timer.OffsetFired && remaining <= audio.Offset)
            {
                timer.OffsetFired = true;
                if (audio.Offset - remaining < CueGrace)
                    fired.Add(Alert(timer.Def, null, Format(timer.Def.AlertText ?? timer.Def.Name, timer.Source, timer.Target), timer.Expires.AddSeconds(-audio.Offset)));
            }
            for (int n = 1; n <= audio.CountdownStart; n++)
            {
                if (remaining > n + 0.3 || remaining <= n - 0.7) continue;
                if (timer.Announced != 0 && timer.Announced <= n) break;
                timer.Announced = n;
                fired.Add(Alert(timer.Def, null, n.ToString(CultureInfo.InvariantCulture), timer.Expires.AddSeconds(-n)));
                break;
            }
        }
    }

    private void Expire(Fight fight, Running timer, DateTime at, List<Signal> events, List<FiredAlert> fired)
    {
        if (timer.RepeatCount < timer.Def.Repeats)
        {
            timer.RepeatCount++;
            timer.Restart(at);
            events.Add(new Signal(SignalKind.TimerExpired, at) { Ref = timer.Def.Id });
            return;
        }
        fight.Timers.Remove(timer.Def.Id);
        var def = timer.Def;
        bool sound = def.Audio.Enabled && def.Audio.Offset == 0;
        bool text = def.AlertOn == BarasAlertOn.OnExpire;
        if (sound || text)
        {
            string message = Format(def.AlertText ?? def.Name, timer.Source, timer.Target);
            fired.Add(text ? Alert(def, message, null, at) : Alert(def, null, message, at));
        }
        if (def.ChainsTo is { } next && fight.Boss.Timers.FirstOrDefault(t => t.Id == next) is { } chained && IsActive(fight, chained, at))
            Start(fight, chained, at, null, null, events, fired);
        events.Add(new Signal(SignalKind.TimerExpired, at) { Ref = def.Id });
    }

    private FiredAlert Alert(BarasTimer timer, string? text, string? speech, DateTime at) =>
        new(nextAlertId(), "baras:" + timer.Id, AlertLevel.Alert, text, speech, false, at, FightParser.DefaultAlertDuration, timer.Color);

    internal static string Format(string text, string? source, string? target) =>
        !text.Contains('{') ? text : text.Replace("{source}", source ?? "", StringComparison.Ordinal).Replace("{target}", target ?? "", StringComparison.Ordinal);

    private bool IsActive(Fight fight, BarasTimer timer, DateTime now) =>
        timer.Enabled &&
        BarasDifficulty.Allows(timer.Difficulties, _difficulty) &&
        (timer.GroupSize is not { } size || _difficulty?.GroupSize == size) &&
        timer.Conditions.All(c => Holds(fight, c, now));

    private static bool Holds(Fight fight, BarasCondition c, DateTime now) => c.Kind switch
    {
        BarasConditionKind.PhaseActive => fight.Phase is { } phase && c.Phases.Contains(phase),
        BarasConditionKind.CounterCompare => BarasCondition.Compare(c.Op, fight.Counters.GetValueOrDefault(c.Ref!), c.Value),
        BarasConditionKind.CounterCompareCounter => BarasCondition.Compare(c.Op, fight.Counters.GetValueOrDefault(c.Ref!), fight.Counters.GetValueOrDefault(c.Other!)),
        BarasConditionKind.TimerTimeRemaining => BarasCondition.Compare(c.Op, fight.Timers.TryGetValue(c.Ref!, out var t) ? Math.Max(0, (t.Expires - now).TotalSeconds) : 0, c.Value),
        BarasConditionKind.AllOf => c.Children.All(child => Holds(fight, child, now)),
        BarasConditionKind.AnyOf => c.Children.Any(child => Holds(fight, child, now)),
        BarasConditionKind.Not => !c.Children.All(child => Holds(fight, child, now)),
        _ => false
    };

    private bool Matches(BarasTrigger t, Signal s)
    {
        var e = s.Event;
        switch (t.Kind)
        {
            case BarasTriggerKind.AnyOf:
                return t.Children.Any(child => Matches(child, s));
            case BarasTriggerKind.CombatStart:
            case BarasTriggerKind.CombatEnd:
            case BarasTriggerKind.TimeElapsed:
            case BarasTriggerKind.Never:
                return false;
            case BarasTriggerKind.AbilityCast:
                return s.Kind == SignalKind.Ability && t.Abilities.Count > 0 && Any(t.Abilities, e!.Ability) && Filters(t, e.Source, s.Entity);
            case BarasTriggerKind.EffectApplied:
                return s.Kind == SignalKind.EffectApplied && t.Abilities.Count > 0 && Any(t.Abilities, e!.Effect.Effect) && Filters(t, e.Source, e.Target);
            case BarasTriggerKind.EffectRemoved:
                return s.Kind == SignalKind.EffectRemoved && t.Abilities.Count > 0 && Any(t.Abilities, e!.Effect.Effect) && Filters(t, e.Source, e.Target);
            case BarasTriggerKind.DamageTaken:
                return s.Kind == SignalKind.Damage &&
                       (t.Abilities.Count == 0 || Any(t.Abilities, e!.Ability)) &&
                       (t.Mitigation.Count == 0 || (e!.Value?.Mitigation is { } m && t.Mitigation.Contains(m.Id))) &&
                       Filters(t, e!.Source, e.Target);
            case BarasTriggerKind.HealingTaken:
                return s.Kind == SignalKind.Heal && t.Abilities.Count > 0 && Any(t.Abilities, e!.Ability) && Filters(t, e.Source, e.Target);
            case BarasTriggerKind.ThreatModified:
                return s.Kind == SignalKind.Threat &&
                       (t.Abilities.Count == 0 || Any(t.Abilities, e!.Ability)) &&
                       (t.Number is not { } threat || Math.Abs(threat - (e!.Threat ?? 0)) < 0.5) &&
                       Filters(t, e!.Source, e.Target);
            case BarasTriggerKind.BossHpBelow:
                return s.Kind == SignalKind.BossHp && s.New > 0 && s.Old > t.Number && s.New <= t.Number + 0.01 && (t.Selector.Count == 0 || Roster(t.Selector, s.Entity));
            case BarasTriggerKind.BossHpAbove:
                return s.Kind == SignalKind.BossHp && s.Old < t.Number && s.New >= t.Number - 0.01 && (t.Selector.Count == 0 || Roster(t.Selector, s.Entity));
            case BarasTriggerKind.NpcAppears:
                return s.Kind == SignalKind.NpcSeen && t.Selector.Count > 0 && Roster(t.Selector, s.Entity) && Positions(t, s.Entity, s.Entity);
            case BarasTriggerKind.EntityDeath:
                return s.Kind == SignalKind.Death && (t.Selector.Count == 0 || Roster(t.Selector, s.Entity)) && Positions(t, s.Entity, s.Entity);
            case BarasTriggerKind.TargetSet:
                return s.Kind == SignalKind.TargetSet && t.Selector.Count > 0 && Roster(t.Selector, e!.Source) && FilterOk(t.Target, e.Target);
            case BarasTriggerKind.PhaseEntered:
                return s.Kind == SignalKind.PhaseChanged && s.Ref == t.Ref;
            case BarasTriggerKind.PhaseEnded:
                return s.Kind == SignalKind.PhaseEnded && s.Ref == t.Ref;
            case BarasTriggerKind.AnyPhaseChange:
                return s.Kind == SignalKind.PhaseChanged;
            case BarasTriggerKind.CounterReaches:
                return s.Kind == SignalKind.CounterChanged && s.Ref == t.Ref && s.New == t.Number && s.Old != t.Number;
            case BarasTriggerKind.CounterChanges:
                return s.Kind == SignalKind.CounterChanged && s.Ref == t.Ref;
            case BarasTriggerKind.TimerExpires:
                return s.Kind == SignalKind.TimerExpired && s.Ref == t.Ref;
            case BarasTriggerKind.TimerStarted:
                return s.Kind == SignalKind.TimerStarted && s.Ref == t.Ref;
            case BarasTriggerKind.TimerCanceled:
                return s.Kind == SignalKind.TimerCanceled && s.Ref == t.Ref;
            default:
                return false;
        }
    }

    private static bool Any(IReadOnlyList<BarasSelector> selectors, GameRef? value) =>
        value is not null && selectors.Any(s => s.MatchesGame(value.Id, value.Name));

    private bool Filters(BarasTrigger t, CombatEntity? source, CombatEntity? target) =>
        Positions(t, source, target) && FilterOk(t.Source, source) && FilterOk(t.Target, target);

    private static bool Positions(BarasTrigger t, CombatEntity? source, CombatEntity? target)
    {
        foreach (var p in t.Position)
        {
            if ((p.OnSource ? source : target)?.Position is not { } pos) return false;
            double v = p.Axis switch
            {
                BarasAxis.X => pos.X,
                BarasAxis.Y => pos.Y,
                BarasAxis.Z => pos.Z,
                BarasAxis.Facing => pos.Facing,
                BarasAxis.XMinusY => pos.X - pos.Y,
                _ => pos.X + pos.Y
            };
            bool ok = p.Op switch
            {
                "gt" => v > p.Value,
                "gte" => v >= p.Value,
                "lt" => v < p.Value,
                "lte" => v <= p.Value,
                _ => v >= p.Min && v <= p.Max
            };
            if (!ok) return false;
        }
        return true;
    }

    private bool FilterOk(BarasFilter filter, CombatEntity? entity)
    {
        bool npc = entity is { Kind: CombatEntityKind.Npc };
        bool boss = npc && _fight is { } fight && fight.BossInstances.Contains(Uid(entity!));
        return filter.Kind switch
        {
            BarasFilterKind.Any => true,
            BarasFilterKind.LocalPlayer => IsMe(entity),
            BarasFilterKind.OtherPlayers => entity is { IsPlayer: true } && !IsMe(entity),
            BarasFilterKind.AnyPlayer or BarasFilterKind.AnyFriendlyPlayer => entity is { IsPlayer: true },
            BarasFilterKind.AnyEnemyPlayer => false,
            BarasFilterKind.AnyCompanion => entity is { Kind: CombatEntityKind.Companion },
            BarasFilterKind.AnyPlayerOrCompanion => entity is { Kind: CombatEntityKind.Player or CombatEntityKind.Companion },
            BarasFilterKind.AnyExceptLocal => !IsMe(entity),
            BarasFilterKind.CurrentTarget => entity is not null && _ownerId is { } me && _targets.TryGetValue(me, out var target) && Uid(target) == Uid(entity),
            BarasFilterKind.Boss => boss,
            BarasFilterKind.NpcExceptBoss => npc && !boss,
            BarasFilterKind.AnyNpc => npc,
            BarasFilterKind.Selector => Roster(filter.Selectors, entity),
            _ => false
        };
    }

    private bool Roster(IReadOnlyList<BarasSelector> selectors, CombatEntity? entity)
    {
        if (entity is null) return false;
        ulong npcId = entity.Kind is CombatEntityKind.Npc or CombatEntityKind.Companion ? entity.Id : 0;
        foreach (var selector in selectors)
        {
            if (selector.Id is { } id)
            {
                if (id == npcId) return true;
                continue;
            }
            var roster = _fight?.Boss.Entities.FirstOrDefault(r => string.Equals(r.Name, selector.Name, StringComparison.OrdinalIgnoreCase));
            if (roster is not null ? roster.Ids.Contains(npcId) : string.Equals(selector.Name, entity.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsMe(CombatEntity? entity) => entity is { IsPlayer: true } && entity.Id == _ownerId;

    private static ulong Uid(CombatEntity entity) => entity.InstanceId ?? entity.Id;

    private enum SignalKind
    {
        Ability,
        EffectApplied,
        EffectRemoved,
        Charges,
        Damage,
        Heal,
        Threat,
        BossHp,
        NpcSeen,
        Death,
        TargetSet,
        PhaseChanged,
        PhaseEnded,
        CounterChanged,
        TimerStarted,
        TimerExpired,
        TimerCanceled
    }

    private sealed record Signal(SignalKind Kind, DateTime At)
    {
        public CombatEvent? Event { get; init; }
        public CombatEntity? Entity { get; init; }
        public string? Ref { get; init; }
        public double Old { get; init; }
        public double New { get; init; }
    }

    private sealed class Running(BarasTimer def, DateTime start, string? source, string? target)
    {
        public BarasTimer Def { get; } = def;
        public DateTime Expires { get; private set; } = start.AddSeconds(def.Duration);
        public string? Source { get; } = source;
        public string? Target { get; } = target;
        public int RepeatCount { get; set; }
        public bool OffsetFired { get; set; }
        public int Announced { get; set; }

        public void Restart(DateTime at)
        {
            Expires = at.AddSeconds(Def.Duration);
            OffsetFired = false;
            Announced = 0;
        }
    }

    private sealed class Fight(BarasBoss boss, DateTime start)
    {
        public BarasBoss Boss { get; } = boss;
        public DateTime Start { get; } = start;
        public DateTime LastBossEvent { get; set; } = start;
        public string? Phase { get; set; }
        public string? PreviousPhase { get; set; }
        public bool Victory { get; set; }
        public Dictionary<string, uint> Counters { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Running> Timers { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CombatTimeStarted { get; } = new(StringComparer.Ordinal);
        public HashSet<ulong> BossInstances { get; } = [];
        public HashSet<ulong> KillSeen { get; } = [];
        public HashSet<ulong> KillDead { get; } = [];
        public Dictionary<(ulong Effect, ulong Entity), Stack> Stacks { get; } = [];
    }

    private sealed record Stack(int Count, CombatEntity Entity, GameRef Effect);
}
