using System.Diagnostics;
using Ashla.SDK;

namespace TacticalReadout;

public sealed class ReadoutSettings
{
    public bool Speech { get; set; }

    public bool SpeakInfo { get; set; } = true;

    public int SpeechVolume { get; set; } = 80;

    public int SpeechRate { get; set; }

    public int Lookahead { get; set; } = 30;

    public int MaxBars { get; set; } = 6;

    public bool ShowWhenIdle { get; set; } = true;
}

internal sealed record LoadedFight(FightFile Fight, bool BuiltIn);

internal sealed class ReadoutService : IDisposable
{
    public static readonly int[] LookaheadChoices = [15, 30, 45, 60, 90];

    private readonly object _gate = new();
    private readonly ReadoutEngine _engine = new();
    private readonly ISpeaker _speaker;
    private readonly IPluginConfig _config;
    private readonly ILogger _log;
    private readonly IToasts _toasts;
    private DateTime _lastEventTime;
    private DateTime _lastEventWall;
    private DateTime _clock;
    private bool _haveEvent;
    private IReadOnlyList<LoadedFight> _loaded = [];
    private IReadOnlyList<FightProblem> _problems = [];
    private int _frame = -1;
    private ReadoutView? _frameView;

    public ReadoutService(IPluginConfig config, ILogger log, IToasts toasts, ISpeaker speaker, string builtInFolder)
    {
        _config = config;
        _log = log;
        _toasts = toasts;
        _speaker = speaker;
        BuiltInFolder = builtInFolder;
        var settingsFile = config.FilePath;
        UserFolder = Path.IsPathRooted(settingsFile)
            ? Path.Combine(Path.GetDirectoryName(settingsFile)!, "TacticalReadout", "Fights")
            : null;
        Settings = Sanitize(config.Load<ReadoutSettings>());
    }

    public ReadoutSettings Settings { get; }

    public string BuiltInFolder { get; }

    public string? UserFolder { get; }

    public bool SpeechAvailable => _speaker.Available;

    public IReadOnlyList<LoadedFight> Loaded
    {
        get { lock (_gate) return _loaded; }
    }

    public IReadOnlyList<FightProblem> Problems
    {
        get { lock (_gate) return _problems; }
    }

    private static ReadoutSettings Sanitize(ReadoutSettings settings)
    {
        settings.SpeechVolume = Math.Clamp(settings.SpeechVolume, 0, 100);
        settings.SpeechRate = Math.Clamp(settings.SpeechRate, -10, 10);
        if (!LookaheadChoices.Contains(settings.Lookahead)) settings.Lookahead = 30;
        settings.MaxBars = Math.Clamp(settings.MaxBars, 1, 12);
        return settings;
    }

    public void SaveSettings() => _config.Save(Settings);

    public void Reload(bool announce = false)
    {
        var loaded = new List<LoadedFight>();
        var problems = new List<FightProblem>();
        var byName = new Dictionary<string, LoadedFight>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, builtIn) in new[] { (BuiltInFolder, true), (UserFolder, false) })
        {
            if (folder is null || !Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*.txt").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var fight = FightParser.Parse(path, File.ReadAllText(path));
                    byName[Path.GetFileName(path)] = new LoadedFight(fight, builtIn);
                }
                catch (FightFormatException error)
                {
                    problems.Add(new FightProblem(path, error.Line, error.Message));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    problems.Add(new FightProblem(path, 0, $"Couldn't read it: {error.Message}"));
                }
            }
        }
        loaded.AddRange(byName.Values.OrderBy(f => f.Fight.Name, StringComparer.OrdinalIgnoreCase));
        lock (_gate)
        {
            _loaded = loaded;
            _problems = problems;
            _engine.SetFights([.. loaded.Select(f => f.Fight)]);
        }
        foreach (var problem in problems) _log.Warn($"[Tactical Readout] {problem}");
        if (announce)
        {
            if (problems.Count > 0)
                _toasts.Show("Tactical Readout", $"{problems.Count} fight file(s) have problems. Settings > Fights lists them.", ToastKind.Warning);
            else
                _toasts.Show("Tactical Readout", $"Loaded {loaded.Count} fight(s).", ToastKind.Success);
        }
    }

    public void Handle(CombatEvent e)
    {
        if (e.Effect.Type.Id == CombatIds.AreaEntered && e.Source is { IsPlayer: true }) Reload();
        IReadOnlyList<FiredAlert> fired;
        lock (_gate)
        {
            _lastEventTime = e.Timestamp;
            _lastEventWall = DateTime.UtcNow;
            _haveEvent = true;
            if (_clock == default || e.Timestamp > _clock || (_clock - e.Timestamp).TotalSeconds > 5) _clock = e.Timestamp;
            fired = _engine.Handle(e);
        }
        Speak(fired);
    }

    public ReadoutView Frame(int frame)
    {
        IReadOnlyList<FiredAlert> fired;
        ReadoutView view;
        lock (_gate)
        {
            if (frame == _frame && _frameView is not null) return _frameView;
            var now = Now();
            fired = _engine.Tick(now);
            view = _engine.View(now, Settings.Lookahead, Settings.MaxBars);
            _frame = frame;
            _frameView = view;
        }
        Speak(fired);
        return view;
    }

    public void Test(AlertLevel level)
    {
        FiredAlert alert;
        lock (_gate) alert = _engine.Test(level, Now());
        Speak([alert]);
    }

    private DateTime Now()
    {
        if (!_haveEvent) return DateTime.Now;
        var elapsed = DateTime.UtcNow - _lastEventWall;
        if (elapsed > TimeSpan.FromMinutes(10)) elapsed = TimeSpan.FromMinutes(10);
        var estimate = _lastEventTime + elapsed;
        if (estimate > _clock) _clock = estimate;
        return _clock;
    }

    private void Speak(IReadOnlyList<FiredAlert> fired)
    {
        if (!Settings.Speech || fired.Count == 0) return;
        foreach (var alert in fired)
        {
            if (alert.Level == AlertLevel.Info && !Settings.SpeakInfo) continue;
            if (alert.Spoken is { } text)
                _speaker.Speak(text, Settings.SpeechVolume, Settings.SpeechRate, interrupt: alert.Level == AlertLevel.Alarm);
        }
    }

    public string? MakeTimeline(IReadOnlyList<CombatEvent> events, string title, out string message)
    {
        var made = TimelineMaker.Make(events, title);
        if (made is null)
        {
            message = "That fight has no boss abilities to build a timeline from.";
            return null;
        }
        if (UserFolder is null)
        {
            message = "Ashla isn't saving plugin settings, so there's no fights folder to write to.";
            return null;
        }
        try
        {
            Directory.CreateDirectory(UserFolder);
            var path = Path.Combine(UserFolder, made.FileName);
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(UserFolder, $"{Path.GetFileNameWithoutExtension(made.FileName)} ({n}).txt");
            File.WriteAllText(path, made.Text);
            message = $"Saved {Path.GetFileName(path)} with {made.Abilities} boss abilit{(made.Abilities == 1 ? "y" : "ies")}.";
            _log.Info($"[Tactical Readout] Made a starter timeline: {path}");
            Reload();
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            message = $"Couldn't save the timeline: {error.Message}";
            return null;
        }
    }

    public static void Open(string path, bool folder)
    {
        try
        {
            if (folder) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose() => _speaker.Dispose();
}
