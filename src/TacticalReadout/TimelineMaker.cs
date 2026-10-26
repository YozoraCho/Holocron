using System.Globalization;
using System.Text;
using Ashla.SDK;

namespace TacticalReadout;

internal sealed record MadeTimeline(string Name, string FileName, string Text, int Abilities, int Bosses);

internal static class TimelineMaker
{
    public const int MaxBosses = 3;
    public const int MaxEffects = 12;
    public const int FrequentCount = 15;
    public const double FrequentGap = 4;

    public static MadeTimeline? Make(IReadOnlyList<CombatEvent> events, string? title = null)
    {
        if (events.Count == 0) return null;
        var owner = events.FirstOrDefault(e => e.Effect.Type.Id == CombatIds.AreaEntered && e.Source is { IsPlayer: true })?.Source
                    ?? events.FirstOrDefault(e => e.Source is { IsPlayer: true })?.Source;
        if (owner is null) return null;
        var area = events.LastOrDefault(e => e.Effect.Type.Id == CombatIds.AreaEntered && e.Source?.Id == owner.Id);
        var start = events.FirstOrDefault(e => e.IsEvent(CombatIds.EnterCombat) && e.Source?.Id == owner.Id)?.Timestamp
                    ?? events[0].Timestamp;

        var npcs = new Dictionary<ulong, (string Name, long MaxHealth, int Actions)>();
        foreach (var e in events)
            foreach (var entity in new[] { e.Source, e.Target })
            {
                if (entity is not { Kind: CombatEntityKind.Npc }) continue;
                npcs.TryGetValue(entity.Id, out var seen);
                long max = Math.Max(seen.MaxHealth, entity.Health?.Max ?? 0);
                int actions = seen.Actions + (e.IsEvent(CombatIds.AbilityActivate) && ReferenceEquals(entity, e.Source) ? 1 : 0);
                npcs[entity.Id] = (entity.Name, max, actions);
            }
        if (npcs.Count == 0) return null;
        long top = npcs.Values.Max(n => n.MaxHealth);
        var bosses = npcs
            .Where(n => n.Value.MaxHealth > 0 && n.Value.MaxHealth >= top / 2 && n.Value.Actions > 0)
            .OrderByDescending(n => n.Value.MaxHealth)
            .Take(MaxBosses)
            .ToList();
        if (bosses.Count == 0) return null;
        var bossIds = bosses.Select(b => b.Key).ToHashSet();

        var casts = events
            .Where(e => e.IsEvent(CombatIds.AbilityActivate) && e.Source is { Kind: CombatEntityKind.Npc } s && bossIds.Contains(s.Id) &&
                        e.Ability is not null && e.Timestamp >= start)
            .ToList();
        var frequent = casts
            .GroupBy(e => e.Ability!.Id)
            .Where(g => g.Count() >= FrequentCount && MedianGap(g.Select(e => e.Timestamp).ToList()) < FrequentGap)
            .ToDictionary(g => g.Key, g => (g.First().Ability!.Name, Count: g.Count()));

        var effects = events
            .Where(e => e.Effect.Type.Id == CombatIds.ApplyEffect && !e.IsDamage && !e.IsHeal &&
                        e.Source is { Kind: CombatEntityKind.Npc } s && bossIds.Contains(s.Id) && e.Target?.Id == owner.Id)
            .GroupBy(e => e.Effect.Effect.Id)
            .Select(g => g.First().Effect.Effect)
            .Take(MaxEffects)
            .ToList();

        string name = bosses[0].Value.Name;
        var text = new StringBuilder();
        text.Append("# Starter timeline made by Tactical Readout from ").Append(title ?? "a recorded fight").Append(" on ")
            .Append(start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(".\n");
        text.Append("# Times are seconds from the pull. Delete lines you don't need, rename the rest, then add triggers.\n\n");
        text.Append("encounter ").Append(Ref.Quote(name)).Append('\n');
        if (area is not null)
        {
            text.Append("area ").Append(area.Effect.Effect.Id).Append("  # ").Append(area.Effect.Effect.Name).Append('\n');
            if (area.Effect.Detail is { } difficulty)
                text.Append("difficulty ").Append(difficulty.Id).Append("  # ").Append(difficulty.Name).Append('\n');
        }
        foreach (var boss in bosses)
            text.Append("boss ").Append(boss.Key).Append("  # ").Append(boss.Value.Name).Append('\n');
        text.Append("\nhideall \"--sync--\"\n\n");
        text.Append("0 \"Pull\"\n");
        int written = 0;
        foreach (var cast in casts)
        {
            if (frequent.ContainsKey(cast.Ability!.Id)) continue;
            double time = Math.Round((cast.Timestamp - start).TotalSeconds, 1);
            text.Append(time.ToString("0.0", CultureInfo.InvariantCulture)).Append(' ').Append(Ref.Quote(cast.Ability.Name))
                .Append(" sync ability ").Append(cast.Ability.Id).Append(" from boss\n");
            written++;
        }
        foreach (var (_, (abilityName, count)) in frequent)
            text.Append("# Left out ").Append(abilityName).Append(", used ").Append(count).Append(" times (probably a basic attack).\n");

        if (effects.Count > 0)
        {
            text.Append("\n# Effects the boss put on you. Uncomment a block to get an alert for it.\n");
            foreach (var effect in effects)
            {
                text.Append("\n# trigger ").Append(Ref.Quote(effect.Name)).Append('\n');
                text.Append("#   match apply ").Append(effect.Id).Append(" on me\n");
                text.Append("#   alert ").Append(Ref.Quote(effect.Name + " on you")).Append('\n');
                text.Append("# end\n");
            }
        }

        return new MadeTimeline(name, SafeFileName(name) + ".txt", text.ToString(), written, bosses.Count);
    }

    private static double MedianGap(List<DateTime> times)
    {
        if (times.Count < 2) return double.MaxValue;
        var gaps = times.Zip(times.Skip(1), (a, b) => (b - a).TotalSeconds).Order().ToList();
        return gaps[gaps.Count / 2];
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string([.. name.Select(c => invalid.Contains(c) ? '-' : c)]).Trim().TrimEnd('.');
        return safe.Length == 0 ? "Fight" : safe;
    }
}
