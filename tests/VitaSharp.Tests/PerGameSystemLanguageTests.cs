using System.Reflection;
using System.Runtime.CompilerServices;
using VitaSharp.Core;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using VitaSharp.Desktop;
using VitaSharp.Desktop.Launcher;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Tests;

/// <summary>
/// The emulator option "Idioma do sistema" of a game (EmulatorOptions.SystemLanguage): the application reads that
/// SceSystemParamLang through every way a game reads the system language (SceAppUtil, SceRegMgrForGame, SceRegMgr and
/// the SceNp/trophy paths that share <see cref="AppUtil.ProcessSystemParamInt"/>), while the system software and every
/// other system parameter keep what the system registry says. Both launchers offer the same choices (<see cref="ChoiceListLanguageTests"/>).
/// </summary>
[Collection("guest")]
public sealed class PerGameSystemLanguageTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Japanese = 0;
    private const int BrazilianPortuguese = 17;
    private const uint LanguageKey = 0x00037502;
    private const uint ButtonAssignKey = 0x00229142;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vs_syslang_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static int Call(object module, string name, params object[] args) =>
        (int)module.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!;

    private static void SetBacking(object target, string property, object value) =>
        typeof(Emulator).GetField($"<{property}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    /// <summary>A process with only its options (a real one would reserve a second guest address space), over a system
    /// whose registry says Brazilian Portuguese.</summary>
    private Emulator Process(int? systemLanguage, bool systemSoftware = false)
    {
        Directory.CreateDirectory(_root);
        var registry = Registry.For(_root);
        registry.Define("/CONFIG/SYSTEM/language", Registry.KeyType.Int, 4);
        registry.Define("/CONFIG/SYSTEM/button_assign", Registry.KeyType.Int, 4);
        Assert.True(registry.SetInt("/CONFIG/SYSTEM/", "language", BrazilianPortuguese));
        Assert.True(registry.SetInt("/CONFIG/SYSTEM/", "button_assign", 0));
        var emu = (Emulator)RuntimeHelpers.GetUninitializedObject(typeof(Emulator));
        SetBacking(emu, "Options", new EmulatorOptions { FsRoot = _root, SystemLanguage = systemLanguage });
        emu.IsSystemProgram = systemSoftware;
        return emu;
    }

    /// <summary>What each way of reading the language answers in <paramref name="emu"/>'s process.</summary>
    private (int AppUtil, int RegMgrForGame, int RegMgrKey, int RegMgrKeys, int Shared) ReadLanguage(Emulator emu)
    {
        uint buf = fixture.Memory.Alloc(0x40, "syslang");
        uint category = fixture.Memory.Alloc(0x20, "syslang-category");
        uint name = fixture.Memory.Alloc(0x20, "syslang-name");
        GuestMemory.WriteCString(category, "/CONFIG/SYSTEM");
        GuestMemory.WriteCString(name, "language");

        var appUtil = new AppUtil { Emu = emu };
        Assert.Equal(0, Call(appUtil, "sceAppUtilSystemParamGetInt", 1u, new Ptr<int>(buf)));
        int fromAppUtil = (int)GuestMemory.R32(buf);

        var regMgr = new RegMgr { Emu = emu, FsRootOverride = _root };
        Assert.Equal(0, Call(regMgr, "sceRegMgrSystemParamGetInt", LanguageKey, buf));
        int fromRegMgrForGame = (int)GuestMemory.R32(buf);

        var regMgrSystem = new RegMgrSystem { Emu = emu };
        Assert.Equal(0, Call(regMgrSystem, "sceRegMgrGetKeyInt", category, name, new Ptr<int>(buf)));
        int fromRegMgrKey = (int)GuestMemory.R32(buf);

        // SceRegMgrKeysInfo { 0x10, name, u16 type, u16 size, value } read in one call
        GuestMemory.W32(buf + 0, 0x10);
        GuestMemory.W32(buf + 4, name);
        GuestMemory.W32(buf + 8, 0);
        GuestMemory.W32(buf + 12, 0xFFFFFFFF);
        Assert.Equal(0, Call(regMgrSystem, "sceRegMgrGetKeys", category, buf, 1));
        int fromRegMgrKeys = (int)GuestMemory.R32(buf + 12);

        return (fromAppUtil, fromRegMgrForGame, fromRegMgrKey, fromRegMgrKeys, AppUtil.ProcessSystemParamInt(1, emu));
    }

    [Fact]
    public void AGameWithItsOwnSystemLanguageReadsItEverywhere()
    {
        Emulator game = Process(Japanese);
        Assert.Equal((Japanese, Japanese, Japanese, Japanese, Japanese), ReadLanguage(game));
        // only the language changes: the other system parameters still come from the system registry
        Assert.Equal(0, AppUtil.ProcessSystemParamInt(2, game));
        uint buf = fixture.Memory.Alloc(0x10, "syslang-button");
        Assert.Equal(0, Call(new RegMgr { Emu = game, FsRootOverride = _root }, "sceRegMgrSystemParamGetInt", ButtonAssignKey, buf));
        Assert.Equal(0u, GuestMemory.R32(buf));
    }

    [Fact]
    public void WithoutTheOptionTheGameReadsTheSystemLanguage()
    {
        Assert.Equal((BrazilianPortuguese, BrazilianPortuguese, BrazilianPortuguese, BrazilianPortuguese, BrazilianPortuguese),
            ReadLanguage(Process(null)));
    }

    [Fact]
    public void TheSystemSoftwareKeepsTheSystemLanguage()
    {
        Assert.Equal((BrazilianPortuguese, BrazilianPortuguese, BrazilianPortuguese, BrazilianPortuguese, BrazilianPortuguese),
            ReadLanguage(Process(Japanese, systemSoftware: true)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(20)]
    public void ALanguageTheFirmwareDoesNotHaveIsIgnored(int language)
    {
        Assert.Equal(BrazilianPortuguese, AppUtil.ProcessSystemParamInt(1, Process(language)));
    }

    [Fact]
    public void TheDesktopLauncherPassesTheGamesLanguage()
    {
        var options = new GameOptions();
        Assert.True(options.IsDefault);
        Assert.DoesNotContain(options.RunOptions(), o => o.StartsWith("--system-language", StringComparison.Ordinal));
        options.SystemLanguage = Japanese;
        Assert.False(options.IsDefault);
        Assert.Contains("--system-language=0", options.RunOptions());
        Assert.Equal(Japanese, Runner.SystemLanguageOpt(["--system-language=0"]));
        Assert.Equal(BrazilianPortuguese, Runner.SystemLanguageOpt(["--system-language=17"]));
        Assert.Null(Runner.SystemLanguageOpt(["--system-language=20"]));
        Assert.Null(Runner.SystemLanguageOpt(["--system-language=ja"]));
        Assert.Null(Runner.SystemLanguageOpt([]));
    }

    [Fact]
    public void TheAndroidAppPassesTheGamesLanguage()
    {
        var app = new AppPreferences();
        Assert.Null(EffectiveSettings.Resolve(app, null).SystemLanguageValue);
        var game = new GameSettings();
        Assert.False(game.HasOverrides);
        game.SystemLanguage = "0";
        Assert.True(game.HasOverrides);
        Assert.Equal(Japanese, EffectiveSettings.Resolve(app, game).SystemLanguageValue);
        game.SystemLanguage = "25"; // not a firmware language (a hand-edited file): the system's
        Assert.Null(EffectiveSettings.Resolve(app, game).SystemLanguageValue);
    }
}
