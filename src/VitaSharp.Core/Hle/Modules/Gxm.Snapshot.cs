using VitaSharp.Core.Gpu;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Save states: contexts, render targets, sync objects, shader patchers and programs, by their guest handles.
/// Programs are rebuilt from the guest's program binaries (restored memory); renderer objects (BackendData) are
/// recreated by the new GPU backend on first use. Recorded command lists of deferred contexts are kept (their draws refer to programs by handle). Not
/// supported yet: a command list being recorded, and a scene in progress or a display queue entry in flight (the save waits for those to finish).
/// </summary>
[GenerateSnapshot, SnapshotIgnoreFields("_contexts", "_renderTargets", "_patchers", "_vertexPrograms", "_fragmentPrograms",
    "_syncObjects", "_displayQueue", "_commandLists")]
public sealed partial class Gxm : ISnapshotState
{
    string? ISnapshotState.CheckSnapshot()
    {
        if (_contexts.Values.Any(c => c.Recording != null)) return "host resource GXM command list recording not supported yet";
        return null;
    }

    void SaveExtra(SnapshotWriter w)
    {
        lock (_displayLock)
            if (_displayPending != 0 || (_displayQueue?.Count ?? 0) != 0) throw new SnapshotRetryException("a frame is in the GXM display queue");
        if (_contexts.Values.Any(c => c.InScene)) throw new SnapshotRetryException("a GXM scene is open");
        w.Write(_displayQueue != null);

        w.Write(_renderTargets.Count);
        foreach (var rt in _renderTargets.Values)
        {
            w.Write(rt.Handle); w.Write(rt.Width); w.Write(rt.Height); w.Write(rt.ScenesPerFrame); w.Write(rt.MultisampleMode); w.Write(rt.DriverMemBlock);
        }
        w.Write(_syncObjects.Count);
        foreach (var h in _syncObjects.Keys) w.Write(h);

        w.Write(_vertexPrograms.Count);
        foreach (var vp in _vertexPrograms.Values)
        {
            w.Write(vp.Handle); w.Write(vp.Info.Address); w.WriteArray(vp.Attributes); w.WriteArray(vp.Streams); w.Write(vp.RefCount);
        }
        w.Write(_fragmentPrograms.Count);
        foreach (var fp in _fragmentPrograms.Values)
        {
            w.Write(fp.Handle); w.Write(fp.Info?.Address ?? 0); w.Write(fp.Blend.HasValue);
            if (fp.Blend is { } b) w.WriteStruct(b);
            w.Write(fp.OutputFormat); w.Write(fp.MultisampleMode); w.Write(fp.IsMaskUpdate); w.Write(fp.RefCount);
        }

        w.Write(_patchers.Count);
        foreach (var sp in _patchers.Values)
        {
            w.Write(sp.Handle); w.Write(sp.UserData);
            w.Write(sp.HostAlloc); w.Write(sp.HostFree); w.Write(sp.BufferAlloc); w.Write(sp.BufferFree);
            w.Write(sp.VertexUsseAlloc); w.Write(sp.VertexUsseFree); w.Write(sp.FragmentUsseAlloc); w.Write(sp.FragmentUsseFree);
            w.Write(sp.BufferMem); w.Write(sp.BufferMemSize); w.Write(sp.VertexUsseMem); w.Write(sp.VertexUsseMemSize);
            w.Write(sp.FragmentUsseMem); w.Write(sp.FragmentUsseMemSize);
            lock (sp)
            {
                w.Write(sp.Registered.Count);
                foreach (var (id, prog) in sp.Registered) { w.Write(id); w.Write(prog); }
                w.Write(sp.VertexCache.Count);
                foreach (var ((prog, hash), vp) in sp.VertexCache) { w.Write(prog); w.Write(hash); w.Write(vp.Handle); }
                w.Write(sp.FragmentCache.Count);
                foreach (var (k, fp) in sp.FragmentCache)
                {
                    w.Write(k.Program); w.Write(k.Format); w.Write(k.Msaa); w.Write(k.Blend); w.Write(k.VertexProgram); w.Write(fp.Handle);
                }
            }
        }

        w.Write(_contexts.Count);
        foreach (var c in _contexts.Values) WriteContext(w, c);

        lock (_commandLists)
        {
            w.Write(_commandLists.Count);
            foreach (var l in _commandLists.Values)
            {
                w.Write(l.Id); w.Write(l.Owner); w.Write(l.VdmStart); w.Write(l.VdmEnd); w.Write(l.LastUse);
                w.Write(l.Draws.Length);
                foreach (var d in l.Draws) WriteRecipe(w, d);
            }
        }
    }

