using System.Collections.Concurrent;
using VitaSharp.Core.Hle.Audio;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceNgs: the Vita's voice-graph audio engine. Voices (players, ATRAC9 decoders, busses) are patched together;
/// sceNgsSystemUpdate renders one granule and the master buss output is read back by the game.
/// </summary>
public sealed class Ngs : HleModule
{
    const int OK = 0, ERROR = unchecked((int)0x804A0001), INVALID_ARG = unchecked((int)0x804A0002), INVALID_STATE = unchecked((int)0x804A0010),
        INVALID_HANDLE = unchecked((int)0x804A000C);
    const int AT9_END_OF_DATA = 0, AT9_SWAPPED = 1, AT9_DECODE_ERROR = 3, AT9_LOOPED = 4;
    const int PLAYER_END_OF_DATA = 0, PLAYER_SWAPPED = 1, PLAYER_LOOPED = 2;

    readonly ConcurrentDictionary<uint, NgsSystem> _systems = new();
    readonly ConcurrentDictionary<uint, NgsRack> _racks = new();
    readonly ConcurrentDictionary<uint, NgsVoice> _voices = new();
    readonly ConcurrentDictionary<uint, NgsPatch> _patches = new();
    uint _definitions;

    uint Definitions()
    {
        if (_definitions != 0) return _definitions;
        uint a = Emu.Memory.Alloc((uint)NgsBuss.Max * 8, "NGS voice definitions", MemoryRegion.User, 16);
        for (uint i = 0; i < (uint)NgsBuss.Max; i++)
        {
            GuestMemory.W32(a + i * 8, i);
            GuestMemory.W32(a + i * 8 + 4, NgsModules.OutputCount((NgsBuss)i));
        }
        return _definitions = a;
    }

    uint Def(NgsBuss b) => Definitions() + (uint)b * 8;

    [Hle] uint sceNgsVoiceDefGetMasterBuss() => Def(NgsBuss.Master);
    [Hle] uint sceNgsVoiceDefGetCompressorBuss() => Def(NgsBuss.Compressor);
    [Hle] uint sceNgsVoiceDefGetCompressorSideChainBuss() => Def(NgsBuss.SideChainCompressor);
    [Hle] uint sceNgsVoiceDefGetDelayBuss() => Def(NgsBuss.Delay);
    [Hle] uint sceNgsVoiceDefGetDistortionBuss() => Def(NgsBuss.Distortion);
    [Hle] uint sceNgsVoiceDefGetEnvelopeBuss() => Def(NgsBuss.Envelope);
    [Hle] uint sceNgsVoiceDefGetEqBuss() => Def(NgsBuss.Equalization);
    [Hle] uint sceNgsVoiceDefGetMixerBuss() => Def(NgsBuss.Mixer);
    [Hle] uint sceNgsVoiceDefGetPauserBuss() => Def(NgsBuss.Pauser);
    [Hle] uint sceNgsVoiceDefGetPitchShiftBuss() => Def(NgsBuss.PitchShift);
    [Hle] uint sceNgsVoiceDefGetReverbBuss() => Def(NgsBuss.Reverb);
    [Hle] uint sceNgsVoiceDefGetSasEmuVoice() => Def(NgsBuss.SasEmulation);
    [Hle] uint sceNgsVoiceDefGetSimpleVoice() => Def(NgsBuss.Simple);
    [Hle] uint sceNgsVoiceDefGetAtrac9Voice() => Def(NgsBuss.Atrac9);
    [Hle] uint sceNgsVoiceDefGetSimpleAtrac9Voice() => Def(NgsBuss.SimpleAtrac9);
    [Hle] uint sceNgsVoiceDefGetScreamVoice() => Def(NgsBuss.Scream);
    [Hle] uint sceNgsVoiceDefGetScreamAtrac9Voice() => Def(NgsBuss.ScreamAtrac9);
    [Hle] uint sceNgsVoiceDefGetTemplate1() => Def(NgsBuss.NormalPlayer);

    // =================================================================== system / rack

    [Hle]
    int sceNgsSystemGetRequiredMemorySize(uint parameters, Ptr<uint> size)
    {
        if (parameters == 0 || size.Address == 0) return INVALID_ARG;
        size.Value = 256;
        return OK;
    }

    // Sulpha is the NGS debugging/profiling link to the PC tools: nothing is connected
    [Hle] int sceSulphaNgsSetVoiceName(uint voice, uint name) => 0;
    [Hle] int sceSulphaNgsSetSynthName(uint synth, uint name) => 0;
    [Hle] int sceSulphaNgsSetRackName(uint rack, uint name) => 0;
    [Hle] int sceSulphaNgsTrace(uint message) => 0;

    [Hle]
    int sceNgsSystemInit(uint memspace, uint memsize, uint p, Ptr<uint> outSystem)
    {
        if (memspace == 0 || p == 0 || outSystem.Address == 0) return INVALID_ARG;
        var s = new NgsSystem
        {
            Handle = memspace,
            MaxVoices = (int)GuestMemory.R32(p + 4),
            Granularity = Math.Max((int)GuestMemory.R32(p + 8), 1),
            SampleRate = (int)GuestMemory.R32(p + 12),
        };
        if (s.SampleRate <= 0) s.SampleRate = 48000;
        s.CallbackInfo = Emu.Memory.Alloc(64, "NGS callback info", MemoryRegion.User, 16);
        _systems[memspace] = s;
        outSystem.Value = memspace;
        Log.Info($"NGS system: granularity {s.Granularity}, {s.SampleRate} Hz, {s.MaxVoices} voices");
        return OK;
    }

    [Hle]
    int sceNgsSystemRelease(uint system)
    {
        if (!_systems.TryRemove(system, out var s)) return INVALID_HANDLE;
        lock (s.Lock)
        {
            foreach (var r in s.Racks) ReleaseRack(r);
            s.Racks.Clear();
            s.Queue.Clear();
        }
        return OK;
    }

