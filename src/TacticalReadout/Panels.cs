using System.Globalization;
using System.Numerics;
using Ashla.SDK;
using Ashla.UI;
using Hexa.NET.ImGui;

namespace TacticalReadout;

internal static class ReadoutColors
{
    public static Vector4 For(AlertLevel level) => level switch
    {
        AlertLevel.Alarm => AshlaColors.Error,
        AlertLevel.Alert => AshlaColors.Warning,
        _ => AshlaColors.Text
    };

    public static float Luminance(Vector4 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    public static Vector4 ForBar(Vector4 color)
    {
        float l = Luminance(color);
        if (l <= 0.5f) return color with { W = 1f };
        float k = 0.5f / l;
        return new Vector4(color.X * k, color.Y * k, color.Z * k, 1f);
    }

    public static Vector4 ForText(Vector4 color)
    {
        float l = Luminance(color);
        if (l >= 0.55f) return color with { W = 1f };
        float t = (0.55f - l) / (1f - l);
        return new Vector4(color.X + (1f - color.X) * t, color.Y + (1f - color.Y) * t, color.Z + (1f - color.Z) * t, 1f);
    }
}

internal sealed class TimelinePanel(ReadoutService service, Action openSettings) : IPluginPanel, IPluginPanelAppearance
{
    public string Id => "readout.timeline";
    public string Title => "Tactical Readout";

    public PanelAppearance Appearance { get; } = new()
    {
        ShowTitleBar = false,
        BackgroundOpacity = 0.8f,
        Padding = 8f,
        FitToContent = true,
        NoScrollbar = true
    };

    public void Draw()
    {
        var view = service.Frame(ImGui.GetFrameCount());
        float width = MathF.Round(ImGui.GetFontSize() * 17f);

        string? title = view.Fight?.Name ?? (view.Boss is { } boss ? boss.Phase is { Length: > 0 } phase ? $"{boss.Boss} · {phase}" : boss.Boss : null);
        if (title is not null)
        {
            ImGui.TextColored(AshlaColors.Text, AshlaDraw.Ellipsize(title, width - ImGui.CalcTextSize("00:00").X - 12f));
            ImGui.SameLine(width - ImGui.CalcTextSize(Clock(view.Position)).X + ImGui.GetStyle().WindowPadding.X);
            ImGui.TextColored(AshlaColors.TextMuted, Clock(view.Position));
        }
        else if (view.LastResult is { } result)
        {
            ImGui.TextColored(AshlaColors.Healing, AshlaDraw.Ellipsize(result, width));
        }
        else if (ReadyText(view) is { } ready)
        {
            ImGui.TextColored(AshlaColors.TextMuted, AshlaDraw.Ellipsize("Ready: " + ready, width));
        }
        else if (service.Settings.ShowWhenIdle)
        {
            ImGui.TextColored(AshlaColors.TextMuted, AshlaDraw.Ellipsize($"No timeline for {view.Area ?? "this area"}", width));
        }
        ImGui.Dummy(new Vector2(width, 0f));

        if (view.Bars.Count > 0)
        {
            float barHeight = MathF.Round(ImGui.GetTextLineHeight() * 1.5f);
            float spacing = ImGui.GetStyle().ItemSpacing.Y;
            if (ImGui.BeginChild("bars", new Vector2(width, view.Bars.Count * (barHeight + spacing)), ImGuiChildFlags.None,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground))
            {
                foreach (var bar in view.Bars)
                {
                    float fraction = bar.Depletes
                        ? (float)Math.Clamp(bar.Remaining / Math.Max(bar.Duration, 0.001), 0, 1)
                        : bar.Active ? 1f : 1f - (float)(bar.Remaining / service.Settings.Lookahead);
                    var color = bar.Color is { } own ? ReadoutColors.ForBar(own) : (bar.Active ? AshlaColors.Healing : bar.Remaining <= 5 ? AshlaColors.Warning : AshlaColors.Accent);
                    AshlaDraw.MeterBar(bar.Text, Remaining(bar), fraction, color, barHeight);
                }
            }
            ImGui.EndChild();
        }

        if (ImGui.BeginPopupContextWindow("readout.menu"))
        {
            if (ImGui.MenuItem("Settings...")) openSettings();
            if (ImGui.MenuItem("Reload fights")) service.Reload(announce: true);
            ImGui.EndPopup();
        }
    }

