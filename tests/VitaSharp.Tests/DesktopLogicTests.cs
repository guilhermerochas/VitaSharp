using VitaSharp.Desktop.Launcher;

namespace VitaSharp.Tests;

/// <summary>
/// The launcher's logic outside the UI (VitaSharp.Desktop): the command line a game is started with from the
/// settings and its per-game options, the library scan of ux0:app (param.sfo title, version, category, region from
/// the title ID) and the key names bindings are stored with (physical positions, Silk.NET names).
/// </summary>
public sealed class DesktopLogicTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-desktop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void RunOptionsFollowTheSettings()
    {
        var s = new LauncherSettings();
        var opts = s.RunOptions().ToList();
        // the defaults: no window or camera options, the shader and PSN switches spelled out
        Assert.DoesNotContain("--fullscreen", opts);
        Assert.Contains("--shaderc-opt=zero", opts);
        Assert.Contains("--async-shaders=on", opts);
        Assert.Contains("--trophy-sound=on", opts);
        Assert.Contains("--psn=off", opts);
        Assert.Contains("--psn-local=on", opts);
        Assert.Contains("--net=off", opts);
        s.Fullscreen = true;
        s.Scale = 2;
        s.VSync = false;
        s.Validation = true;
        s.CameraFront = "none";
        s.CpuBackend = "interp";
        s.CpuMhz = 444;
        s.FastShaderCompile = false;
        s.BackgroundShaderCompile = false;
        s.DebugMode = true;
        s.CapturePreRoll = true;
        s.TrophySound = false;
        s.PsnSignedIn = true;
        s.PsnLocalServices = false;
        s.RealInternet = true;
        s.AudioDevice = "Speakers";
        s.HleModules = "libc, libm;libfios2";
        opts = s.RunOptions().ToList();
        foreach (var o in new[] { "--fullscreen", "--scale=2", "--no-vsync", "--validation", "--camera-front=none", "--cpu=interp", "--cpu-mhz=444",
            "--shaderc-opt=perf", "--async-shaders=off", "--debug-mode", "--capture-preroll", "--trophy-sound=off", "--psn=on", "--psn-local=off",
            "--net=real", "--audio-device=Speakers", "--hle-modules=libc,libm,libfios2" })
        {
            Assert.Contains(o, opts);
        }

        Assert.DoesNotContain(opts, o => o.StartsWith("--rpcn", StringComparison.Ordinal)); // no RPCN account configured

        // a game's own options come first, and only for that game
        s.Games["PCSA00001"] = new GameOptions { ResScale = 2, FrameRate = "30" };
        Assert.Equal(["--res-scale=2", "--frame-rate=30"], s.RunOptions("PCSA00001").Take(2).ToArray());
        Assert.DoesNotContain("--res-scale=2", s.RunOptions("PCSB00002"));
        Assert.False(s.Games["PCSA00001"].IsDefault);
        Assert.True(new GameOptions().IsDefault);
        Assert.Empty(new GameOptions { ResScale = 7, FrameRate = "native" }.RunOptions());
        Assert.Equal(4, GameOptions.ResScales.Length);
        Assert.Equal(4, GameOptions.FrameRates.Length);

        // the old single camera setting moves to both cameras
        var old = new LauncherSettings { Camera = "Webcam X" };
        old.MigrateCamera();
        Assert.Equal(("Webcam X", "Webcam X", ""), (old.CameraFront, old.CameraBack, old.Camera));
    }

    static void WriteSfo(string path, params (string Key, string Value)[] entries)
    {
        // a PSF file: header, index table, key table, data table (UTF-8 strings)
        var keys = new MemoryStream();
        var data = new MemoryStream();
        var index = new List<(int KeyOff, int DataOff, int Len, int Max)>();
        foreach (var (k, v) in entries)
        {
            int ko = (int)keys.Length;
            keys.Write(System.Text.Encoding.ASCII.GetBytes(k + "\0"));
            var bytes = System.Text.Encoding.UTF8.GetBytes(v + "\0");
            int max = (bytes.Length + 3) & ~3;
            int doff = (int)data.Length;
            data.Write(bytes);
            data.Write(new byte[max - bytes.Length]);
            index.Add((ko, doff, bytes.Length, max));
        }
        while (keys.Length % 4 != 0)
        {
            keys.WriteByte(0);
        }

        int keyStart = 20 + index.Count * 16, dataStart = keyStart + (int)keys.Length;
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x46535000u);
        w.Write(0x00000101u);
        w.Write(keyStart);
        w.Write(dataStart);
        w.Write(index.Count);
        foreach (var (ko, doff, len, max) in index)
        {
            w.Write((ushort)ko);
            w.Write((ushort)0x0204);
            w.Write(len);
            w.Write(max);
            w.Write(doff);
        }
        w.Write(keys.ToArray());
        w.Write(data.ToArray());
    }

    [Fact]
    public void LibraryScanReadsParamSfo()
    {
        string ux0 = Path.Combine(_root, "ux0");
        string game = Path.Combine(ux0, "app", "PCSB00123"), homebrew = Path.Combine(ux0, "app", "VSHB00001"), broken = Path.Combine(ux0, "app", "NOEBOOT00");
        foreach (var d in new[] { game, homebrew, broken })
        {
            Directory.CreateDirectory(Path.Combine(d, "sce_sys"));
        }

        File.WriteAllBytes(Path.Combine(game, "eboot.bin"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(homebrew, "eboot.bin"), new byte[16]);
        WriteSfo(Path.Combine(game, "sce_sys", "param.sfo"), ("APP_VER", "01.05"), ("CATEGORY", "gd"), ("TITLE", "Zeta\nGame"), ("TITLE_ID", "PCSB00123"), ("CONTENT_ID", "EP0000-PCSB00123_00-0000000000000000"));
        var list = GameLibrary.Scan(ux0);
        Assert.Equal(2, list.Count); // a folder without eboot.bin is no title
        var g = list.Single(e => e.TitleId == "PCSB00123");
        Assert.Equal("Zeta Game", g.Title);
        Assert.Equal("1.05", g.Version);
        Assert.Equal("gd", g.Category);
        Assert.Equal("Game", g.CategoryText);
        Assert.True(g.SizeBytes >= 2048);
        Assert.Equal("ZG", g.Initials);
        Assert.Null(g.IconPath);
        Assert.Null(g.Pic0Path);
        var h = list.Single(e => e.TitleId == "VSHB00001"); // no param.sfo: the folder name
        Assert.Equal("VSHB00001", h.Title);
        Assert.Equal(VitaSharp.Desktop.Launcher.Ui.Strings.RegionEurope, g.Region); // the content ID's region letter
        Assert.Equal(VitaSharp.Desktop.Launcher.Ui.Strings.RegionHomebrew, h.Region); // no content ID
        Assert.EndsWith("KB", new GameEntry("X", "X", "", "gp", "", null, 4096).SizeText);
        Assert.EndsWith("MB", new GameEntry("X", "X", "", "ac", "", null, 5L << 20).SizeText);
        Assert.EndsWith("GB", new GameEntry("X", "X", "", "gd", "", null, 3L << 30).SizeText);
        Assert.Equal("Game patch", new GameEntry("X", "X", "", "gp", "", null, 0).CategoryText);
        Assert.Equal("Add-on", new GameEntry("X", "X", "", "ac", "", null, 0).CategoryText);
        Assert.Empty(GameLibrary.Scan(Path.Combine(_root, "nothing")));
    }

    [Fact]
    public void KeyNamesArePhysicalPositions()
    {
        Assert.Equal("A", KeyNames.FromAvalonia(Avalonia.Input.PhysicalKey.A, Avalonia.Input.Key.Q)); // AZERTY: the A position
        Assert.Equal("Number3", KeyNames.FromAvalonia(Avalonia.Input.PhysicalKey.Digit3, Avalonia.Input.Key.None));
        Assert.Equal("Keypad7", KeyNames.FromAvalonia(Avalonia.Input.PhysicalKey.NumPad7, Avalonia.Input.Key.None));
        Assert.Equal("Up", KeyNames.FromAvalonia(Avalonia.Input.PhysicalKey.ArrowUp, Avalonia.Input.Key.None));
        Assert.Equal("Apostrophe", KeyNames.FromAvalonia(Avalonia.Input.PhysicalKey.Quote, Avalonia.Input.Key.None));
        Assert.Equal("ShiftLeft", KeyNames.FromAvalonia(Avalonia.Input.PhysicalKey.ShiftLeft, Avalonia.Input.Key.None));
        // without a physical key the logical one is used
        Assert.Equal("Z", KeyNames.FromAvalonia(Avalonia.Input.PhysicalKey.None, Avalonia.Input.Key.Z));
        Assert.Equal("Number0", KeyNames.FromAvalonia(Avalonia.Input.PhysicalKey.None, Avalonia.Input.Key.D0));
        Assert.Equal("Backspace", KeyNames.FromAvalonia(Avalonia.Input.PhysicalKey.None, Avalonia.Input.Key.Back));
        Assert.Equal("ControlRight", KeyNames.FromAvalonia(Avalonia.Input.PhysicalKey.None, Avalonia.Input.Key.RightCtrl));
        Assert.Null(KeyNames.FromAvalonia(Avalonia.Input.PhysicalKey.None, Avalonia.Input.Key.None));
    }
}
