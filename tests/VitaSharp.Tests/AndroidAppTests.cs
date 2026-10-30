using VitaSharp.Android.Input;
using VitaSharp.Android.Library;
using VitaSharp.Android.Logging;
using VitaSharp.Android.Settings;
using VitaSharp.Android.Storage;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>The platform-neutral layers of the Android app (src/VitaSharp.Android.Common): logcat formatting, settings,
/// library and trophies, on-screen pad and gamepad mapping.</summary>
public sealed class AndroidAppTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-android-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }

    // ---------------------------------------------------------------- logcat

    [Theory]
    [InlineData(LogLevel.Trace, LogcatPriority.Verbose)]
    [InlineData(LogLevel.Debug, LogcatPriority.Debug)]
    [InlineData(LogLevel.Info, LogcatPriority.Info)]
    [InlineData(LogLevel.Warn, LogcatPriority.Warn)]
    [InlineData(LogLevel.Error, LogcatPriority.Error)]
    public void LogcatPriorityFollowsLevel(LogLevel level, LogcatPriority expected)
    {
        Assert.Equal(expected, LogcatFormat.Priority(level));
        Assert.Equal((int)expected, (int)LogcatFormat.Format(level, "t", "x").Single().Priority);
    }

    [Fact]
    public void LogcatTagsAndText()
    {
        var e = LogcatFormat.Format(LogLevel.Info, "gpu", "Swapchain 2400x1080").Single();
        Assert.Equal(("VitaSharp", "[gpu] Swapchain 2400x1080"), (e.Tag, e.Text));

        // guest output: own tag, no thread prefix, the plain prefixes removed
        e = LogcatFormat.Format(LogLevel.Info, "main", "[guest] hello from the game").Single();
        Assert.Equal(("VitaSharp-Guest", "hello from the game"), (e.Tag, e.Text));
        e = LogcatFormat.Format(LogLevel.Info, "main", "[guest stdout] line").Single();
        Assert.Equal(("VitaSharp-Guest", "line"), (e.Tag, e.Text));
        e = LogcatFormat.Format(LogLevel.Info, "main", "[guest stderr] oops").Single();
        Assert.Equal(("VitaSharp-Guest", "[guest stderr] oops"), (e.Tag, e.Text));
        e = LogcatFormat.Format(LogLevel.Error, "main", "[guest] sceClibAbort called").Single();
        Assert.Equal((LogcatPriority.Error, "VitaSharp-Guest"), (e.Priority, e.Tag));
        Assert.Equal("VitaSharp", LogcatFormat.Route("[guestbook] not guest output").Tag);
    }

    [Fact]
    public void LogcatSplitsLinesAndLongMessages()
    {
        var lines = LogcatFormat.Format(LogLevel.Error, "t", "Exception: boom\r\n   at A()\n   at B()").ToList();
        Assert.Equal(["[t] Exception: boom", "[t]    at A()", "[t]    at B()"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Equal(LogcatPriority.Error, l.Priority));

        // 10,000 characters with 3-byte and 4-byte (surrogate pair) characters: every chunk fits, nothing is lost
        string big = string.Concat(Enumerable.Repeat("ab€😀", 2000));
        var chunks = LogcatFormat.Format(LogLevel.Info, "w", big).ToList();
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(System.Text.Encoding.UTF8.GetByteCount(c.Text) <= LogcatFormat.MaxEntryBytes));
        Assert.Equal(big, string.Concat(chunks.Select(c => c.Text["[w] ".Length..])));
        Assert.All(chunks, c => Assert.False(char.IsHighSurrogate(c.Text[^1])));

        Assert.Equal("[t] ", LogcatFormat.Format(LogLevel.Info, "t", "").Single().Text);
    }

    sealed class ListSink : ILogSink
    {
        public readonly List<(LogLevel, string, string)> Lines = new();
        public void Write(LogLevel level, string thread, string message) { lock (Lines) Lines.Add((level, thread, message)); }
    }

    [Fact]
    public void LogSinkReceivesWrittenLines()
    {
        var sink = new ListSink();
        var prevLevel = Log.Level;
        Log.Sink = sink;
        try
        {
            Log.Level = LogLevel.Info;
            Log.Debug("filtered out");
            Log.Warn("android-sink-test warning");
            Assert.Contains(sink.Lines, l => l.Item1 == LogLevel.Warn && l.Item3 == "android-sink-test warning");
            Assert.DoesNotContain(sink.Lines, l => l.Item3 == "filtered out");
        }
        finally
        {
            Log.Sink = null;
            Log.Level = prevLevel;
        }
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void GameSettingsOverrideGlobalOnes()
    {
        var app = new AppPreferences { CpuBackend = "interp", CpuMhz = 444, GpuProfile = "adreno", VSync = true, ShowFps = false };
        var e = EffectiveSettings.Resolve(app, null);
        Assert.Equal(("interp", 444, "adreno", true, false), (e.CpuBackend, e.CpuMhz, e.GpuProfile, e.VSync, e.ShowFps));
        Assert.Equal(444_000_000L, e.ClockHz);

        var game = new GameSettings { CpuMhz = 0, GpuProfile = "", ShowFps = true };
        e = EffectiveSettings.Resolve(app, game);
        Assert.Equal(("interp", 0, "", true, true), (e.CpuBackend, e.CpuMhz, e.GpuProfile, e.VSync, e.ShowFps));
        Assert.Equal(VitaSharp.Core.Cpu.CpuCore.DefaultClockHz, e.ClockHz);

        // invalid values fall back to the defaults instead of reaching the emulator
        e = EffectiveSettings.Resolve(new AppPreferences { CpuBackend = "nope", CpuMhz = 999, GpuProfile = "voodoo" }, null);
        Assert.Equal(("", 0, ""), (e.CpuBackend, e.CpuMhz, e.GpuProfile));
    }

    [Fact]
    public void SettingsRoundTripThroughJson()
    {
        var data = new DataLayout(_root);
        var app = new AppPreferences { CpuMhz = 444, GpuProfile = "mali", OnScreenOpacity = 0.3f, LastPlayed = "PCSC00045" };
        app.Network.RealInternet = true;
        app.Network.RpcnServer = "rpcn.example:31313";
        app.Save(data.AppSettingsFile);
        var back = AppPreferences.Load(data.AppSettingsFile);
        Assert.Equal((444, "mali", 0.3f, "PCSC00045", true, "rpcn.example:31313"),
            (back.CpuMhz, back.GpuProfile, back.OnScreenOpacity, back.LastPlayed, back.Network.RealInternet, back.Network.RpcnServer));

        var g = new GameSettings { CpuBackend = "native" };
        g.RecordSession(TimeSpan.FromMinutes(10), 18_000, new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc));
        g.Save(data.GameSettingsFile("PCSC00045"));
        var gb = GameSettings.Load(data.GameSettingsFile("PCSC00045"));
        Assert.Equal(("native", (int?)null, 600L, 30.0), (gb.CpuBackend, gb.CpuMhz, gb.PlaySeconds, gb.AverageFps));
        Assert.True(gb.HasOverrides);
        Assert.DoesNotContain("CpuMhz", File.ReadAllText(data.GameSettingsFile("PCSC00045"))); // nulls are not written

        // missing or broken files give the defaults
        File.WriteAllText(data.AppSettingsFile, "{ not json");
        Assert.Equal(0, AppPreferences.Load(data.AppSettingsFile).CpuMhz);
        Assert.False(GameSettings.Load(Path.Combine(_root, "none.json")).HasOverrides);
        Assert.EndsWith(Path.Combine("games", "_.json"), data.GameSettingsFile("../../.."));
    }

    [Fact]
    public void PerGameChoicesStartWithTheGlobalDefault()
    {
        var c = SettingsChoices.WithDefault(SettingsChoices.Clocks, "444");
        Assert.Equal(SettingsChoices.DefaultValue, c[0].Value);
        Assert.Equal("Padrão (444 MHz (carregamentos mais rápidos))", c[0].Label);
        Assert.Equal(3, c.Count);
        Assert.Contains(CpuChoices.Available(), x => x.Value == "interp");
        Assert.Equal("", CpuChoices.Available()[0].Value);
    }

    // ---------------------------------------------------------------- library and trophies

    string InstallFakeGame(DataLayout data, string titleId, string title, bool withTrophies)
    {
        string dir = Path.Combine(data.Apps, titleId);
        Directory.CreateDirectory(Path.Combine(dir, "sce_sys"));
        File.WriteAllBytes(Path.Combine(dir, "eboot.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(dir, "sce_sys", "param.sfo"), SfoWriter.Make(titleId, "gd", title, appVer: "01.12"));
        if (withTrophies)
        {
            string tdir = Path.Combine(dir, "sce_sys", "trophy", "NPWR00001_00");
            Directory.CreateDirectory(tdir);
            File.WriteAllBytes(Path.Combine(tdir, "TROPHY.TRP"), NpLocalTests.BuildTrp(("TROP.SFM", NpLocalTests.Sfm("Game", "First"))));
        }
        return dir;
    }

    [Fact]
    public void LibraryScansInstalledGamesWithTrophyProgress()
    {
        var data = new DataLayout(_root);
        data.EnsureCreated();
        InstallFakeGame(data, "PCSC00045", "Killzone Mercenary", true);
        InstallFakeGame(data, "PCSA00029", "Uncharted: Golden Abyss", false);
        Directory.CreateDirectory(Path.Combine(data.Apps, "NOTAGAME")); // no eboot.bin: skipped

        var games = GameLibrary.Scan(data);
        Assert.Equal(["Killzone Mercenary", "Uncharted: Golden Abyss"], games.Select(g => g.Title));
        var kz = games[0];
        Assert.Equal(("PCSC00045", "1.12", "KM"), (kz.TitleId, kz.Version, kz.Initials));
        Assert.StartsWith("PCSC00045 · v1.12 · ", kz.Subtitle);

        Assert.Equal("Sem troféus", GameLibrary.Trophies(games[1], data).Text);
        var none = GameLibrary.Trophies(kz, data);
        Assert.Equal(("0/3", 0), (none.Text, none.Percent));

        TrophyUnlocks.Save(TrophyUnlocks.PathFor(data.Fs, "NPWR00001_00"), new() { [1] = 63_900_000_000_000_000UL, [2] = 63_900_000_100_000_000UL });
        var p = GameLibrary.Trophies(kz, data);
        Assert.Equal(("2/3", 66), (p.Text, p.Percent));
        Assert.Equal((1, 1), p.ByGrade[TrophyGrade.Gold]);
        Assert.Equal((0, 1), p.ByGrade[TrophyGrade.Platinum]);

        var list = TrophyList.Load(kz, data);
        Assert.Equal(3, list.Rows.Count);
        Assert.Equal([2, 1], list.Filtered(TrophyList.Filter.Earned).Select(r => r.Id)); // most recent first
        var missing = Assert.Single(list.Filtered(TrophyList.Filter.Missing));
        Assert.Equal((0, "All"), (missing.Id, missing.ShownName));
        var hidden = list.Rows.Single(r => r.Id == 1);
        Assert.Equal("First", hidden.ShownName); // hidden but unlocked: shown

        var settings = new Dictionary<string, GameSettings> { ["PCSA00029"] = new() { LastPlayedUtc = DateTime.UtcNow } };
        GameSettings S(GameEntry g) => settings.GetValueOrDefault(g.TitleId) ?? new();
        Assert.Equal(["PCSA00029"], GameLibrary.Query(games, "", GameLibrary.Filter.Recent, S).Select(g => g.TitleId));
        Assert.Equal(["PCSC00045"], GameLibrary.Query(games, "killz", GameLibrary.Filter.All, S).Select(g => g.TitleId));
        Assert.Equal(2, GameLibrary.Query(games, "", GameLibrary.Filter.Playable, S).Count);
    }

    [Theory]
    [InlineData("Killzone Mercenary", "KM")]
    [InlineData("Resistance: Burning Skies", "RBS")]
    [InlineData("Uncharted: Golden Abyss", "UGA")]
    [InlineData("Tearaway", "TE")]
    [InlineData("LittleBigPlanet™ PS Vita", "LPV")]
    [InlineData("Persona 4 Golden", "P4G")]
    public void InitialsOfTitles(string title, string expected) => Assert.Equal(expected, GameLibrary.Initials(title));

    [Fact]
    public void PlayTimeAndLastPlayedTexts()
    {
        Assert.Equal("12h 40m", GameLibrary.PlayTime(12 * 3600 + 40 * 60));
        Assert.Equal("5m", GameLibrary.PlayTime(300));
        Assert.Equal("—", GameLibrary.PlayTime(0));
        var now = new DateTime(2026, 10, 30, 15, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal("Hoje", GameLibrary.LastPlayed(now.AddHours(-1), now));
        Assert.Equal("Ontem", GameLibrary.LastPlayed(now.AddDays(-1), now));
        Assert.Equal("há 3 dias", GameLibrary.LastPlayed(now.AddDays(-3), now));
        Assert.Equal("Nunca", GameLibrary.LastPlayed(null, now));
    }

    // ---------------------------------------------------------------- input

    static (int, float, float)[] Finger(OnScreenControl c, float fx = 0.5f, float fy = 0.5f, int id = 1) =>
        [(id, c.X + c.Width * fx, c.Y + c.Height * fy)];

    static void Update(OnScreenPad pad, params (int, float, float)[] fingers) => pad.Update(fingers);

    [Fact]
    public void OnScreenButtonsSticksAndFrontTouch()
    {
        var pad = new OnScreenPad();
        var cross = pad.Layout.Single(c => c.Control == VitaControl.Cross);
        var lstick = pad.Layout.Single(c => c.Control == VitaControl.LStickUp);
        Update(pad, Finger(cross));
        Assert.Equal(VitaButtons.Cross, pad.GetController(0).Buttons);
        Assert.True(pad.IsPressed(cross));

        // stick pushed right; the finger keeps the stick even when it leaves the ring
        Update(pad, Finger(lstick, 0.99f, 0.5f, 2));
        Assert.True(pad.GetController(0).Lx > 240);
        Update(pad, (2, lstick.X + lstick.Width * 3, lstick.Y + lstick.Height / 2));
        Assert.Equal(((byte)255, (byte)128), (pad.GetController(0).Lx, pad.GetController(0).Ly));
        Assert.Equal(1f, pad.Knob(lstick).X, 3);

        // a finger on the image is a front touch, normalised to the image
        var im = pad.Image;
        Update(pad, (3, im.X + im.Width / 4, im.Y + im.Height / 2));
        var t = Assert.Single(pad.GetTouches(false));
        Assert.Equal((0.25f, 0.5f), (t.X, t.Y), new FloatPairComparer());
        Assert.Empty(pad.GetTouches(true));

        // ... and stays one when it slides over a button
        Update(pad, (3, cross.X + cross.Width / 2, cross.Y + cross.Height / 2));
        Assert.Equal(VitaButtons.None, pad.GetController(0).Buttons);
        pad.Reset();
        Assert.Equal(ControllerState.Neutral.Lx, pad.GetController(0).Lx);
    }

    sealed class FloatPairComparer : IEqualityComparer<(float, float)>
    {
        public bool Equals((float, float) a, (float, float) b) => Math.Abs(a.Item1 - b.Item1) < 1e-4 && Math.Abs(a.Item2 - b.Item2) < 1e-4;
        public int GetHashCode((float, float) obj) => 0;
    }

    [Fact]
    public void RearTouchToggleAndPsButton()
    {
        var pad = new OnScreenPad();
        var back = pad.Layout.Single(c => c.Control == VitaControl.TouchBack);
        var ps = pad.Layout.Single(c => c.Control == VitaControl.PS);
        int psPresses = 0;
        pad.PsPressed += () => psPresses++;

        Update(pad, Finger(back));
        Update(pad);
        Assert.True(pad.RearTouch); // a tap switches it on, it stays on
        Assert.True(pad.IsPressed(back));
        var im = pad.Image;
        Update(pad, (5, im.X + im.Width / 2, im.Y + im.Height / 2));
        Assert.Single(pad.GetTouches(true));
        Assert.Empty(pad.GetTouches(false));
        Update(pad);
        Update(pad, Finger(back));
        Update(pad);
        Assert.False(pad.RearTouch);

        Update(pad, Finger(ps));
        Update(pad, Finger(ps)); // held: one press
        Assert.Equal(VitaButtons.PsButton, pad.GetController(0).Buttons);
        Update(pad);
        Assert.Equal(1, psPresses);
    }

    [Fact]
    public void AndroidGamepadMapsToTheStandardLayout()
    {
        var pads = new AndroidGamepads();
        Assert.Equal("030000004c050000e60c000000000000", AndroidGamepads.Guid(0x054C, 0x0CE6));
        pads.Connect(7, "DualSense Wireless Controller", 0x054C, 0x0CE6,
            [AndroidKeys.AxisX, AndroidKeys.AxisY, AndroidKeys.AxisZ, AndroidKeys.AxisRz, AndroidKeys.AxisLTrigger, AndroidKeys.AxisRTrigger, AndroidKeys.AxisHatX, AndroidKeys.AxisHatY]);
        Assert.Equal(ControllerKind.PlayStation, Assert.Single(pads.Devices).Kind);

        Assert.True(pads.OnKey(7, AndroidKeys.ButtonA, true));
        Assert.True(pads.OnKey(7, AndroidKeys.ButtonMode, true));
        Assert.False(pads.OnKey(7, 29 /* KEYCODE_A of a keyboard */, true));
        Assert.False(pads.OnKey(99, AndroidKeys.ButtonA, true)); // unknown device
        Assert.Equal(VitaButtons.Cross | VitaButtons.PsButton, pads.Map().State.Buttons);
        pads.OnKey(7, AndroidKeys.ButtonA, false);
        pads.OnKey(7, AndroidKeys.ButtonMode, false);

        var axes = new Dictionary<int, float>
        {
            [AndroidKeys.AxisX] = 1f, [AndroidKeys.AxisZ] = -1f, [AndroidKeys.AxisHatY] = -1f, [AndroidKeys.AxisRTrigger] = 1f,
            [AndroidKeys.AxisRx] = 1f, // ignored: the right stick of this pad is Z/RZ
        };
        pads.OnMotion(7, a => axes.GetValueOrDefault(a));
        var m = pads.Map().State;
        Assert.Equal(VitaButtons.Up | VitaButtons.RTrigger, m.Buttons); // hat up = D-pad up, R2 = R
        Assert.Equal(((byte)255, (byte)0, (byte)128), (m.Lx, m.Rx, m.Ry));

        // a pad with the right stick on RX/RY and digital triggers
        pads.Connect(8, "Generic USB Pad", 0x0079, 0x0011, [AndroidKeys.AxisX, AndroidKeys.AxisY, AndroidKeys.AxisRx, AndroidKeys.AxisRy]);
        pads.OnMotion(8, a => a == AndroidKeys.AxisRy ? 1f : 0f);
        pads.OnKey(8, AndroidKeys.ButtonL2, true);
        m = pads.Map().State;
        Assert.Equal(255, m.Ry);
        Assert.True((m.Buttons & VitaButtons.LTrigger) != 0);

        pads.Disconnect(7);
        pads.Disconnect(8);
        Assert.False(pads.AnyConnected);
        Assert.Equal(ControllerState.Neutral.Lx, pads.Map().State.Lx);
    }

    [Fact]
    public void AndroidInputCombinesTouchAndGamepads()
    {
        var pad = new OnScreenPad();
        var pads = new AndroidGamepads();
        var input = new AndroidInput(pad, pads);
        var square = pad.Layout.Single(c => c.Control == VitaControl.Square);
        Update(pad, Finger(square));
        pads.Connect(1, "Xbox Wireless Controller", 0x045E, 0x0B13, [AndroidKeys.AxisX, AndroidKeys.AxisY, AndroidKeys.AxisZ, AndroidKeys.AxisRz]);
        pads.OnKey(1, AndroidKeys.ButtonStart, true);
        Assert.Equal(VitaButtons.Square | VitaButtons.Start, input.GetController(0).Buttons);
        Assert.Equal(ControllerState.Neutral.Lx, input.GetController(1).Lx);

        var im = pad.Image;
        Update(pad, (4, im.X + im.Width / 2, im.Y + im.Height / 2));
        Span<TouchPoint> dest = stackalloc TouchPoint[4];
        Assert.Equal(1, input.GetTouches(false, dest));
        Assert.Single(input.GetTouches(false));
    }
}
