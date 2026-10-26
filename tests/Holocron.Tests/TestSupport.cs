using System.Collections.Concurrent;
using Ashla.CombatLog;
using Ashla.SDK;

namespace Holocron.Tests;

internal sealed class TestLogger : ILogger
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public IEnumerable<string> Warnings => Lines.Where(l => l.StartsWith("WARN", StringComparison.Ordinal));

    public void Info(string message) => Lines.Enqueue($"INFO {message}");
    public void Warn(string message) => Lines.Enqueue($"WARN {message}");
    public void Error(string message, Exception? exception = null) => Lines.Enqueue($"ERROR {message} {exception?.Message}");
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "holocron-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class Fixture
{
    public static string LogPath => System.IO.Path.Combine(AppContext.BaseDirectory, "combat-logs", "combat_2026-10-22_04_06_32_959698.txt");

    private static readonly Lazy<List<CombatEvent>> Parsed = new(() =>
    {
        var session = new CombatLogSession(LogPath);
        var events = new List<CombatEvent>();
        foreach (var line in File.ReadLines(LogPath, CombatLogFile.Encoding))
            if (session.Parse(line) is { } e) events.Add(e);
        return events;
    });

    public static List<CombatEvent> Events() => [.. Parsed.Value];
}