    [Hle] int sceNgsSystemSetFlags(uint system, uint flags) => OK;
    [Hle] int sceNgsSystemLock(uint system) { if (_systems.TryGetValue(system, out var s)) Monitor.Enter(s.Lock); return OK; }
    [Hle] int sceNgsSystemUnlock(uint system) { if (_systems.TryGetValue(system, out var s) && Monitor.IsEntered(s.Lock)) Monitor.Exit(s.Lock); return OK; }
    [Hle] int sceNgsSystemSetParamErrorCallback(uint system, uint cb) => OK;
    [Hle] int sceNgsRackSetParamErrorCallback(uint rack, uint cb) => OK;

    static uint VoiceFootprint(NgsBuss type, uint patchesPerOutput)
    {
        uint paramBytes = 0;
        foreach (var k in NgsModules.Chain(type)) paramBytes += 2 * ((NgsModules.ParamSize(k) + 15) & ~15u);
        return 32 + paramBytes + patchesPerOutput * NgsModules.OutputCount(type) * 32;
    }

    [Hle]
    int sceNgsRackGetRequiredMemorySize(uint system, uint desc, Ptr<uint> size)
    {
        if (!_systems.ContainsKey(system)) return INVALID_HANDLE;
        if (desc == 0 || size.Address == 0) return INVALID_ARG;
        uint def = GuestMemory.R32(desc);
        if (def == 0) return INVALID_ARG;
        var type = (NgsBuss)GuestMemory.R32(def);
        int voices = (int)GuestMemory.R32(desc + 4);
        uint patchesPerOutput = GuestMemory.R32(desc + 16);
        if (voices <= 0 || type >= NgsBuss.Max) return INVALID_ARG;
        size.Value = 64 + (uint)voices * VoiceFootprint(type, patchesPerOutput);
        return OK;
    }

    [Hle]
    int sceNgsRackInit(uint system, uint bufferInfo, uint desc, Ptr<uint> outRack)
    {
        if (!_systems.TryGetValue(system, out var sys)) return INVALID_HANDLE;
        if (bufferInfo == 0 || desc == 0 || outRack.Address == 0) return INVALID_ARG;
        uint mem = GuestMemory.R32(bufferInfo), memSize = GuestMemory.R32(bufferInfo + 4);
        uint def = GuestMemory.R32(desc);
        if (mem == 0 || def == 0) return INVALID_ARG;
        var type = (NgsBuss)GuestMemory.R32(def);
        var rack = new NgsRack
        {
            Handle = mem, System = sys, Type = type, Chain = NgsModules.Chain(type),
            ChannelsPerVoice = (int)GuestMemory.R32(desc + 8), MaxPatchesPerInput = (int)GuestMemory.R32(desc + 12),
            PatchesPerOutput = Math.Max((int)GuestMemory.R32(desc + 16), 1),
            OutputCount = GuestMemory.R32(def + 4), AllocCursor = mem + 64, AllocEnd = mem + memSize,
        };
        int voiceCount = (int)GuestMemory.R32(desc + 4);
        GuestMemory.Fill(mem, 0, Math.Min(memSize, 64));
        lock (sys.Lock)
        {
            for (int v = 0; v < voiceCount; v++)
            {
                uint handle = rack.Alloc(32);
                if (handle == 0) return ERROR;
                var voice = new NgsVoice { Handle = handle, Rack = rack };
                voice.Patches = new NgsPatch?[4][];
                for (int o = 0; o < 4; o++) voice.Patches[o] = new NgsPatch?[rack.PatchesPerOutput];
                voice.Input = new float[sys.Granularity * 2];
                voice.Modules = new NgsModuleData[rack.Chain.Length];
                for (int m = 0; m < rack.Chain.Length; m++)
                {
                    var kind = rack.Chain[m];
                    uint psize = NgsModules.ParamSize(kind);
                    uint addr = psize == 0 ? 0 : rack.Alloc(2 * ((psize + 15) & ~15u));
                    if (psize != 0 && addr == 0) return ERROR;
                    if (addr != 0) GuestMemory.Fill(addr, 0, psize);
                    var md = new NgsModuleData { Voice = voice, Index = m, Kind = kind, ParamAddr = addr, ParamSize = psize };
                    md.GuestState = kind switch
                    {
                        NgsModuleKind.Player or NgsModuleKind.Atrac9 => new byte[24],
                        NgsModuleKind.Output => new byte[sys.Granularity * 4],
                        NgsModuleKind.Envelope => new byte[20],
                        NgsModuleKind.Compressor => new byte[16],
                        _ => [],
                    };
                    if (kind is NgsModuleKind.Player or NgsModuleKind.Atrac9) md.Source = new NgsSourceState();
                    voice.Modules[m] = md;
                }
                rack.Voices.Add(voice);
                _voices[handle] = voice;
            }
            sys.Racks.Add(rack);
        }
        _racks[mem] = rack;
        outRack.Value = mem;
        return OK;
    }

    void ReleaseRack(NgsRack rack)
    {
        foreach (var v in rack.Voices)
        {
            rack.System.Queue.Remove(v);
            _voices.TryRemove(v.Handle, out _);
            foreach (var po in v.Patches) foreach (var p in po) if (p != null) _patches.TryRemove(p.Handle, out _);
        }
        _racks.TryRemove(rack.Handle, out _);
    }

    [Hle]
    int sceNgsRackRelease(uint rack, uint callback)
    {
        if (!_racks.TryGetValue(rack, out var r)) return INVALID_ARG;
        lock (r.System.Lock)
        {
            ReleaseRack(r);
            r.System.Racks.Remove(r);
        }
        if (callback != 0) GuestThread.Current?.CallGuest(callback, rack);
        return OK;
    }

    [Hle]
    int sceNgsRackGetVoiceHandle(uint rack, uint index, Ptr<uint> outVoice)
    {
        if (!_racks.TryGetValue(rack, out var r) || outVoice.Address == 0) return INVALID_ARG;
        if (index >= r.Voices.Count) return INVALID_ARG;
        outVoice.Value = r.Voices[(int)index].Handle;
        return OK;
    }

    // =================================================================== patches

