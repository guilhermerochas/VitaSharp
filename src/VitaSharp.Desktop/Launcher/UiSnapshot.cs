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
                try
                {
                    rc = args.Contains("--trophies") ? await TrophyScript(outDir)
                        : args.Any(a => a == "--home" || a.StartsWith("--livearea")) ? await HomeScript(outDir, args)
                        : await Script(outDir);
                }
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

    /// <summary><c>ui-snapshot &lt;out-dir&gt; --trophies</c>: the game list (trophy badges) and the trophy list of the
    /// first game with trophies.</summary>
    static async Task<int> TrophyScript(string outDir)
    {
        async Task Shot(Window w, string name)
        {
            await Task.Delay(500);
            var size = new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height);
            using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
            rtb.Render(w);
            var path = Path.Combine(outDir, name + ".png");
            rtb.Save(path);
            Console.WriteLine($"  wrote {path}");
        }
        var main = new MainWindow();
        main.Show();
        await Shot(main, "library");
        var game = GameLibrary.Scan(Program.Ux0).FirstOrDefault(g => TrophySetView.ForGame(g.Path, Program.FsRoot, 1).Count > 0);
        if (game == null) { Console.WriteLine("no game with trophies"); return 1; }
        var tw = new TrophyWindow(game, TrophySetView.ForGame(game.Path, Program.FsRoot, 1));
        tw.Show();
        await Shot(tw, "trophies");
        tw.Close();
        main.Close();
        return 0;
    }

    /// <summary>
    /// <c>ui-snapshot &lt;out-dir&gt; --home [--livearea[=ID1,ID2]]</c>: the launcher in "Tela inicial" view — the first
    /// home page, the next page and the selection ring — and the LiveArea of each listed title (default: every installed
    /// game). Settings are not saved; the clock shows 12:34.
    /// </summary>
    static async Task<int> HomeScript(string outDir, string[] args)
    {
        Shell.ShellCanvas.ClockOverride = new DateTime(2026, 10, 29, 12, 34, 0);
        var main = new MainWindow(new LauncherSettings { View = LauncherSettings.ViewHome }) { Width = 1200, Height = 780 };
        main.Show();
        int failures = 0;
        async Task Shot(string name, int delay = 600)
        {
            await Task.Delay(delay);
            var size = new PixelSize((int)main.Bounds.Width, (int)main.Bounds.Height);
            using var rtb = new RenderTargetBitmap(size, new Vector(96, 96));
            rtb.Render(main);
            var path = Path.Combine(outDir, name + ".png");
            rtb.Save(path);
            Console.WriteLine($"  wrote {path}");
        }
        var shell = main.ShellView;
        if (shell == null) { Console.WriteLine("home view missing"); return 1; }
        var assets = shell.Visuals.Assets;
        Console.WriteLine($"firmware: {(assets.HasFirmware ? "yes" : "no")}, theme: {shell.Visuals.Theme?.Title ?? "(default look)"}, " +
                          $"wallpaper: {(shell.Visuals.Wallpaper != null ? "yes" : "no")}, apps: {shell.Home.Apps.Count}, pages: {shell.Home.Pages}");
        if (assets.HasFirmware)
        {
            foreach (var (rco, id) in new[] { (Core.Shell.ShellAssets.TopMenuRco, Core.Shell.ShellAssets.Textures.PageDot), (Core.Shell.ShellAssets.IndicatorRco, Core.Shell.ShellAssets.Textures.Battery),
                         (Core.Shell.ShellAssets.IndicatorRco, Core.Shell.ShellAssets.Textures.Wifi) })
                if (assets.Texture(rco, id) == null) { Console.WriteLine($"  texture {id} of {rco}: MISSING"); failures++; }
            foreach (var e in Enum.GetValues<Core.Shell.ShellSoundEffect>())
            {
                var s = assets.Sound(e);
                Console.WriteLine($"  sound {e}: {(s == null ? "missing" : $"{s.Seconds:F2} s, {s.SampleRate} Hz, {s.Channels} ch")}");
            }
            Console.WriteLine($"  system BGM: {assets.SystemBgm ?? "none"}");
            if (assets.SystemBgm is { } bgm)
            {
                // decode the first seconds (not played) to check the ATRAC9 path
                var reader = Core.Shell.At9Reader.Open(bgm);
                var pcm = new List<short>();
                while (pcm.Count < reader.SampleRate * reader.Channels * 3 && reader.Read(pcm, out _)) { }
                int peak = pcm.Count == 0 ? 0 : pcm.Max(s => Math.Abs((int)s));
                Console.WriteLine($"  BGM decode: {reader.SampleRate} Hz, {reader.Channels} ch, {reader.TotalSamples / (double)reader.SampleRate:F1} s, first 3 s peak {peak}");
                if (peak == 0) failures++;
            }
        }
        await Shot("home_page1", 900);
        if (shell.Home.Pages > 1)
        {
            shell.Home.PageBy(1);
            await Shot("home_page2", 900);
            shell.Home.PageBy(-1);
        }
        if (shell.Home.Pages > 2)
        {
            shell.Home.GoToPage(shell.Home.Pages - 1, animate: false);
            await Shot("home_last_page");
            shell.Home.GoToPage(0, animate: false);
        }
        shell.Home.Move(1, 0);
        shell.Home.Move(0, 1);
        await Shot("home_selection");
        // a system app's LiveArea (vs0:app), whose Start explains that system apps do not run yet
        if (shell.Home.Apps.FirstOrDefault(a => a.IsSystem) is { } sys)
        {
            shell.OpenLiveArea(sys);
            shell.LiveArea!.Start();
            await Shot("livearea_system_" + sys.TitleId, 500);
            shell.CloseLiveArea(sound: false);
        }

        var wanted = args.FirstOrDefault(a => a.StartsWith("--livearea="))?["--livearea=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool all = wanted == null && args.Contains("--livearea");
        foreach (var app in shell.Home.Apps.Where(a => all ? !a.IsSystem : wanted?.Contains(a.TitleId, StringComparer.OrdinalIgnoreCase) == true))
        {
            shell.OpenLiveArea(app);
            var la = shell.LiveArea!;
            Console.WriteLine($"  {app.TitleId}: style {la.Template?.Style ?? "(none)"}{(la.Template?.IsDefault == true ? " (firmware default)" : "")}, " +
                              $"frames {la.Template?.Frames.Count ?? 0}, items {la.Template?.Frames.Sum(f => f.Items.Count) ?? 0}");
            await Shot("livearea_" + app.TitleId, 500);
            shell.CloseLiveArea(sound: false);
        }
        main.Close();
        Shell.ShellCanvas.ClockOverride = null;
        Console.WriteLine(failures == 0 ? "PASS" : $"{failures} problem(s)");
        return failures == 0 ? 0 : 1;
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
        // swap confirm (Cross on the pad lights Circle), bigger left deadzone (the stick at 0.2 stays centred),
        // inverted right stick Y (pushed up = dot down)
        pads.SetVirtual(v, axis: PadAxis.LeftX, value: 0.2f);
        pads.SetVirtual(v, axis: PadAxis.LeftY, value: 0f);
        pads.SetVirtual(v, axis: PadAxis.RightX, value: 0f);
        pads.SetVirtual(v, axis: PadAxis.RightY, value: -0.8f);
        panel.EditProfile(p => { p.SwapConfirm = true; p.LeftStick.Deadzone = 0.3f; p.RightStick.InvertY = true; });
        await Snap("swap_deadzone_inverty");
        panel.EditProfile(p => { p.SwapConfirm = false; p.LeftStick.Deadzone = StickSettings.DefaultDeadzone; p.RightStick.InvertY = false; });
        pads.SetVirtual(v, axis: PadAxis.RightY, value: 0f);
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
        dlg.ShowTab(SettingsDialog.GeneralTab);
        await Snap("general_tab");
        dlg.Firmware.BringIntoView();
        await Snap("general_firmware");
        dlg.Close();
        Console.WriteLine(failures == 0 ? "PASS" : $"{failures} problem(s)");
        return failures == 0 ? 0 : 1;
    }
}
