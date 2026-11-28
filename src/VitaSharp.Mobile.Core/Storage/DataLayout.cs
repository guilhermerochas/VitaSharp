namespace VitaSharp.Mobile.Core.Storage;

/// <summary>
/// The app's data folder, laid out like the desktop's (docs/android/README.md, "Storage"):
/// <code>
/// &lt;root&gt;/fs/ux0/app/TITLEID      installed games (VPK installs land here)
/// &lt;root&gt;/fs/ux0/user/00/savedata saves, as on the console (the emulator writes them)
/// &lt;root&gt;/fs/ux0/user/00/trophy   trophy unlocks
/// &lt;root&gt;/fs/vs0, os0, sa0, pd0      the installed firmware (FirmwareSetup)
/// &lt;root&gt;/logs                      last.log of the current session and the previous ones
/// &lt;root&gt;/np, patches                as on the desktop
/// &lt;root&gt;/config/app.json           global settings (AppPreferences)
/// &lt;root&gt;/config/games/TITLEID.json per-game settings and play statistics (GameSettings)
/// &lt;root&gt;/config/input.json         controller profiles (Core InputProfileStore)
/// &lt;root&gt;/gpu_driver.json           GPU driver choice and libadrenotools options (GpuDriverScreen)
/// &lt;internal&gt;/gpu_drivers/TITLE    installed GPU driver packages, &lt;internal&gt;/gpu_driver_files the driver's redirected files
/// &lt;internal&gt;/run/game.json         the running game process (GameProcessMarker)
/// &lt;internal&gt;/cache/trophies/COMMID the Trofeus screen's cache: texts and extracted pictures (TrophyListCache)
/// &lt;internal&gt;/cache/shadercache    shader pack, pipelines, driver cache and ASTC textures (&lt;root&gt;/shadercache when
///                                  the root is the internal folder itself)
/// &lt;internal&gt;/cache/codecache      the ARM blocks each module ran (same rule)
/// </code>
/// The root is chosen by <see cref="DataRoot"/>: /storage/emulated/0/.vitasharp (no root, all-files access), else the
/// app-specific external storage (Context.GetExternalFilesDir(null), /sdcard/Android/data/&lt;package&gt;/files), else
/// the internal files folder. All are plain POSIX paths the emulator opens and maps directly. &lt;internal&gt; is the internal
/// files folder (Context.FilesDir): GPU driver libraries must be there, because the linker refuses to load a library from
/// shared storage (libadrenotools: "MUST NOT be on sdcard/storage").
/// <para>
/// The caches the emulator rebuilds by itself (<see cref="ShaderCache"/>, <see cref="CodeCache"/>) stay in the internal
/// folder even when the data folder is in shared storage: shared storage goes through FUSE, which costs every file
/// open and write (Redmi Note 14, 8 KB files with their folder: ~45 ms each in /storage/emulated/0 against ~23 ms in
/// internal storage, from the shell; God of War II wrote 207 new ASTC levels in 12.8 s on the GPU thread) and the ASTC
/// cache alone is thousands of small files. Games, saves, states, captures, firmware and settings stay in the data
/// folder the user chose. A cache still in the old place (&lt;root&gt;/shadercache, &lt;root&gt;/codecache) keeps being used
/// until <see cref="CacheMigration"/> moved it (so no session starts on an empty cache meanwhile).
/// </para>
/// </summary>
public sealed class DataLayout(string root, string? internalRoot = null)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string InternalRoot { get; } = Path.GetFullPath(internalRoot ?? root);
    public string GpuDriverSettingsFile => Path.Combine(Root, "gpu_driver.json");
    public string GpuDrivers => Path.Combine(InternalRoot, "gpu_drivers");
    public string GpuDriverFiles => Path.Combine(InternalRoot, "gpu_driver_files");
    public string GameProcessFile => Path.Combine(InternalRoot, "run", "game.json");
    /// <summary>The Trofeus screen's cache (<see cref="Library.TrophyListCache"/>): internal storage, rebuilt from the TRPs.</summary>
    public string TrophyCache => Path.Combine(InternalRoot, "cache", "trophies");
    public string Fs => Path.Combine(Root, "fs");
    public string Ux0 => Path.Combine(Fs, "ux0");
    public string Apps => VitaSharp.Core.Io.FirmwareLayout.Applications.Host(Fs);
    public string SaveData => VitaSharp.Core.Io.FirmwareLayout.SaveData.Host(Fs);
    public string Logs => Path.Combine(Root, "logs");
    /// <summary>The save states, one folder per title (<see cref="VitaSharp.Core.SaveState.SaveStates.Folder"/>).</summary>
    public string SaveStates => Path.GetDirectoryName(VitaSharp.Core.SaveState.SaveStates.Folder(Root, "_"))!;
    /// <summary>Whether the rebuildable caches live apart from the data folder (in the internal folder).</summary>
    public bool CachesInternal => InternalRoot != Root;
    /// <summary>The folder of the rebuildable caches when they are apart from the data folder.</summary>
    public string CacheRoot => Path.Combine(InternalRoot, "cache");
    /// <summary>The shader cache in the data folder (where it was before; still used until it is moved).</summary>
    public string LegacyShaderCache => Path.Combine(Root, "shadercache");
    /// <summary>The code cache in the data folder (where it was before; still used until it is moved).</summary>
    public string LegacyCodeCache => Path.Combine(Root, "codecache");
    /// <summary>The shader cache where it belongs: the internal folder when the data folder is elsewhere.</summary>
    public string InternalShaderCache => CachesInternal ? Path.Combine(CacheRoot, "shadercache") : LegacyShaderCache;
    /// <summary>The code cache where it belongs: the internal folder when the data folder is elsewhere.</summary>
    public string InternalCodeCache => CachesInternal ? Path.Combine(CacheRoot, "codecache") : LegacyCodeCache;
    /// <summary>The shader cache a session uses (shader pack, pipelines, driver cache, ASTC textures).</summary>
    public string ShaderCache => InUse(LegacyShaderCache, InternalShaderCache);
    /// <summary>The code cache a session uses: the ARM blocks each module had translated (<see cref="VitaSharp.Core.Cpu.CodeCache"/>).</summary>
    public string CodeCache => InUse(LegacyCodeCache, InternalCodeCache);
    public string Np => Path.Combine(Root, "np");
    public string Patches => Path.Combine(Root, "patches");
    public string Config => Path.Combine(Root, "config");
    public string AppSettingsFile => Path.Combine(Config, "app.json");
    public string GameSettingsDir => Path.Combine(Config, "games");
    public string InputProfiles => Path.Combine(Config, VitaSharp.Core.Input.InputProfileStore.FileName);

    /// <summary>Per-game settings file. Title ids are 9 characters (PCSA00029); anything else is sanitised.</summary>
    public string GameSettingsFile(string titleId) => Path.Combine(GameSettingsDir, Sanitize(titleId) + ".json");

    /// <summary>The save folder of a title (ux0:user/00/savedata/TITLEID).</summary>
    public string SaveDir(string titleId) => Path.Combine(SaveData, Sanitize(titleId));

    public void EnsureCreated()
    {
        foreach (string d in new[] { Root, Fs, Ux0, Apps, SaveData, Logs, Config, GameSettingsDir })
        {
            Directory.CreateDirectory(d);
        }
    }

    /// <summary>The new place once it exists or when there is nothing in the old one; else the old one, until
    /// <see cref="CacheMigration"/> moves it.</summary>
    private static string InUse(string legacy, string target)
    {
        if (legacy == target || Directory.Exists(target) || !Directory.Exists(legacy))
        {
            return target;
        }

        return legacy;
    }

    private static string Sanitize(string s)
    {
        char[] chars = s.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray();
        return chars.Length > 0 ? new string(chars) : "_";
    }
}
