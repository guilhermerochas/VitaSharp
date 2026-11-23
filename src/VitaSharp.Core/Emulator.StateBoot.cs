using VitaSharp.Core.Io;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Package;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core;

/// <summary>
/// The program of a save state brought up without its files (<see cref="SaveStates.BootFromState"/>): the identity the
/// loader gave it and its modules as the state's memory holds them, then nothing runs until the state's threads are
/// loaded. A capture recorded on another computer re-runs this way where the game, its update and the user's plugins
/// are not installed (docs/notes/session-capture.md).
/// </summary>
public sealed partial class Emulator
{
    /// <summary>The program's identity and mounts as <see cref="LoadTitle"/> leaves them, from the state: app0 is the
    /// application's folder of THIS data folder (the guest path the state names; it may not exist here), never a host
    /// path of the computer that saved the state.</summary>
    internal void PrepareStateBoot(StateBootImage image)
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(this);
        _launchParam = null;
        string guest = image.AppGuestPath.Length > 0 ? image.AppGuestPath : FirmwareLayout.Applications.Guest(image.TitleId);
        AppPath = Path.GetFullPath(FirmwareLayout.GuestToHost(Options.FsRoot, guest));
        CurrentSelf = image.Self;
        Vfs.ClearApp0Overlays();
        // the game's folder of this data folder when it is installed here; else nothing is made: its files are not found
        Vfs.MountAsIs("app0", AppPath);
        ParamSfo = image.ParamSfo.Length > 0 ? new Sfo(image.ParamSfo) : null;
        TitleId = image.TitleId;
        Title = image.Title;
        Vfs.MountSaveData("savedata0", FirmwareLayout.SaveData.Host(Options.FsRoot, TitleId));
        Vfs.Mount("addcont0", FirmwareLayout.AddCont.Host(Options.FsRoot, TitleId));
        IsSystemProgram = image.IsSystemProgram;
        RunsOnSystemCore = image.RunsOnSystemCore;
        ProgramAuthorityId = image.AuthorityId;
        Access = ProgramAccess.Of(image.AuthorityId, image.IsSystemProgram, image.RetailApplication);
        try
        {
            Gpu.Backend?.BeginTitle(TitleId);
        }
        catch (Exception e)
        {
            Log.Warn($"GPU backend title setup failed: {e.Message}");
        }

        Log.Info($"Boot from a save state: {TitleId} \"{Title}\" ({image.Modules.Count} modules from the state's memory, app0 at {guest})");
    }

    /// <summary>A module of the state, in the process that holds it (a kernel module: the kernel process), rebuilt from
    /// the state's memory (already restored) as the loader had placed it.</summary>
    internal LoadedModule AddModuleFromState(StateBootImage.ModuleEntry entry) =>
        entry.Kernel && !IsKernelProcess ? KernelHost.AddModuleFromState(entry) : RegisterStateModule(entry);

    private LoadedModule RegisterStateModule(StateBootImage.ModuleEntry entry)
    {
        using ProcessScope.Scope processScope = ProcessScope.Enter(this);
        LoadedModule loadedModule;
        using (ProcessScope.Enter(entry.Kernel ? KernelSpace.KernelPid : ProcessScope.CurrentPid))
        {
            loadedModule = ModuleLoader.FromMemory(entry.Path, entry.Segments, entry.SegmentFlags, entry.InfoSegment, entry.ModuleInfoAddress,
                Linker, entry.Kernel, entry.SyscallsForUsers);
            loadedModule.Uid = Kernel.Objects.Register(new ModuleObject(loadedModule));
        }

        loadedModule.ImageHash = entry.ImageHash;
        loadedModule.Started = entry.Started;
        Kernel.Modules[loadedModule.Uid] = loadedModule;
        CheckPendingVariables(loadedModule);
        return loadedModule;
    }

    /// <summary>The process as <see cref="Start"/> leaves it, without starting anything: the state's load brings its
    /// threads.</summary>
    internal void FinishStateBoot(StateBootImage image, LoadedModule main)
    {
        MainModule = main;
        Kernel.ProcessParam = image.ProcessParam != 0 ? image.ProcessParam : Linker.ProcessParam;
        Kernel.TlsInit = image.TlsInit;
        Kernel.TlsInitSize = image.TlsInitSize;
        Kernel.TlsAreaSize = image.TlsAreaSize;
        Running = true;
        if (Parent == null)
        {
            Vita.Power.Start(() => Host.Input);
        }
    }
}
