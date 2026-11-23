using System.Diagnostics;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.SaveState;

public static partial class SaveStates
{
    /// <summary>The state's <see cref="StateBootImage"/>: how its program and modules were loaded.</summary>
    private const string BootSection = "boot";

    /// <summary>Whether the state in <paramref name="path"/> can be booted without its program's files (it has a boot
    /// section: saved by a build that writes one).</summary>
    public static bool CanBootFromState(string path)
    {
        try
        {
            using FileStream file = File.OpenRead(path);
            using var reader = new SaveStateReader(file);
            while (reader.Next(out string? name, out _))
            {
                if (name == BootSection)
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is IOException or SnapshotException or InvalidDataException)
        {
        }

        return false;
    }

    /// <summary>
    /// Brings up, in a new <paramref name="emu"/> (nothing loaded, nothing started), the program the state in
    /// <paramref name="path"/> was saved in, without reading any of its files: the state's memory is put in place and
    /// every module the process and the kernel had loaded (the game's executable and modules, the firmware modules, the
    /// user's kernel plugins) is rebuilt from it in load order (<see cref="ModuleLoader.FromMemory"/>). No thread runs:
    /// <see cref="Load"/> with the same file follows and brings the state's threads, kernel objects and HLE state, as a
    /// load into the running game does. A capture re-runs this way on a computer that has neither the game nor the
    /// plugins (docs/notes/session-capture.md). States of several processes (the shell with applications) are not
    /// supported yet.
    /// </summary>
    public static void BootFromState(Emulator emu, string path)
    {
        var sw = Stopwatch.StartNew();
        if (emu.MainModule != null || emu.Running)
        {
            throw new SnapshotException("a state boots only into an emulator with no program loaded");
        }

        SaveStateHeader header;
        var sections = new List<(string Name, byte[] Data)>();
        using (FileStream file = File.OpenRead(path))
        using (var reader = new SaveStateReader(file))
        {
            header = reader.Header;
            while (reader.Next(out string? name, out _))
            {
                sections.Add((name, reader.ReadAll()));
            }
        }

        byte[] boot = sections.FirstOrDefault(s => s.Name == BootSection).Data
            ?? throw new SnapshotException("the state has no boot section (saved by an earlier build): it loads only into the running game");
        var image = StateBootImage.Read(boot);
        if (sections.FirstOrDefault(s => s.Name == ProcessesSection).Data is { } processes && new SnapshotReader(processes).ReadInt32() > 1)
        {
            throw new SnapshotException("the state holds several processes (the shell and the applications it opened): booting it without their programs is not supported yet");
        }

        emu.PrepareStateBoot(image);
        if (SaveStateFile.Incompatibility(header, emu.TitleId) is { } why)
        {
            throw new SaveStateIncompatibleException(why);
        }

        if (image.Modules.Any(m => m.Kernel))
        {
            emu.Vita.KernelProcessFor(emu);
        }

        LoadedModule? main = null;
        var shells = new List<LoadedModule>();
        lock (s_gate)
        {
            RestoreMemory(emu, sections);
            foreach (StateBootImage.ModuleEntry entry in image.Modules)
            {
                LoadedModule module = emu.AddModuleFromState(entry);
                if (entry.Uid == image.MainModuleUid && !entry.Kernel)
                {
                    main = module;
                }

                if (entry.FirmwareShell)
                {
                    shells.Add(module);
                }
            }
        }

        if (shells.Count > 0 && emu.Vita.KernelProcess is { } kernel)
        {
            kernel.FirmwareKernel.AdoptFromState(shells);
        }

        emu.FinishStateBoot(image, main ?? throw new SnapshotException($"the state's main module (uid 0x{image.MainModuleUid:X}) is not in its module list"));
        Log.Info($"Boot from a save state: {path}: {image.Modules.Count} modules rebuilt from its memory in {sw.ElapsedMilliseconds} ms (no file of the program read); loading the state");
    }
}
