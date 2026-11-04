using VitaSharp.Core.Cpu;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: every fiber with its saved register context, and per thread the fiber it runs and the
/// context it returns to.</summary>
[GenerateSnapshot, SnapshotIgnoreFields("_fibers", "_threads", "_freeFibers", "_names")]
public sealed unsafe partial class Fiber
{
    static void WriteCtx(SnapshotWriter w, CpuState c) => GuestThread.WriteCpu(w, &c, false);
    static CpuState ReadCtx(SnapshotReader r) { var c = default(CpuState); GuestThread.ReadCpu(r, &c); return c; }

    void SaveExtra(SnapshotWriter w)
    {
        lock (_fibers)
        {
            w.Write(_fibers.Count);
            foreach (var (k, f) in _fibers)
            {
                w.Write(k); w.Write(f.Address); w.Write(f.Entry); w.Write(f.ArgOnInitialize); w.Write(f.ContextAddr); w.Write(f.ContextSize);
                w.Write(f.ArgOnRunPtr); w.Write(f.Name); w.Write((int)f.Status); WriteCtx(w, f.Context);
            }
            w.Write(_threads.Count);
            foreach (var (t, tf) in _threads)
            {
                w.Write(t.Uid);
                w.Write(tf.Current == null ? 0u : _fibers.FirstOrDefault(kv => ReferenceEquals(kv.Value, tf.Current)).Key);
                WriteCtx(w, tf.ThreadContext); w.Write(tf.ArgOnReturnPtr);
            }
        }
    }

    void LoadExtra(SnapshotReader r)
    {
        lock (_fibers)
        {
            _fibers.Clear(); _threads.Clear();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                uint k = r.ReadUInt32();
                var f = new FiberObj { Address = r.ReadUInt32(), Entry = r.ReadUInt32(), ArgOnInitialize = r.ReadUInt32(), ContextAddr = r.ReadUInt32(), ContextSize = r.ReadUInt32() };
                f.ArgOnRunPtr = r.ReadUInt32(); f.Name = r.ReadStringNotNull(); f.Status = (Status)r.ReadInt32(); f.Context = ReadCtx(r);
                _fibers[k] = f;
            }
            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int uid = r.ReadInt32();
                uint cur = r.ReadUInt32();
                var tf = new ThreadFibers { Current = cur != 0 ? _fibers.GetValueOrDefault(cur) : null, ThreadContext = ReadCtx(r), ArgOnReturnPtr = r.ReadUInt32() };
                if (r.Objects?.Get<GuestThread>(uid) is { } t) _threads[t] = tf;
            }
        }
    }
}
