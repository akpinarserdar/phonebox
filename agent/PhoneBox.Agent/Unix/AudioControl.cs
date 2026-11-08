using System.Globalization;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// Master volume on Linux (PipeWire's wpctl, or PulseAudio's pactl) and the selected macOS output.
/// macOS falls back to CoreAudio channels and, for a matching display, optional DDC/CI readback.
/// </summary>
public sealed partial class AudioControl
{
    private readonly Lock _lock = new();
    private AudioSnapshot? _cached;
    private long _at;

    public AudioSnapshot? Snapshot()
    {
        lock (_lock)
        {
            if (Environment.TickCount64 - _at < 900) return _cached;
            _at = Environment.TickCount64;
            return _cached = Read();
        }
    }

    public bool SetVolume(int level)
    {
        if (OperatingSystem.IsMacOS() && Snapshot() is not { CanControl: true }) return false;
        level = Math.Clamp(level, 0, 100);
        var ok = OperatingSystem.IsMacOS()
            ? Sh.Run("osascript", "-e", $"set volume output volume {level} without output muted") is not null
            : Sh.Which("wpctl") is not null
                ? Sh.Run("wpctl", "set-volume", "@DEFAULT_AUDIO_SINK@", (level / 100.0).ToString("0.00", CultureInfo.InvariantCulture)) is not null
                  && (level == 0 || Sh.Run("wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", "0") is not null)
                : Sh.Run("pactl", "set-sink-volume", "@DEFAULT_SINK@", $"{level}%") is not null;
        Invalidate();
        return ok;
    }

    public bool Step(int delta) => Snapshot() is { } s && SetVolume(s.Volume + delta);

    public bool ToggleMute()
    {
        if (OperatingSystem.IsMacOS() && Snapshot() is not { CanControl: true }) return false;
        var ok = OperatingSystem.IsMacOS()
            ? Sh.Run("osascript", "-e", "set volume output muted (not (output muted of (get volume settings)))") is not null
            : Sh.Which("wpctl") is not null
                ? Sh.Run("wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", "toggle") is not null
                : Sh.Run("pactl", "set-sink-mute", "@DEFAULT_SINK@", "toggle") is not null;
        Invalidate();
        return ok;
    }

    private void Invalidate() { lock (_lock) _at = 0; }

    private static AudioSnapshot? Read()
    {
        if (OperatingSystem.IsMacOS())
        {
            var s = Sh.Run("osascript", "-e", "set v to get volume settings\nreturn ((output volume of v) as string) & \",\" & ((output muted of v) as string)")?.Trim();
            var p = s?.Split(',');
            if (p is { Length: 2 } && int.TryParse(p[0], out var vol) && bool.TryParse(p[1], out var nativeMuted))
                return new AudioSnapshot(vol, nativeMuted);
            var output = MacAudioProbe.SelectedOutput();
            if (output is null) return null;
            var level = output.Value.Volume ?? MacAudioProbe.DisplayVolume(output.Value.Id, output.Value.Name);
            return level is { } measured ? new AudioSnapshot(measured, output.Value.Muted ?? false, false) : null;
        }
        if (Sh.Which("wpctl") is not null)
        {
            // "Volume: 0.45 [MUTED]"
            var m = WpctlRe().Match(Sh.Run("wpctl", "get-volume", "@DEFAULT_AUDIO_SINK@") ?? "");
            if (m.Success) return new AudioSnapshot((int)Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 100), m.Groups[2].Success);
        }
        var vol2 = PercentRe().Match(Sh.Run("pactl", "get-sink-volume", "@DEFAULT_SINK@") ?? "");
        if (!vol2.Success) return null;
        var muted = (Sh.Run("pactl", "get-sink-mute", "@DEFAULT_SINK@") ?? "").Contains("yes");
        return new AudioSnapshot(int.Parse(vol2.Groups[1].Value), muted);
    }

    [GeneratedRegex(@"Volume:\s*([\d.]+)(\s*\[MUTED\])?")] private static partial Regex WpctlRe();
    [GeneratedRegex(@"(\d+)%")] private static partial Regex PercentRe();
}
