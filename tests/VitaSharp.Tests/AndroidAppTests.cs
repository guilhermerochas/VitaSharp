using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Host;
using VitaSharp.Core.Input;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Input;
using VitaSharp.Mobile.Core.Library;
using VitaSharp.Mobile.Core.Logging;
using VitaSharp.Mobile.Core.Settings;
using VitaSharp.Mobile.Core.Storage;
using FrameRate = VitaSharp.Core.Hle.Modules.FrameRate;
using FrameRateMode = VitaSharp.Core.Hle.Modules.FrameRateMode;

namespace VitaSharp.Tests;

/// <summary>The platform-neutral layers of the Android app (src/VitaSharp.Mobile.Core): logcat formatting, settings,
/// library and trophies, on-screen pad and gamepad mapping.</summary>
public sealed class AndroidAppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-android-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (DirectoryNotFoundException) { }
    }

    // ---------------------------------------------------------------- env.txt

    [Fact]
    public void EnvironmentFileTakesOnlyVitaSharpVariables()
    {
        List<KeyValuePair<string, string>> vars = EnvironmentFile.Parse("# comment\r\nVITASHARP_GPU_QUIRKS=-RenderPasses,+TileBased\r\n\r\nexport VITASHARP_ASTC = \"0\"\nPATH=/x\nVITASHARP_BAD NAME=1\n=1\nVITASHARP_EMPTY=\n");
        Assert.Equal([new("VITASHARP_GPU_QUIRKS", "-RenderPasses,+TileBased"), new("VITASHARP_ASTC", "0"), new("VITASHARP_EMPTY", "")], vars);
    }

    // ---------------------------------------------------------------- runtime report

    [Fact]
    public void RuntimeReportOnTheDesktopHasNoAndroidBuildVariant()
    {
        // the variant comes from the VitaSharp.Android assembly's metadata, which the test host does not load
        Assert.Null(RuntimeReport.BuildRuntime);
        var lines = RuntimeReport.Lines().ToList();
        Assert.EndsWith(", build ?", lines[0]);
        Assert.Equal($"Dynamic code: supported {RuntimeReport.DynamicCodeSupported}, compiled {RuntimeReport.DynamicCodeCompiled}", lines[1]);
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
        LogcatEntry e = LogcatFormat.Format(LogLevel.Info, "gpu", "Swapchain 2400x1080").Single();
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

    private sealed class ListSink : ILogSink
    {
        public readonly List<(LogLevel, string, string)> Lines = new();
        public void Write(LogLevel level, string thread, string message)
        {
            lock (Lines)
            {
                Lines.Add((level, thread, message));
            }
        }
    }

    [Fact]
    public void LogSinkReceivesWrittenLines()
    {
        var sink = new ListSink();
        LogLevel prevLevel = Log.Level;
        Log.Sink = sink;
        try
        {
            Log.Level = LogLevel.Info;
            Log.Debug("filtered out");
            Log.Warn("android-sink-test warning");
            // the periodic session report goes to the log file only, not to logcat
            Log.InfoFileOnly("android-sink-test file only");
            // other tests running in parallel log through the same global sink: read a snapshot under the sink's lock
            (LogLevel, string, string)[] lines;
            lock (sink.Lines)
            {
                lines = sink.Lines.ToArray();
            }

            Assert.Contains(lines, l => l.Item1 == LogLevel.Warn && l.Item3 == "android-sink-test warning");
            Assert.DoesNotContain(lines, l => l.Item3 == "filtered out");
            Assert.DoesNotContain(lines, l => l.Item3 == "android-sink-test file only");
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
    public void InternalResolutionOffersHalfToQuadruple()
    {
        var app = new AppPreferences();
        Assert.Equal(0f, EffectiveSettings.Resolve(app, null).ResScaleValue); // native by default
        foreach (string scale in new[] { "0.5", "0.75", "2", "3", "4" })
        {
            var settings = EffectiveSettings.Resolve(app, new GameSettings { ResScale = scale });
            Assert.Equal(float.Parse(scale, System.Globalization.CultureInfo.InvariantCulture), settings.ResScaleValue);
        }
        // a factor the option does not offer stays native
        Assert.Equal(0f, EffectiveSettings.Resolve(app, new GameSettings { ResScale = "8" }).ResScaleValue);
        Assert.Equal(new[] { "", "0.75", "0.5", "2", "3", "4" }, SettingsChoices.ResScales.Select(c => c.Value));
    }

    [Fact]
    public void FrameRateIsAPerGameSettingThatStartsEverySession()
    {
        var app = new AppPreferences();
        Assert.Equal("", EffectiveSettings.Resolve(app, null).FrameRate);
        Assert.Equal("30", EffectiveSettings.Resolve(app, new GameSettings { FrameRate = "30" }).FrameRate);
        Assert.Equal("unlimited", EffectiveSettings.Resolve(app, new GameSettings { FrameRate = "unlimited" }).FrameRate);
        // a value the option does not offer never reaches the display module
        Assert.Equal("", EffectiveSettings.Resolve(app, new GameSettings { FrameRate = "45" }).FrameRate);
        Assert.True(new GameSettings { FrameRate = "60" }.HasOverrides);

        // the game's choice wins over VITASHARP_FRAME_RATE from env.txt; native falls back to it, then to native, so
        // a mode set by the game played before never carries over
        var fixed60 = EffectiveSettings.Resolve(app, new GameSettings { FrameRate = "60" });
        Assert.Equal(FrameRateMode.Fixed60, fixed60.StartFrameRate("unlimited"));
        var native = EffectiveSettings.Resolve(app, null);
        Assert.Equal(FrameRateMode.Unlimited, native.StartFrameRate("unlimited"));
        Assert.Equal(FrameRateMode.Native, native.StartFrameRate(null));

        // every choice is a value the display module understands, and the pause menu's row cycles through all of them
        foreach (Choice choice in SettingsChoices.FrameRates)
        {
            Assert.Equal(choice.Value.Length > 0 ? choice.Value : "native", FrameRate.Name(FrameRate.Parse(choice.Value)));
        }
        Assert.Equal(new[] { "30", "60", "unlimited", "" }, new[] { "", "30", "60", "unlimited" }.Select(SettingsChoices.NextFrameRate));
        Assert.Equal("", SettingsChoices.NextFrameRate("45"));

        // the FPS overlay shows the mode only when it is not native
        Assert.Equal("29 FPS", SettingsChoices.FpsText(29, ""));
        Assert.Equal($"30 FPS · {SettingsChoices.LabelOf(SettingsChoices.FrameRates, "30")}", SettingsChoices.FpsText(30, "30"));
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

        var g = new GameSettings { CpuBackend = "native", FrameRate = "unlimited" };
        g.RecordSession(TimeSpan.FromMinutes(10), 18_000, new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc));
        g.Save(data.GameSettingsFile("PCSC00045"));
        var gb = GameSettings.Load(data.GameSettingsFile("PCSC00045"));
        Assert.Equal(("native", (int?)null, 600L, 30.0, "unlimited"), (gb.CpuBackend, gb.CpuMhz, gb.PlaySeconds, gb.AverageFps, gb.FrameRate));
        Assert.True(gb.HasOverrides);
        Assert.DoesNotContain("CpuMhz", File.ReadAllText(data.GameSettingsFile("PCSC00045"))); // nulls are not written

        // missing or broken files give the defaults
        File.WriteAllText(data.AppSettingsFile, "{ not json");
        Assert.Equal(0, AppPreferences.Load(data.AppSettingsFile).CpuMhz);
        // "Som de troféu" is on by default, also for a settings file written before the option existed
        Assert.True(AppPreferences.Load(data.AppSettingsFile).TrophySound);
        File.WriteAllText(data.AppSettingsFile, "{ \"CpuMhz\": 444 }");
        Assert.True(AppPreferences.Load(data.AppSettingsFile).TrophySound);
        new AppPreferences { TrophySound = false }.Save(data.AppSettingsFile);
        Assert.False(AppPreferences.Load(data.AppSettingsFile).TrophySound);
        Assert.False(GameSettings.Load(Path.Combine(_root, "none.json")).HasOverrides);
        Assert.EndsWith(Path.Combine("games", "_.json"), data.GameSettingsFile("../../.."));
    }

    [Fact]
    public void PerGameChoicesStartWithTheGlobalDefault()
    {
        IReadOnlyList<Choice> c = SettingsChoices.WithDefault(SettingsChoices.Clocks, "444");
        Assert.Equal(SettingsChoices.DefaultValue, c[0].Value);
        Assert.Equal(Loc.Format("MDefaultParen", Loc.Get("MClock444")), c[0].Label);
        Assert.Equal(3, c.Count);
        Assert.Contains(CpuChoices.Available(), x => x.Value == "interp");
        Assert.Equal("", CpuChoices.Available()[0].Value);
    }

    // ---------------------------------------------------------------- library and trophies

    private string InstallFakeGame(DataLayout data, string titleId, string title, bool withTrophies)
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

        List<GameEntry> games = GameLibrary.Scan(data);
        Assert.Equal(["Killzone Mercenary", "Uncharted: Golden Abyss"], games.Select(g => g.Title));
        GameEntry kz = games[0];
        Assert.Equal(("PCSC00045", "1.12", "KM"), (kz.TitleId, kz.Version, kz.Initials));
        Assert.StartsWith("PCSC00045 · v1.12 · ", kz.Subtitle);

        Assert.Equal(Loc.Get("MNoTrophies"), GameLibrary.Trophies(games[1], data).Text);
        TrophyProgress none = GameLibrary.Trophies(kz, data);
        Assert.Equal(("0/3", 0), (none.Text, none.Percent));

        TrophyUnlocks.Save(TrophyUnlocks.PathFor(data.Fs, "NPWR00001_00"), new()
        {
            [1] = 63_900_000_000_000_000UL,
            [2] = 63_900_000_100_000_000UL
        });
        TrophyProgress p = GameLibrary.Trophies(kz, data);
        Assert.Equal(("2/3", 66), (p.Text, p.Percent));
        Assert.Equal((1, 1), p.ByGrade[TrophyGrade.Gold]);
        Assert.Equal((0, 1), p.ByGrade[TrophyGrade.Platinum]);

        var list = TrophyList.Load(kz, data);
        Assert.Equal(3, list.Rows.Count);
        Assert.Equal([2, 1], list.Filtered(TrophyList.Filter.Earned).Select(r => r.Id)); // most recent first
        TrophyRow missing = Assert.Single(list.Filtered(TrophyList.Filter.Missing));
        Assert.Equal((0, "All"), (missing.Id, missing.ShownName));
        TrophyRow hidden = list.Rows.Single(r => r.Id == 1);
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
        DateTime now = new DateTime(2026, 10, 30, 15, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal(Loc.Get("MToday"), GameLibrary.LastPlayed(now.AddHours(-1), now));
        Assert.Equal(Loc.Get("MYesterday"), GameLibrary.LastPlayed(now.AddDays(-1), now));
        Assert.Equal(Loc.Format("MDaysAgo", 3), GameLibrary.LastPlayed(now.AddDays(-3), now));
        Assert.Equal(Loc.Get("MNever"), GameLibrary.LastPlayed(null, now));
    }

    // ---------------------------------------------------------------- input

    private static (int, float, float)[] Finger(OnScreenControl c, float fx = 0.5f, float fy = 0.5f, int id = 1) =>
        [(id, c.X + c.Width * fx, c.Y + c.Height * fy)];

    private static void Update(OnScreenPad pad, params (int, float, float)[] fingers) => pad.Update(fingers);

    [Fact]
    public void OnScreenButtonsSticksAndFrontTouch()
    {
        var pad = new OnScreenPad();
        OnScreenControl cross = pad.Layout.Single(c => c.Control == VitaControl.Cross);
        OnScreenControl lstick = pad.Layout.Single(c => c.Control == VitaControl.LStickUp);
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
        (float X, float Y, float Width, float Height) im = pad.Image;
        Update(pad, (3, im.X + im.Width / 4, im.Y + im.Height / 2));
        TouchPoint t = Assert.Single(pad.GetTouches(false));
        Assert.Equal((0.25f, 0.5f), (t.X, t.Y), new FloatPairComparer());
        Assert.Empty(pad.GetTouches(true));

        // ... and stays one when it slides over a button
        Update(pad, (3, cross.X + cross.Width / 2, cross.Y + cross.Height / 2));
        Assert.Equal(VitaButtons.None, pad.GetController(0).Buttons);
        pad.Reset();
        Assert.Equal(ControllerState.Neutral.Lx, pad.GetController(0).Lx);
    }

    [Fact]
    public void RoundControlsAreHitAsTheirCirclesAndTheirCornersAreFrontTouch()
    {
        // a 2400x1080 phone: the image placed as the game screen places it
        const float W = 2400, H = 1080;
        var pad = new OnScreenPad { Aspect = W / H };
        float scale = Math.Min(W / 960f, H / 544f);
        float iw = 960 * scale / W, ih = 544 * scale / H;
        pad.Image = ((1 - iw) / 2, (1 - ih) / 2, iw, ih);
        OnScreenControl rstick = pad.Layout.Single(c => c.Control == VitaControl.RStickUp);
        OnScreenControl lstick = pad.Layout.Single(c => c.Control == VitaControl.LStickUp);
        OnScreenControl cross = pad.Layout.Single(c => c.Control == VitaControl.Cross);

        // Uncharted's journal icon at (2080, 680): inside the right stick's rectangle, outside its circle
        (float X, float Y) icon = (2080 / W, 680 / H);
        Assert.True(rstick.Contains(icon.X, icon.Y));
        Update(pad, (1, icon.X, icon.Y));
        Assert.Single(pad.GetTouches(false));
        Assert.False(pad.IsPressed(rstick));
        Update(pad);

        // on each stick's circle, up to the margin, the stick; past it, the game
        foreach (OnScreenControl stick in new[] { rstick, lstick })
        {
            float radius = Math.Min(stick.Width * pad.Aspect, stick.Height) / 2;
            float cx = stick.X + stick.Width / 2, cy = stick.Y + stick.Height / 2;
            Update(pad, (2, cx + radius * 1.05f / pad.Aspect, cy));
            Assert.True(pad.IsPressed(stick));
            Assert.Empty(pad.GetTouches(false));
            Update(pad);
            Update(pad, (3, cx + radius * 0.8f / pad.Aspect, cy - radius * 0.8f)); // the corner of its rectangle
            Assert.False(pad.IsPressed(stick));
            Update(pad);
        }

        // a round button: its centre presses it, the corner of its rectangle is a front touch
        Update(pad, Finger(cross));
        Assert.Equal(VitaButtons.Cross, pad.GetController(0).Buttons);
        Update(pad);
        Update(pad, (4, cross.X + cross.Width * 0.03f, cross.Y + cross.Height * 0.03f));
        Assert.Equal(VitaButtons.None, pad.GetController(0).Buttons);
        Assert.Single(pad.GetTouches(false));
        Update(pad);
    }

    [Fact]
    public void OnScreenButtonsVibrateWhileTheStickIsHeld()
    {
        var pad = new OnScreenPad();
        var pulses = new List<int>();
        var haptics = new OnScreenHaptics(pad, pulses.Add);
        OnScreenControl cross = pad.Layout.Single(c => c.Control == VitaControl.Cross);
        OnScreenControl square = pad.Layout.Single(c => c.Control == VitaControl.Square);
        OnScreenControl lstick = pad.Layout.Single(c => c.Control == VitaControl.LStickUp);
        (int, float, float) thumbOnStick = Finger(lstick, 0.8f, 0.5f, 1)[0];

        // the thumb takes the stick: one pulse
        Update(pad, thumbOnStick);
        Assert.Single(pulses);
        // moving the stick is not a new press
        Update(pad, Finger(lstick, 0.2f, 0.5f, 1));
        Assert.Single(pulses);
        // the other thumb presses buttons while the stick stays held: one pulse per press (the bug: none)
        Update(pad, thumbOnStick, Finger(cross, id: 2)[0]);
        Assert.Equal(2, pulses.Count);
        Update(pad, thumbOnStick, Finger(cross, id: 2)[0]); // still held: no pulse
        Assert.Equal(2, pulses.Count);
        Update(pad, thumbOnStick);
        Update(pad, thumbOnStick, Finger(square, id: 3)[0]);
        Assert.Equal(3, pulses.Count);
        // a button held while another one is pressed vibrates too
        Update(pad, thumbOnStick, Finger(square, id: 3)[0], Finger(cross, id: 4)[0]);
        Assert.Equal(4, pulses.Count);

        // the strength sets the amplitude; off gives no pulses
        Assert.All(pulses, p => Assert.Equal(OnScreenHaptics.Amplitude(OnScreenHaptics.Medium), p));
        pad.Reset();
        haptics.Strength = OnScreenHaptics.Strong;
        Update(pad, Finger(cross));
        Assert.Equal(255, pulses[^1]);
        Assert.True(OnScreenHaptics.Amplitude(OnScreenHaptics.Weak) < OnScreenHaptics.Amplitude(OnScreenHaptics.Medium));
        Assert.Equal(OnScreenHaptics.Amplitude(OnScreenHaptics.Medium), OnScreenHaptics.Amplitude(7));
        pad.Reset();
        haptics.Enabled = false;
        int before = pulses.Count;
        Update(pad, Finger(cross));
        Assert.Equal(before, pulses.Count);

        // the strength choices are the values the haptics understand
        Assert.Equal(new[] { "1", "2", "3" }, SettingsChoices.HapticStrengths.Select(c => c.Value));
        Assert.Equal(2, new AppPreferences().HapticStrength);
    }

    private sealed class FloatPairComparer : IEqualityComparer<(float, float)>
    {
        public bool Equals((float, float) a, (float, float) b) => Math.Abs(a.Item1 - b.Item1) < 1e-4 && Math.Abs(a.Item2 - b.Item2) < 1e-4;
        public int GetHashCode((float, float) obj) => 0;
    }

    [Fact]
    public void RearTouchToggleAndPsButton()
    {
        var pad = new OnScreenPad();
        OnScreenControl back = pad.Layout.Single(c => c.Control == VitaControl.TouchBack);
        OnScreenControl ps = pad.Layout.Single(c => c.Control == VitaControl.PS);
        int psPresses = 0;
        pad.PsPressed += () => psPresses++;

        Update(pad, Finger(back));
        Update(pad);
        Assert.True(pad.RearTouch); // a tap switches it on, it stays on
        Assert.True(pad.IsPressed(back));
        (float X, float Y, float Width, float Height) im = pad.Image;
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
            [AndroidKeys.AxisX] = 1f,
            [AndroidKeys.AxisZ] = -1f,
            [AndroidKeys.AxisHatY] = -1f,
            [AndroidKeys.AxisRTrigger] = 1f,
            [AndroidKeys.AxisRx] = 1f, // ignored: the right stick of this pad is Z/RZ
        };
        pads.OnMotion(7, a => axes.GetValueOrDefault(a));
        ControllerState m = pads.Map().State;
        Assert.Equal(VitaButtons.Up | VitaButtons.RTrigger, m.Buttons); // hat up = D-pad up, R2 = R
        Assert.Equal(((byte)255, (byte)0, (byte)128), (m.Lx, m.Rx, m.Ry));

        // a pad with the right stick on RX/RY and digital triggers
        pads.Connect(8, "Generic USB Pad", 0x0079, 0x0011, [AndroidKeys.AxisX, AndroidKeys.AxisY, AndroidKeys.AxisRx, AndroidKeys.AxisRy]);
        pads.OnMotion(8, a => a == AndroidKeys.AxisRy ? 1f : 0f);
        pads.OnKey(8, AndroidKeys.ButtonL2, true);
        m = pads.Map().State;
        Assert.Equal(255, m.Ry);
        Assert.True((m.Buttons & VitaButtons.LTrigger) != 0);

        // a pad that lists AXIS_LTRIGGER but moves the left trigger on AXIS_BRAKE (and the right one on AXIS_GAS)
        pads.OnKey(8, AndroidKeys.ButtonL2, false);
        pads.Disconnect(8);
        pads.Connect(9, "Bluetooth Gamepad", 0x045E, 0x0B13,
            [AndroidKeys.AxisX, AndroidKeys.AxisY, AndroidKeys.AxisZ, AndroidKeys.AxisRz, AndroidKeys.AxisLTrigger, AndroidKeys.AxisRTrigger, AndroidKeys.AxisBrake, AndroidKeys.AxisGas]);
        pads.Disconnect(7);
        pads.OnMotion(9, a => a == AndroidKeys.AxisBrake ? 1f : 0f);
        Assert.True((pads.Map().State.Buttons & VitaButtons.LTrigger) != 0);
        pads.OnMotion(9, a => a == AndroidKeys.AxisGas ? 1f : 0f);
        m = pads.Map().State;
        Assert.True((m.Buttons & VitaButtons.RTrigger) != 0);
        Assert.True((m.Buttons & VitaButtons.LTrigger) == 0);
        // ... and one with only BRAKE/GAS, or only LTRIGGER/RTRIGGER, still works
        pads.Disconnect(9);
        pads.Connect(10, "Brake Gas Pad", 0x0001, 0x0002, [AndroidKeys.AxisX, AndroidKeys.AxisY, AndroidKeys.AxisBrake, AndroidKeys.AxisGas]);
        pads.OnMotion(10, a => a == AndroidKeys.AxisBrake ? 1f : 0f);
        Assert.True((pads.Map().State.Buttons & VitaButtons.LTrigger) != 0);
        pads.Disconnect(10);
        pads.Connect(11, "Trigger Pad", 0x0001, 0x0003, [AndroidKeys.AxisX, AndroidKeys.AxisY, AndroidKeys.AxisLTrigger, AndroidKeys.AxisRTrigger]);
        pads.OnMotion(11, a => a == AndroidKeys.AxisLTrigger ? 1f : 0f);
        Assert.True((pads.Map().State.Buttons & VitaButtons.LTrigger) != 0);
        pads.Disconnect(11);
        Assert.False(pads.AnyConnected);
        Assert.Equal(ControllerState.Neutral.Lx, pads.Map().State.Lx);
    }

    [Fact]
    public void AndroidInputCombinesTouchAndGamepads()
    {
        var pad = new OnScreenPad();
        var pads = new AndroidGamepads();
        var input = new AndroidInput(pad, pads);
        OnScreenControl square = pad.Layout.Single(c => c.Control == VitaControl.Square);
        Update(pad, Finger(square));
        pads.Connect(1, "Xbox Wireless Controller", 0x045E, 0x0B13, [AndroidKeys.AxisX, AndroidKeys.AxisY, AndroidKeys.AxisZ, AndroidKeys.AxisRz]);
        pads.OnKey(1, AndroidKeys.ButtonStart, true);
        Assert.Equal(VitaButtons.Square | VitaButtons.Start, input.GetController(0).Buttons);
        Assert.Equal(ControllerState.Neutral.Lx, input.GetController(1).Lx);

        (float X, float Y, float Width, float Height) im = pad.Image;
        Update(pad, (4, im.X + im.Width / 2, im.Y + im.Height / 2));
        Span<TouchPoint> dest = stackalloc TouchPoint[4];
        Assert.Equal(1, input.GetTouches(false, dest));
        Assert.Single(input.GetTouches(false));
    }

    [Fact]
    public void AndroidKeyNamesAreTheDesktopKeyNames()
    {
        Assert.Equal("A", AndroidKeyNames.Name(29));
        Assert.Equal("Z", AndroidKeyNames.Name(54));
        Assert.Equal("Number0", AndroidKeyNames.Name(7));
        Assert.Equal("Keypad8", AndroidKeyNames.Name(152));
        Assert.Equal("F12", AndroidKeyNames.Name(142));
        Assert.Equal("Up", AndroidKeyNames.Name(AndroidKeys.DpadUp));
        Assert.Equal("Backspace", AndroidKeyNames.Name(67));   // KEYCODE_DEL
        Assert.Equal("Home", AndroidKeyNames.Name(122));       // KEYCODE_MOVE_HOME (KEYCODE_HOME is the system's)
        Assert.Equal("ShiftRight", AndroidKeyNames.Name(60));
        Assert.Equal("Enter", AndroidKeyNames.Name(66));
        Assert.Null(AndroidKeyNames.Name(24));                 // volume up
        Assert.Null(AndroidKeyNames.Name(AndroidKeys.Back));
        Assert.Null(AndroidKeyNames.Name(AndroidKeys.ButtonA));
        // every key of the default keyboard profile has an Android key code
        var names = Enumerable.Range(0, 300).Select(AndroidKeyNames.Name).OfType<string>().ToHashSet();
        foreach (List<string> keys in DefaultMappings.Keyboard().Bindings.Values)
        {
            foreach (string k in keys)
            {
                Assert.True(InputBinding.TryParse(k, out InputBinding b) && b.Key is { } key && names.Contains(key), k);
            }
        }
    }

    [Fact]
    public void AndroidKeyboardUsesTheDesktopDefaultMapping()
    {
        var kb = new AndroidKeyboard();
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(AndroidKeys.DpadLeft, true));
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(52 /* X */, true));
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(32 /* D */, true));
        ControllerState s = kb.Map().State;
        Assert.Equal(VitaButtons.Left | VitaButtons.Cross, s.Buttons);
        Assert.Equal(255, s.Lx);
        Assert.Equal(128, s.Ly);
        // an auto-repeat is consumed and changes nothing; the key-up lets go
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(52, true, repeat: true));
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(52, false));
        Assert.Equal(VitaButtons.Left, kb.Map().State.Buttons);
        // keys outside the profile stay with Android
        Assert.Equal(KeyboardKeyAction.None, kb.OnKey(24 /* volume up */, true));
        Assert.Equal(KeyboardKeyAction.None, kb.OnKey(AndroidKeys.Back, true));
        Assert.Equal(KeyboardKeyAction.None, kb.OnKey(AndroidKeyNames.Escape, true));
        // Home = PS button (the activity opens the pause menu in a game without the shell); once per press
        Assert.Equal(KeyboardKeyAction.Handled | KeyboardKeyAction.PsButton, kb.OnKey(AndroidKeyNames.MoveHome, true));
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(AndroidKeyNames.MoveHome, true, repeat: true));
        Assert.True((kb.Map().State.Buttons & VitaButtons.PsButton) != 0);
        kb.OnKey(AndroidKeyNames.MoveHome, false);

        // P = pause: never reaches the game, a held key opens the menu once
        Assert.Equal(KeyboardKeyAction.Handled | KeyboardKeyAction.Pause, kb.OnKey(44 /* P */, true));
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(44, true, repeat: true));
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(44, true));
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(44, false));
        Assert.Equal(KeyboardKeyAction.Handled | KeyboardKeyAction.Pause, kb.OnKey(44, true));
        Assert.Equal(VitaButtons.Left, kb.Map().State.Buttons);

        // a dialog took the focus: everything is released
        kb.ReleaseAll();
        Assert.False(kb.AnyHeld);
        Assert.Equal(ControllerState.Neutral.Buttons, kb.Map().State.Buttons);
        Assert.Equal(KeyboardKeyAction.Handled | KeyboardKeyAction.Pause, kb.OnKey(44, true));
    }

    [Fact]
    public void AndroidKeyboardFollowsTheSavedKeyboardProfile()
    {
        var store = new InputProfileStore(Path.Combine(_root, "input.json"));
        ControllerProfile p = DefaultMappings.Keyboard();
        p.Remove(VitaControl.Cross, InputBinding.OfKey("X"));
        p.Add(VitaControl.Cross, InputBinding.OfKey("K"));
        p.Remove(VitaControl.RStickDown, InputBinding.OfKey("K"));
        store.Put(p);
        var kb = new AndroidKeyboard(store);
        Assert.Equal(KeyboardKeyAction.None, kb.OnKey(52 /* X */, true));
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(39 /* K */, true));
        Assert.Equal(VitaButtons.Cross, kb.Map().State.Buttons);
        Assert.Equal(128, kb.Map().State.Ry);
    }

    [Fact]
    public void ARemappingAppliesWithTheGameRunning()
    {
        // the keyboard: another store (the launcher's Controles screen) rewrites input.json while the game runs
        string path = Path.Combine(_root, "input.json");
        var game = InputProfileStore.Load(path);
        game.DiskCheckIntervalMs = 0; // the game looks at the file on every poll (500 ms apart in the app)
        var kb = new AndroidKeyboard(game);
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(52 /* X */, true));
        Assert.Equal(VitaButtons.Cross, kb.Map().State.Buttons);
        var launcher = InputProfileStore.Load(path);
        ControllerProfile p = DefaultMappings.Keyboard();
        p.Remove(VitaControl.Cross, InputBinding.OfKey("X"));
        p.Add(VitaControl.Cross, InputBinding.OfKey("K"));
        p.Remove(VitaControl.RStickDown, InputBinding.OfKey("K"));
        launcher.Put(p);
        launcher.Save();
        Assert.Equal(ControllerState.Neutral.Buttons, kb.Map().State.Buttons); // X let go: no longer bound
        Assert.Equal(KeyboardKeyAction.None, kb.OnKey(52, true));
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(39 /* K */, true));
        Assert.Equal(VitaButtons.Cross, kb.Map().State.Buttons);

        // a connected controller: the Controles screen saves its profile in the shared store
        var store = new InputProfileStore();
        var pads = new AndroidGamepads(store);
        pads.Connect(1, "Xbox Wireless Controller", 0x045E, 0x0B13, [AndroidKeys.AxisX, AndroidKeys.AxisY, AndroidKeys.AxisZ, AndroidKeys.AxisRz]);
        pads.OnKey(1, AndroidKeys.ButtonA, true);
        Assert.Equal(VitaButtons.Cross, pads.Map().State.Buttons);
        (int id, string? name, ControllerKind kind) = Assert.Single(pads.Devices);
        ControllerProfile pad = store.ProfileFor(AndroidGamepads.Guid(0x045E, 0x0B13), name, kind, isGamepad: true);
        pad.SwapConfirm = !pad.SwapConfirm;
        store.Put(pad);
        Assert.Equal(VitaButtons.Circle, pads.Map().State.Buttons);
    }

    [Fact]
    public void AndroidKeyboardTiltKeysDriveTheMotionSensors()
    {
        long now = 1_000_000;
        var kb = new AndroidKeyboard(null, () => now);
        Span<MotionSample> samples = stackalloc MotionSample[64];
        // at rest until a tilt key is used
        Assert.False(kb.HasSensors);
        Assert.Equal(0, kb.ReadSamples(samples));
        // tilt keys are motion controls, not Vita buttons
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(152 /* numpad 8 */, true));
        Assert.Equal(ControllerState.Neutral.Buttons, kb.Map().State.Buttons);
        Assert.True(kb.HasSensors);
        Assert.Equal(1, kb.ReadSamples(samples)); // the first read: one sample of the current pose
        now += 200_000;
        int n = kb.ReadSamples(samples);
        Assert.Equal(50, n); // 200 ms at 250 Hz
        Assert.True(kb.Virtual.Angles.Pitch < -5, $"pitch {kb.Virtual.Angles.Pitch}"); // forward: the top edge goes down
        kb.OnKey(152, false);
        // the letter cluster too: H tilts right, B (re-centre) is bound
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(36 /* H */, true));
        now += 400_000;
        kb.ReadSamples(samples);
        Assert.True(kb.Virtual.Angles.Roll > 5, $"roll {kb.Virtual.Angles.Roll}");
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(30 /* B */, true));
        Assert.Equal(KeyboardKeyAction.Handled, kb.OnKey(149 /* numpad 5 */, true));

        // motion source Off: the tilt keys do nothing
        var store = new InputProfileStore(Path.Combine(_root, "input.json"));
        ControllerProfile off = DefaultMappings.Keyboard();
        off.MotionSource = MotionSourceMode.Off;
        store.Put(off);
        var kbOff = new AndroidKeyboard(store, () => now);
        kbOff.OnKey(152, true);
        Assert.False(kbOff.HasSensors);
        Assert.Equal(0, kbOff.ReadSamples(samples));
    }

    [Fact]
    public void AndroidInputCombinesTheKeyboard()
    {
        var pad = new OnScreenPad();
        var pads = new AndroidGamepads();
        var kb = new AndroidKeyboard();
        var input = new AndroidInput(pad, pads, kb);
        pads.Connect(1, "Xbox Wireless Controller", 0x045E, 0x0B13, [AndroidKeys.AxisX, AndroidKeys.AxisY, AndroidKeys.AxisZ, AndroidKeys.AxisRz]);
        Assert.True(pads.IsConnected(1));
        Assert.False(pads.IsConnected(2));
        pads.OnKey(1, AndroidKeys.ButtonStart, true);
        kb.OnKey(31 /* C */, true);
        kb.OnKey(51 /* W */, true);
        ControllerState s = input.GetController(0);
        Assert.Equal(VitaButtons.Start | VitaButtons.Circle, s.Buttons);
        Assert.Equal(0, s.Ly);
        kb.ReleaseAll();
        Assert.Equal(VitaButtons.Start, input.GetController(0).Buttons);
    }
}
