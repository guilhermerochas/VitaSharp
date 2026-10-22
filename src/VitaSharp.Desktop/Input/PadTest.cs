using VitaSharp.Core.Host;
using VitaSharp.Core.Input;

namespace VitaSharp.Desktop.Input;

/// <summary>
/// <c>pad-test [--seconds=N] [--virtual]</c>: lists the connected controllers (SDL), their family, mapping source and
/// profile, then prints the mapped Vita state whenever it changes. <c>--virtual</c> plugs in a simulated DualSense
/// through SDL's virtual joystick API (hot-plug, database mapping, buttons, sticks, unplug) and checks the results.
/// </summary>
static class PadTest
{
    public static int Run(string[] args)
    {
        double seconds = args.FirstOrDefault(a => a.StartsWith("--seconds=")) is { } s ? double.Parse(s[10..], System.Globalization.CultureInfo.InvariantCulture) : 5;
        using var pads = new SdlGamepads(Program.DataRoot);
        if (!pads.Available) { Console.WriteLine($"SDL unavailable: {pads.Error}"); return 1; }
        var store = InputProfileStore.Load(HostInput.ConfigPath(Program.DataRoot));
        pads.DevicesChanged += () => Console.WriteLine($"[hot-plug] {pads.Devices.Count} controller(s): {string.Join("; ", pads.Devices.Select(d => d.Label))}");
        pads.Flush();
        Console.WriteLine($"{pads.Devices.Count} controller(s) connected");
        foreach (var d in pads.Devices) Describe(d, store);
        return args.Contains("--virtual") ? Virtual(pads, store) : Watch(pads, store, seconds);
    }

    static void Describe(PadInfo d, InputProfileStore store)
    {
        var saved = store.FindSaved(d.Guid, d.Name) != null;
        Console.WriteLine($"  #{d.InstanceId} {d.Name} | family {d.Kind} | model {d.Model} | {(d.IsGamepad ? "database mapping" : "raw joystick")} | touch pad {(d.HasTouchpad ? "yes" : "no")} | guid {d.Guid} | {(saved ? "saved profile" : "default mapping")}");
    }

    static int Watch(SdlGamepads pads, InputProfileStore store, double seconds)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        var snap = new PadSnapshot();
        var last = new Dictionary<int, string>();
        while (DateTime.UtcNow < end)
        {
            foreach (var d in pads.Devices)
            {
                if (!pads.TryGetState(d.InstanceId, snap)) continue;
                var m = InputMapper.Map(new CompiledProfile(store.ProfileFor(d.Guid, d.Name, d.Kind, d.IsGamepad)), snap, null);
                var line = Format(m);
                if (last.TryGetValue(d.InstanceId, out var prev) && prev == line) continue;
                last[d.InstanceId] = line;
                Console.WriteLine($"  #{d.InstanceId}: {line}");
            }
            Thread.Sleep(16);
        }
        return 0;
    }

    static string Format(in MappedInput m)
        => $"buttons [{m.State.Buttons}] L ({m.State.Lx},{m.State.Ly}) R ({m.State.Rx},{m.State.Ry}) touch front {m.FrontCount} rear {m.BackCount}";

    static int Virtual(SdlGamepads pads, InputProfileStore store)
    {
        int failures = 0;
        void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failures++; }

        int before = pads.Devices.Count;
        var v = pads.AttachVirtual("DualSense Wireless Controller (simulated)", ControllerClassifier.VendorSony, 0x0CE6);
        if (v == null) { Console.WriteLine("could not attach a virtual controller"); return 1; }
        pads.Flush();
        var info = pads.Devices.FirstOrDefault(d => d.IsVirtual);
        Check(pads.Devices.Count == before + 1 && info != null, "hot-plug: virtual controller detected");
        if (info == null) return 1;
        Describe(info, store);
        Check(info.IsGamepad, "standard gamepad mapping from the SDL database");
        Check(info.Kind == ControllerKind.PlayStation, $"recognised as PlayStation (got {info.Kind})");

        var profile = new CompiledProfile(store.ProfileFor(info.Guid, info.Name, info.Kind, info.IsGamepad));
        var snap = new PadSnapshot();
        MappedInput Read() { pads.Flush(); pads.TryGetState(info.InstanceId, snap); return InputMapper.Map(profile, snap, null); }

        Check(Read().State.Equals(ControllerState.Neutral), "idle = neutral");
        pads.SetVirtual(v, PadButton.South, true);
        var m = Read();
        Check(m.State.Buttons == VitaButtons.Cross, $"bottom face button -> Cross ({m.State.Buttons})");
        pads.SetVirtual(v, PadButton.South, false);
        pads.SetVirtual(v, PadButton.East, true);
        m = Read();
        Check(m.State.Buttons == VitaButtons.Circle, $"right face button -> Circle ({m.State.Buttons})");
        pads.SetVirtual(v, PadButton.East, false);
        pads.SetVirtual(v, PadButton.DpadLeft, true);
        pads.SetVirtual(v, PadButton.Start, true);
        m = Read();
        Check(m.State.Buttons == (VitaButtons.Left | VitaButtons.Start), $"D-pad left + Start ({m.State.Buttons})");
        pads.SetVirtual(v, PadButton.DpadLeft, false);
        pads.SetVirtual(v, PadButton.Start, false);
        pads.SetVirtual(v, axis: PadAxis.LeftX, value: 1f);
        pads.SetVirtual(v, axis: PadAxis.RightY, value: -1f);
        m = Read();
        Check(m.State.Lx == 255 && m.State.Ry == 0 && m.State.Ly == 128, $"sticks: left right, right up ({Format(m)})");
        pads.SetVirtual(v, axis: PadAxis.LeftX, value: 0.05f); // inside the default deadzone
        pads.SetVirtual(v, axis: PadAxis.RightY, value: 0f);
        pads.SetVirtual(v, axis: PadAxis.RightTrigger, value: 1f);
        m = Read();
        Check(m.State.Lx == 128 && m.State.Buttons == VitaButtons.RTrigger, $"deadzone + R2 -> R ({Format(m)})");
        pads.SetVirtual(v, axis: PadAxis.RightTrigger, value: 0f);
        Check(Read().State.Equals(ControllerState.Neutral), "released = neutral");

        // the game window's path: keyboard (default keys) + every controller through HostInput
        using (var host = new HostInput(Program.DataRoot, pads: pads))
        {
            host.KeyChanged("Space", true);
            host.KeyChanged("W", true);
            pads.SetVirtual(v, PadButton.North, true);
            pads.SetVirtual(v, axis: PadAxis.RightX, value: -1f);
            pads.Flush();
            var h = host.Poll();
            Check(h.State.Buttons == (VitaButtons.Cross | VitaButtons.Triangle) && h.State.Ly == 0 && h.State.Rx == 0,
                $"HostInput: Space + W (keyboard) + Triangle + right stick left (pad) ({Format(h)})");
            host.KeyChanged("Space", false);
            host.KeyChanged("W", false);
            pads.SetVirtual(v, PadButton.North, false);
            pads.SetVirtual(v, axis: PadAxis.RightX, value: 0f);
            pads.Flush();
            Check(host.Poll().State.Equals(ControllerState.Neutral), "HostInput: released = neutral");
        }

        pads.DetachVirtual(v);
        pads.Flush();
        Check(pads.Devices.Count == before, "hot-unplug: virtual controller removed");
        Console.WriteLine(failures == 0 ? "PASS" : $"{failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }
}
