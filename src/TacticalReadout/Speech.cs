using System.Runtime.Versioning;
using System.Speech.Synthesis;
using Ashla.SDK;

namespace TacticalReadout;

internal interface ISpeaker : IDisposable
{
    bool Available { get; }

    void Speak(string text, int volume, int rate, bool interrupt);
}

internal sealed class SilentSpeaker : ISpeaker
{
    public bool Available => false;

    public void Speak(string text, int volume, int rate, bool interrupt)
    {
    }

    public void Dispose()
    {
    }
}

internal static class Speakers
{
    public static ISpeaker Create(ILogger log) => OperatingSystem.IsWindows() ? new WindowsSpeaker(log) : new SilentSpeaker();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsSpeaker(ILogger log) : ISpeaker
{
    private readonly object _gate = new();
    private SpeechSynthesizer? _synthesizer;
    private bool _failed;

    public bool Available => !_failed;

    public void Speak(string text, int volume, int rate, bool interrupt)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            if (_failed) return;
            try
            {
                if (_synthesizer is null)
                {
                    _synthesizer = new SpeechSynthesizer();
                    _synthesizer.SetOutputToDefaultAudioDevice();
                }
                if (interrupt) _synthesizer.SpeakAsyncCancelAll();
                _synthesizer.Volume = Math.Clamp(volume, 0, 100);
                _synthesizer.Rate = Math.Clamp(rate, -10, 10);
                _synthesizer.SpeakAsync(text);
            }
            catch (Exception error) when (error is PlatformNotSupportedException or InvalidOperationException
                                              or System.Runtime.InteropServices.COMException or TypeInitializationException)
            {
                _failed = true;
                log.Warn($"[Tactical Readout] Text-to-speech isn't available on this PC: {error.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _synthesizer?.SpeakAsyncCancelAll();
            _synthesizer?.Dispose();
            _synthesizer = null;
        }
    }
}
