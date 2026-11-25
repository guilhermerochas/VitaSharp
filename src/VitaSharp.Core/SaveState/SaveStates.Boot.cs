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
    /// plugins (docs/notes/session-capture.md). A state of several processes (the shell and the applications it had
    /// opened) brings each other process up the same way (<see cref="Emulator.AddProcessFromState"/>, from its own boot
    /// section), with the pid and application id it had, before the memory is put back in each process's space.
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
        List<SavedProcess> others = OtherProcesses(sections);
        emu.PrepareStateBoot(image);
        if (SaveStateFile.Incompatibility(header, emu.TitleId) is { } why)
        {
            throw new SaveStateIncompatibleException(why);
        }

        if (image.Modules.Any(m => m.Kernel))
        {
            emu.Vita.KernelProcessFor(emu);
        }

        // the other processes exist before the memory goes back: their blocks are put in their own address spaces
        var processes = new List<(Emulator Process, StateBootImage Image)> { (emu, image) };
        // the objects made from here on (the processes' own, the modules) keep clear of the ids the state gives them
        // (the state's load puts its own object table in place)
        int highest = others.Count == 0 ? 0 : others.Max(other => Math.Max(other.Pid, other.AppId));
        if (emu.Vita.Objects.NextUid < highest)
        {
            emu.Vita.Objects.NextUid = highest;
        }

        foreach (SavedProcess other in others)
        {
            processes.Add((emu.AddProcessFromState(other.Pid, other.AppId, other.Image, other.Suspended), other.Image));
        }

        var mains = new LoadedModule?[processes.Count];
        var shells = new List<LoadedModule>();
        int modules = 0;
        lock (s_gate)
        {
            RestoreMemory(emu, sections);
            for (int index = 0; index < processes.Count; index++)
            {
                (Emulator process, StateBootImage processImage) = processes[index];
                foreach (StateBootImage.ModuleEntry entry in processImage.Modules)
                {
                    LoadedModule module = process.AddModuleFromState(entry);
                    modules++;
                    if (entry.Uid == processImage.MainModuleUid && !entry.Kernel)
                    {
                        mains[index] = module;
                    }

                    if (entry.FirmwareShell)
                    {
                        shells.Add(module);
                    }
                }
            }
        }

        if (shells.Count > 0 && emu.Vita.KernelProcess is { } kernel)
        {
            kernel.FirmwareKernel.AdoptFromState(shells);
        }

        for (int index = 0; index < processes.Count; index++)
        {
            (Emulator process, StateBootImage processImage) = processes[index];
            LoadedModule main = mains[index]
                ?? throw new SnapshotException($"the main module (uid 0x{processImage.MainModuleUid:X}) of {process.Describe()} is not in its module list");
            process.FinishStateBoot(processImage, main);
        }

        SavedProcess? onScreen = others.FirstOrDefault(other => other.Foreground);
        if (onScreen != null)
        {
            emu.Vita.SetForegroundFromState(processes[others.IndexOf(onScreen) + 1].Process);
        }

        Log.Info($"Boot from a save state: {path}: {processes.Count} processes, {modules} modules rebuilt from its memory in {sw.ElapsedMilliseconds} ms (no file of the program read); loading the state");
    }

    /// <summary>A process of the state besides the first: its ids, its state at the save and its boot image.</summary>
    private sealed record SavedProcess(int Pid, int AppId, string TitleId, bool Suspended, bool Foreground, StateBootImage Image);

    /// <summary>The processes after the first one of the state's process list, each with its boot section; a state of
    /// one process (or without a list) has none. A state of several processes from a build that did not write their
    /// boot sections cannot be brought up without their programs.</summary>
    private static List<SavedProcess> OtherProcesses(List<(string Name, byte[] Data)> sections)
    {
        var others = new List<SavedProcess>();
        byte[]? list = sections.FirstOrDefault(s => s.Name == ProcessesSection).Data;
        if (list == null)
        {
            return others;
        }

        var reader = new SnapshotReader(list);
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
        {
            int pid = reader.ReadInt32();
            int appId = reader.ReadInt32();
            string titleId = reader.ReadStringNotNull();
            bool suspended = reader.ReadBool();
            bool foreground = reader.ReadBool();
            if (index == 0)
            {
                continue;
            }

            byte[] boot = sections.FirstOrDefault(s => s.Name == ProcessPrefix(index) + BootSection).Data
                ?? throw new SnapshotException($"the state's process {titleId} (pid 0x{pid:X}) has no boot section (saved by an earlier build): the state loads only into a system running the same applications");
            others.Add(new SavedProcess(pid, appId, titleId, suspended, foreground, StateBootImage.Read(boot)));
        }

        return others;
    }
}
