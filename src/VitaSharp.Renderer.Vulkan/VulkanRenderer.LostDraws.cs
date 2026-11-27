using System.Globalization;
using System.Text;
using VitaSharp.Core.Gpu;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// What a lost device was running: every frame keeps the distinct programs it drew with (vertex and fragment program,
/// colour surface, the largest uniform data each stage was given, how many draws), so the device-lost report in the log
/// (and the F8 capture that saves itself there) names the work in flight without a GPU dump. FIFA 13 on a Mali-G57:
/// one vertex program with 32 KB of vertex uniforms (player skinning) into a 2048x1024 surface lost the device; finding
/// it took a dump replayed on the phone and a draw bisection. Recording is a short linear search over at most
/// <see cref="MaxProgramUses"/> entries of a preallocated array per frame, with no allocation.
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private const int MaxProgramUses = 32;

    private struct ProgramUse
    {
        public ulong Vs, Fs;
        public uint RtAddr;
        public ushort RtW, RtH;
        public int VertexUniforms, FragmentUniforms, Draws;
    }

    /// <summary>The draw's programs into the frame being recorded (Draw, after the programs were looked up).</summary>
    private void NoteProgramUse(DrawCommand d)
    {
        Frame f = F;
        ulong vs = d.VertexProgram.Info.Program.Hash, fs = d.FragmentProgram.Info.Program.Hash;
        uint rt = _renderTarget?.Address ?? 0;
        for (int i = f.ProgramCount - 1; i >= 0; i--)
        {
            ref ProgramUse u = ref f.Programs[i];
            if (u.Vs == vs && u.Fs == fs && u.RtAddr == rt)
            {
                u.Draws++;
                u.VertexUniforms = Math.Max(u.VertexUniforms, d.VertexUniformsLength);
                u.FragmentUniforms = Math.Max(u.FragmentUniforms, d.FragmentUniformsLength);
                return;
            }
        }

        if (f.ProgramCount == MaxProgramUses)
        {
            f.ProgramsDropped++;
            return;
        }

        f.Programs[f.ProgramCount++] = new ProgramUse
        {
            Vs = vs,
            Fs = fs,
            RtAddr = rt,
            RtW = (ushort)(_renderTarget?.Width ?? 0),
            RtH = (ushort)(_renderTarget?.Height ?? 0),
            VertexUniforms = d.VertexUniformsLength,
            FragmentUniforms = d.FragmentUniformsLength,
            Draws = 1,
        };
    }

    /// <summary>
    /// The programs of the frames the GPU had not finished (submitted and not retired, and the one being recorded), the
    /// most used first: "in flight when the device was lost: vs=... fs=... rt=0x...(WxH) vu=... fu=... draws=N".
    /// <paramref name="includeRetired"/>: every frame slot (tests).
    /// </summary>
    internal List<string> ProgramsInFlight(bool includeRetired = false)
    {
        var lines = new List<string>();
        foreach (Frame f in _frames)
        {
            bool inFlight = f.Recording || (f.Submitted && !f.Retired);
            if (!inFlight && !includeRetired)
            {
                continue;
            }

            if (f.ProgramCount == 0)
            {
                continue;
            }

            var uses = new List<ProgramUse>(f.Programs.AsSpan(0, f.ProgramCount).ToArray());
            uses.Sort((a, b) => b.Draws.CompareTo(a.Draws));
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"frame slot {Array.IndexOf(_frames, f)} ({(f.Recording ? "recording" : f.Retired ? "retired" : $"submitted, seq {f.SubmitSeq}")}): {f.ProgramCount} program pairs");
            if (f.ProgramsDropped > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" (+{f.ProgramsDropped} draws of others not kept)");
            }

            lines.Add(sb.ToString());
            foreach (ProgramUse u in uses)
            {
                lines.Add(FormattableString.Invariant($"  vs={u.Vs:X16} fs={u.Fs:X16} rt=0x{u.RtAddr:X8}({u.RtW}x{u.RtH}) vu={u.VertexUniforms} fu={u.FragmentUniforms} draws={u.Draws}"));
            }
        }

        return lines;
    }
}