    static void WriteRecipe(SnapshotWriter w, DrawRecipe d)
    {
        w.Write(d.Vp.Handle); w.Write(d.Fp.Handle); w.WriteStruct(d.State);
        w.WriteArray(d.VUbs); w.WriteArray(d.FUbs); w.WriteArray(d.Streams); w.WriteArray(d.VTex); w.WriteArray(d.FTex);
        w.Write(d.PrimType); w.Write(d.IndexType); w.Write(d.IndexData); w.Write(d.IndexCount); w.Write(d.InstanceCount);
        w.Write(d.VisEnabled); w.Write(d.VisIncrement); w.Write(d.VisIndex); w.Write(d.ViewportSet); w.Write(d.RegionClipSet);
    }

    DrawRecipe ReadRecipe(SnapshotReader r)
    {
        uint vp = r.ReadUInt32(), fp = r.ReadUInt32();
        var d = new DrawRecipe
        {
            Vp = _vertexPrograms.GetValueOrDefault(vp) ?? throw new SnapshotException($"GXM command list: vertex program 0x{vp:X8} is missing"),
            Fp = _fragmentPrograms.GetValueOrDefault(fp) ?? throw new SnapshotException($"GXM command list: fragment program 0x{fp:X8} is missing"),
            State = r.ReadStruct<DrawState>(),
        };
        ReadArray(r, d.VUbs); ReadArray(r, d.FUbs); ReadArray(r, d.Streams); ReadArray(r, d.VTex); ReadArray(r, d.FTex);
        d.PrimType = r.ReadUInt32(); d.IndexType = r.ReadUInt32(); d.IndexData = r.ReadUInt32(); d.IndexCount = r.ReadUInt32(); d.InstanceCount = r.ReadUInt32();
        d.VisEnabled = r.ReadBool(); d.VisIncrement = r.ReadBool(); d.VisIndex = r.ReadUInt32(); d.ViewportSet = r.ReadBool(); d.RegionClipSet = r.ReadBool();
        return d;
    }

