using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VitaSharp.Core.Input;
using VitaSharp.Desktop.Input;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// <c>ui-snapshot &lt;out-dir&gt;</c> (use with a scratch --data folder): opens Settings > Controls, drives it with a
/// simulated DualSense (SDL virtual controller: hot-plug, buttons, sticks, triggers, rebinding, unplug) and simulated
/// key presses, and saves a PNG of the window after each step. Also checks that the launcher's key names exist in
/// the game window's key set.
/// </summary>
static class UiSnapshot
{
    public static int Run(string[] args)
    {
        string outDir = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "ui-snapshots");
        Directory.CreateDirectory(outDir);
        int rc = 0;
        LauncherApp.StartupOverride = desktop =>
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.UIThread.Post(async () =>
            {
                try { rc = await Script(outDir); }
                catch (Exception e) { Console.WriteLine("ui-snapshot failed: " + e); rc = 1; }
                desktop.Shutdown(rc);
            });
        };
        LauncherApp.Run([]);
        return rc;
    }

    static int CheckKeyNames()
    {
        int bad = 0;
        foreach (var pk in Enum.GetValues<PhysicalKey>())
        {
            var name = KeyNames.FromAvalonia(pk, Key.None);
            if (name != null && !Enum.TryParse<Silk.NET.Input.Key>(name, out _)) { Console.WriteLine($"  key name {pk} -> {name} is not a Silk.NET key"); bad++; }
        }
        foreach (var k in Enum.GetValues<Key>())
        {
            var name = KeyNames.FromAvalonia(PhysicalKey.None, k);
            if (name != null && !Enum.TryParse<Silk.NET.Input.Key>(name, out _)) { Console.WriteLine($"  key name {k} -> {name} is not a Silk.NET key"); bad++; }
        }
        foreach (var c in VitaControls.All)
            foreach (var b in DefaultMappings.Keyboard().Get(c))
                if (!Enum.TryParse<Silk.NET.Input.Key>(b.Key, out _)) { Console.WriteLine($"  default key {b.Key} is not a Silk.NET key"); bad++; }
        Console.WriteLine(bad == 0 ? "key names: ok" : $"key names: {bad} problem(s)");
        return bad;
    }

    static async Task<int> Script(string outDir)
    {
        int failures = CheckKeyNames();
        using var pads = new SdlGamepads(Program.DataRoot);
        var dlg = new SettingsDialog(new LauncherSettings(), SettingsDialog.ControlsTab, pads) { Width = 1120, Height = 760, WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen };
        dlg.Show();
        await Task.Delay(400);
        var panel = dlg.Controls!;
        int shot = 0;

        async Task Snap(string name)
        {
            pads.Flush();
            panel.Pump();
            await Task.Delay(120);
            panel.Pump();
            await Task.Delay(60);
            var size = new PixelSize((int)dlg.Bounds.Width, (int)dlg.Bounds.Height);
            using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
            rtb.Render(dlg);
            var path = Path.Combine(outDir, $"{++shot:00}_{name}.png");
            rtb.Save(path);
            Console.WriteLine($"  wrote {path}");
        }

        void Key(Key k, PhysicalKey pk, bool down) => dlg.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = down ? InputElement.KeyDownEvent : InputElement.KeyUpEvent, Key = k, PhysicalKey = pk, Source = dlg,
        });

        await Snap("keyboard_idle");

        // hot-plug: a simulated DualSense appears and is selected automatically
        var v = pads.AttachVirtual("DualSense Wireless Controller", ControllerClassifier.VendorSony, 0x0CE6);
        if (v == null) { Console.WriteLine("SDL virtual controller unavailable"); return 1; }
        await Snap("dualsense_connected");

        // buttons light up, sticks move (left outside the deadzone, right inside it), R2 presses R
        pads.SetVirtual(v, PadButton.South, true);
        pads.SetVirtual(v, PadButton.DpadUp, true);
        pads.SetVirtual(v, PadButton.LeftShoulder, true);
        pads.SetVirtual(v, axis: PadAxis.LeftX, value: 0.7f);
        pads.SetVirtual(v, axis: PadAxis.LeftY, value: -0.55f);
        pads.SetVirtual(v, axis: PadAxis.RightX, value: 0.07f);
        pads.SetVirtual(v, axis: PadAxis.RightTrigger, value: 1f);
        await Snap("dualsense_pressed");
        pads.SetVirtual(v, PadButton.South, false);
        pads.SetVirtual(v, PadButton.DpadUp, false);
        pads.SetVirtual(v, PadButton.LeftShoulder, false);
        pads.SetVirtual(v, axis: PadAxis.LeftX, value: 0);
        pads.SetVirtual(v, axis: PadAxis.LeftY, value: 0);
        pads.SetVirtual(v, axis: PadAxis.RightX, value: 0);
        pads.SetVirtual(v, axis: PadAxis.RightTrigger, value: 0);
        pads.Flush();
        panel.Pump();

        // manual remap: click Triangle, press the controller's Share button
        panel.BeginListen(VitaControl.Triangle);
        await Snap("listening_triangle");
        pads.SetVirtual(v, PadButton.Back, true);
        await Snap("triangle_rebound");
        pads.SetVirtual(v, PadButton.Back, false);
        pads.Flush();
        panel.Pump();
        // stick sequence: the left stick's up direction from the right stick pushed up
        panel.BeginListen(VitaControl.LStickUp);
        pads.SetVirtual(v, axis: PadAxis.RightY, value: -1f);
        await Snap("left_stick_up_from_right_stick");
        pads.SetVirtual(v, axis: PadAxis.RightY, value: 0f);
        pads.Flush();
        panel.Pump();

        // the keyboard with the same UI: arrows + Space + WASD, and a key bound to the front touch point
        if (!panel.SelectDevice(ControllerProfile.KeyboardId)) { Console.WriteLine("keyboard not selectable"); failures++; }
        panel.BeginListen(VitaControl.TouchFront);
        Key(Avalonia.Input.Key.T, PhysicalKey.T, true);
        Key(Avalonia.Input.Key.T, PhysicalKey.T, false);
        Key(Avalonia.Input.Key.Space, PhysicalKey.Space, true);
        Key(Avalonia.Input.Key.Right, PhysicalKey.ArrowRight, true);
        Key(Avalonia.Input.Key.W, PhysicalKey.W, true);
        Key(Avalonia.Input.Key.D, PhysicalKey.D, true);
        Key(Avalonia.Input.Key.L, PhysicalKey.L, true);
        Key(Avalonia.Input.Key.T, PhysicalKey.T, true);
        await Snap("keyboard_pressed");
        foreach (var (k, pk) in new[] { (Avalonia.Input.Key.Space, PhysicalKey.Space), (Avalonia.Input.Key.Right, PhysicalKey.ArrowRight), (Avalonia.Input.Key.W, PhysicalKey.W),
                     (Avalonia.Input.Key.D, PhysicalKey.D), (Avalonia.Input.Key.L, PhysicalKey.L), (Avalonia.Input.Key.T, PhysicalKey.T) })
            Key(k, pk, false);

        // back to the controller, then unplug it: the list falls back to the keyboard
        panel.SelectDevice(pads.Devices.FirstOrDefault(d => d.IsVirtual)?.Guid ?? "");
        await Snap("dualsense_profile_edited");
        panel.Save();
        var json = File.ReadAllText(HostInput.ConfigPath(Program.DataRoot));
        bool saved = json.Contains("\"Triangle\"") && json.Contains("pad:back") && json.Contains("key:T");
        Console.WriteLine($"input.json saved ({json.Length} bytes): {(saved ? "ok" : "MISSING BINDINGS")}");
        if (!saved) failures++;
        pads.DetachVirtual(v);
        await Snap("dualsense_unplugged");

        // the saved profile is loaded again for the same controller
        var store = InputProfileStore.Load(HostInput.ConfigPath(Program.DataRoot));
        var info = new { Guid = store.Config.Controllers.FirstOrDefault()?.Id ?? "" };
        var reloaded = store.ProfileFor(info.Guid, "DualSense Wireless Controller", ControllerKind.PlayStation, true);
        bool roundTrip = reloaded.Get(VitaControl.Triangle).SequenceEqual([InputBinding.Of(PadButton.Back)]);
        Console.WriteLine($"profile reload: {(roundTrip ? "ok" : "FAILED")}");
        if (!roundTrip) failures++;
        dlg.Close();
        Console.WriteLine(failures == 0 ? "PASS" : $"{failures} problem(s)");
        return failures == 0 ? 0 : 1;
    }
}
