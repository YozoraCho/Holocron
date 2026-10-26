using System.Globalization;
using System.Numerics;
using Ashla.SDK;
using Ashla.UI;
using Hexa.NET.ImGui;

namespace CombatMeter;

public sealed class MeterSettings
{
    public int Tab { get; set; }

    public bool Compact { get; set; }

    public bool HideNames { get; set; }
}

internal sealed class MeterPanel(EncounterTracker tracker, IPluginConfig config, IEncounterArchive archive)
    : IPluginPanel, IPluginPanelAppearance
{
    private const string MenuId = "meter.menu";
    private const string EncountersId = "meter.encounters";
    private static readonly string[] TabLabels = ["Damage", "Healing", "Tank", "Threat"];

    private readonly MeterSettings _settings = Sanitize(config.Load<MeterSettings>());
    private int? _pinned;
    private string? _focusKey;
    private SavedEncounter? _savedEntry;
    private EncounterView? _saved;

    public string Id => "meter";
    public string Title => "Combat Meter";

    private static readonly PanelAppearance FullLook = new()
    {
        ShowTitleBar = false,
        BackgroundOpacity = 0.86f,
        Padding = 10f,
        DefaultWidth = 400f,
        DefaultHeight = 380f,
        NoScrollbar = true
    };

    private static readonly PanelAppearance CompactLook = FullLook with { Padding = 8f, FitToContent = true };

    public PanelAppearance Appearance => _settings.Compact ? CompactLook : FullLook;

    private float _width;

    private MeterTab Tab => (MeterTab)_settings.Tab;

    private static MeterSettings Sanitize(MeterSettings settings)
    {
        settings.Tab = Math.Clamp(settings.Tab, 0, TabLabels.Length - 1);
        return settings;
    }

    private void Save() => config.Save(_settings);

    private static Vector4 TabColor(MeterTab tab) => tab switch
    {
        MeterTab.Healing => AshlaColors.Healing,
        MeterTab.Tank => AshlaColors.DamageTaken,
        MeterTab.Threat => AshlaColors.Threat,
        _ => AshlaColors.Accent
    };

    private static string RateUnit(MeterTab tab) => tab switch
    {
        MeterTab.Healing => "HPS",
        MeterTab.Tank => "DTPS",
        MeterTab.Threat => "TPS",
        _ => "DPS"
    };

    public void Draw()
    {
        var live = tracker.Live();
        var history = tracker.History();
        var view = _saved ?? (_pinned is { } number ? history.FirstOrDefault(e => e.Number == number) : live);
        if (view is null && _pinned is not null)
        {
            _pinned = null;
            view = live;
        }

        _width = _settings.Compact ? MathF.Round(ImGui.GetFontSize() * 17f) : ImGui.GetContentRegionAvail().X;
        DrawHeader(view, live, history);
        DrawMenu();
        if (view is null)
        {
            ImGui.Spacing();
            if (!_settings.Compact) AshlaFonts.LargeText("Waiting for combat", AshlaColors.TextMuted);
            ImGui.PushStyleColor(ImGuiCol.Text, AshlaColors.TextMuted);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + _width);
            ImGui.TextUnformatted(_settings.Compact
                ? "Waiting for combat."
                : "Fights appear here as they happen. Needs combat logging on in SWTOR (Preferences > Combat Logging).");
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
            return;
        }

        var page = view.Tab(Tab);
        var color = TabColor(Tab);
        if (page.Rows.Count == 0)
        {
            ImGui.Spacing();
            AshlaDraw.MutedText(Tab switch
            {
                MeterTab.Healing => "No healing this encounter.",
                MeterTab.Tank => "Nothing hit you this encounter.",
                MeterTab.Threat => "No threat logged this encounter.",
                _ => "No damage this encounter."
            });
            return;
        }

        var columns = Columns(Tab);
        var rowWidths = Fit(columns.Select(c => (c.Header, c.Sample)), _width);
        if (!_settings.Compact) ColumnHeaders(columns.Select(c => c.Header).ToList(), rowWidths, "NAME");

        float rowHeight = MathF.Round(ImGui.GetTextLineHeight() * (_settings.Compact ? 1.45f : 1.6f));
        double top = page.Rows.Max(r => Math.Abs(r.Amount));
        var focus = page.Rows.FirstOrDefault(r => r.Key == _focusKey) ?? page.Rows[0];
        foreach (var row in page.Rows)
        {
            var cells = columns.Take(rowWidths.Length).Select(c => c.Cell(row, view, page.Total)).ToList();
            bool selected = !_settings.Compact && page.Rows.Count > 1 && row.Key == focus.Key;
            if (BarRow(row.Key, rowHeight, top > 0 ? (float)(Math.Abs(row.Amount) / top) : 0f,
                    RowColor(row.Kind, color), DisplayName(row, view), cells, rowWidths, selected, _width))
                _focusKey = row.Key;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(RowTooltip(row, view));
        }

        if (_settings.Compact) return;
        DrawAbilities(view, page, focus, color);
    }

    private void DrawHeader(EncounterView? view, EncounterView? live, IReadOnlyList<EncounterView> history)
    {
        var style = ImGui.GetStyle();
        float menuSize = ImGui.GetFrameHeight();
        string duration = view is null ? "" : FormatDuration(view.Duration);
        string rate = view is null ? "" : $"{AshlaDraw.Compact(view.PerSecond(view.Tab(Tab).Total))} {RateUnit(Tab)}";

        var rowStart = ImGui.GetCursorScreenPos();
        float width = _width;
        bool liveNow = view is { InProgress: true };
        bool following = _pinned is null && _saved is null;
        AshlaDraw.StatusDot(liveNow ? AshlaColors.Healing : following ? AshlaColors.TextMuted : AshlaColors.Warning);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(liveNow ? "Encounter in progress"
                : _savedEntry is { } saved ? $"Saved fight from {DayLabel(saved.Start)}"
                : following ? "Last encounter" : "From this session");
        ImGui.SameLine(0, style.ItemInnerSpacing.X);

        string right = _settings.Compact && view is not null ? $"{rate}   {duration}" : duration;
        float rightWidth = ImGui.CalcTextSize(right).X + style.ItemSpacing.X + menuSize;
        float titleRoom = width - (ImGui.GetCursorScreenPos().X - rowStart.X) - rightWidth - style.ItemSpacing.X;
        string title = view is null ? "No encounters yet" : view.Title;
        var titlePos = ImGui.GetCursorScreenPos();
        var titleText = AshlaDraw.Ellipsize(title, MathF.Max(0f, titleRoom - ImGui.GetFontSize()));
        var titleSize = ImGui.CalcTextSize(titleText) + new Vector2(ImGui.GetFontSize(), 0);
        if (ImGui.InvisibleButton("##encounters", new Vector2(MathF.Max(1f, titleSize.X), ImGui.GetTextLineHeight())))
            ImGui.OpenPopup(EncountersId);
        bool titleHovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(titlePos, (titleHovered ? AshlaColors.Accent : AshlaColors.Text).U32(), titleText);
        Caret(draw, titlePos + new Vector2(ImGui.CalcTextSize(titleText).X + 4f, 0));
        if (titleHovered)
        {
            var lines = new List<string>();
            if (view?.Area is { } area) lines.Add(area);
            if (_savedEntry is { } entry)
                lines.Add($"{DayLabel(entry.Start)} {entry.Start.ToString("t", CultureInfo.CurrentCulture)}" +
                          (_settings.HideNames ? "" : $"  ·  {entry.Character}"));
            lines.Add("Click for earlier encounters and saved fights");
            ImGui.SetTooltip(string.Join('\n', lines));
        }

        ImGui.SameLine();
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X + width - rightWidth, ImGui.GetCursorScreenPos().Y));
        AshlaDraw.MutedText(right);
        ImGui.SameLine();
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X + width - menuSize, rowStart.Y - style.FramePadding.Y * 0.5f));
        if (MenuButton(menuSize)) ImGui.OpenPopup(MenuId);
        DrawEncounterList(live, history);

        var lineY = ImGui.GetCursorScreenPos().Y + 1f;
        draw.AddLine(new Vector2(rowStart.X, lineY), new Vector2(rowStart.X + width, lineY), TabColor(Tab).WithAlpha(0.55f).U32(),
            MathF.Max(1f, ImGui.GetFontSize() / 12f));
        ImGui.Dummy(new Vector2(0, 3f));
        if (_settings.Compact) return;

        int tab = _settings.Tab;
        if (AshlaDraw.Tabs("pages", TabLabels, ref tab, TabColor((MeterTab)tab)))
        {
            _settings.Tab = tab;
            Save();
        }
        if (view is not null)
        {
            ImGui.SameLine();
            float rateWidth = ImGui.CalcTextSize(rate).X;
            float available = ImGui.GetContentRegionAvail().X;
            if (available > rateWidth) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + available - rateWidth);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + style.FramePadding.Y * 0.4f);
            ImGui.TextColored(TabColor(Tab).Shade(0.25f), rate);
        }
        ImGui.Dummy(new Vector2(0, 1f));
    }

    private void DrawEncounterList(EncounterView? live, IReadOnlyList<EncounterView> history)
    {
        if (!ImGui.BeginPopup(EncountersId)) return;
        float rowWidth = ImGui.GetFontSize() * 22f;
        bool following = _pinned is null && _saved is null;
        AshlaDraw.MutedText("THIS SESSION");
        string followLabel = live is { InProgress: true } ? $"Live: {live.Title}" : "Follow new encounters";
        if (ImGui.Selectable(followLabel, following, ImGuiSelectableFlags.None, new Vector2(rowWidth, 0)))
            ShowSession(null);
        foreach (var encounter in history)
        {
            bool chosen = _saved is null && (_pinned == encounter.Number ||
                                             (_pinned is null && live is { InProgress: false } && live.Number == encounter.Number));
            if (EncounterRow($"session.{encounter.Number}", encounter.Title, encounter.Duration, encounter.Start, null, chosen, rowWidth))
                ShowSession(encounter.Number);
        }
        if (history.Count == 0) AshlaDraw.MutedText("No finished fights yet.");

        if (archive.IsAvailable)
        {
            ImGui.Spacing();
            AshlaDraw.MutedText("SAVED");
            var inSession = history.Select(e => (e.Start, e.Owner)).ToHashSet();
            var saved = archive.List().Where(s => !inSession.Contains((s.Start, s.Character))).ToList();
            if (saved.Count == 0)
            {
                AshlaDraw.MutedText("Fights are saved as they end and kept for later.");
            }
            else
            {
                int lines = saved.Count + saved.Select(s => s.Start.Date).Distinct().Count();
                float height = MathF.Min(lines * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y,
                    ImGui.GetFontSize() * 18f);
                ImGui.BeginChild("saved", new Vector2(rowWidth + ImGui.GetStyle().ScrollbarSize, height));
                bool picked = false;
                DateTime? day = null;
                foreach (var entry in saved)
                {
                    if (entry.Start.Date != day)
                    {
                        day = entry.Start.Date;
                        ImGui.TextColored(AshlaColors.Accent.WithAlpha(0.8f), DayLabel(entry.Start));
                    }
                    if (EncounterRow($"saved.{entry.Id}", entry.Title, entry.Duration, entry.Start,
                            _settings.HideNames ? null : entry.Character, _savedEntry?.Id == entry.Id, rowWidth))
                    {
                        ShowSaved(entry);
                        picked = true;
                    }
                }
                ImGui.EndChild();
                if (picked) ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndPopup();
    }

    private static bool EncounterRow(string id, string title, TimeSpan duration, DateTime start, string? character, bool chosen,
        float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.Selectable($"##{id}", chosen, ImGuiSelectableFlags.None, new Vector2(width, 0));
        var draw = ImGui.GetWindowDrawList();
        string right = $"{FormatDuration(duration)}   {start.ToString("t", CultureInfo.CurrentCulture)}";
        float rightWidth = ImGui.CalcTextSize(right).X;
        float pad = ImGui.GetStyle().FramePadding.X * 0.5f;
        string left = character is null ? title : $"{title}  ·  {character}";
        draw.AddText(origin + new Vector2(pad, 0), AshlaColors.Text.U32(),
            AshlaDraw.Ellipsize(left, width - rightWidth - pad * 4));
        draw.AddText(origin + new Vector2(width - rightWidth - pad, 0), AshlaColors.TextMuted.U32(), right);
        return clicked;
    }

    private void ShowSession(int? number)
    {
        _pinned = number;
        _saved = null;
        _savedEntry = null;
        _focusKey = null;
    }

    private void ShowSaved(SavedEncounter entry)
    {
        var replay = new EncounterTracker();
        foreach (var e in archive.Load(entry.Id)) replay.Handle(e);
        var view = replay.History().FirstOrDefault() ?? replay.Live();
        if (view is null) return;
        _saved = view;
        _savedEntry = entry;
        _pinned = null;
        _focusKey = null;
    }

    private static string DayLabel(DateTime start)
    {
        var today = DateTime.Today;
        if (start.Date == today) return "Today";
        if (start.Date == today.AddDays(-1)) return "Yesterday";
        return start.ToString(start.Year == today.Year ? "ddd, MMM d" : "ddd, MMM d yyyy", CultureInfo.CurrentCulture);
    }

    private void DrawMenu()
    {
        if (!ImGui.BeginPopupContextWindow(MenuId)) return;
        for (int i = 0; i < TabLabels.Length; i++)
            if (ImGui.MenuItem(TabLabels[i], "", _settings.Tab == i))
            {
                _settings.Tab = i;
                Save();
            }
        ImGui.Separator();
        bool compact = _settings.Compact;
        if (ImGui.MenuItem("Compact view", "", ref compact))
        {
            _settings.Compact = compact;
            Save();
        }
        bool hide = _settings.HideNames;
        if (ImGui.MenuItem("Hide names", "", ref hide))
        {
            _settings.HideNames = hide;
            Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shows your discipline and \"Companion\" instead of names, for screenshots");
        ImGui.Separator();
        if (ImGui.MenuItem("Split encounter", "", false, tracker.InProgress))
        {
            tracker.Split();
            ShowSession(null);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Ends the current encounter here and starts a new one, e.g. between pulls without leaving combat");
        if (ImGui.MenuItem("Clear this session"))
        {
            tracker.ClearHistory();
            ShowSession(null);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Empties this session's list; saved fights stay saved");
        ImGui.EndPopup();
    }

    private static bool MenuButton(float size)
    {
        var origin = ImGui.GetCursorScreenPos();
        bool pressed = ImGui.InvisibleButton("##menu", new Vector2(size, size));
        bool hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetTooltip("Pages, compact view, hide names, split encounter");
        var draw = ImGui.GetWindowDrawList();
        if (hovered) draw.AddRectFilled(origin, origin + new Vector2(size), AshlaColors.SurfaceRaised.U32(), AshlaTheme.Round(4f));
        uint color = (hovered ? AshlaColors.Text : AshlaColors.TextMuted).U32();
        float x0 = origin.X + size * 0.28f, x1 = origin.X + size * 0.72f, thickness = MathF.Max(1f, size / 14f);
        for (int i = -1; i <= 1; i++)
        {
            float y = origin.Y + size * 0.5f + i * size * 0.18f;
            draw.AddLine(new Vector2(x0, y), new Vector2(x1, y), color, thickness);
        }
        return pressed;
    }

    private static void Caret(ImDrawListPtr draw, Vector2 at)
    {
        float h = ImGui.GetTextLineHeight();
        var center = at + new Vector2(h * 0.3f, h * 0.55f);
        float s = h * 0.18f;
        draw.AddTriangleFilled(center + new Vector2(-s, -s * 0.6f), center + new Vector2(s, -s * 0.6f),
            center + new Vector2(0, s * 0.7f), AshlaColors.TextMuted.U32());
    }

    private sealed record Column(string Header, string Sample, Func<CombatantRow, EncounterView, double, string> Cell);

    private sealed record AbilityColumn(string Header, string Sample, Func<AbilityRow, double, string> Cell);

    private List<Column> Columns(MeterTab tab)
    {
        static string Pct(double part, double whole) => whole > 0 ? (part / whole).ToString("0%", CultureInfo.CurrentCulture) : "-";
        static string Rate(double value) => value.ToString("0%", CultureInfo.CurrentCulture);
        var columns = tab switch
        {
            MeterTab.Healing => new List<Column>
            {
                new("HPS", "88.8k", (r, v, _) => AshlaDraw.Compact(v.PerSecond(r.Amount))),
                new("%", "100%", (r, _, t) => Pct(r.Amount, t)),
                new("HEALED", "8.88M", (r, _, _) => AshlaDraw.Compact(r.Amount)),
                new("OVER", "100%", (r, _, _) => Rate(r.OverhealRate)),
                new("CRIT", "100%", (r, _, _) => Rate(r.CritRate)),
            },
            MeterTab.Tank => new List<Column>
            {
                new("DTPS", "88.8k", (r, v, _) => AshlaDraw.Compact(v.PerSecond(r.Amount))),
                new("TAKEN", "8.88M", (r, _, _) => AshlaDraw.Compact(r.Amount)),
                new("ABSORB", "888.8k", (r, _, _) => AshlaDraw.Compact(r.Absorbed)),
                new("AVOID", "100%", (r, _, _) => Rate(r.AvoidRate)),
                new("DEATHS", "99", (r, _, _) => r.Deaths.ToString(CultureInfo.CurrentCulture)),
            },
            MeterTab.Threat => new List<Column>
            {
                new("TPS", "88.8k", (r, v, _) => AshlaDraw.Compact(v.PerSecond(r.Amount))),
                new("%", "100%", (r, _, t) => Pct(r.Amount, t)),
                new("THREAT", "8.88M", (r, _, _) => AshlaDraw.Compact(r.Amount)),
            },
            _ => new List<Column>
            {
                new("DPS", "88.8k", (r, v, _) => AshlaDraw.Compact(v.PerSecond(r.Amount))),
                new("%", "100%", (r, _, t) => Pct(r.Amount, t)),
                new("TOTAL", "8.88M", (r, _, _) => AshlaDraw.Compact(r.Amount)),
                new("CRIT", "100%", (r, _, _) => Rate(r.CritRate)),
                new("MAX", "88.8k", (r, _, _) => AshlaDraw.Compact(r.Max)),
            }
        };
        return _settings.Compact ? columns.Take(tab == MeterTab.Tank ? 1 : 2).ToList() : columns;
    }

    private static List<AbilityColumn> AbilityColumns(MeterTab tab)
    {
        static string Pct(double part, double whole) => whole > 0 ? (part / whole).ToString("0%", CultureInfo.CurrentCulture) : "-";
        var columns = new List<AbilityColumn>
        {
            new(tab switch { MeterTab.Healing => "HEALED", MeterTab.Tank => "TAKEN", MeterTab.Threat => "THREAT", _ => "TOTAL" },
                "8.88M", (a, _) => AshlaDraw.Compact(a.Amount)),
            new("%", "100%", (a, t) => Pct(a.Amount, t)),
        };
        if (tab == MeterTab.Threat) return columns;
        columns.Add(new("HITS", "9999", (a, _) => a.Hits.ToString(CultureInfo.CurrentCulture)));
        if (tab is MeterTab.Damage or MeterTab.Healing)
            columns.Add(new("CRIT", "100%", (a, _) => a.CritRate.ToString("0%", CultureInfo.CurrentCulture)));
        return columns;
    }

    private static float[] Fit(IEnumerable<(string Header, string Sample)> columns, float width)
    {
        float gap = ImGui.GetFontSize() * 0.9f, nameMin = ImGui.GetFontSize() * 6f;
        var widths = columns.Select(c => MathF.Max(ImGui.CalcTextSize(c.Header).X, ImGui.CalcTextSize(c.Sample).X) + gap).ToList();
        while (widths.Count > 1 && widths.Sum() + nameMin > width) widths.RemoveAt(widths.Count - 1);
        return [.. widths];
    }

    private static void ColumnHeaders(IReadOnlyList<string> headers, float[] widths, string first)
    {
        var min = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X, pad = MathF.Round(ImGui.GetTextLineHeight() * 0.45f);
        float height = ImGui.GetTextLineHeight();
        ImGui.Dummy(new Vector2(width, height));
        var draw = ImGui.GetWindowDrawList();
        uint color = AshlaColors.TextMuted.WithAlpha(0.8f).U32();
        float right = min.X + width - pad;
        for (int i = widths.Length - 1; i >= 0; i--)
        {
            var size = ImGui.CalcTextSize(headers[i]);
            draw.AddText(new Vector2(right - size.X, min.Y), color, headers[i]);
            right -= widths[i];
        }
        draw.AddText(new Vector2(min.X + pad, min.Y), color, AshlaDraw.Ellipsize(first, right - (min.X + pad) - pad));
    }

    private static bool BarRow(string id, float height, float fraction, Vector4 color, string name,
        IReadOnlyList<string> cells, float[] widths, bool selected, float rowWidth = 0f)
    {
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos();
        float width = MathF.Max(1f, rowWidth > 0 ? rowWidth : ImGui.GetContentRegionAvail().X);
        bool clicked = ImGui.InvisibleButton($"##row.{id}", new Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        var max = min + new Vector2(width, height);
        float rounding = AshlaTheme.Round(MathF.Min(4f, height * 0.3f));

        draw.AddRectFilled(min, max, (hovered ? AshlaColors.SurfaceRaised : AshlaColors.Surface).WithAlpha(0.85f).U32(), rounding);
        float fill = Math.Clamp(fraction, 0f, 1f) * width;
        if (fill >= 1f)
        {
            var fillMax = new Vector2(min.X + fill, max.Y);
            draw.AddRectFilled(min, fillMax, color.Shade(-0.3f).WithAlpha(0.85f).U32(), rounding);
            draw.AddRectFilled(min, new Vector2(fillMax.X, min.Y + height * 0.5f), color.WithAlpha(0.3f).U32(), rounding,
                ImDrawFlags.RoundCornersTop);
            draw.AddRectFilled(min, new Vector2(min.X + MathF.Min(3f, fill), max.Y), color.U32(), rounding,
                ImDrawFlags.RoundCornersLeft);
        }
        if (selected) draw.AddRect(min, max, color.WithAlpha(0.8f).U32(), rounding, ImDrawFlags.None, 1f);

        float textHeight = ImGui.GetTextLineHeight();
        float textY = min.Y + (height - textHeight) * 0.5f, pad = MathF.Round(textHeight * 0.45f);
        float right = max.X - pad;
        for (int i = Math.Min(cells.Count, widths.Length) - 1; i >= 0; i--)
        {
            var size = ImGui.CalcTextSize(cells[i]);
            Shadow(draw, new Vector2(right - size.X, textY), cells[i], i == 0 ? AshlaColors.Text : AshlaColors.Text.WithAlpha(0.85f));
            right -= widths[i];
        }
        Shadow(draw, new Vector2(min.X + pad, textY), AshlaDraw.Ellipsize(name, right - (min.X + pad) - pad), AshlaColors.Text);
        return clicked;
    }

    private static void Shadow(ImDrawListPtr draw, Vector2 pos, string text, Vector4 color)
    {
        draw.AddText(pos + new Vector2(1, 1), new Vector4(0, 0, 0, 0.7f).U32(), text);
        draw.AddText(pos, color.U32(), text);
    }

    private void DrawAbilities(EncounterView view, TabView page, CombatantRow focus, Vector4 color)
    {
        var abilities = page.Abilities.Where(a => a.CombatantKey == focus.Key).ToList();
        if (abilities.Count == 0) return;
        ImGui.Spacing();
        string who = DisplayName(focus, view);
        string heading = Tab switch
        {
            MeterTab.Tank => $"HITS ON {who.ToUpperInvariant()}",
            MeterTab.Threat => $"THREAT · {who.ToUpperInvariant()}",
            _ => $"ABILITIES · {who.ToUpperInvariant()}"
        };
        var columns = AbilityColumns(Tab);
        var widths = Fit(columns.Select(c => (c.Header, c.Sample)), ImGui.GetContentRegionAvail().X);
        ColumnHeaders(columns.Select(c => c.Header).ToList(), widths, heading);

        float height = MathF.Round(ImGui.GetTextLineHeight() * 1.3f);
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        int fit = Math.Max(1, (int)((ImGui.GetContentRegionAvail().Y + spacing) / (height + spacing)));
        double top = Math.Abs(abilities[0].Amount);
        var barColor = RowColor(focus.Kind, color).WithAlpha(0.75f);
        foreach (var ability in abilities.Take(fit))
        {
            string name = Tab == MeterTab.Tank && ability.From is { } from ? $"{ability.Name}  ·  {from}" : ability.Name;
            var cells = columns.Take(widths.Length).Select(c => c.Cell(ability, focus.Amount)).ToList();
            BarRow($"{focus.Key}.{ability.Name}.{ability.From}", height, top > 0 ? (float)(Math.Abs(ability.Amount) / top) : 0f,
                barColor, name, cells, widths, selected: false);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(AbilityTooltip(ability));
        }
    }

    private string DisplayName(CombatantRow row, EncounterView view)
    {
        if (!_settings.HideNames) return row.Name;
        return row.Kind switch
        {
            CombatEntityKind.Player => view.Discipline ?? "You",
            CombatEntityKind.Companion => "Companion",
            _ => row.Name
        };
    }

    private static Vector4 RowColor(CombatEntityKind kind, Vector4 pageColor) => kind switch
    {
        CombatEntityKind.Player => pageColor,
        CombatEntityKind.Companion => AshlaColors.Companion,
        _ => AshlaColors.Neutral
    };

    private string RowTooltip(CombatantRow row, EncounterView view)
    {
        string N(double v) => v.ToString("N0", CultureInfo.CurrentCulture);
        string P(double v) => v.ToString("0.#%", CultureInfo.CurrentCulture);
        return Tab switch
        {
            MeterTab.Healing => $"{N(row.Amount)} effective healing ({N(view.PerSecond(row.Amount))} HPS)\n" +
                                $"{N(row.Raw)} before overhealing, {P(row.OverhealRate)} overheal\n" +
                                $"{row.Hits} heals, {row.Crits} crits ({P(row.CritRate)}), biggest {N(row.Max)}",
            MeterTab.Tank => $"{N(row.Amount)} damage taken ({N(view.PerSecond(row.Amount))} DTPS)\n" +
                             $"{N(row.Absorbed)} absorbed, {row.Hits} attacks\n" +
                             $"{row.Avoided} avoided ({P(row.AvoidRate)}), {row.Shielded} shielded ({P(row.ShieldRate)})\n" +
                             $"{row.Deaths} death{(row.Deaths == 1 ? "" : "s")}",
            MeterTab.Threat => $"{N(row.Amount)} threat ({N(view.PerSecond(row.Amount))} TPS) from {row.Hits} actions",
            _ => $"{N(row.Amount)} damage ({N(view.PerSecond(row.Amount))} DPS)\n" +
                 $"{row.Hits} hits, {row.Crits} crits ({P(row.CritRate)}), biggest {N(row.Max)}"
        } + (row.Kind == CombatEntityKind.Player && view.Discipline is { } discipline ? $"\n{discipline}" : "");
    }

    private string AbilityTooltip(AbilityRow ability)
    {
        string N(double v) => v.ToString("N0", CultureInfo.CurrentCulture);
        var lines = new List<string> { Tab == MeterTab.Tank && ability.From is { } from ? $"{ability.Name} ({from})" : ability.Name };
        lines.Add(Tab == MeterTab.Threat
            ? $"{N(ability.Amount)} threat from {ability.Hits} actions"
            : $"{N(ability.Amount)} total, {ability.Hits} hits, average {N(ability.Average)}, biggest {N(ability.Max)}");
        if (Tab is MeterTab.Damage or MeterTab.Healing)
            lines.Add($"{ability.Crits} crits ({ability.CritRate.ToString("0.#%", CultureInfo.CurrentCulture)})");
        if (ability.DamageType is { } type) lines.Add($"{char.ToUpperInvariant(type[0])}{type[1..]} damage");
        return string.Join('\n', lines);
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
}