    internal static string? ReadyText(ReadoutView view)
    {
        var parts = view.Ready.Select(f => f.Name).ToList();
        if (view.BarasArea is { } area) parts.Add($"{area} ({view.BarasBosses} boss{(view.BarasBosses == 1 ? "" : "es")})");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    internal static string Clock(double seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";
    }

    internal static string Remaining(TimelineBar bar) => bar.Active
        ? "now"
        : bar.Remaining < 10
            ? bar.Remaining.ToString("0.0", CultureInfo.InvariantCulture)
            : bar.Remaining.ToString("0", CultureInfo.InvariantCulture);
}

internal sealed class AlertPanel(ReadoutService service) : IPluginPanel, IPluginPanelAppearance
{
    public const int MaxShown = 3;

    public string Id => "readout.alerts";
    public string Title => "Tactical Readout Alerts";

    public bool ShowArea { get; set; }

    public PanelAppearance Appearance { get; } = new()
    {
        ShowTitleBar = false,
        BackgroundOpacity = 0f,
        Padding = 6f,
        DefaultWidth = 560f,
        DefaultHeight = 160f,
        NoScrollbar = true
    };

    public void Draw()
    {
        var view = service.Frame(ImGui.GetFrameCount());
        float width = ImGui.GetContentRegionAvail().X;
        var drawList = ImGui.GetWindowDrawList();
        if (!view.Alerts.Any(a => a.Text is { Length: > 0 }))
        {
            if (ShowArea || ImGui.IsWindowHovered())
            {
                var min = ImGui.GetWindowPos();
                var max = min + ImGui.GetWindowSize();
                drawList.AddRect(min + Vector2.One, max - Vector2.One, AshlaColors.Border.U32(), AshlaTheme.Round(6f), ImDrawFlags.None, 1.5f);
                const string hint = "Alerts appear here. Drag to move, drag a corner to resize.";
                var hintSize = ImGui.CalcTextSize(hint);
                ImGui.SetCursorPos(new Vector2((ImGui.GetWindowWidth() - hintSize.X) * 0.5f, (ImGui.GetWindowHeight() - hintSize.Y) * 0.5f));
                ImGui.TextColored(AshlaColors.TextMuted, hint);
            }
            return;
        }

        bool large = AshlaFonts.HasLarge;
        if (large) ImGui.PushFont(AshlaFonts.Large, AshlaFonts.LargeSize);
        foreach (var alert in view.Alerts.Where(a => a.Text is { Length: > 0 }).Take(MaxShown))
        {
            if (alert.Text is not { Length: > 0 } text) continue;
            double left = alert.At.AddSeconds(alert.Duration).Subtract(view.Now).TotalSeconds;
            float alpha = (float)Math.Clamp(left / 0.4, 0, 1);
            var color = (alert.Color is { } own ? ReadoutColors.ForText(own) : ReadoutColors.For(alert.Level)).WithAlpha(alpha);
            var textSize = ImGui.CalcTextSize(text);
            var cursor = ImGui.GetCursorScreenPos();
            var position = new Vector2(cursor.X + MathF.Max(0f, (width - textSize.X) * 0.5f), cursor.Y);
            uint shadow = new Vector4(0f, 0f, 0f, 0.85f * alpha).U32();
            foreach (var offset in Outline) drawList.AddText(position + offset, shadow, text);
            drawList.AddText(position, color.U32(), text);
            ImGui.Dummy(new Vector2(width, textSize.Y + 2f));
        }
        if (large) ImGui.PopFont();
    }

