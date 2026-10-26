using System.Globalization;
using System.Numerics;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace TacticalReadout;

internal static class BarasLoader
{
    private static readonly Dictionary<string, BarasTriggerKind> TriggerKinds = new(StringComparer.Ordinal)
    {
        ["combat_start"] = BarasTriggerKind.CombatStart,
        ["combat_end"] = BarasTriggerKind.CombatEnd,
        ["ability_cast"] = BarasTriggerKind.AbilityCast,
        ["effect_applied"] = BarasTriggerKind.EffectApplied,
        ["effect_removed"] = BarasTriggerKind.EffectRemoved,
        ["damage_taken"] = BarasTriggerKind.DamageTaken,
        ["damage_dealt"] = BarasTriggerKind.DamageTaken,
        ["healing_taken"] = BarasTriggerKind.HealingTaken,
        ["threat_modified"] = BarasTriggerKind.ThreatModified,
        ["boss_hp_below"] = BarasTriggerKind.BossHpBelow,
        ["boss_hp_above"] = BarasTriggerKind.BossHpAbove,
        ["npc_appears"] = BarasTriggerKind.NpcAppears,
        ["entity_death"] = BarasTriggerKind.EntityDeath,
        ["target_set"] = BarasTriggerKind.TargetSet,
        ["phase_entered"] = BarasTriggerKind.PhaseEntered,
        ["phase_ended"] = BarasTriggerKind.PhaseEnded,
        ["any_phase_change"] = BarasTriggerKind.AnyPhaseChange,
        ["counter_reaches"] = BarasTriggerKind.CounterReaches,
        ["counter_changes"] = BarasTriggerKind.CounterChanges,
        ["timer_expires"] = BarasTriggerKind.TimerExpires,
        ["timer_started"] = BarasTriggerKind.TimerStarted,
        ["timer_canceled"] = BarasTriggerKind.TimerCanceled,
        ["time_elapsed"] = BarasTriggerKind.TimeElapsed,
        ["manual"] = BarasTriggerKind.Never,
        ["never"] = BarasTriggerKind.Never,
        ["killing_blow"] = BarasTriggerKind.Never,
        ["charges_changed"] = BarasTriggerKind.Never,
        ["self_charges_changed"] = BarasTriggerKind.Never,
        ["resource_spent"] = BarasTriggerKind.Never,
        ["any_of"] = BarasTriggerKind.AnyOf
    };

    private static readonly Dictionary<string, BarasFilterKind> FilterKinds = new(StringComparer.Ordinal)
    {
        ["any"] = BarasFilterKind.Any,
        ["local_player"] = BarasFilterKind.LocalPlayer,
        ["other_players"] = BarasFilterKind.OtherPlayers,
        ["group_members_except_local"] = BarasFilterKind.OtherPlayers,
        ["any_player"] = BarasFilterKind.AnyPlayer,
        ["group_members"] = BarasFilterKind.AnyPlayer,
        ["any_friendly_player"] = BarasFilterKind.AnyFriendlyPlayer,
        ["any_enemy_player"] = BarasFilterKind.AnyEnemyPlayer,
        ["any_companion"] = BarasFilterKind.AnyCompanion,
        ["any_player_or_companion"] = BarasFilterKind.AnyPlayerOrCompanion,
        ["any_except_local"] = BarasFilterKind.AnyExceptLocal,
        ["current_target"] = BarasFilterKind.CurrentTarget,
        ["boss"] = BarasFilterKind.Boss,
        ["npc_except_boss"] = BarasFilterKind.NpcExceptBoss,
        ["any_npc"] = BarasFilterKind.AnyNpc
    };

    private static readonly Dictionary<string, ulong> Mitigations = new(StringComparer.Ordinal)
    {
        ["miss"] = 836045448945502,
        ["parry"] = 836045448945503,
        ["dodge"] = 836045448945505,
        ["immune"] = 836045448945506,
        ["resist"] = 836045448945507,
        ["deflect"] = 836045448945508,
        ["shield"] = 836045448945509,
        ["cover"] = 836045448945510,
        ["absorbed"] = 836045448945511,
        ["reflected"] = 836045448953649
    };