    void LoadExtra(SnapshotReader r)
    {
        bool hadQueue = r.ReadBool();
        if (hadQueue && _displayQueue == null) throw new SnapshotException("GXM is not initialised in this session yet; let the game start further, then load the state");
        _ubBlock = null;

        _renderTargets.Clear();
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var rt = new GxmRenderTarget { Handle = r.ReadUInt32(), Width = r.ReadUInt16(), Height = r.ReadUInt16(), ScenesPerFrame = r.ReadUInt16(), MultisampleMode = r.ReadUInt32(), DriverMemBlock = r.ReadInt32() };
            _renderTargets[rt.Handle] = rt;
        }
        _syncObjects.Clear();
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) { uint h = r.ReadUInt32(); _syncObjects[h] = new GxmSyncObject { Handle = h }; }

        _vertexPrograms.Clear();
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            uint h = r.ReadUInt32(), prog = r.ReadUInt32();
            var vp = new GxmVertexProgram { Handle = h, Info = GxmProgramInfo.Get(prog), Attributes = r.ReadArray<GxmVertexAttribute>() ?? [], Streams = r.ReadArray<GxmVertexStream>() ?? [] };
            vp.RefCount = r.ReadInt32();
            _vertexPrograms[h] = vp;
        }
        _fragmentPrograms.Clear();
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            uint h = r.ReadUInt32(), prog = r.ReadUInt32();
            var fp = new GxmFragmentProgram { Handle = h, Info = prog != 0 ? GxmProgramInfo.Get(prog) : null! };
            if (r.ReadBool()) fp.Blend = r.ReadStruct<GxmBlendInfo>();
            fp.OutputFormat = r.ReadUInt32(); fp.MultisampleMode = r.ReadUInt32(); fp.IsMaskUpdate = r.ReadBool(); fp.RefCount = r.ReadInt32();
            _fragmentPrograms[h] = fp;
        }

        _patchers.Clear();
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var sp = new GxmShaderPatcher { Handle = r.ReadUInt32() };
            sp.UserData = r.ReadUInt32();
            sp.HostAlloc = r.ReadUInt32(); sp.HostFree = r.ReadUInt32(); sp.BufferAlloc = r.ReadUInt32(); sp.BufferFree = r.ReadUInt32();
            sp.VertexUsseAlloc = r.ReadUInt32(); sp.VertexUsseFree = r.ReadUInt32(); sp.FragmentUsseAlloc = r.ReadUInt32(); sp.FragmentUsseFree = r.ReadUInt32();
            sp.BufferMem = r.ReadUInt32(); sp.BufferMemSize = r.ReadUInt32(); sp.VertexUsseMem = r.ReadUInt32(); sp.VertexUsseMemSize = r.ReadUInt32();
            sp.FragmentUsseMem = r.ReadUInt32(); sp.FragmentUsseMemSize = r.ReadUInt32();
            int m = r.ReadInt32();
            for (int j = 0; j < m; j++) sp.Registered[r.ReadUInt32()] = r.ReadUInt32();
            m = r.ReadInt32();
            for (int j = 0; j < m; j++)
            {
                uint prog = r.ReadUInt32(); ulong hash = r.ReadUInt64(); uint h = r.ReadUInt32();
                if (_vertexPrograms.TryGetValue(h, out var vp)) sp.VertexCache[(prog, hash)] = vp;
            }
            m = r.ReadInt32();
            for (int j = 0; j < m; j++)
            {
                uint prog = r.ReadUInt32(), fmt = r.ReadUInt32(), msaa = r.ReadUInt32(); ulong blend = r.ReadUInt64(); uint vprog = r.ReadUInt32(), h = r.ReadUInt32();
                if (_fragmentPrograms.TryGetValue(h, out var fp)) sp.FragmentCache[(prog, fmt, msaa, blend, vprog)] = fp;
            }
            _patchers[sp.Handle] = sp;
        }

        _contexts.Clear();
        n = r.ReadInt32();
        for (int i = 0; i < n; i++)
        {
            var c = ReadContext(r);
            _contexts[c.Handle] = c;
        }

        lock (_commandLists)
        {
            _commandLists.Clear();
            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                uint id = r.ReadUInt32(), owner = r.ReadUInt32(), vs = r.ReadUInt32(), ve = r.ReadUInt32();
                long last = r.ReadInt64();
                var draws = new DrawRecipe[r.ReadInt32()];
                for (int k = 0; k < draws.Length; k++) draws[k] = ReadRecipe(r);
                _commandLists[id] = new RecordedCommandList { Id = id, Owner = owner, Draws = draws, VdmStart = vs, VdmEnd = ve, LastUse = last };
            }
        }
    }

    static void WriteArray(SnapshotWriter w, GxmTextureDesc[] a) => w.WriteArray(a);
    static void ReadArray<T>(SnapshotReader r, T[] into) where T : unmanaged
    {
        var a = r.ReadArray<T>() ?? [];
        Array.Copy(a, into, Math.Min(a.Length, into.Length));
    }

    static void WriteContext(SnapshotWriter w, GxmContext c)
    {
        w.Write(c.Handle);
        w.WriteArray(c.StreamScratch); w.WriteArray(c.PreVertexUbs); w.WriteArray(c.PreFragmentUbs);
        WriteArray(w, c.PreVertexTextures); WriteArray(w, c.PreFragmentTextures);
        w.Write(c.VdmRing); w.Write(c.VdmRingSize); w.Write(c.VertexRing); w.Write(c.VertexRingSize); w.Write(c.FragmentRing); w.Write(c.FragmentRingSize);
        w.Write(c.VertexRingUsed); w.Write(c.FragmentRingUsed); w.Write(c.VertexDefaultReserved); w.Write(c.FragmentDefaultReserved);
        w.Write(c.RenderTarget?.Handle ?? 0); w.Write(c.FragmentSyncObject);
        w.WriteStruct(c.State);
        w.Write(c.VertexProgram?.Handle ?? 0); w.Write(c.FragmentProgram?.Handle ?? 0);
        w.WriteArray(c.VertexUniformBuffers); w.WriteArray(c.FragmentUniformBuffers); w.WriteArray(c.Streams);
        WriteArray(w, c.FragmentTextures); WriteArray(w, c.VertexTextures);
        w.Write(c.PrecomputedVertexState); w.Write(c.PrecomputedFragmentState);
        w.Write(c.VisFrontEnable); w.Write(c.VisBackEnable); w.Write(c.VisFrontIncrement); w.Write(c.VisBackIncrement);
        w.Write(c.VisFrontIndex); w.Write(c.VisBackIndex); w.Write(c.VisBuffer); w.Write(c.VisStride); w.Write(c.MarkerDepth);
        w.WriteArray(c.YuvProfile);
        w.Write(c.Deferred); w.Write(c.InCommandList); w.Write(c.ViewportSet); w.Write(c.RegionClipSet);
        w.Write(c.VdmCallback); w.Write(c.VertexCallback); w.Write(c.FragmentCallback); w.Write(c.CallbackData);
        w.Write(c.VdmBuffer); w.Write(c.VdmBufferSize); w.Write(c.VdmUsed); w.Write(c.VdmListStart); w.Write(c.SizeScratch);
    }

    GxmContext ReadContext(SnapshotReader r)
    {
        var c = new GxmContext { Handle = r.ReadUInt32() };
        ReadArray(r, c.StreamScratch); ReadArray(r, c.PreVertexUbs); ReadArray(r, c.PreFragmentUbs);
        ReadArray(r, c.PreVertexTextures); ReadArray(r, c.PreFragmentTextures);
        c.VdmRing = r.ReadUInt32(); c.VdmRingSize = r.ReadUInt32(); c.VertexRing = r.ReadUInt32(); c.VertexRingSize = r.ReadUInt32();
        c.FragmentRing = r.ReadUInt32(); c.FragmentRingSize = r.ReadUInt32();
        c.VertexRingUsed = r.ReadUInt32(); c.FragmentRingUsed = r.ReadUInt32(); c.VertexDefaultReserved = r.ReadBool(); c.FragmentDefaultReserved = r.ReadBool();
        c.RenderTarget = _renderTargets.GetValueOrDefault(r.ReadUInt32()); c.FragmentSyncObject = r.ReadUInt32();
        c.State = r.ReadStruct<DrawState>();
        c.VertexProgram = _vertexPrograms.GetValueOrDefault(r.ReadUInt32()); c.FragmentProgram = _fragmentPrograms.GetValueOrDefault(r.ReadUInt32());
        ReadArray(r, c.VertexUniformBuffers); ReadArray(r, c.FragmentUniformBuffers); ReadArray(r, c.Streams);
        ReadArray(r, c.FragmentTextures); ReadArray(r, c.VertexTextures);
        c.PrecomputedVertexState = r.ReadUInt32(); c.PrecomputedFragmentState = r.ReadUInt32();
        c.VisFrontEnable = r.ReadBool(); c.VisBackEnable = r.ReadBool(); c.VisFrontIncrement = r.ReadBool(); c.VisBackIncrement = r.ReadBool();
        c.VisFrontIndex = r.ReadUInt32(); c.VisBackIndex = r.ReadUInt32(); c.VisBuffer = r.ReadUInt32(); c.VisStride = r.ReadUInt32(); c.MarkerDepth = r.ReadInt32();
        ReadArray(r, c.YuvProfile);
        c.Deferred = r.ReadBool(); c.InCommandList = r.ReadBool(); c.ViewportSet = r.ReadBool(); c.RegionClipSet = r.ReadBool();
        c.VdmCallback = r.ReadUInt32(); c.VertexCallback = r.ReadUInt32(); c.FragmentCallback = r.ReadUInt32(); c.CallbackData = r.ReadUInt32();
        c.VdmBuffer = r.ReadUInt32(); c.VdmBufferSize = r.ReadUInt32(); c.VdmUsed = r.ReadUInt32(); c.VdmListStart = r.ReadUInt32(); c.SizeScratch = r.ReadUInt32();
        return c;
    }
}