    [Hle]
    int sceNgsPatchCreateRouting(uint setup, Ptr<uint> outPatch)
    {
        if (setup == 0 || outPatch.Address == 0) return INVALID_ARG;
        if (!_voices.TryGetValue(GuestMemory.R32(setup), out var src) || !_voices.TryGetValue(GuestMemory.R32(setup + 12), out var dst)) return INVALID_ARG;
        int outIndex = (int)GuestMemory.R32(setup + 4), sub = (int)GuestMemory.R32(setup + 8), destIndex = (int)GuestMemory.R32(setup + 16);
        var sys = src.Rack.System;
        lock (sys.Lock)
        {
            if (outIndex < 0 || outIndex >= 4) return ERROR;
            var slots = src.Patches[outIndex];
            if (sub == -1)
            {
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i] == null || !slots[i]!.Active) { sub = i; break; }
            }
            if (sub < 0 || sub >= slots.Length) return ERROR;
            if (slots[sub] is { Active: true }) return ERROR;
            var p = slots[sub];
            if (p == null)
            {
                uint h = src.Rack.Alloc(32);
                if (h == 0) h = Emu.Memory.Alloc(32, "NGS patch", MemoryRegion.User, 16);
                p = new NgsPatch { Handle = h, Source = src, Dest = dst };
                slots[sub] = p;
                _patches[h] = p;
            }
            else { p.Source = src; p.Dest = dst; }
            p.OutputIndex = outIndex;
            p.SubIndex = sub;
            p.DestIndex = destIndex;
            Array.Clear(p.Volume);
            Resort(sys);
            outPatch.Value = p.Handle;
        }
        return OK;
    }

    [Hle]
    int sceNgsPatchRemoveRouting(uint patch)
    {
        if (!_patches.TryGetValue(patch, out var p)) return INVALID_ARG;
        lock (p.Source.Rack.System.Lock) p.SubIndex = -1;
        return OK;
    }

    [Hle]
    int sceNgsPatchGetInfo(uint patch, uint propInfo, uint deliveryInfo)
    {
        if (!_patches.TryGetValue(patch, out var p)) return INVALID_ARG;
        if (propInfo != 0)
        {
            GuestMemory.W32(propInfo, (uint)p.Source.Rack.ChannelsPerVoice);
            GuestMemory.W32(propInfo + 4, (uint)p.Dest.Rack.ChannelsPerVoice);
            for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) GuestMemory.Write(propInfo + 8 + (uint)(i * 2 + j) * 4, p.Volume[i, j]);
        }
        if (deliveryInfo != 0)
        {
            GuestMemory.W32(deliveryInfo, p.Source.Handle);
            GuestMemory.W32(deliveryInfo + 4, (uint)p.OutputIndex);
            GuestMemory.W32(deliveryInfo + 8, (uint)p.SubIndex);
            GuestMemory.W32(deliveryInfo + 12, p.Dest.Handle);
            GuestMemory.W32(deliveryInfo + 16, (uint)p.DestIndex);
        }
        return OK;
    }

    [Hle]
    int sceNgsVoicePatchSetVolumesMatrix(uint patch, uint matrix)
    {
        if (!_patches.TryGetValue(patch, out var p) || !p.Active || matrix == 0) return INVALID_ARG;
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) p.Volume[i, j] = GuestMemory.Read<float>(matrix + (uint)(i * 2 + j) * 4);
        return OK;
    }

    [Hle]
    int sceNgsVoicePatchSetVolume(uint patch, int outCh, int inCh, float vol)
    {
        if (!_patches.TryGetValue(patch, out var p) || !p.Active || outCh is < 0 or > 1 || inCh is < 0 or > 1) return INVALID_ARG;
        p.Volume[outCh, inCh] = vol;
        return OK;
    }

    [Hle]
    int sceNgsVoicePatchSetVolumes(uint patch, int outCh, uint volumes, int count)
    {
        if (!_patches.TryGetValue(patch, out var p) || !p.Active || volumes == 0 || outCh is < 0 or > 1) return INVALID_ARG;
        for (int i = 0; i < Math.Min(count, 2); i++) p.Volume[outCh, i] = GuestMemory.Read<float>(volumes + (uint)i * 4);
        return OK;
    }

    [Hle]
    int sceNgsVoiceGetOutputPatch(uint voice, int outIndex, int sub, Ptr<uint> outPatch)
    {
        if (!_voices.TryGetValue(voice, out var v) || outPatch.Address == 0 || outIndex is < 0 or >= 4) return INVALID_ARG;
        if (sub < 0 || sub >= v.Patches[outIndex].Length) return INVALID_ARG;
        var p = v.Patches[outIndex][sub];
        outPatch.Value = p is { Active: true } ? p.Handle : 0;
        return OK;
    }

    /// <summary>Keeps every source voice before its destinations in the processing queue.</summary>
    static void Resort(NgsSystem s)
    {
        var q = s.Queue;
        for (int pass = 0; pass < q.Count; pass++)
        {
            bool moved = false;
            for (int i = 0; i < q.Count; i++)
            {
                var v = q[i];
                foreach (var outs in v.Patches)
                    foreach (var p in outs)
                    {
                        if (p is not { Active: true }) continue;
                        int d = q.IndexOf(p.Dest);
                        if (d >= 0 && d < i)
                        {
                            q.RemoveAt(d);
                            q.Add(p.Dest);
                            moved = true;
                        }
                    }
            }
            if (!moved) break;
        }
    }

    // =================================================================== voices

    NgsModuleData? Module(uint voice, uint index) => _voices.TryGetValue(voice, out var v) && index < v.Modules.Length ? v.Modules[index] : null;

    [Hle]
    int sceNgsVoiceInit(uint voice, uint preset, uint flags)
    {
        if (!_voices.TryGetValue(voice, out var v)) return INVALID_ARG;
        if (v.State == NgsVoiceState.Active) return INVALID_STATE;
        lock (v.Rack.System.Lock)
        {
            if (flags is 0 or 7) v.State = NgsVoiceState.Available;
            if ((flags & 1) != 0)
                foreach (var outs in v.Patches) foreach (var p in outs) if (p != null) p.SubIndex = -1;
            if ((flags & 2) != 0)
            {
                if (preset != 0) SetPreset(v, preset);
                else foreach (var m in v.Modules) { m.Source?.Reset(); Array.Clear(m.GuestState); }
            }
            if ((flags & 4) != 0) foreach (var m in v.Modules) { m.Callback = 0; m.UserData = 0; }
        }
        return OK;
    }

    bool SetPreset(NgsVoice v, uint preset)
    {
        int dataOff = (int)GuestMemory.R32(preset + 8);
        uint dataSize = GuestMemory.R32(preset + 12);
        int bypassOff = (int)GuestMemory.R32(preset + 16);
        uint bypassCount = GuestMemory.R32(preset + 20);
        bool ok = true;
        if (dataOff != 0) ok = ParseParamsBlock(v, (uint)(preset + dataOff), dataSize) == 0;
        if (bypassOff != 0)
            for (uint i = 0; i < bypassCount; i++)
            {
                uint idx = GuestMemory.R32((uint)(preset + bypassOff) + i * 4);
                if (idx < v.Modules.Length) v.Modules[idx].Bypassed = true;
            }
        return ok;
    }

    [Hle]
    int sceNgsVoiceSetPreset(uint voice, uint preset)
    {
        if (!_voices.TryGetValue(voice, out var v) || preset == 0) return INVALID_ARG;
        lock (v.Rack.System.Lock) return SetPreset(v, preset) ? OK : ERROR;
    }

    static int ParseParamsBlock(NgsVoice v, uint header, uint size)
    {
        int errors = 0;
        uint p = header, end = header + size;
        while (p + 16 <= end)
        {
            uint moduleId = GuestMemory.R32(p);
            uint descSize = GuestMemory.R32(p + 12);
            if (descSize == 0) { errors++; break; }
            if (moduleId < v.Modules.Length)
            {
                var m = v.Modules[moduleId];
                if (m.Locked != null || descSize > m.ParamSize || m.ParamAddr == 0) errors++;
                else GuestMemory.Copy(m.ParamAddr, p + 8, descSize);
            }
            else errors++;
            p += 8 + descSize;
        }
        return errors;
    }

    [Hle]
    int sceNgsVoiceSetParamsBlock(uint voice, uint header, uint size, Ptr<int> numErrors)
    {
        if (!_voices.TryGetValue(voice, out var v)) return INVALID_ARG;
        int errors;
        lock (v.Rack.System.Lock) errors = ParseParamsBlock(v, header, size);
        numErrors.TrySet(errors);
        return errors == 0 ? OK : ERROR;
    }

    [Hle]
    int sceNgsVoiceLockParams(uint voice, uint module, uint paramId, uint bufferInfo)
    {
        var m = Module(voice, module);
        if (m == null || bufferInfo == 0) return INVALID_ARG;
        lock (m.Voice.Rack.System.Lock)
        {
            if (m.Locked != null) return ERROR;
            m.Locked = m.ParamSize == 0 ? [] : GuestMemory.Span(m.ParamAddr, (int)m.ParamSize).ToArray();
            if (m.ParamAddr != 0) GuestMemory.W32(m.ParamAddr, paramId);
        }
        GuestMemory.W32(bufferInfo, m.ParamAddr);
        GuestMemory.W32(bufferInfo + 4, m.ParamSize);
        return OK;
    }

    [Hle]
    int sceNgsVoiceUnlockParams(uint voice, uint module)
    {
        var m = Module(voice, module);
        if (m == null) return INVALID_ARG;
        lock (m.Voice.Rack.System.Lock)
        {
            if (m.Locked == null) return ERROR;
            m.Locked = null;
        }
        return OK;
    }

    [Hle]
    int sceNgsVoiceBypassModule(uint voice, uint module, uint flag)
    {
        var m = Module(voice, module);
        if (m == null) return INVALID_ARG;
        m.Bypassed = (flag & 2) != 0;
        return OK;
    }

    [Hle]
    int sceNgsVoiceGetModuleBypass(uint voice, uint module, Ptr<uint> flag)
    {
        var m = Module(voice, module);
        if (m == null) return INVALID_ARG;
        flag.TrySet(m.Bypassed ? 2u : 0u);
        return OK;
    }

    [Hle]
    int sceNgsVoiceGetModuleType(uint voice, uint module, Ptr<uint> type)
    {
        var m = Module(voice, module);
        if (m == null) return INVALID_ARG;
        type.TrySet(NgsModules.Id(m.Kind));
        return OK;
    }

    [Hle]
    int sceNgsVoiceSetModuleCallback(uint voice, uint module, uint callback, uint userData)
    {
        var m = Module(voice, module);
        if (m == null) return INVALID_ARG;
        m.Callback = callback;
        m.UserData = userData;
        return OK;
    }

    [Hle]
    int sceNgsVoiceSetFinishedCallback(uint voice, uint callback, uint userData)
    {
        if (!_voices.TryGetValue(voice, out var v)) return INVALID_ARG;
        v.FinishedCallback = callback;
        v.FinishedUserData = userData;
        return OK;
    }

    [Hle]
    int sceNgsVoiceGetStateData(uint voice, uint module, uint mem, uint size)
    {
        var m = Module(voice, module);
        if (m == null) return INVALID_ARG;
        if (mem != 0)
        {
            GuestMemory.Fill(mem, 0, size);
            lock (m.Voice.Rack.System.Lock)
                m.GuestState.AsSpan(0, (int)Math.Min(size, (uint)m.GuestState.Length)).CopyTo(GuestMemory.Span(mem, (int)size));
        }
        return OK;
    }

    static uint StateBits(NgsVoice v)
    {
        uint s = v.State switch { NgsVoiceState.Active => 1u, NgsVoiceState.Finalizing => 4u, NgsVoiceState.Unloading => 8u, _ => 0u };
        if (v.Pending) s |= 0x10;
        if (v.Paused) s |= 0x20;
        if (v.KeyedOff) s |= 0x40;
        return s;
    }

    int _infoCalls;

    [Hle]
    int sceNgsVoiceGetInfo(uint voice, uint info)
    {
        if (!_voices.TryGetValue(voice, out var v) || info == 0) return INVALID_ARG;
        if (Log.Level <= LogLevel.Debug && (++_infoCalls & 63) == 0)
        {
            var pm = v.Modules.Length > 0 ? v.Modules[0] : null;
            Log.Debug($"ngs info voice 0x{voice:X8} state 0x{StateBits(v):X} frames {v.FrameCount} queued {v.Rack.System.Queue.Contains(v)}"
                + (pm?.Source != null ? $" player pos {GetState(pm, 0)} buf {GetState(pm, 4)} samples {GetState(pm, 8)} bytes {GetState(pm, 12)} avail {pm.Source.Available}" : ""));
        }
        GuestMemory.W32(info, StateBits(v));
        GuestMemory.W32(info + 4, (uint)v.Modules.Length);
        GuestMemory.W32(info + 8, 1);
        GuestMemory.W32(info + 12, v.Rack.OutputCount);
        GuestMemory.W32(info + 16, (uint)v.Rack.PatchesPerOutput);
        GuestMemory.W32(info + 20, v.FrameCount);
        return OK;
    }

    [Hle] int sceNgsVoiceGetParamsOutOfRange(uint voice, uint module, uint buf) => OK;

    void Transition(NgsVoice v, NgsVoiceState to)
    {
        var from = v.State;
        v.State = to;
        foreach (var m in v.Modules)
        {
            if (m.Source == null) continue;
            if (to == NgsVoiceState.Active && from == NgsVoiceState.Available)
            {
                m.Source.Reset();
                Array.Clear(m.GuestState);
                if (m.Kind == NgsModuleKind.Player)
                {
                    var pl = PlayerLayout(m); SetState(m, 4, pl.StartBuffer >= 0 ? m.ParamI8(pl.StartBuffer) : 0); // start buffer
                    SetState(m, 0, pl.StartBytes >= 0 ? (int)m.Param32(pl.StartBytes) : 0); // start bytes
                }
            }
        }
    }

    /// <summary>
    /// Offsets inside SceNgsPlayerParams. Two SDK layouts exist and games ship either: the 84-byte one with 12-byte
    /// buffer entries (buffer, bytes, loops, next) and a startBytes/startBuffer tail, and the 96-byte one (older
    /// titles such as God of War) with 16-byte entries (4 reserved bytes) and no start fields. The params descriptor
    /// size tells them apart.
    /// </summary>
    readonly record struct PlayerParamLayout(int Stride, int Freq, int StartBytes, int Channels, int Type, int StartBuffer);

    static PlayerParamLayout PlayerLayout(NgsModuleData m)
    {
        uint size = m.ParamSize == 0 ? 0 : m.Param32(4);
        return size >= 0x60
            ? new PlayerParamLayout(16, 0x48, -1, 0x58, 0x5B, -1)
            : new PlayerParamLayout(12, 56, 72, 76, 79, 81);
    }

    static void SetState(NgsModuleData m, int off, int v) => BitConverter.TryWriteBytes(m.GuestState.AsSpan(off), v);
    static int GetState(NgsModuleData m, int off) => BitConverter.ToInt32(m.GuestState, off);

    [Hle]
    int sceNgsVoicePlay(uint voice)
    {
        if (!_voices.TryGetValue(voice, out var v)) return INVALID_ARG;
        var s = v.Rack.System;
        lock (s.Lock)
        {
            if (v.State != NgsVoiceState.Available) return ERROR;
            Transition(v, NgsVoiceState.Active);
            if (!v.Paused && !s.Queue.Contains(v))
            {
                s.Queue.Add(v);
                Resort(s);
            }
        }
        return OK;
    }

    void Stop(NgsVoice v)
    {
        if (v.State is not (NgsVoiceState.Active or NgsVoiceState.Finalizing)) return;
        Transition(v, NgsVoiceState.Available);
        v.Rack.System.Queue.Remove(v);
        v.Paused = false;
    }

    [Hle]
    int sceNgsVoiceKill(uint voice)
    {
        if (!_voices.TryGetValue(voice, out var v)) return INVALID_ARG;
        lock (v.Rack.System.Lock) Stop(v);
        return OK;
    }

    [Hle]
    int sceNgsVoiceKeyOff(uint voice)
    {
        if (!_voices.TryGetValue(voice, out var v)) return INVALID_ARG;
        lock (v.Rack.System.Lock)
        {
            if (v.State == NgsVoiceState.Active) v.State = NgsVoiceState.Finalizing;
            v.KeyedOff = true;
            InvokeCallback(v, v.FinishedCallback, v.FinishedUserData, 0, 0, 0, 0);
            v.KeyedOff = false;
            Stop(v);
        }
        return OK;
    }

    [Hle]
    int sceNgsVoicePause(uint voice)
    {
        if (!_voices.TryGetValue(voice, out var v)) return INVALID_ARG;
        lock (v.Rack.System.Lock)
        {
            if (v.Paused) return INVALID_STATE;
            v.Paused = true;
            v.Rack.System.Queue.Remove(v);
        }
        return OK;
    }

    [Hle]
    int sceNgsVoiceResume(uint voice)
    {
        if (!_voices.TryGetValue(voice, out var v)) return INVALID_ARG;
        var s = v.Rack.System;
        lock (s.Lock)
        {
            if (!v.Paused) return OK;
            v.Paused = false;
            if (v.State is NgsVoiceState.Active or NgsVoiceState.Finalizing && !s.Queue.Contains(v)) { s.Queue.Add(v); Resort(s); }
        }
        return OK;
    }

    // =================================================================== update

    void InvokeCallback(NgsVoice v, uint callback, uint userData, uint moduleId, int reason, int reason2, uint ptr)
    {
        if (callback == 0) return;
        var t = GuestThread.Current;
        if (t == null) return;
        uint info = v.Rack.System.CallbackInfo;
        GuestMemory.W32(info, v.Handle);
        GuestMemory.W32(info + 4, v.Rack.Handle);
        GuestMemory.W32(info + 8, moduleId);
        GuestMemory.W32(info + 12, (uint)reason);
        GuestMemory.W32(info + 16, (uint)reason2);
        GuestMemory.W32(info + 20, ptr);
        GuestMemory.W32(info + 24, userData);
        t.CallGuest(callback, info);
    }

    [Hle]
    int sceNgsSystemUpdate(uint system)
    {
        if (!_systems.TryGetValue(system, out var s)) return INVALID_HANDLE;
        lock (s.Lock)
        {
            var queue = s.Queue.ToArray();
            foreach (var v in queue) Array.Clear(v.Input);
            foreach (var v in queue)
            {
                v.Product = null;
                bool finished = false;
                uint finishedModule = 0;
                foreach (var m in v.Modules)
                {
                    if (Process(v, m))
                    {
                        finished = true;
                        finishedModule = NgsModules.Id(m.Kind);
                    }
                }
                if (finished)
                {
                    v.KeyedOff = true;
                    v.State = NgsVoiceState.Finalizing;
                    InvokeCallback(v, v.FinishedCallback, v.FinishedUserData, finishedModule, 0, 0, 0);
                    v.KeyedOff = false;
                    Stop(v);
                }
                if (v.Product != null) Deliver(v, queue);
                v.FrameCount++;
            }
        }
        return OK;
    }

    static void Deliver(NgsVoice v, NgsVoice[] queue)
    {
        var src = v.Product!;
        int g = v.Rack.System.Granularity;
        foreach (var p in v.Patches[0])
        {
            if (p is not { Active: true } || Array.IndexOf(queue, p.Dest) < 0) continue;
            var dst = p.Dest.Input;
            float m00 = p.Volume[0, 0], m01 = p.Volume[0, 1], m10 = p.Volume[1, 0], m11 = p.Volume[1, 1];
            if (v.Rack.ChannelsPerVoice == 1) { m10 = 0; m11 = 0; }
            if (p.Dest.Rack.ChannelsPerVoice == 1) { m01 = 0; m11 = 0; }
            for (int k = 0; k < g; k++)
            {
                float l = src[k * 2], r = src[k * 2 + 1];
                dst[k * 2] = Math.Clamp(dst[k * 2] + l * m00 + r * m10, -1f, 1f);
                dst[k * 2 + 1] = Math.Clamp(dst[k * 2 + 1] + l * m01 + r * m11, -1f, 1f);
            }
        }
    }

    bool Process(NgsVoice v, NgsModuleData m)
    {
        int g = v.Rack.System.Granularity;
        switch (m.Kind)
        {
            case NgsModuleKind.InputMixer:
                v.Product = v.Input;
                return false;
            case NgsModuleKind.Output:
            {
                var span = m.GuestState.AsSpan();
                for (int i = 0; i < g * 2; i++)
                    BitConverter.TryWriteBytes(span[(i * 2)..], (short)Math.Clamp(v.Input[i] * 32768f, -32768f, 32767f));
                return false;
            }
            case NgsModuleKind.Player:
                return ProcessPlayer(v, m, g);
            case NgsModuleKind.Atrac9:
                return ProcessAtrac9(v, m, g);
            default:
                return false; // effects are passthrough for now
        }
    }

    float[] Emit(NgsModuleData m, int g)
    {
        var st = m.Source!;
        // reused per module: the mixer consumes Product before the next tick
        var outBuf = m.EmitBuf is { } eb && eb.Length == g * 2 ? eb : m.EmitBuf = new float[g * 2];
        Array.Clear(outBuf);
        int avail = Math.Min(st.Available, g);
        for (int i = 0; i < avail * 2; i++) outBuf[i] = st.Pcm[st.PcmRead * 2 + i];
        st.PcmRead += avail;
        return outBuf;
    }

    // ---- player (PCM16 / HEVAG)

    bool ProcessPlayer(NgsVoice v, NgsModuleData m, int g)
    {
        var st = m.Source!;
        var pl = PlayerLayout(m);
        int channels = m.ParamI8(pl.Channels);
        if (channels <= 0) channels = 2;
        bool adpcm = m.ParamI8(pl.Type) != 0;
        float freq = m.ParamF(pl.Freq), scalar = m.ParamF(pl.Freq + 4);
        if (!(freq > 0 && freq <= 192000)) freq = v.Rack.System.SampleRate;
        if (!(scalar > 0 && scalar <= 10)) scalar = 1;
        double ratio = freq * scalar / v.Rack.System.SampleRate;
        bool finished = false;
        st.Compact();
        int guard = 0;
        while (st.Available < g && guard++ < 64)
        {
            int cur = GetState(m, 4);
            if (cur < 0 || cur >= 4) { finished = true; break; }
            int bp = 8 + cur * pl.Stride;
            uint buf = m.Param32(bp);
            int bytes = (int)m.Param32(bp + 4);
            if (buf == 0 || bytes == 0) { finished = true; break; }
            int pos = GetState(m, 0);
            if (pos >= bytes)
            {
                SetState(m, 0, 0);
                st.LoopCount++;
                int loops = m.ParamI16(bp + 8);
                if (loops != -1 && st.LoopCount > loops)
                {
                    int next = m.ParamI16(bp + 10);
                    st.LoopCount = 0;
                    SetState(m, 4, next);
                    if (next < 0 || next >= 4 || m.Param32(8 + next * pl.Stride) == 0 || m.Param32(8 + next * pl.Stride + 4) == 0)
                    {
                        InvokeCallback(v, m.Callback, m.UserData, 0x5CE6, PLAYER_END_OF_DATA, 0, 0);
                        finished = true;
                        break;
                    }
                    InvokeCallback(v, m.Callback, m.UserData, 0x5CE6, PLAYER_SWAPPED, cur, m.Param32(8 + next * pl.Stride));
                }
                else InvokeCallback(v, m.Callback, m.UserData, 0x5CE6, PLAYER_LOOPED, st.LoopCount, buf);
                continue;
            }
            int needFrames = (int)Math.Ceiling((g - st.Available) * ratio) + 16;
            int decodedLen;
            int consumed;
            if (adpcm)
            {
                int frameBytes = 16 * channels;
                int frames = Math.Max(1, (needFrames + 27) / 28);
                consumed = Math.Min(frames * frameBytes, bytes - pos);
                consumed -= consumed % frameBytes;
                if (consumed <= 0) { SetState(m, 0, bytes); continue; }
                decodedLen = DecodeHevag(buf + (uint)pos, consumed, channels, st);
            }
            else
            {
                consumed = Math.Min(needFrames * channels * 2, bytes - pos);
                consumed -= consumed % (channels * 2);
                if (consumed <= 0) { SetState(m, 0, bytes); continue; }
                var src = GuestMemory.Span<short>(buf + (uint)pos, consumed / 2);
                int n = consumed / 2 / channels;
                var decoded = st.DecodeBuffer(n * 2);
                decodedLen = n * 2;
                for (int i = 0; i < n; i++)
                {
                    float l = src[i * channels] / 32768f;
                    decoded[i * 2] = l;
                    decoded[i * 2 + 1] = channels > 1 ? src[i * channels + 1] / 32768f : l;
                }
            }
            st.Push(st.DecodeBuf.AsSpan(0, decodedLen), ratio);
            SetState(m, 0, pos + consumed);
            SetState(m, 12, GetState(m, 12) + consumed);
            SetState(m, 20, GetState(m, 20) + consumed);
        }
        int produced = Math.Min(st.Available, g);
        v.Product = Emit(m, g);
        SetState(m, 8, GetState(m, 8) + produced * channels);
        SetState(m, 16, GetState(m, 16) + produced * channels);
        return finished;
    }

    /// <summary>Decodes into the source state's reusable buffer; returns the number of floats written.</summary>
    static int DecodeHevag(uint addr, int size, int channels, NgsSourceState st)
    {
        int framesPerChannel = size / 16 / channels;
        int outLen = framesPerChannel * 28 * 2;
        var outBuf = st.DecodeBuffer(outLen);
        int ch = 0, block = 0;
        for (int f = 0; f < size / 16; f++)
        {
            uint fr = addr + (uint)f * 16;
            byte b0 = GuestMemory.Read<byte>(fr), b1 = GuestMemory.Read<byte>(fr + 1);
            int coef = ((b0 >> 4) & 0xF) | (b1 & 0xF0);
            int shift = b0 & 0xF;
            int flag = b1 & 0xF;
            if (coef > 127) coef = 0;
            shift = 20 - shift;
            int h1 = st.AdpcmHist[ch, 0], h2 = st.AdpcmHist[ch, 1], h3 = st.AdpcmHist[ch, 2], h4 = st.AdpcmHist[ch, 3];
            for (int j = 0; j < 28; j++)
            {
                int sample = 0;
                if (flag < 7)
                {
                    byte nib = GuestMemory.Read<byte>(fr + 2 + (uint)(j / 2));
                    sample = HevagTables.Nibbles[(j & 1) != 0 ? nib >> 4 : nib & 0xF] << shift;
                    sample += (h1 * HevagTables.Coefs[coef, 0] + h2 * HevagTables.Coefs[coef, 1] + h3 * HevagTables.Coefs[coef, 2] + h4 * HevagTables.Coefs[coef, 3]) >> 5;
                    sample >>= 8;
                }
                float fs = Math.Clamp(sample, -32768, 32767) / 32768f;
                int o = (block * 28 + j) * 2;
                if (channels == 1) { outBuf[o] = fs; outBuf[o + 1] = fs; }
                else if (ch < 2) outBuf[o + ch] = fs;
                h4 = h3; h3 = h2; h2 = h1; h1 = sample;
            }
            st.AdpcmHist[ch, 0] = h1; st.AdpcmHist[ch, 1] = h2; st.AdpcmHist[ch, 2] = h3; st.AdpcmHist[ch, 3] = h4;
            ch++;
            if (ch == channels) { ch = 0; block++; }
        }
        return outLen;
    }

    // ---- ATRAC9

    bool ProcessAtrac9(NgsVoice v, NgsModuleData m, int g)
    {
        var st = m.Source!;
        float freq = m.ParamF(72), scalar = m.ParamF(76);
        if (!(freq > 0 && freq <= 192000)) freq = v.Rack.System.SampleRate;
        if (!(scalar > 0 && scalar <= 10)) scalar = 1;
        double ratio = freq * scalar / v.Rack.System.SampleRate;
        uint config = m.Param32(92);
        int cur0 = GetState(m, 4);
        if (cur0 < 0 || cur0 >= 4 || m.Param32(8 + cur0 * 16) == 0) { v.Product = Emit(m, g); return true; }

        if (st.At9 == null || st.At9Config != config)
        {
            try
            {
                st.At9 = new global::VitaSharp.Core.Hle.Audio.Atrac9.Atrac9Decoder();
                st.At9.Initialize(BitConverter.GetBytes(config));
                st.At9Config = config;
                st.At9Out = new short[st.At9.Config.ChannelCount][];
                for (int c = 0; c < st.At9Out.Length; c++) st.At9Out[c] = new short[st.At9.Config.SuperframeSamples];
                st.Staging = new byte[st.At9.Config.SuperframeBytes];
                st.StagingLen = 0;
            }
            catch (Exception e)
            {
                Log.WarnOnce("at9cfg" + config, $"NGS ATRAC9: bad config 0x{config:X8}: {e.GetBaseException()}");
                v.Product = Emit(m, g);
                return true;
            }
        }
        var cfg = st.At9.Config;
        int sfBytes = cfg.SuperframeBytes, sfSamples = cfg.SuperframeSamples;
        bool finished = false;
        st.Compact();
        int guard = 0;
        while (st.Available < g && guard++ < 64)
        {
            int cur = GetState(m, 4);
            if (cur < 0 || cur >= 4) { finished = true; break; }
            int bp = 8 + cur * 16;
            uint buf = m.Param32(bp);
            int bytes = (int)m.Param32(bp + 4);
            if (buf == 0 || bytes == 0) { finished = true; break; }
            int pos = GetState(m, 0);
            if (pos >= bytes)
            {
                SetState(m, 0, 0);
                st.LoopCount++;
                int loops = m.ParamI16(bp + 8);
                if (loops != -1 && st.LoopCount > loops)
                {
                    int next = m.ParamI16(bp + 10);
                    st.LoopCount = 0;
                    SetState(m, 4, next);
                    if (next < 0 || next >= 4 || m.Param32(8 + next * 16) == 0 || m.Param32(8 + next * 16 + 4) == 0)
                    {
                        InvokeCallback(v, m.Callback, m.UserData, 0x5CAA, AT9_END_OF_DATA, 0, 0);
                        finished = true;
                        break;
                    }
                    InvokeCallback(v, m.Callback, m.UserData, 0x5CAA, AT9_SWAPPED, cur, m.Param32(8 + next * 16));
                }
                else InvokeCallback(v, m.Callback, m.UserData, 0x5CAA, AT9_LOOPED, st.LoopCount, buf);
                continue;
            }
            // skip a RIFF header if the game hands us a whole .at9 file
            if (pos == 0 && bytes > 12 && GuestMemory.R32(buf) == 0x46464952 && GuestMemory.R32(buf + 8) == 0x45564157)
            {
                uint p = buf + 12;
                while (p + 8 <= buf + (uint)bytes && GuestMemory.R32(p) != 0x61746164) p += 8 + GuestMemory.R32(p + 4);
                SetState(m, 0, (int)(p + 8 - buf));
                continue;
            }
            int take = Math.Min(sfBytes - st.StagingLen, bytes - pos);
            GuestMemory.Span(buf + (uint)pos, take).CopyTo(st.Staging.AsSpan(st.StagingLen));
            st.StagingLen += take;
            SetState(m, 0, pos + take);
            if (st.StagingLen < sfBytes) continue;
            st.StagingLen = 0;

            int start = 0, count = sfSamples;
            int sampleIndex = pos / sfBytes * sfSamples;
            int discardStart = m.ParamI16(bp + 12), discardEnd = m.ParamI16(bp + 14);
            if (discardStart > sampleIndex) { int skip = Math.Min(sfSamples, discardStart - sampleIndex); start += skip; count -= skip; }
            int samplesLeftAfter = Math.Max(0, (bytes - pos) / sfBytes - 1) * sfSamples;
            if (discardEnd > samplesLeftAfter) count -= Math.Min(count, discardEnd - samplesLeftAfter);
            try
            {
                st.At9.Decode(st.Staging, st.At9Out!);
            }
            catch (Exception)
            {
                InvokeCallback(v, m.Callback, m.UserData, 0x5CAA, AT9_DECODE_ERROR, GetState(m, 0), buf);
                continue;
            }
            var pcm = new float[Math.Max(count, 0) * 2];
            for (int i = 0; i < count; i++)
            {
                float l = st.At9Out![0][start + i] / 32768f;
                pcm[i * 2] = l;
                pcm[i * 2 + 1] = st.At9Out.Length > 1 ? st.At9Out[1][start + i] / 32768f : l;
            }
            st.Push(pcm, ratio);
            SetState(m, 12, GetState(m, 12) + sfBytes);
            SetState(m, 20, GetState(m, 20) + sfBytes);
            int ch = Math.Max(1, (int)m.ParamI8(88));
            SetState(m, 8, GetState(m, 8) + count * ch);
            SetState(m, 16, GetState(m, 16) + count * ch);
        }
        v.Product = Emit(m, g);
        return finished;
    }

    // =================================================================== misc

    [Hle]
    int sceNgsAT9GetSectionDetails(uint samplesStart, uint numSamples, uint config, uint info)
    {
        if (info == 0) return INVALID_ARG;
        if ((config & 0xFF) != 0xFE) return ERROR;
        uint srIndex = (config >> 12) & 0xF;
        uint frameBytes = ((((config >> 16) & 0xFF) << 3) | ((config >> 29) & 7)) + 1;
        int spf = srIndex switch { 1 => 64, 4 => 128, 7 => 256, _ => 0 };
        if (spf == 0) return ERROR;
        bool super = ((config >> 27) & 3) != 0;
        if ((samplesStart & 0x80000000) != 0)
        {
            samplesStart &= 0x7FFFFFFF;
            samplesStart = samplesStart > spf ? samplesStart - (uint)spf : 0;
        }
        GuestMemory.W32(info + 12, super ? 1u : 0u);
        if (super)
        {
            int sfb = (int)frameBytes * 4, sfs = spf * 4;
            int s0 = (int)samplesStart + spf;
            int sfOff = s0 / sfs;
            int startByte = sfOff * sfb, startSkip = s0 - sfOff * sfs;
            int total = (s0 + (int)numSamples + sfs - 1) / sfs;
            int numBytes = (total - sfOff) * sfb;
            int endSkip = total * sfs - (s0 + (int)numSamples);
            if (startSkip < spf && sfOff > 0) { startByte -= sfb; startSkip += sfs; numBytes += sfb; }
            GuestMemory.W32(info, (uint)startByte);
            GuestMemory.W32(info + 4, (uint)numBytes);
            GuestMemory.Write(info + 8, (short)startSkip);
            GuestMemory.Write(info + 10, (short)endSkip);
        }
        else
        {
            int fOff = (int)samplesStart / spf;
            int startSkip = ((int)samplesStart + spf) - fOff * spf;
            int total = (startSkip + (int)numSamples + spf - 1) / spf;
            GuestMemory.W32(info, (uint)(fOff * frameBytes));
            GuestMemory.W32(info + 4, (uint)(total * frameBytes));
            GuestMemory.Write(info + 8, (short)startSkip);
            GuestMemory.Write(info + 10, (short)(total * spf - (startSkip + (int)numSamples)));
        }
        return OK;
    }

    [Hle] int sceNgsModuleGetNumPresets(uint system, uint module, Ptr<uint> num) { num.TrySet(0); return OK; }
    [Hle] int sceNgsModuleGetPreset(uint system, uint module, uint index, uint buf) => ERROR;
}