    public static BarasArea Parse(string path, string text)
    {
        var root = ReadToml(path, text);
        var area = Table(root, "area") ?? throw new BarasFormatException("There's no [area] table.");
        string name = Str(area, "name") ?? throw new BarasFormatException("[area] has no name.");
        ulong areaId = Id(area, "area_id") ?? Id(area, "id") ?? 0;
        var bosses = new List<BarasBoss>();
        foreach (var boss in Tables(root, "boss"))
        {
            string id = Str(boss, "id") ?? throw new BarasFormatException("A [[boss]] has no id.");
            try
            {
                bosses.Add(Boss(boss, id));
            }
            catch (BarasFormatException error)
            {
                throw new BarasFormatException($"Boss \"{id}\": {error.Message}");
            }
        }
        return new BarasArea(path, name, areaId, bosses);
    }

    internal static TomlTable ReadToml(string path, string text)
    {
        var document = SyntaxParser.Parse(text, path, false);
        if (document.HasErrors)
        {
            var first = Enumerable.Range(0, document.Diagnostics.Count).Select(i => document.Diagnostics[i]).First(d => d.Kind == DiagnosticMessageKind.Error);
            throw new BarasFormatException($"Line {first.Span.Start.Line + 1}: {first.Message}");
        }
        var root = new TomlTable();
        foreach (var pair in document.KeyValues) Assign(root, pair!);
        foreach (var table in document.Tables)
        {
            var keys = Keys(table!.Name!);
            var parent = Walk(root, keys.Take(keys.Count - 1), table.Name!);
            string last = keys[^1];
            TomlTable target;
            if (table is TableArraySyntax)
            {
                if (!parent.TryGetValue(last, out var existing)) parent[last] = existing = new TomlTableArray();
                if (existing is not TomlTableArray array) throw new BarasFormatException($"Line {Line(table)}: [[{string.Join('.', keys)}]] isn't a list of tables.");
                target = new TomlTable();
                array.Add(target);
            }
            else
            {
                if (!parent.TryGetValue(last, out var existing)) parent[last] = existing = new TomlTable();
                target = existing switch
                {
                    TomlTable found => found,
                    TomlTableArray array when array.Count > 0 => array[^1],
                    _ => throw new BarasFormatException($"Line {Line(table)}: [{string.Join('.', keys)}] isn't a table.")
                };
            }
            foreach (var pair in table.Items) Assign(target, pair!);
        }
        return root;
    }

    private static int Line(SyntaxNode node) => node.Span.Start.Line + 1;

    private static void Assign(TomlTable table, KeyValueSyntax pair)
    {
        var keys = Keys(pair.Key!);
        var target = Walk(table, keys.Take(keys.Count - 1), pair);
        target[keys[^1]] = Value(pair.Value!);
    }

    private static TomlTable Walk(TomlTable table, IEnumerable<string> keys, SyntaxNode node)
    {
        foreach (var key in keys)
        {
            if (!table.TryGetValue(key, out var next)) table[key] = next = new TomlTable();
            table = next switch
            {
                TomlTable found => found,
                TomlTableArray array when array.Count > 0 => array[^1],
                _ => throw new BarasFormatException($"Line {Line(node)}: {key} isn't a table.")
            };
        }
        return table;
    }

    private static List<string> Keys(KeySyntax key)
    {
        var keys = new List<string> { KeyText(key.Key!) };
        foreach (var dotted in key.DotKeys) keys.Add(KeyText(dotted!.Key!));
        return keys;
    }

    private static string KeyText(BareKeyOrStringValueSyntax key) => key switch
    {
        BareKeySyntax bare => bare.Key!.Text ?? "",
        StringValueSyntax quoted => quoted.Value ?? "",
        _ => ""
    };

    private static object Value(ValueSyntax value)
    {
        switch (value)
        {
            case StringValueSyntax s:
                return s.Value ?? "";
            case IntegerValueSyntax i:
                return i.Value;
            case FloatValueSyntax f:
                return f.Value;
            case BooleanValueSyntax b:
                return b.Value;
            case ArraySyntax a:
                var array = new TomlArray();
                foreach (var item in a.Items) array.Add(Value(item!.Value!));
                return array;
            case InlineTableSyntax inline:
                var table = new TomlTable();
                foreach (var item in inline.Items) Assign(table, item!.KeyValue!);
                return table;
            case DateTimeValueSyntax d:
                return d.Value.ToString();
            default:
                throw new BarasFormatException($"Line {Line(value)}: that value isn't supported.");
        }
    }

