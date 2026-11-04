using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Save states: the NGS object graph (systems, racks, voices with their module data and decoding positions, patches)
/// by guest handles. ATRAC9 decoders are recreated from their configuration on the next tick (their inter-frame
/// history is lost: at most one superframe of the restored stream differs).
/// </summary>
[GenerateSnapshot, SnapshotIgnoreFields("_systems", "_racks", "_voices", "_patches")]
public sealed partial class Ngs : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        foreach (var s in _systems.Values) if (s.UpdateDepth != 0) return "an NGS update is running";
        return null;
    }

    static void WriteFloats(SnapshotWriter w, float[]? a) => w.WriteArray(a);

    void SaveExtra(SnapshotWriter w)
    {
        if (_systems.Values.Any(s => s.UpdateDepth != 0 || s.GuestLockCount != 0)) throw new SnapshotRetryException("an NGS system is locked or updating");
        w.Write(_systems.Count);
        foreach (var s in _systems.Values)
        {
            w.Write(s.Handle); w.Write(s.Granularity); w.Write(s.SampleRate); w.Write(s.MaxVoices); w.Write(s.CallbackInfo);
        }
        w.Write(_racks.Count);
        foreach (var r in _racks.Values)
        {
            w.Write(r.Handle); w.Write(r.System.Handle); w.Write((uint)r.Type);
            w.Write(r.Chain.Length); foreach (var k in r.Chain) w.Write((int)k);
            w.Write(r.ChannelsPerVoice); w.Write(r.PatchesPerOutput); w.Write(r.MaxPatchesPerInput); w.Write(r.OutputCount);
            w.Write(r.AllocCursor); w.Write(r.AllocEnd);
        }
        w.Write(_voices.Count);
        foreach (var v in _voices.Values)
        {
            w.Write(v.Handle); w.Write(v.Rack.Handle); w.Write((int)v.State); w.Write(v.Paused); w.Write(v.KeyedOff); w.Write(v.Pending);
            w.Write(v.FrameCount); w.Write(v.Input.Length); w.Write(v.FinishedCallback); w.Write(v.FinishedUserData);
            w.Write(v.Modules.Length);
            foreach (var m in v.Modules)
            {
                w.Write(m.Index); w.Write((int)m.Kind); w.Write(m.ParamAddr); w.Write(m.ParamSize); w.Write(m.Locked); w.Write(m.Bypassed);
                w.Write(m.Callback); w.Write(m.UserData); w.Write(m.GuestState); WriteFloats(w, m.EmitBuf);
                w.Write(m.Source != null);
                if (m.Source is { } st)
                {
                    WriteFloats(w, st.Pcm.ToArray()); w.Write(st.PcmRead); w.Write(st.LoopCount);
                    for (int a = 0; a < 2; a++) for (int b = 0; b < 4; b++) w.Write(st.AdpcmHist[a, b]);
                    w.Write(st.Staging); w.Write(st.StagingLen); w.Write(st.At9Config); w.Write(st.ResamplePos); w.Write(st.LastL); w.Write(st.LastR);
                }
            }
        }
        w.Write(_patches.Count);
        foreach (var p in _patches.Values)
        {
            w.Write(p.Handle); w.Write(p.Source.Handle); w.Write(p.Dest.Handle); w.Write(p.OutputIndex); w.Write(p.SubIndex); w.Write(p.DestIndex);
            for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++) w.Write(p.Volume[a, b]);
        }
        // the lists that link the graph, by handle
        foreach (var s in _systems.Values)
        {
            w.Write(s.Racks.Count); foreach (var r in s.Racks) w.Write(r.Handle);
            lock (s.Lock) { w.Write(s.Queue.Count); foreach (var v in s.Queue) w.Write(v.Handle); }
        }
        foreach (var r in _racks.Values) { w.Write(r.Voices.Count); foreach (var v in r.Voices) w.Write(v.Handle); }
        foreach (var v in _voices.Values)
        {
            w.Write(v.Patches.Length);
            foreach (var row in v.Patches) { w.Write(row.Length); foreach (var p in row) w.Write(p?.Handle ?? 0); }
        }
    }

    void LoadExtra(SnapshotReader r)
    {
        _systems.Clear(); _racks.Clear(); _voices.Clear(); _patches.Clear();
        int n = r.ReadInt32();
        var systems = new List<NgsSystem>();
        for (int i = 0; i < n; i++)
        {
            var s = new NgsSystem { Handle = r.ReadUInt32(), Granularity = r.ReadInt32(), SampleRate = r.ReadInt32(), MaxVoices = r.ReadInt32(), CallbackInfo = r.ReadUInt32() };
            _systems[s.Handle] = s; systems.Add(s);
        }
        n = r.ReadInt32();
        var racks = new List<NgsRack>();
        for (int i = 0; i < n; i++)
        {
            uint h = r.ReadUInt32(), sys = r.ReadUInt32();
            var rk = new NgsRack { Handle = h, System = _systems[sys], Type = (NgsBuss)r.ReadUInt32() };
            rk.Chain = new NgsModuleKind[r.ReadInt32()];
            for (int k = 0; k < rk.Chain.Length; k++) rk.Chain[k] = (NgsModuleKind)r.ReadInt32();
            rk.ChannelsPerVoice = r.ReadInt32(); rk.PatchesPerOutput = r.ReadInt32(); rk.MaxPatchesPerInput = r.ReadInt32(); rk.OutputCount = r.ReadUInt32();
            rk.AllocCursor = r.ReadUInt32(); rk.AllocEnd = r.ReadUInt32();
            _racks[h] = rk; racks.Add(rk);
        }
        n = r.ReadInt32();
        var voices = new List<NgsVoice>();
        for (int i = 0; i < n; i++)
        {
            uint h = r.ReadUInt32(), rack = r.ReadUInt32();
            var v = new NgsVoice { Handle = h, Rack = _racks[rack], State = (NgsVoiceState)r.ReadInt32(), Paused = r.ReadBool(), KeyedOff = r.ReadBool(), Pending = r.ReadBool() };
            v.FrameCount = r.ReadUInt32(); v.Input = new float[r.ReadInt32()]; v.FinishedCallback = r.ReadUInt32(); v.FinishedUserData = r.ReadUInt32();
            v.Modules = new NgsModuleData[r.ReadInt32()];
            for (int k = 0; k < v.Modules.Length; k++)
            {
                var m = new NgsModuleData { Voice = v, Index = r.ReadInt32(), Kind = (NgsModuleKind)r.ReadInt32(), ParamAddr = r.ReadUInt32(), ParamSize = r.ReadUInt32() };
                m.Locked = r.ReadBytes(); m.Bypassed = r.ReadBool(); m.Callback = r.ReadUInt32(); m.UserData = r.ReadUInt32();
                m.GuestState = r.ReadBytes() ?? []; m.EmitBuf = r.ReadArray<float>();
                if (r.ReadBool())
                {
                    var st = new NgsSourceState();
                    st.Pcm.AddRange(r.ReadArray<float>() ?? []); st.PcmRead = r.ReadInt32(); st.LoopCount = r.ReadInt32();
                    for (int a = 0; a < 2; a++) for (int b = 0; b < 4; b++) st.AdpcmHist[a, b] = r.ReadInt32();
                    st.Staging = r.ReadBytes() ?? []; st.StagingLen = r.ReadInt32(); st.At9Config = r.ReadUInt32();
                    st.ResamplePos = r.ReadDouble(); st.LastL = r.ReadSingle(); st.LastR = r.ReadSingle();
                    // the decoder is created again by the next tick (At9 == null), which also resets its staging
                    m.Source = st;
                }
                v.Modules[k] = m;
            }
            _voices[h] = v; voices.Add(v);
        }
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            uint h = r.ReadUInt32(), src = r.ReadUInt32(), dst = r.ReadUInt32();
            var p = new NgsPatch { Handle = h, Source = _voices[src], Dest = _voices[dst], OutputIndex = r.ReadInt32(), SubIndex = r.ReadInt32(), DestIndex = r.ReadInt32() };
            for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++) p.Volume[a, b] = r.ReadSingle();
            _patches[h] = p;
        }
        foreach (var s in systems)
        {
            int c = r.ReadInt32(); for (int k = 0; k < c; k++) s.Racks.Add(_racks[r.ReadUInt32()]);
            c = r.ReadInt32(); for (int k = 0; k < c; k++) s.Queue.Add(_voices[r.ReadUInt32()]);
        }
        foreach (var rk in racks) { int c = r.ReadInt32(); for (int k = 0; k < c; k++) rk.Voices.Add(_voices[r.ReadUInt32()]); }
        foreach (var v in voices)
        {
            v.Patches = new NgsPatch?[r.ReadInt32()][];
            for (int k = 0; k < v.Patches.Length; k++)
            {
                var row = new NgsPatch?[r.ReadInt32()];
                for (int j = 0; j < row.Length; j++) { uint h = r.ReadUInt32(); row[j] = h != 0 ? _patches[h] : null; }
                v.Patches[k] = row;
            }
        }
    }
}
