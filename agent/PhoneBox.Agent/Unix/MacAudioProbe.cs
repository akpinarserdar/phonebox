using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Reads the currently selected macOS output. DDC is only used when that output is a matching display.</summary>
internal static class MacAudioProbe
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Global = 0x676c6f62; // 'glob'
    private const uint Output = 0x6f757470; // 'outp'
    private const uint DefaultOutput = 0x644f7574; // 'dOut'
    private const uint Name = 0x6c6e616d; // 'lnam'
    private const uint Volume = 0x766f6c6d; // 'volm'
    private const uint Mute = 0x6d757465; // 'mute'

    [StructLayout(LayoutKind.Sequential)]
    private struct Address(uint selector, uint scope, uint element)
    {
        public uint Selector = selector;
        public uint Scope = scope;
        public uint Element = element;
    }

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int GetUInt(uint id, ref Address address, uint qualifierSize, nint qualifier, ref uint size, out uint value);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int GetFloat(uint id, ref Address address, uint qualifierSize, nint qualifier, ref uint size, out float value);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int GetPointer(uint id, ref Address address, uint qualifierSize, nint qualifier, ref uint size, out nint value);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(nint value, byte[] buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);

    private static uint? UInt(uint id, uint selector, uint scope, uint element)
    {
        var address = new Address(selector, scope, element);
        uint size = sizeof(uint);
        return GetUInt(id, ref address, 0, 0, ref size, out var value) == 0 ? value : null;
    }

    private static float? Float(uint id, uint selector, uint scope, uint element)
    {
        var address = new Address(selector, scope, element);
        uint size = sizeof(float);
        return GetFloat(id, ref address, 0, 0, ref size, out var value) == 0 && float.IsFinite(value) ? value : null;
    }

    private static string? DeviceName(uint device)
    {
        var address = new Address(Name, Global, 0);
        uint size = (uint)IntPtr.Size;
        if (GetPointer(device, ref address, 0, 0, ref size, out var cfString) != 0 || cfString == 0) return null;
        try
        {
            var buffer = new byte[512];
            if (!CFStringGetCString(cfString, buffer, buffer.Length, 0x08000100)) return null;
            var length = Array.IndexOf(buffer, (byte)0);
            return length >= 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
        }
        finally { CFRelease(cfString); }
    }

    public static (uint Id, string Name, int? Volume, bool? Muted)? SelectedOutput()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var device = UInt(1, DefaultOutput, Global, 0);
            if (device is null or 0) return null;
            var scalar = Float(device.Value, Volume, Output, 0);
            if (scalar is null)
            {
                var channels = new[] { Float(device.Value, Volume, Output, 1), Float(device.Value, Volume, Output, 2) }
                    .Where(value => value is not null).Select(value => value!.Value).ToArray();
                if (channels.Length > 0) scalar = channels.Average();
            }
            var muted = UInt(device.Value, Mute, Output, 0);
            return (device.Value, DeviceName(device.Value) ?? "", scalar is { } s ? (int)Math.Round(Math.Clamp(s, 0, 1) * 100) : null,
                muted is { } m ? m != 0 : null);
        }
        catch { return null; }
    }

    private static uint _displayDevice;
    private static string _displayName = "";
    private static int? _displayIndex;
    private static long _displayAt;

    public static int? DisplayVolume(uint device, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Sh.Which("m1ddc") is null) return null;
        if (_displayDevice != device || _displayName != name || Environment.TickCount64 - _displayAt > 30_000)
        {
            _displayDevice = device;
            _displayName = name;
            _displayAt = Environment.TickCount64;
            _displayIndex = null;
            var list = Sh.RunWithTimeout(5000, "m1ddc", "display", "list");
            if (list is not null)
            {
                foreach (var line in list.Split('\n'))
                {
                    var match = Regex.Match(line, @"^\[(\d+)\] (.+?) \(");
                    if (match.Success && string.Equals(match.Groups[2].Value.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(match.Groups[1].Value, out var index)) { _displayIndex = index; break; }
                }
            }
        }
        if (_displayIndex is not { } display) return null;
        var current = Sh.RunWithTimeout(3000, "m1ddc", "display", display.ToString(), "get", "volume")?.Trim();
        var maximum = Sh.RunWithTimeout(3000, "m1ddc", "display", display.ToString(), "max", "volume")?.Trim();
        return int.TryParse(current, out var value) && int.TryParse(maximum, out var max) && max > 0
            ? (int)Math.Round(Math.Clamp(value / (double)max, 0, 1) * 100) : null;
    }
}