    private static readonly Vector2[] Outline = [new(-2, 0), new(2, 0), new(0, -2), new(0, 2), new(2, 2)];
}

internal sealed class SettingsPanel(ReadoutService service, AlertPanel alerts, IEncounterArchive archive, IToasts toasts)
    : IPluginPanel, IPluginPanelAppearance
{
    private static readonly string[] TabLabels = ["Fights", "Alerts", "Timeline", "Make a Timeline"];
    private int _tab;
    private string? _lastMade;

    public string Id => "readout.settings";
    public string Title => "Tactical Readout Settings";

    public PanelAppearance Appearance { get; } = new() { DefaultWidth = 480f, DefaultHeight = 430f };

    public void Draw()
    {
        var view = service.Frame(ImGui.GetFrameCount());
        AshlaDraw.Tabs("readout.tabs", TabLabels, ref _tab, AshlaColors.Accent);
        ImGui.Spacing();
        switch (_tab)
        {
            case 0:
                DrawFights(view);
                break;
            case 1:
                DrawAlerts();
                break;
            case 2:
                DrawTimeline();
                break;
            default:
                DrawMaker();
                break;
        }
    }

    private void DrawFights(ReadoutView view)
    {
        string area = view.Area is null ? "Not known yet" : view.Difficulty is null ? view.Area : $"{view.Area} ({view.Difficulty})";
        ImGui.TextColored(AshlaColors.TextMuted, "Current area:");
        ImGui.SameLine();
        ImGui.TextUnformatted(area);
        ImGui.TextColored(AshlaColors.TextMuted, "Ready here:");
        ImGui.SameLine();
        ImGui.TextUnformatted(TimelinePanel.ReadyText(view) ?? "No fights");
        ImGui.Spacing();

        var baras = service.Baras;
        ImGui.SeparatorText($"BARAS Boss Timers ({baras.Sum(b => b.Area.Bosses.Count)} bosses)");
        bool useBaras = service.Settings.UseBaras;
        if (ImGui.Checkbox("Use BARAS boss timers", ref useBaras)) service.SetUseBaras(useBaras);
        ImGui.PushTextWrapPos(0f);
        AshlaDraw.MutedText($"Timers, phases and alerts for {BarasArea.Describe(baras.Select(b => b.Area))} from BARAS (MIT license). " +
                            "When one of your fight files has the boss, its timeline is shown instead.");
        ImGui.PopTextWrapPos();
        if (baras.Count > 0 && ImGui.TreeNode("Areas"))
        {
            foreach (var entry in baras)
            {
                ImGui.TextUnformatted(entry.Area.Name);
                ImGui.SameLine();
                AshlaDraw.MutedText($"{(entry.BuiltIn ? "built-in" : "yours")}  ·  {entry.Area.Bosses.Count} bosses, {entry.Area.TimerCount} timers");
            }
            ImGui.TreePop();
        }

        var loaded = service.Loaded;
        ImGui.SeparatorText($"Fight Files ({loaded.Count})");
        if (loaded.Count == 0)
        {
            ImGui.PushTextWrapPos(0f);
            AshlaDraw.MutedText("No fight files yet. Make one from a saved fight, or add .txt files to your fights folder.");
            ImGui.PopTextWrapPos();
        }
        foreach (var fight in loaded)
        {
            ImGui.TextUnformatted(fight.Fight.Name);
            ImGui.SameLine();
            AshlaDraw.MutedText($"{(fight.BuiltIn ? "built-in" : "yours")}  ·  {Path.GetFileName(fight.Fight.Path)}  ·  " +
                                $"{fight.Fight.Timeline.Count} timeline lines, {fight.Fight.Triggers.Count} triggers");
        }

        var problems = service.Problems;
        if (problems.Count > 0)
        {
            ImGui.SeparatorText("Problems");
            ImGui.PushTextWrapPos(0f);
            foreach (var problem in problems) ImGui.TextColored(AshlaColors.Error, problem.ToString());
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        if (ImGui.Button("Reload")) service.Reload(announce: true);
        if (service.UserFolder is { } folder)
        {
            ImGui.SameLine();
            if (ImGui.Button("Open my fights folder")) ReadoutService.Open(folder, folder: true);
        }
        ImGui.PushTextWrapPos(0f);
        AshlaDraw.MutedText("Fight files are .txt files in your fights folder, and BARAS encounter files are .toml files. " +
                            "One with the same name and folder as a built-in file replaces it.");
        ImGui.PopTextWrapPos();
    }

    private void DrawAlerts()
    {
        var settings = service.Settings;
        bool changed = false;
        bool speech = settings.Speech;
        if (ImGui.Checkbox("Speak alerts (text-to-speech)", ref speech))
        {
            settings.Speech = speech;
            changed = true;
        }
        if (!service.SpeechAvailable) AshlaDraw.MutedText("Text-to-speech needs Windows' built-in voices.");
        ImGui.BeginDisabled(!settings.Speech);
        bool info = settings.SpeakInfo;
        if (ImGui.Checkbox("Also speak info alerts", ref info))
        {
            settings.SpeakInfo = info;
            changed = true;
        }
        int volume = settings.SpeechVolume;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12f);
        if (ImGui.SliderInt("Volume", ref volume, 0, 100))
        {
            settings.SpeechVolume = volume;
            changed = true;
        }
        int rate = settings.SpeechRate;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12f);
        if (ImGui.SliderInt("Speed", ref rate, -5, 5))
        {
            settings.SpeechRate = rate;
            changed = true;
        }
        ImGui.EndDisabled();
        if (changed) service.SaveSettings();

        ImGui.SeparatorText("Try It");
        if (ImGui.Button("Info")) service.Test(AlertLevel.Info);
        ImGui.SameLine();
        if (ImGui.Button("Alert")) service.Test(AlertLevel.Alert);
        ImGui.SameLine();
        if (ImGui.Button("Alarm")) service.Test(AlertLevel.Alarm);
        bool area = alerts.ShowArea;
        if (ImGui.Checkbox("Show where alerts appear", ref area)) alerts.ShowArea = area;
        ImGui.PushTextWrapPos(0f);
        AshlaDraw.MutedText("Alarms are the most urgent and stop anything still being spoken. Drag the alert area to move it.");
        ImGui.PopTextWrapPos();
    }

