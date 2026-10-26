using Ashla.SDK;

namespace TacticalReadout;

public sealed class TacticalReadoutPlugin : IAshlaPlugin
{
    private ReadoutService? _service;
    private IDisposable? _subscription;
    private readonly List<IPanelRegistration> _panels = [];

    public string Name => "Tactical Readout";

    public void Initialize(IPluginContext context)
    {
        var folder = Path.Combine(Path.GetDirectoryName(typeof(TacticalReadoutPlugin).Assembly.Location) ?? AppContext.BaseDirectory, "Fights");
        _service = new ReadoutService(context.Config, context.Log, context.Toasts, Speakers.Create(context.Log), folder);
        _service.Reload();
        context.Log.Info($"[Tactical Readout] Loaded {_service.Loaded.Count} fight(s).");
        _subscription = context.Events.Subscribe<CombatEvent>(_service.Handle);
        if (!context.Overlays.IsAvailable) return;

        var alerts = new AlertPanel(_service);
        var settings = context.Overlays.Register(new SettingsPanel(_service, alerts, context.Encounters, context.Toasts));
        settings.IsVisible = false;
        _panels.Add(context.Overlays.Register(new TimelinePanel(_service, () => settings.IsVisible = true)));
        _panels.Add(context.Overlays.Register(alerts));
        _panels.Add(settings);
    }

    public void Dispose()
    {
        foreach (var panel in _panels) panel.Dispose();
        _panels.Clear();
        _subscription?.Dispose();
        _service?.Dispose();
    }
}
