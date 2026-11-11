using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Tests;

/// <summary>Storage read speed: the Vita memory card's by default, "Carregamento rapido" (fast loading) per game.</summary>
public sealed class IoTimingTests
{
    [Fact]
    public void DefaultIsTheVitaMemoryCardSpeed()
    {
        Assert.Equal(7e6, IoTiming.VitaBytesPerSecond);
        // the test run sets no VITASHARP_IO_SPEED: a session without fast loading reads at the Vita's speed
        Assert.Null(Environment.GetEnvironmentVariable(EnvVars.IoSpeed));
        Assert.Equal(IoTiming.VitaBytesPerSecond, IoTiming.DefaultBytesPerSecond);
        Assert.False(new AppPreferences().FastLoading);
        Assert.Null(new GameSettings().FastLoading);
        Assert.False(EffectiveSettings.Resolve(new AppPreferences(), null).FastLoading);
        Assert.False(EffectiveSettings.Resolve(new AppPreferences(), new GameSettings()).FastLoading);
    }

    [Theory]
    [InlineData("vita", 7e6)]
    [InlineData(" Fast ", 0.0)]
    [InlineData("unlimited", 0.0)]
    [InlineData("0", 0.0)]
    [InlineData("25", 25e6)]
    [InlineData("6.5", 6.5e6)]
    public void SpeedSettingsParse(string text, double bytesPerSecond) => Assert.Equal(bytesPerSecond, IoTiming.ParseSpeed(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-3")]
    [InlineData("quick")]
    [InlineData("NaN")]
    public void UnknownSpeedSettingsAreIgnored(string? text) => Assert.Null(IoTiming.ParseSpeed(text));

    [Fact]
    public void FastLoadingLiftsTheLimitAndOffGoesBackToTheVitaSpeed()
    {
        double saved = IoTiming.BytesPerSecond;
        try
        {
            IoTiming.FastLoading = true;
            Assert.Equal(0, IoTiming.BytesPerSecond);
            Assert.True(IoTiming.FastLoading);
            IoTiming.FastLoading = false;
            Assert.Equal(IoTiming.VitaBytesPerSecond, IoTiming.BytesPerSecond);
            Assert.False(IoTiming.FastLoading);
        }
        finally { IoTiming.BytesPerSecond = saved; }
    }

    [Fact]
    public void GameChoiceOverridesTheGlobalDefault()
    {
        var on = new AppPreferences { FastLoading = true };
        var off = new AppPreferences();
        Assert.True(EffectiveSettings.Resolve(on, null).FastLoading);
        Assert.True(EffectiveSettings.Resolve(on, new GameSettings()).FastLoading); // no choice of its own: the global one
        Assert.False(EffectiveSettings.Resolve(on, new GameSettings { FastLoading = false }).FastLoading);
        Assert.True(EffectiveSettings.Resolve(off, new GameSettings { FastLoading = true }).FastLoading);
        Assert.True(new GameSettings { FastLoading = false }.HasOverrides);

        // what a session applies (EmulationSession: IoTiming.FastLoading = settings.FastLoading)
        double saved = IoTiming.BytesPerSecond;
        try
        {
            IoTiming.FastLoading = EffectiveSettings.Resolve(off, new GameSettings { FastLoading = true }).FastLoading;
            Assert.Equal(0, IoTiming.BytesPerSecond);
            IoTiming.FastLoading = EffectiveSettings.Resolve(off, new GameSettings()).FastLoading;
            Assert.Equal(7e6, IoTiming.BytesPerSecond);
        }
        finally { IoTiming.BytesPerSecond = saved; }
    }

    [Fact]
    public void FastLoadingPersists()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vitasharp-iotiming-" + Guid.NewGuid().ToString("N"));
        try
        {
            string app = Path.Combine(dir, "app.json"), game = Path.Combine(dir, "games", "PCSC00045.json");
            new AppPreferences { FastLoading = true }.Save(app);
            Assert.True(AppPreferences.Load(app).FastLoading);
            File.WriteAllText(app, "{ \"CpuMhz\": 444 }"); // written before the option existed: off
            Assert.False(AppPreferences.Load(app).FastLoading);

            new GameSettings { FastLoading = true }.Save(game);
            Assert.True(GameSettings.Load(game).FastLoading);
            new GameSettings { FastLoading = false }.Save(game);
            Assert.False(GameSettings.Load(game).FastLoading);
            new GameSettings().Save(game);
            Assert.Null(GameSettings.Load(game).FastLoading);
            Assert.DoesNotContain("FastLoading", File.ReadAllText(game)); // no choice of its own: not written
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