    private static BarasBoss Boss(TomlTable t, string id)
    {
        var entities = Tables(t, "entities").Concat(Tables(t, "entity")).Select(Entity).ToList();
        var phases = Tables(t, "phases").Concat(Tables(t, "phase")).Select(Phase).ToList();
        var counters = Tables(t, "counters").Concat(Tables(t, "counter")).Select(Counter).ToList();
        var timers = new List<BarasTimer>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in Tables(t, "timer"))
        {
            var timer = Timer(table);
            if (timer.Enabled && seen.Add(timer.Id)) timers.Add(timer);
        }
        return new BarasBoss(
            id,
            Str(t, "name") ?? id,
            Bool(t, "enabled", true),
            entities,
            phases,
            counters,
            timers,
            OptTrigger(t, "encounter_trigger"),
            Num(t, "encounter_trigger_fallback_secs"),
            Bool(t, "has_victory_trigger", false),
            OptTrigger(t, "victory_trigger"),
            Strings(t, "victory_trigger_difficulties"));
    }

    private static BarasEntity Entity(TomlTable t)
    {
        string name = Str(t, "name") ?? throw new BarasFormatException("An entity has no name.");
        bool isBoss = Bool(t, "is_boss", false);
        return new BarasEntity(name, Ids(t, "ids"), isBoss, t.ContainsKey("triggers_encounter") ? Bool(t, "triggers_encounter", isBoss) : isBoss,
            Bool(t, "is_kill_target", false));
    }

    private static BarasPhase Phase(TomlTable t)
    {
        string id = Str(t, "id") ?? throw new BarasFormatException("A phase has no id.");
        try
        {
            var start = OptTrigger(t, "start_trigger") ?? OptTrigger(t, "trigger") ?? throw new BarasFormatException("It has no start_trigger.");
            var conditions = Conditions(t, "conditions").ToList();
            if (Table(t, "counter_condition") is { } legacy) conditions.Add(CounterCondition(legacy));
            return new BarasPhase(id, Str(t, "name") ?? id, Str(t, "display_text"), Bool(t, "enabled", true), start, OptTrigger(t, "end_trigger"),
                Str(t, "preceded_by"), conditions, Strings(t, "resets_counters"), Strings(t, "difficulties"));
        }
        catch (BarasFormatException error)
        {
            throw new BarasFormatException($"phase \"{id}\": {error.Message}");
        }
    }

    private static BarasCounter Counter(TomlTable t)
    {
        string id = Str(t, "id") ?? throw new BarasFormatException("A counter has no id.");
        try
        {
            BarasStacks? stacks = null;
            if (Table(t, "track_effect_stacks") is { } s)
            {
                var aggregation = Str(s, "aggregation") switch
                {
                    null or "max" => BarasAggregation.Max,
                    "sum" => BarasAggregation.Sum,
                    "min" => BarasAggregation.Min,
                    var other => throw new BarasFormatException($"Unknown aggregation \"{other}\".")
                };
                stacks = new BarasStacks(Selectors(s, "effects"), Filter(s, "target"), aggregation);
            }
            var increment = OptTrigger(t, "increment_on") ?? (stacks is null ? throw new BarasFormatException("It has no increment_on.") : BarasTrigger.Never);
            uint? setValue = Num(t, "set_value") is { } v ? (uint)Math.Max(0, v) : null;
            return new BarasCounter(id, Str(t, "name") ?? id, Bool(t, "enabled", true), increment, OptTrigger(t, "decrement_on"),
                OptTrigger(t, "reset_on") ?? BarasTrigger.CombatEnd, (uint)Math.Max(0, Num(t, "initial_value") ?? 0), Bool(t, "decrement", false),
                setValue, stacks);
        }
        catch (BarasFormatException error)
        {
            throw new BarasFormatException($"counter \"{id}\": {error.Message}");
        }
    }

    private static BarasTimer Timer(TomlTable t)
    {
        string id = Str(t, "id") ?? throw new BarasFormatException("A timer has no id.");
        try
        {
            var trigger = OptTrigger(t, "trigger") ?? throw new BarasFormatException("It has no trigger.");
            var alertOn = Str(t, "alert_on") switch
            {
                null or "none" => BarasAlertOn.None,
                "on_apply" => BarasAlertOn.OnApply,
                "on_expire" => BarasAlertOn.OnExpire,
                "countdown" => BarasAlertOn.Countdown,
                var other => throw new BarasFormatException($"Unknown alert_on \"{other}\".")
            };
            var conditions = Conditions(t, "conditions").ToList();
            var phases = Strings(t, "phases");
            if (phases.Count > 0) conditions.Add(new BarasCondition(BarasConditionKind.PhaseActive) { Phases = phases });
            if (Table(t, "counter_condition") is { } legacy) conditions.Add(CounterCondition(legacy));
            var audio = Table(t, "audio") is { } a
                ? new BarasAudio(Bool(a, "enabled", false), (int)(Num(a, "offset") ?? 0), (int)Math.Clamp(Num(a, "countdown_start") ?? 0, 0, 10), Str(a, "alert_text"))
                : BarasAudio.Off;
            int? groupSize = Num(t, "group_size") is { } g ? (int)g : null;
            return new BarasTimer(
                id,
                Str(t, "name") ?? throw new BarasFormatException("It has no name."),
                Str(t, "display_text"),
                trigger,
                Math.Max(0, Num(t, "duration_secs") ?? 0),
                Bool(t, "is_alert", false),
                alertOn,
                Str(t, "alert_text"),
                Num(t, "alert_countdown_secs"),
                Color(t, "color"),
                conditions,
                Strings(t, "difficulties"),
                groupSize,
                Bool(t, "enabled", true),
                Bool(t, "can_be_refreshed", false),
                (int)Math.Max(0, Num(t, "repeats") ?? 0),
                Str(t, "chains_to"),
                OptTrigger(t, "cancel_trigger"),
                Math.Max(0, Num(t, "show_at_secs") ?? 0),
                Shown(t),
                audio);
        }
        catch (BarasFormatException error)
        {
            throw new BarasFormatException($"timer \"{id}\": {error.Message}");
        }
    }

    private static bool Shown(TomlTable t)
    {
        object? value = t.TryGetValue("display_targets", out var many) ? many : t.TryGetValue("display_target", out var one) ? one : null;
        return value switch
        {
            null => true,
            string s => s != "none",
            TomlArray array => array.OfType<string>().Any(s => s != "none"),
            _ => throw new BarasFormatException("display_targets should be text or a list of text.")
        };
    }

    private static Vector4 Color(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var value)) return new Vector4(200, 200, 200, 255) / 255f;
        if (value is not TomlArray { Count: 4 } array) throw new BarasFormatException($"{key} should be [r, g, b, a].");
        var c = array.Select(v => (float)Math.Clamp(ToDouble(v, key), 0, 255) / 255f).ToArray();
        return new Vector4(c[0], c[1], c[2], c[3]);
    }

    private static BarasCondition CounterCondition(TomlTable t) =>
        new(BarasConditionKind.CounterCompare)
        {
            Ref = Str(t, "counter_id") ?? throw new BarasFormatException("A counter_condition has no counter_id."),
            Op = Str(t, "operator") ?? "eq",
            Value = Num(t, "value") ?? 0
        };

    private static IEnumerable<BarasCondition> Conditions(TomlTable t, string key) => Tables(t, key).Select(Condition);

    private static BarasCondition Condition(TomlTable t)
    {
        string type = Str(t, "type") ?? throw new BarasFormatException("A condition has no type.");
        return type switch
        {
            "phase_active" => new BarasCondition(BarasConditionKind.PhaseActive) { Phases = Strings(t, "phase_ids") },
            "counter_compare" => new BarasCondition(BarasConditionKind.CounterCompare)
            {
                Ref = Required(t, "counter_id", type), Op = Op(t, "eq"), Value = Num(t, "value") ?? 0
            },
            "counter_compare_counter" => new BarasCondition(BarasConditionKind.CounterCompareCounter)
            {
                Ref = Required(t, "counter_id", type), Other = Required(t, "other_counter_id", type), Op = Op(t, "eq")
            },
            "timer_time_remaining" => new BarasCondition(BarasConditionKind.TimerTimeRemaining)
            {
                Ref = Required(t, "timer_id", type), Op = Op(t, "gte"), Value = Num(t, "value") ?? 0
            },
            "all_of" => new BarasCondition(BarasConditionKind.AllOf) { Children = [.. Conditions(t, "conditions")] },
            "any_of" => new BarasCondition(BarasConditionKind.AnyOf) { Children = [.. Conditions(t, "conditions")] },
            "not" => new BarasCondition(BarasConditionKind.Not)
            {
                Children = [Condition(Table(t, "condition") ?? throw new BarasFormatException("A not condition has no condition."))]
            },
            _ => throw new BarasFormatException($"Unknown condition type \"{type}\".")
        };
    }

    private static string Op(TomlTable t, string fallback)
    {
        string op = Str(t, "operator") ?? fallback;
        return op is "eq" or "ne" or "lt" or "lte" or "gt" or "gte" ? op : throw new BarasFormatException($"Unknown operator \"{op}\".");
    }

    private static BarasTrigger? OptTrigger(TomlTable t, string key) => Table(t, key) is { } table ? Trigger(table) : null;

    private static BarasTrigger Trigger(TomlTable t)
    {
        string type = Str(t, "type") ?? throw new BarasFormatException("A trigger has no type.");
        if (!TriggerKinds.TryGetValue(type, out var kind)) throw new BarasFormatException($"Unknown trigger type \"{type}\".");
        var trigger = new BarasTrigger(kind) { Position = Positions(t) };
        return kind switch
        {
            BarasTriggerKind.AbilityCast or BarasTriggerKind.DamageTaken or BarasTriggerKind.HealingTaken or BarasTriggerKind.ThreatModified => trigger with
            {
                Abilities = Selectors(t, "abilities"),
                Source = Filter(t, "source"),
                Target = Filter(t, "target"),
                Mitigation = MitigationIds(t),
                Number = Num(t, "threat_value")
            },
            BarasTriggerKind.EffectApplied or BarasTriggerKind.EffectRemoved => trigger with
            {
                Abilities = Selectors(t, "effects"),
                Source = Filter(t, "source"),
                Target = Filter(t, "target")
            },
            BarasTriggerKind.BossHpBelow or BarasTriggerKind.BossHpAbove => trigger with
            {
                Number = Num(t, "hp_percent") ?? throw new BarasFormatException($"{type} has no hp_percent."),
                Selector = Selectors(t, "selector")
            },
            BarasTriggerKind.NpcAppears or BarasTriggerKind.EntityDeath => trigger with { Selector = Selectors(t, "selector") },
            BarasTriggerKind.TargetSet => trigger with { Selector = Selectors(t, "selector"), Target = Filter(t, "target") },
            BarasTriggerKind.PhaseEntered or BarasTriggerKind.PhaseEnded => trigger with { Ref = Required(t, "phase_id", type) },
            BarasTriggerKind.CounterReaches => trigger with
            {
                Ref = Required(t, "counter_id", type),
                Number = Num(t, "value") ?? throw new BarasFormatException("counter_reaches has no value.")
            },
            BarasTriggerKind.CounterChanges => trigger with { Ref = Required(t, "counter_id", type) },
            BarasTriggerKind.TimerExpires or BarasTriggerKind.TimerStarted or BarasTriggerKind.TimerCanceled => trigger with { Ref = Required(t, "timer_id", type) },
            BarasTriggerKind.TimeElapsed => trigger with { Number = Num(t, "secs") ?? 0 },
            BarasTriggerKind.AnyOf => trigger with { Children = [.. Tables(t, "conditions").Select(Trigger)] },
            _ => trigger
        };
    }

    private static IReadOnlyList<ulong> MitigationIds(TomlTable t) =>
        [.. Strings(t, "mitigation").Select(m => Mitigations.TryGetValue(m, out var id) ? id : throw new BarasFormatException($"Unknown mitigation \"{m}\"."))];

    private static IReadOnlyList<BarasPosition> Positions(TomlTable t)
    {
        var list = new List<BarasPosition>();
        foreach (var p in Tables(t, "position"))
        {
            bool onSource = Str(p, "entity") switch
            {
                "source" => true,
                "target" => false,
                var other => throw new BarasFormatException($"Unknown position entity \"{other}\".")
            };
            var axis = Str(p, "axis") switch
            {
                "x" => BarasAxis.X,
                "y" => BarasAxis.Y,
                "z" => BarasAxis.Z,
                "facing" => BarasAxis.Facing,
                "x_minus_y" => BarasAxis.XMinusY,
                "x_plus_y" => BarasAxis.XPlusY,
                var other => throw new BarasFormatException($"Unknown position axis \"{other}\".")
            };
            string op = Str(p, "op") ?? "";
            if (op is not ("gt" or "gte" or "lt" or "lte" or "between")) throw new BarasFormatException($"Unknown position op \"{op}\".");
            list.Add(new BarasPosition(onSource, axis, op, Num(p, "value") ?? 0, Num(p, "min") ?? 0, Num(p, "max") ?? 0));
        }
        return list;
    }

    private static BarasFilter Filter(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var value)) return BarasFilter.Any;
        if (value is string s)
            return FilterKinds.TryGetValue(s, out var kind) ? new BarasFilter(kind, []) : throw new BarasFormatException($"Unknown {key} \"{s}\".");
        if (value is TomlTable table && table.ContainsKey("selector"))
            return new BarasFilter(BarasFilterKind.Selector, Selectors(table, "selector"));
        throw new BarasFormatException($"{key} should be text or {{ selector = [...] }}.");
    }

    private static IReadOnlyList<BarasSelector> Selectors(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var value)) return [];
        if (value is not TomlArray array) throw new BarasFormatException($"{key} should be a list.");
        return [.. array.Select(item => item switch
        {
            long n => new BarasSelector((ulong)n, null),
            double d => new BarasSelector((ulong)d, null),
            string s => new BarasSelector(null, s),
            _ => throw new BarasFormatException($"{key} should list ids or names.")
        })];
    }

    private static IReadOnlyList<ulong> Ids(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var value)) return [];
        if (value is not TomlArray array) throw new BarasFormatException($"{key} should be a list of ids.");
        return [.. array.Select(item => item is long n ? (ulong)n : throw new BarasFormatException($"{key} should be a list of ids."))];
    }

    private static string Required(TomlTable t, string key, string type) => Str(t, key) ?? throw new BarasFormatException($"{type} has no {key}.");

    private static TomlTable? Table(TomlTable t, string key) =>
        t.TryGetValue(key, out var value) ? value as TomlTable ?? throw new BarasFormatException($"{key} should be a table.") : null;

    private static IEnumerable<TomlTable> Tables(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var value)) return [];
        return value switch
        {
            TomlTableArray tables => tables,
            TomlArray array => array.Select(item => item as TomlTable ?? throw new BarasFormatException($"{key} should be a list of tables.")).ToList(),
            TomlTable single => [single],
            _ => throw new BarasFormatException($"{key} should be a list of tables.")
        };
    }

    private static string? Str(TomlTable t, string key) =>
        t.TryGetValue(key, out var value) ? value as string ?? throw new BarasFormatException($"{key} should be text.") : null;

    private static IReadOnlyList<string> Strings(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var value)) return [];
        return value switch
        {
            string s => [s],
            TomlArray array => [.. array.Select(item => item as string ?? throw new BarasFormatException($"{key} should be a list of text."))],
            _ => throw new BarasFormatException($"{key} should be a list of text.")
        };
    }

    private static bool Bool(TomlTable t, string key, bool fallback) =>
        t.TryGetValue(key, out var value) ? value as bool? ?? throw new BarasFormatException($"{key} should be true or false.") : fallback;

    private static double? Num(TomlTable t, string key) => t.TryGetValue(key, out var value) ? ToDouble(value, key) : null;

    private static ulong? Id(TomlTable t, string key) => t.TryGetValue(key, out var value) && value is long n ? (ulong)n : null;

    private static double ToDouble(object? value, string key) => value switch
    {
        long n => n,
        double d => d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new BarasFormatException($"{key} should be a number.")
    };
}