    private void DrawTimeline()
    {
        var settings = service.Settings;
        bool changed = false;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12f);
        if (ImGui.BeginCombo("Look ahead", $"{settings.Lookahead} seconds"))
        {
            foreach (var choice in ReadoutService.LookaheadChoices)
                if (ImGui.Selectable($"{choice} seconds", choice == settings.Lookahead))
                {
                    settings.Lookahead = choice;
                    changed = true;
                }
            ImGui.EndCombo();
        }
        int bars = settings.MaxBars;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12f);
        if (ImGui.SliderInt("Bars", ref bars, 1, 12))
        {
            settings.MaxBars = bars;
            changed = true;
        }
        bool idle = settings.ShowWhenIdle;
        if (ImGui.Checkbox("Show a line when this area has no timeline", ref idle))
        {
            settings.ShowWhenIdle = idle;
            changed = true;
        }
        if (changed) service.SaveSettings();
    }

    private void DrawMaker()
    {
        ImGui.PushTextWrapPos(0f);
        AshlaDraw.MutedText("Pick a saved boss fight. Tactical Readout writes a starter fight file to your fights folder with every " +
                            "ability the boss used and when. Edit it, then press Reload in Fights.");
        ImGui.PopTextWrapPos();
        if (_lastMade is { } made)
        {
            ImGui.TextColored(AshlaColors.Healing, Path.GetFileName(made));
            ImGui.SameLine();
            if (ImGui.SmallButton("Open")) ReadoutService.Open(made, folder: false);
        }
        ImGui.Spacing();
        if (!archive.IsAvailable)
        {
            AshlaDraw.MutedText("Saved fights aren't available. Turn on saving in Ashla's Settings > Fights.");
            return;
        }
        var fights = archive.List();
        if (fights.Count == 0)
        {
            AshlaDraw.MutedText("No saved fights yet.");
            return;
        }
        if (!ImGui.BeginChild("saved", Vector2.Zero, ImGuiChildFlags.None)) { ImGui.EndChild(); return; }
        foreach (var fight in fights.Take(50))
        {
            ImGui.PushID(fight.Id);
            if (ImGui.SmallButton("Make"))
            {
                var path = service.MakeTimeline(archive.Load(fight.Id), fight.Title, out var message);
                if (path is not null) _lastMade = path;
                toasts.Show("Tactical Readout", message, path is null ? ToastKind.Warning : ToastKind.Success);
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(fight.Title);
            ImGui.SameLine();
            AshlaDraw.MutedText($"{fight.Area ?? "?"}  ·  {TimelinePanel.Clock(fight.Duration.TotalSeconds)}  ·  " +
                                fight.Start.ToString("MMM d HH:mm", CultureInfo.InvariantCulture));
            ImGui.PopID();
        }
        ImGui.EndChild();
    }
}
