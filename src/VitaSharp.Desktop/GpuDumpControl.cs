using System.Collections;
using VitaSharp.Core;
using VitaSharp.Core.Gpu;
using VitaSharp.Core.Util;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>
/// Recording side of the GPU dump (see docs/notes/gpu-dump.md): F8 in the game window starts and stops a recording,
/// <c>--gpu-dump-at=F1:F2[:file]</c> (or VITASHARP_GPU_DUMP_AT) records frames F1..F2 of a run. Dumps go to
/// &lt;DataRoot&gt;/gpudumps/&lt;TITLEID&gt;_&lt;yyyyMMdd_HHmmss&gt;.vsgd unless a file is given.
/// </summary>
static class GpuDumpControl
{
    public static string Root => Path.Combine(Program.DataRoot, "gpudumps");

    /// <summary>Frames recorded by F8 before the recording stops by itself (~60 s at 30 fps).</summary>
    const long InteractiveMaxFrames = 1800;

    static string NewPath(Emulator emu)
    {
        string title = string.Concat((string.IsNullOrEmpty(emu.TitleId) ? "NOTITLE" : emu.TitleId).Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        string baseName = Path.Combine(Root, $"{title}_{DateTime.Now:yyyyMMdd_HHmmss}");
        string path = baseName + GpuDumpFormat.Extension;
        for (int i = 2; File.Exists(path); i++) path = $"{baseName}_{i}{GpuDumpFormat.Extension}";
        return path;
    }

    /// <summary>Title, build, GPU, renderer settings and environment for the file header.</summary>
    public static GpuDumpHeader BuildHeader(Emulator emu, VulkanRenderer? renderer, string[] options, string? hostInfo)
    {
        var h = new GpuDumpHeader();
        h["title-id"] = emu.TitleId;
        h["title"] = emu.Title;
        h["app-path"] = emu.AppPath;
        string build = Diagnostics.BuildVersion();
        h["build"] = build;
        int plus = build.IndexOf('+');
        if (plus >= 0) h["commit"] = new string(build[(plus + 1)..].TakeWhile(char.IsLetterOrDigit).ToArray());
        h["created"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        h["os"] = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, .NET {Environment.Version}";
        h["command-line"] = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));
        h["cpu"] = Core.Cpu.Jit.Describe();
        if (renderer != null)
        {
            h["gpu"] = renderer.DeviceName;
            int i = 0;
            foreach (var line in renderer.DescribeForDiagnostics().Split('\n')) h[$"renderer.{i++}"] = line.TrimEnd('\r');
        }
        h["msaa"] = Environment.GetEnvironmentVariable("VITASHARP_MSAA") ?? "native (default)";
        h["debug-mode"] = DebugMode.Enabled ? "on" : "off";
        if (hostInfo != null) h["host"] = hostInfo;
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && k.StartsWith("VITASHARP_", StringComparison.OrdinalIgnoreCase)) h["env." + k] = e.Value as string ?? "";
        return h;
    }

    /// <summary>F8: starts a recording at the next present, or stops the running one at the next present.</summary>
    public static void Toggle(Emulator emu, Func<VulkanRenderer?> renderer, string[] options, Action<string>? status, Func<string>? hostInfo)
    {
        if (GpuDumpRecorder.Current is { } active)
        {
            active.Stop();
            Log.Info($"[gpudump] F8: stopping after {active.FramesRecorded} frames");
            status?.Invoke("Salvando dump da GPU...");
            return;
        }
        var rec = Start(emu, renderer(), options, NewPath(emu), -1, InteractiveMaxFrames, status, hostInfo?.Invoke());
        if (rec == null) status?.Invoke("Já existe uma gravação do dump da GPU em andamento");
    }

    static GpuDumpRecorder? Start(Emulator emu, VulkanRenderer? renderer, string[] options, string path, long startAfter, long frames,
        Action<string>? status, string? hostInfo)
    {
        var opt = new GpuDumpOptions
        {
            Path = path,
            Header = BuildHeader(emu, renderer, options, hostInfo),
            StartAfterPresent = startAfter,
            MaxFrames = frames,
            Started = _ => status?.Invoke("Gravando dump da GPU (F8 para parar)"),
            Finished = r => status?.Invoke(r.Error != null ? $"Falha ao gravar o dump da GPU: {r.Error}"
                : $"Dump da GPU salvo: {r.Path} ({r.Frames} quadros, {r.FileBytes / 1048576.0:F0} MB)"),
        };
        var rec = GpuDumpRecorder.Start(opt);
        if (rec != null) Log.Info($"[gpudump] armed: {path}" + (startAfter >= 0 ? $" after present #{startAfter}" : " at the next present") + (frames > 0 ? $", {frames} frames" : ""));
        return rec;
    }

    /// <summary>--gpu-dump-at=F1:F2[:file] / VITASHARP_GPU_DUMP_AT: records frames F1..F2 (renderer presents, the
    /// numbers of the dumped frame_N.png). Armed now; the GPU worker starts it exactly after present F1-1.</summary>
    public static void StartScheduled(Emulator emu, Func<VulkanRenderer?> renderer, string[] options)
    {
        string? spec = options.FirstOrDefault(o => o.StartsWith("--gpu-dump-at="))?["--gpu-dump-at=".Length..]
                       ?? Environment.GetEnvironmentVariable("VITASHARP_GPU_DUMP_AT");
        if (string.IsNullOrWhiteSpace(spec)) return;
        var parts = spec.Split(':', 3);
        if (parts.Length < 2 || !long.TryParse(parts[0].TrimStart('f'), out long f1) || !long.TryParse(parts[1].TrimStart('f'), out long f2) || f1 < 1 || f2 < f1)
        {
            Log.Warn($"--gpu-dump-at={spec} not recognised (use FIRST:LAST[:file], frames counted from 1)");
            return;
        }
        string? file = parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null;
        // the header needs the renderer (GPU, settings): arm once the title runs, before frame F1
        new Thread(() =>
        {
            while (renderer() == null || string.IsNullOrEmpty(emu.TitleId)) Thread.Sleep(5);
            Start(emu, renderer(), options, Path.GetFullPath(file ?? NewPath(emu)), f1 - 1, f2 - f1 + 1, null, null);
        }) { IsBackground = true, Name = "gpu-dump-at" }.Start();
    }

    /// <summary>End of a session: completes a running recording (the GPU worker must still be running).</summary>
    public static void StopAtExit()
    {
        if (GpuDumpRecorder.Current is not { } rec) return;
        Log.Info("[gpudump] session ending: finishing the recording");
        if (rec.StopNow(15000) == null) Log.Warn("[gpudump] the recording did not finish in time (GPU worker stuck?)");
    }
}
