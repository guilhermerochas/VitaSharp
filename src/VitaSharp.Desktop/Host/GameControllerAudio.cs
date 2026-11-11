using System.Runtime.InteropServices;
using VitaSharp.Core.Host;
using VitaSharp.Core.Util;

namespace VitaSharp.Desktop.Host;

/// <summary>
/// Which audio outputs belong to a game controller, from the Windows device tree: an audio endpoint (device class
/// AudioEndpoint) whose container ID (the physical device it is part of) is also the container of a game controller
/// (<see cref="AudioOutputFollower.ControllerOutputs"/>). The name rule (<see cref="AudioOutputFollower.LooksLikeController"/>)
/// stays as the fallback, and is all there is on other systems.
/// </summary>
public static unsafe partial class GameControllerAudio
{
    /// <summary>"Is this output a game controller's" for <see cref="AudioOutputFollower"/>: the device rule (refreshed
    /// at most every 3 s, since it walks the whole device tree) or the name rule.</summary>
    public static Func<string, bool> Predicate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return AudioOutputFollower.LooksLikeController;
        }

        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        long at = long.MinValue;
        object gate = new();
        return name =>
        {
            lock (gate)
            {
                if (Environment.TickCount64 - at > 3000)
                {
                    set = Find();
                    at = Environment.TickCount64;
                }
                return set.Contains(AudioOutputFollower.FriendlyName(name)) || AudioOutputFollower.LooksLikeController(name);
            }
        };
    }

    /// <summary>Friendly names of the audio endpoints that are part of a game controller (empty on failure).</summary>
    public static HashSet<string> Find()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var outputs = new List<(string, Guid)>();
        var devices = new List<(Guid, IReadOnlyList<string>, string, string)>();
        try
        {
            nint set = SetupDiGetClassDevsW(0, null, 0, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == -1)
            {
                return [];
            }

            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    if (Property(set, ref data, ContainerId) is not { Length: 16 } c)
                    {
                        continue;
                    }

                    var container = new Guid(c);
                    string cls = Text(Property(set, ref data, DeviceClass));
                    if (cls.Equals("AudioEndpoint", StringComparison.OrdinalIgnoreCase))
                    {
                        string name = Text(Property(set, ref data, FriendlyName));
                        if (name.Length > 0)
                        {
                            outputs.Add((name, container));
                        }

                        continue;
                    }
                    var compat = Text(Property(set, ref data, CompatibleIds)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    devices.Add((container, compat, cls, Text(Property(set, ref data, Service))));
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"Audio: the device tree is not readable ({e.Message}); game controller outputs are told by name");
            return [];
        }
        return AudioOutputFollower.ControllerOutputs(outputs, devices);
    }

    static string Text(byte[]? b) => b == null ? "" : System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0');

    static byte[]? Property(nint set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        byte* buf = stackalloc byte[4096];
        if (!SetupDiGetDevicePropertyW(set, ref data, &key, out _, buf, 4096, out uint size, 0))
        {
            return null;
        }

        return new ReadOnlySpan<byte>(buf, (int)Math.Min(size, 4096)).ToArray();
    }

    const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    static readonly Guid DeviceProps = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    static readonly DEVPROPKEY ContainerId = new() { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
    static readonly DEVPROPKEY CompatibleIds = new() { fmtid = DeviceProps, pid = 4 };
    static readonly DEVPROPKEY Service = new() { fmtid = DeviceProps, pid = 6 };
    static readonly DEVPROPKEY DeviceClass = new() { fmtid = DeviceProps, pid = 9 };
    static readonly DEVPROPKEY FriendlyName = new() { fmtid = DeviceProps, pid = 14 };

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [LibraryImport("setupapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SetupDiGetClassDevsW(nint classGuid, string? enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(nint set, uint index, ref SP_DEVINFO_DATA data);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDevicePropertyW(nint set, ref SP_DEVINFO_DATA data, DEVPROPKEY* key, out uint type, byte* buffer, uint size, out uint required, uint flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint set);
}
