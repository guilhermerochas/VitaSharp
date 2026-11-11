using System.Text;

namespace VitaSharp.Core.Gpu;

/// <summary>
/// Which draws of a GPU dump produce each presented frame. A present record lies in the stream where the renderer
/// executed it, i.e. where the display queue flipped, and a game that runs ahead of its display (three buffers, two
/// flips pending) flips a buffer one or two frames after it drew into it: the draws between present #F-1 and #F are
/// then those of the frame shown at #F+2 (FIFA 13, Jak and Daxter). The index maps present #F to the last scene that
/// rendered into the buffer shown at #F, so that <c>--draw-limit=N@F</c> and <c>--trace-draws=F</c> act on the draws
/// behind frame_F.png: frame F = the draws after the previous present's last such scene up to the end of F's.
/// </summary>
public sealed class GpuDumpFrameIndex
{
    public sealed class Frame
    {
        /// <summary>Renderer present number (frame_N.png).</summary>
        public long Present;
        /// <summary>Framebuffer shown.</summary>
        public uint Address;
        /// <summary>Draw numbers in the dump (1-based, inclusive) that produce the frame; empty when none was found.</summary>
        public long FirstDraw, LastDraw;
        /// <summary>The draws between present #Present-1 and #Present (the stream order).</summary>
        public long IntervalFirst, IntervalLast;
        /// <summary>Present count when the last scene into the shown buffer began (-1: no such scene in the dump).</summary>
        public long ScenePresent = -1;
        public bool Mapped => ScenePresent >= 0 && LastDraw >= FirstDraw;
        public long Draws => Math.Max(0, LastDraw - FirstDraw + 1);
        /// <summary>Presents between the frame's draws and its display (0 = drawn right before it was shown).</summary>
        public long Lag => Mapped ? Present - ScenePresent - 1 : -1;
    }

    sealed class Scene
    {
        public uint Address;
        public long First, Last, BeginPresent;
    }

    public readonly List<Frame> Frames = new();
    readonly Dictionary<long, Frame> _byPresent = new();
    public long FirstPresent = -1, LastPresent = -1;
    public double Seconds;

    public Frame? this[long present] => _byPresent.GetValueOrDefault(present);

    /// <summary>Reads the dump once (no rendering) and maps every present to the draws that produce it.</summary>
    public static GpuDumpFrameIndex Build(string path)
    {
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        using var reader = new GpuDumpReader(path);
        var ix = new GpuDumpFrameIndex();
        long present = reader.Header.Long("first-present"), draws = 0, lastPresentDraws = 0;
        var scenes = new List<Scene>();
        var begun = new List<int>(); // scenes begun before each present
        Scene? cur = null;
        while (reader.Next())
        {
            switch (reader.Command)
            {
                case BeginSceneCommand b:
                    cur = new Scene { Address = b.Color?.Data ?? 0, First = draws + 1, Last = draws, BeginPresent = present };
                    scenes.Add(cur);
                    break;
                case DrawCommand:
                    draws++;
                    cur?.Last = draws;
                    break;
                case EndSceneCommand:
                    cur = null;
                    break;
                case PresentCommand p:
                    present++;
                    var f = new Frame { Present = present, Address = p.Frame.Address, IntervalFirst = lastPresentDraws + 1, IntervalLast = draws };
                    lastPresentDraws = draws;
                    ix.Frames.Add(f);
                    begun.Add(scenes.Count);
                    if (ix.FirstPresent < 0)
                    {
                        ix.FirstPresent = present;
                    }

                    ix.LastPresent = present;
                    break;
            }
        }
        // an open scene's last draw is only known at the end: resolve the ranges now, in present order
        long prevEnd = 0;
        for (int i = 0; i < ix.Frames.Count; i++)
        {
            var f = ix.Frames[i];
            // the last scene with draws into the shown buffer, begun before the present (the game cannot draw into the
            // buffer again before it is shown; a later empty scene into it, a clear, does not move the frame)
            Scene? sc = null;
            for (int k = begun[i] - 1, n = 0; k >= 0 && n < 4096; k--, n++)
            {
                if (scenes[k].Address == f.Address && f.Address != 0 && scenes[k].Last >= scenes[k].First)
                {
                    sc = scenes[k];
                    break;
                }
            }

            f.FirstDraw = prevEnd + 1;
            if (sc != null && sc.Last >= sc.First && sc.Last >= f.FirstDraw)
            {
                f.LastDraw = sc.Last;
                f.ScenePresent = sc.BeginPresent;
                prevEnd = sc.Last;
            }
            else
            {
                f.LastDraw = prevEnd; // nothing found (CPU framebuffer, or drawn before the dump starts): empty
            }

            ix._byPresent[f.Present] = f;
        }
        ix.Seconds = t0.Elapsed.TotalSeconds;
        return ix;
    }

    /// <summary>One line per present: the draw range, the stream interval and the lag.</summary>
    public string Describe()
    {
        var sb = new StringBuilder("# present framebuffer draws(first-last,count) issued-between-presents lag | draws between the previous present and this one\n");
        foreach (var f in Frames)
        {
            sb.Append(f.Mapped
                ? $"#{f.Present} 0x{f.Address:X8} draws {f.FirstDraw}-{f.LastDraw} ({f.Draws}) between #{f.ScenePresent}-#{f.ScenePresent + 1} lag {f.Lag}"
                : $"#{f.Present} 0x{f.Address:X8} no scene found (CPU framebuffer, or drawn before the dump starts)")
              .Append($" | interval {f.IntervalFirst}-{f.IntervalLast} ({Math.Max(0, f.IntervalLast - f.IntervalFirst + 1)})\n");
        }

        return sb.ToString();
    }

    /// <summary>How far the frames were drawn ahead of their display: "lag 2 (all 201 presents)" or a histogram.</summary>
    public string LagSummary()
    {
        var hist = new SortedDictionary<long, int>();
        foreach (var f in Frames)
        {
            long k = f.Lag;
            hist[k] = hist.GetValueOrDefault(k) + 1;
        }
        return string.Join(", ", hist.Select(kv => kv.Key < 0 ? $"unmapped {kv.Value}" : $"lag {kv.Key}: {kv.Value} presents"));
    }
}
