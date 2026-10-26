using Ashla.SDK;

namespace CombatMeter;

public sealed class CombatMeterPlugin : IAshlaPlugin
{
    private readonly EncounterTracker _tracker = new();
    private IDisposable? _subscription;
    private IPanelRegistration? _panel;
    private ILogger? _log;

    public string Name => "Combat Meter";

    public void Initialize(IPluginContext context)
    {
        _log = context.Log;
        _tracker.EncounterEnded += encounter => _log.Info($"[Combat Meter] {encounter.Summary()}");
        _subscription = context.Events.Subscribe<CombatEvent>(_tracker.Handle);
        if (context.Overlays.IsAvailable) _panel = context.Overlays.Register(new MeterPanel(_tracker, context.Config, context.Encounters));
    }

    public void Dispose()
    {
        _panel?.Dispose();
        _subscription?.Dispose();
    }
}
