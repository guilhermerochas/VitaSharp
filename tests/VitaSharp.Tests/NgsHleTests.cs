using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceNgs through its HLE entry points: system and rack setup, voice definitions, patches (routing and volume matrix),
/// the player module rendering PCM16 into the master buss, voice states (play, pause, key off, kill) and parameter
/// locking. Error codes are the SDK's SCE_NGS_ERROR_* (0x804A00xx).
/// </summary>
[Collection("guest")]
public sealed class NgsHleTests(GuestMemoryFixture fx) : IDisposable
{
    const int OK = 0, ERROR = unchecked((int)0x804A0001), INVALID_ARG = unchecked((int)0x804A0002),
        INVALID_STATE = unchecked((int)0x804A0010), INVALID_HANDLE = unchecked((int)0x804A000C), PATCH_NOT_AVAIL = unchecked((int)0x804A000E);
    const int Granularity = 256;

    readonly HleHarness _h = new(fx, "ngs");
    Ngs N => _h.Module<Ngs>();

    public void Dispose() => _h.Dispose();

    uint Out() => _h.Word(0);

    /// <summary>SceNgsSystemInitParams: +4 max voices, +8 granularity, +12 sample rate.</summary>
    uint System()
    {
        uint p = _h.Alloc(32);
        GuestMemory.W32(p + 4, 16); GuestMemory.W32(p + 8, Granularity); GuestMemory.W32(p + 12, 48000);
        uint mem = _h.Alloc(256), o = Out();
        Assert.Equal(OK, Call(N, "sceNgsSystemInit", mem, 256u, p, new Ptr<uint>(o)));
        return GuestMemory.R32(o);
    }

    /// <summary>A rack of <paramref name="voices"/> voices of the given definition: SceNgsRackDescription {definition,
    /// voices, channelsPerVoice, maxPatchesPerInput, patchesPerOutput}, memory sized by GetRequiredMemorySize.</summary>
    uint Rack(uint system, uint def, int voices, int channels)
    {
        uint desc = _h.Alloc(32);
        GuestMemory.W32(desc, def); GuestMemory.W32(desc + 4, (uint)voices); GuestMemory.W32(desc + 8, (uint)channels);
        GuestMemory.W32(desc + 12, 2); GuestMemory.W32(desc + 16, 2);
        uint size = Out();
        Assert.Equal(OK, Call(N, "sceNgsRackGetRequiredMemorySize", system, desc, new Ptr<uint>(size)));
        uint bytes = GuestMemory.R32(size);
        uint mem = _h.Alloc(bytes), info = _h.Alloc(8), o = Out();
        GuestMemory.W32(info, mem); GuestMemory.W32(info + 4, bytes);
        Assert.Equal(OK, Call(N, "sceNgsRackInit", system, info, desc, new Ptr<uint>(o)));
        return GuestMemory.R32(o);
    }

    uint Voice(uint rack, uint index)
    {
        uint o = Out();
        Assert.Equal(OK, Call(N, "sceNgsRackGetVoiceHandle", rack, index, new Ptr<uint>(o)));
        return GuestMemory.R32(o);
    }

    uint Patch(uint src, uint dst)
    {
        uint setup = _h.Alloc(20), o = Out();
        GuestMemory.W32(setup, src); GuestMemory.W32(setup + 4, 0); GuestMemory.W32(setup + 8, 0xFFFFFFFF);
        GuestMemory.W32(setup + 12, dst); GuestMemory.W32(setup + 16, 0);
        Assert.Equal(OK, Call(N, "sceNgsPatchCreateRouting", setup, new Ptr<uint>(o)));
        return GuestMemory.R32(o);
    }

    /// <summary>The 84-byte SceNgsPlayerParams of the player module (module 0) as a params block: buffer 0 holds
    /// <paramref name="bytes"/> of stereo PCM16 played <paramref name="loops"/> extra times, no next buffer.</summary>
    int SetPlayer(uint voice, uint pcm, uint bytes, short loops, int channels = 2)
    {
        uint blk = _h.Alloc(8 + 84);
        GuestMemory.W32(blk, 0);                    // module index
        uint d = blk + 8;
        GuestMemory.W32(d, 0x01015CE6); GuestMemory.W32(d + 4, 84);
        GuestMemory.W32(d + 8, pcm); GuestMemory.W32(d + 12, bytes);
        GuestMemory.Write(d + 16, loops); GuestMemory.Write(d + 18, (short)-1);
        GuestMemory.Write(d + 56, 48000f); GuestMemory.Write(d + 60, 1f);
        GuestMemory.Write(d + 76, (byte)channels);
        uint n = _h.Word(0xFF);
        int r = Call(N, "sceNgsVoiceSetParamsBlock", voice, blk, 8u + 84u, new Ptr<int>(n));
        Assert.Equal(0u, GuestMemory.R32(n));
        return r;
    }

    uint Pcm(int frames, short l, short r)
    {
        uint p = _h.Alloc((uint)frames * 4);
        for (int i = 0; i < frames; i++) { GuestMemory.Write(p + (uint)i * 4, l); GuestMemory.Write(p + (uint)i * 4 + 2, r); }
        return p;
    }

    short[] MasterOutput(uint master)
    {
        uint mem = _h.Alloc(Granularity * 4);
        Assert.Equal(OK, Call(N, "sceNgsVoiceGetStateData", master, 1u, mem, (uint)Granularity * 4));
        return GuestMemory.ReadSpan(mem, Granularity * 4).ToArray().Chunk(2).Select(b => BitConverter.ToInt16(b)).ToArray();
    }

    [Fact]
    public void DefinitionsAndSystemArguments()
    {
        // each definition carries its voice type and output count
        uint master = (uint)Invoke(N, "sceNgsVoiceDefGetMasterBuss")!, simple = (uint)Invoke(N, "sceNgsVoiceDefGetSimpleVoice")!;
        Assert.Equal(0u, GuestMemory.R32(master));
        Assert.Equal(1u, GuestMemory.R32(master + 4));
        Assert.Equal(12u, GuestMemory.R32(simple));
        Assert.Equal(2u, GuestMemory.R32(simple + 4));
        Assert.Equal(4u, GuestMemory.R32((uint)Invoke(N, "sceNgsVoiceDefGetAtrac9Voice")! + 4));
        foreach (var name in new[] { "sceNgsVoiceDefGetCompressorBuss", "sceNgsVoiceDefGetCompressorSideChainBuss", "sceNgsVoiceDefGetDelayBuss",
            "sceNgsVoiceDefGetDistortionBuss", "sceNgsVoiceDefGetEnvelopeBuss", "sceNgsVoiceDefGetEqBuss", "sceNgsVoiceDefGetMixerBuss",
            "sceNgsVoiceDefGetPauserBuss", "sceNgsVoiceDefGetPitchShiftBuss", "sceNgsVoiceDefGetReverbBuss", "sceNgsVoiceDefGetSasEmuVoice",
            "sceNgsVoiceDefGetSimpleAtrac9Voice", "sceNgsVoiceDefGetScreamVoice", "sceNgsVoiceDefGetScreamAtrac9Voice", "sceNgsVoiceDefGetTemplate1",
            "sceNgsVoiceDefGetMasterBussInternal" })
            Assert.NotEqual(0u, (uint)Invoke(N, name)!);

        uint size = Out();
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsSystemGetRequiredMemorySize", 0u, new Ptr<uint>(size)));
        Assert.Equal(OK, Call(N, "sceNgsSystemGetRequiredMemorySize", _h.Alloc(32), new Ptr<uint>(size)));
        Assert.True(GuestMemory.R32(size) > 0);
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsSystemInit", 0u, 256u, _h.Alloc(32), new Ptr<uint>(size)));
        Assert.Equal(INVALID_HANDLE, Call(N, "sceNgsSystemRelease", 0x1234u));
        Assert.Equal(INVALID_HANDLE, Call(N, "sceNgsSystemUpdate", 0x1234u));
        uint sys = System();
        Assert.Equal(OK, Call(N, "sceNgsSystemSetFlags", sys, 0u));
        Assert.Equal(OK, Call(N, "sceNgsSystemSetParamErrorCallback", sys, 0u));
        // the guest lock nests for its holder; an unknown system is ignored
        Assert.Equal(OK, Call(N, "sceNgsSystemLock", sys));
        Assert.Equal(OK, Call(N, "sceNgsSystemLock", sys));
        Assert.Equal(OK, Call(N, "sceNgsSystemUpdate", sys)); // the holder may update
        Assert.Equal(OK, Call(N, "sceNgsSystemUnlock", sys));
        Assert.Equal(OK, Call(N, "sceNgsSystemUnlock", sys));
        Assert.Equal(OK, Call(N, "sceNgsSystemUnlock", sys));
        Assert.Equal(OK, Call(N, "sceNgsSystemLock", 0x1234u));

        // rack arguments
        uint desc = _h.Alloc(32);
        Assert.Equal(INVALID_HANDLE, Call(N, "sceNgsRackGetRequiredMemorySize", 0x1234u, desc, new Ptr<uint>(size)));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsRackGetRequiredMemorySize", sys, desc, new Ptr<uint>(size))); // no definition
        GuestMemory.W32(desc, simple);
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsRackGetRequiredMemorySize", sys, desc, new Ptr<uint>(size))); // 0 voices
        GuestMemory.W32(desc + 4, 1);
        Assert.Equal(OK, Call(N, "sceNgsRackGetRequiredMemorySize", sys, desc, new Ptr<uint>(size)));
        uint one = GuestMemory.R32(size);
        GuestMemory.W32(desc + 4, 3);
        Call(N, "sceNgsRackGetRequiredMemorySize", sys, desc, new Ptr<uint>(size));
        Assert.True(GuestMemory.R32(size) > one); // grows with the voice count
        Assert.Equal(INVALID_HANDLE, Call(N, "sceNgsRackInit", 0x1234u, _h.Alloc(8), desc, new Ptr<uint>(size)));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsRackInit", sys, 0u, desc, new Ptr<uint>(size)));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsRackInit", sys, _h.Alloc(8), desc, new Ptr<uint>(size))); // NULL memory
        // a rack given too little memory fails
        uint small = _h.Alloc(8);
        GuestMemory.W32(small, _h.Alloc(64)); GuestMemory.W32(small + 4, 64);
        Assert.Equal(ERROR, Call(N, "sceNgsRackInit", sys, small, desc, new Ptr<uint>(size)));

        uint rack = Rack(sys, simple, 2, 2);
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsRackGetVoiceHandle", rack, 2u, new Ptr<uint>(size)));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsRackGetVoiceHandle", 0x1234u, 0u, new Ptr<uint>(size)));
        Assert.NotEqual(Voice(rack, 0), Voice(rack, 1));
        Assert.Equal(OK, Call(N, "sceNgsRackSetParamErrorCallback", rack, 0u));
        Assert.Equal(OK, Call(N, "sceNgsRackRelease", rack, 0u));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsRackRelease", rack, 0u));
        Assert.Equal(OK, Call(N, "sceNgsSystemRelease", sys));
        Assert.Equal(INVALID_HANDLE, Call(N, "sceNgsSystemRelease", sys));
    }

    [Fact]
    public void SulphaDebugLinkAcceptsEverything()
    {
        uint cfg = _h.Alloc(8), size = Out();
        Assert.Equal(INVALID_ARG, Call(N, "sceSulphaNgsGetDefaultConfig", 0u));
        Assert.Equal(OK, Call(N, "sceSulphaNgsGetDefaultConfig", cfg));
        Assert.Equal(1024u, GuestMemory.R32(cfg));
        Assert.Equal(4096u, GuestMemory.R32(cfg + 4));
        Assert.Equal(OK, Call(N, "sceSulphaNgsGetNeededMemory", cfg, new Ptr<uint>(size)));
        Assert.True(GuestMemory.R32(size) >= 4096);
        Assert.Equal(INVALID_ARG, Call(N, "sceSulphaNgsGetNeededMemory", 0u, new Ptr<uint>(size)));
        Assert.Equal(INVALID_ARG, Call(N, "sceSulphaNgsInit", 0u, 0u, 0u));
        Assert.Equal(OK, Call(N, "sceSulphaNgsInit", cfg, _h.Alloc(16), 16u));
        Assert.Equal(INVALID_ARG, Call(N, "sceSulphaNgsSetSampleName", 0u, 0u, 0u));
        Assert.Equal(OK, Call(N, "sceSulphaNgsSetSampleName", cfg, 4u, 0u));
        Assert.Equal(OK, Call(N, "sceSulphaNgsSetVoiceName", 0u, 0u));
        Assert.Equal(OK, Call(N, "sceSulphaNgsSetSynthName", 0u, 0u));
        Assert.Equal(OK, Call(N, "sceSulphaNgsSetRackName", 0u, 0u));
        Assert.Equal(OK, Call(N, "sceSulphaNgsTrace", 0u));
        Assert.Equal(OK, Call(N, "sceSulphaNgsShutdown"));
        Assert.Equal(OK, Call(N, "sceNgsModuleGetNumPresets", 0u, 0u, new Ptr<uint>(size)));
        Assert.Equal(0u, GuestMemory.R32(size));
        Assert.Equal(ERROR, Call(N, "sceNgsModuleGetPreset", 0u, 0u, 0u, 0u));
    }

    [Fact]
    public void PlayerRendersPcmIntoTheMasterBuss()
    {
        uint sys = System();
        uint masterRack = Rack(sys, (uint)Invoke(N, "sceNgsVoiceDefGetMasterBuss")!, 1, 2);
        uint voiceRack = Rack(sys, (uint)Invoke(N, "sceNgsVoiceDefGetSimpleVoice")!, 1, 2);
        uint master = Voice(masterRack, 0), voice = Voice(voiceRack, 0);

        // no routing yet: PATCH_NOT_AVAIL with a NULL handle
        uint o = _h.Word(0xFFFFFFFF);
        Assert.Equal(PATCH_NOT_AVAIL, Call(N, "sceNgsVoiceGetOutputPatch", voice, 0, 0, new Ptr<uint>(o)));
        Assert.Equal(0u, GuestMemory.R32(o));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceGetOutputPatch", voice, 4, 0, new Ptr<uint>(o)));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceGetOutputPatch", voice, 0, 9, new Ptr<uint>(o)));
        uint patch = Patch(voice, master);
        Assert.Equal(OK, Call(N, "sceNgsVoiceGetOutputPatch", voice, 0, 0, new Ptr<uint>(o)));
        Assert.Equal(patch, GuestMemory.R32(o));
        // a new routing starts silent; set the identity matrix
        uint m = _h.Alloc(16);
        GuestMemory.Write(m, 1f); GuestMemory.Write(m + 12, 1f);
        Assert.Equal(OK, Call(N, "sceNgsVoicePatchSetVolumesMatrix", patch, m));
        uint prop = _h.Alloc(24), deliv = _h.Alloc(20);
        Assert.Equal(OK, Call(N, "sceNgsPatchGetInfo", patch, prop, deliv));
        Assert.Equal(2u, GuestMemory.R32(prop));                 // source channels
        Assert.Equal(1f, GuestMemory.Read<float>(prop + 8));     // [0][0]
        Assert.Equal(0f, GuestMemory.Read<float>(prop + 12));    // [0][1]
        Assert.Equal(voice, GuestMemory.R32(deliv));
        Assert.Equal(master, GuestMemory.R32(deliv + 12));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsPatchGetInfo", 0x1234u, prop, deliv));

        // 1024 frames of L = 0.5, R = -0.25
        uint pcm = Pcm(1024, 0x4000, -0x2000);
        Assert.Equal(OK, SetPlayer(voice, pcm, 1024 * 4, 0));
        Assert.Equal(OK, Call(N, "sceNgsVoicePlay", master));
        Assert.Equal(OK, Call(N, "sceNgsVoicePlay", voice));
        Assert.Equal(ERROR, Call(N, "sceNgsVoicePlay", voice)); // already playing
        Assert.Equal(INVALID_STATE, Call(N, "sceNgsVoiceInit", voice, 0u, 0u));
        Assert.Equal(OK, Call(N, "sceNgsSystemUpdate", sys));
        var outS = MasterOutput(master);
        Assert.All(outS.Where((_, i) => i % 2 == 0), s => Assert.Equal(16384, s));
        Assert.All(outS.Where((_, i) => i % 2 == 1), s => Assert.Equal(-8192, s));

        // the player state: +0 bytes into the buffer, +4 current buffer, +8 samples since key on
        uint st = _h.Alloc(24);
        Assert.Equal(OK, Call(N, "sceNgsVoiceGetStateData", voice, 0u, st, 24u));
        Assert.Equal(0u, GuestMemory.R32(st + 4));
        Assert.Equal((uint)Granularity * 2, GuestMemory.R32(st + 8));
        Assert.True(GuestMemory.R32(st) >= Granularity * 4);
        uint info = _h.Alloc(24);
        Assert.Equal(OK, Call(N, "sceNgsVoiceGetInfo", voice, info));
        Assert.Equal(1u, GuestMemory.R32(info) & 1);      // ACTIVE
        Assert.Equal(6u, GuestMemory.R32(info + 4));      // Simple voice: 6 modules
        Assert.Equal(2u, GuestMemory.R32(info + 12));     // outputs
        Assert.Equal(1u, GuestMemory.R32(info + 20));     // one frame rendered

        // half the volume on the left from the patch: SetVolume(out, in)
        Assert.Equal(OK, Call(N, "sceNgsVoicePatchSetVolume", patch, 0, 0, 0.5f));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoicePatchSetVolume", patch, 2, 0, 0.5f));
        Assert.Equal(OK, Call(N, "sceNgsSystemUpdate", sys));
        Assert.Equal(8192, MasterOutput(master)[0]);
        uint vols = _h.Alloc(8);
        GuestMemory.Write(vols, 1f); GuestMemory.Write(vols + 4, 0f);
        Assert.Equal(OK, Call(N, "sceNgsVoicePatchSetVolumes", patch, 0, vols, 2));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoicePatchSetVolumes", patch, 0, 0u, 2));

        // the buffer runs out after 1024 frames (4 granules): the voice finishes and leaves the queue
        for (int i = 0; i < 4; i++) Call(N, "sceNgsSystemUpdate", sys);
        Call(N, "sceNgsVoiceGetInfo", voice, info);
        Assert.Equal(0u, GuestMemory.R32(info) & 1);
        Call(N, "sceNgsSystemUpdate", sys);
        Assert.All(MasterOutput(master), s => Assert.Equal(0, s)); // silence once it ended

        // removing the routing frees the slot; the patch is no longer active
        Assert.Equal(OK, Call(N, "sceNgsPatchRemoveRouting", patch));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsPatchRemoveRouting", 0x1234u));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoicePatchSetVolumesMatrix", patch, m));
        Assert.Equal(PATCH_NOT_AVAIL, Call(N, "sceNgsVoiceGetOutputPatch", voice, 0, 0, new Ptr<uint>(o)));
        // the same slot is reused by the next routing
        Assert.Equal(patch, Patch(voice, master));
        Assert.Equal(OK, Call(N, "sceNgsSystemRelease", sys));
    }

    [Fact]
    public void LoopsMonoAndVoiceStates()
    {
        uint sys = System();
        uint masterRack = Rack(sys, (uint)Invoke(N, "sceNgsVoiceDefGetMasterBuss")!, 1, 2);
        uint voiceRack = Rack(sys, (uint)Invoke(N, "sceNgsVoiceDefGetSimpleVoice")!, 1, 2);
        uint master = Voice(masterRack, 0), voice = Voice(voiceRack, 0);
        uint patch = Patch(voice, master);
        uint m = _h.Alloc(16);
        GuestMemory.Write(m, 1f); GuestMemory.Write(m + 12, 1f);
        Call(N, "sceNgsVoicePatchSetVolumesMatrix", patch, m);
        // a mono buffer of 128 frames looping forever (-1): the left sample is copied to both sides
        uint pcm = _h.Alloc(256);
        for (uint i = 0; i < 128; i++) GuestMemory.Write(pcm + i * 2, (short)0x2000);
        Assert.Equal(OK, SetPlayer(voice, pcm, 256, -1, channels: 1));
        Call(N, "sceNgsVoicePlay", master);
        Call(N, "sceNgsVoicePlay", voice);
        for (int i = 0; i < 6; i++) Assert.Equal(OK, Call(N, "sceNgsSystemUpdate", sys));
        var o = MasterOutput(master);
        Assert.All(o, s => Assert.Equal(8192, s));
        uint info = _h.Alloc(24);
        Call(N, "sceNgsVoiceGetInfo", voice, info);
        Assert.Equal(1u, GuestMemory.R32(info) & 1); // still playing

        // pause: out of the queue (silence), a second pause is INVALID_STATE; resume plays again
        Assert.Equal(OK, Call(N, "sceNgsVoicePause", voice));
        Assert.Equal(INVALID_STATE, Call(N, "sceNgsVoicePause", voice));
        Call(N, "sceNgsVoiceGetInfo", voice, info);
        Assert.Equal(0x20u, GuestMemory.R32(info) & 0x20);
        Call(N, "sceNgsSystemUpdate", sys);
        Assert.All(MasterOutput(master), s => Assert.Equal(0, s));
        Assert.Equal(OK, Call(N, "sceNgsVoiceResume", voice));
        Assert.Equal(OK, Call(N, "sceNgsVoiceResume", voice)); // not paused: nothing to do
        Call(N, "sceNgsSystemUpdate", sys);
        Assert.Equal(8192, MasterOutput(master)[0]);

        // key off without an envelope: it ends on the next update
        Assert.Equal(OK, Call(N, "sceNgsVoiceKeyOff", voice));
        Call(N, "sceNgsVoiceGetInfo", voice, info);
        Assert.Equal(4u | 0x40u, GuestMemory.R32(info) & 0x45); // FINALIZING, keyed off
        Call(N, "sceNgsSystemUpdate", sys);
        Call(N, "sceNgsVoiceGetInfo", voice, info);
        Assert.Equal(0u, GuestMemory.R32(info) & 5);
        Assert.Equal(OK, Call(N, "sceNgsVoiceKeyOff", voice)); // not playing: OK, nothing happens

        // play again and kill: it stops at once
        Assert.Equal(OK, Call(N, "sceNgsVoicePlay", voice));
        Assert.Equal(OK, Call(N, "sceNgsVoiceKill", voice));
        Call(N, "sceNgsVoiceGetInfo", voice, info);
        Assert.Equal(0u, GuestMemory.R32(info) & 1);
        foreach (var fn in new[] { "sceNgsVoicePlay", "sceNgsVoiceKill", "sceNgsVoiceKeyOff", "sceNgsVoicePause", "sceNgsVoiceResume" })
            Assert.Equal(INVALID_ARG, Call(N, fn, 0x1234u));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceGetInfo", 0x1234u, info));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceGetInfo", voice, 0u));

        // init with flags 1 removes the voice's routings
        Assert.Equal(OK, Call(N, "sceNgsVoiceInit", voice, 0u, 1u));
        Assert.Equal(PATCH_NOT_AVAIL, Call(N, "sceNgsVoiceGetOutputPatch", voice, 0, 0, new Ptr<uint>(_h.Word(0))));
        Assert.Equal(OK, Call(N, "sceNgsVoiceInit", voice, 0u, 7u));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceInit", 0x1234u, 0u, 0u));
    }

    [Fact]
    public void ModulesParamsLockingAndBypass()
    {
        uint sys = System();
        uint rack = Rack(sys, (uint)Invoke(N, "sceNgsVoiceDefGetSimpleVoice")!, 1, 2);
        uint voice = Voice(rack, 0);
        // module types of the simple voice: player, EQ, envelope, pauser, filter, filter
        uint type = Out();
        uint[] expect = [0x5CE6, 0x5CEC, 0x5CE3, 0x5CE5, 0x5CE4, 0x5CE4];
        for (uint i = 0; i < expect.Length; i++)
        {
            Assert.Equal(OK, Call(N, "sceNgsVoiceGetModuleType", voice, i, new Ptr<uint>(type)));
            Assert.Equal(expect[i], GuestMemory.R32(type));
        }
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceGetModuleType", voice, 6u, new Ptr<uint>(type)));

        // bypass flag 2
        Assert.Equal(OK, Call(N, "sceNgsVoiceBypassModule", voice, 1u, 2u));
        Assert.Equal(OK, Call(N, "sceNgsVoiceGetModuleBypass", voice, 1u, new Ptr<uint>(type)));
        Assert.Equal(2u, GuestMemory.R32(type));
        Assert.Equal(OK, Call(N, "sceNgsVoiceBypassModule", voice, 1u, 0u));
        Assert.Equal(OK, Call(N, "sceNgsVoiceGetModuleBypass", voice, 1u, new Ptr<uint>(type)));
        Assert.Equal(0u, GuestMemory.R32(type));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceBypassModule", voice, 9u, 2u));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceGetModuleBypass", voice, 9u, new Ptr<uint>(type)));

        // lock: {params*, size}; the parameter id goes into the first word; a second lock fails; unlock once
        uint bi = _h.Alloc(8);
        Assert.Equal(OK, Call(N, "sceNgsVoiceLockParams", voice, 3u, 0x01015CE5u, bi));
        uint addr = GuestMemory.R32(bi);
        Assert.NotEqual(0u, addr);
        Assert.Equal(16u, GuestMemory.R32(bi + 4)); // SceNgsPauserParams
        Assert.Equal(0x01015CE5u, GuestMemory.R32(addr));
        Assert.Equal(ERROR, Call(N, "sceNgsVoiceLockParams", voice, 3u, 0x01015CE5u, bi));
        // a params block for a locked module counts as an error
        uint blk = _h.Alloc(8 + 16), n = _h.Word(0);
        GuestMemory.W32(blk, 3); GuestMemory.W32(blk + 8, 0x01015CE5); GuestMemory.W32(blk + 12, 16);
        Assert.Equal(ERROR, Call(N, "sceNgsVoiceSetParamsBlock", voice, blk, 24u, new Ptr<int>(n)));
        Assert.Equal(1u, GuestMemory.R32(n));
        Assert.Equal(OK, Call(N, "sceNgsVoiceUnlockParams", voice, 3u));
        Assert.Equal(ERROR, Call(N, "sceNgsVoiceUnlockParams", voice, 3u));
        Assert.Equal(OK, Call(N, "sceNgsVoiceSetParamsBlock", voice, blk, 24u, new Ptr<int>(n)));
        Assert.Equal(0u, GuestMemory.R32(n));
        // an unknown module, or a descriptor larger than the module's parameters
        GuestMemory.W32(blk, 9);
        Assert.Equal(ERROR, Call(N, "sceNgsVoiceSetParamsBlock", voice, blk, 24u, new Ptr<int>(n)));
        GuestMemory.W32(blk, 3); GuestMemory.W32(blk + 12, 200);
        Assert.Equal(ERROR, Call(N, "sceNgsVoiceSetParamsBlock", voice, blk, 8u + 200u, new Ptr<int>(n)));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceLockParams", voice, 9u, 0u, bi));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceUnlockParams", voice, 9u));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceSetParamsBlock", 0x1234u, blk, 24u, new Ptr<int>(n)));

        // callbacks are recorded; state data of an unknown module is INVALID_ARG; NULL memory reads nothing
        Assert.Equal(OK, Call(N, "sceNgsVoiceSetModuleCallback", voice, 0u, 0x81000001u, 5u));
        Assert.Equal(OK, Call(N, "sceNgsVoiceSetFinishedCallback", voice, 0x81000001u, 5u));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceSetModuleCallback", voice, 9u, 0u, 0u));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceSetFinishedCallback", 0x1234u, 0u, 0u));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceGetStateData", voice, 9u, _h.Alloc(8), 8u));
        Assert.Equal(OK, Call(N, "sceNgsVoiceGetStateData", voice, 0u, 0u, 8u));
        Assert.Equal(OK, Call(N, "sceNgsVoiceGetParamsOutOfRange", voice, 0u, 0u));
        // init with flags 4 clears them, flags 2 resets the modules
        Assert.Equal(OK, Call(N, "sceNgsVoiceInit", voice, 0u, 6u));
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsVoiceSetPreset", voice, 0u));
    }

    [Fact]
    public void Atrac9SectionDetails()
    {
        uint info = _h.Alloc(16);
        Assert.Equal(INVALID_ARG, Call(N, "sceNgsAT9GetSectionDetails", 0u, 0u, 0u, 0u));
        Assert.Equal(ERROR, Call(N, "sceNgsAT9GetSectionDetails", 0u, 1024u, 0x12345678u, info)); // not an ATRAC9 config
        // 48 kHz (index 7: 256 samples per frame), 0x100-byte frames, superframes of 4 frames
        uint frameField = 0x100 - 1;
        uint config = 0xFEu | (7u << 12) | ((frameField >> 3) << 16) | ((frameField & 7) << 29) | (2u << 27);
        Assert.Equal(OK, Call(N, "sceNgsAT9GetSectionDetails", 0u, 2048u, config, info));
        Assert.Equal(1u, GuestMemory.R32(info + 12)); // superframes
        Assert.Equal(0u, GuestMemory.R32(info));      // starts at the first superframe
        Assert.Equal(256, GuestMemory.Read<short>(info + 8)); // the decoder delay of one frame is skipped
        uint bytes = GuestMemory.R32(info + 4);
        Assert.Equal(0u, bytes % 0x400);              // whole superframes
        Assert.True(bytes >= 3 * 0x400);              // 2048 + 256 samples need three 1024-sample superframes
        // without superframes: plain frames
        uint plain = 0xFEu | (7u << 12) | ((frameField >> 3) << 16) | ((frameField & 7) << 29);
        Assert.Equal(OK, Call(N, "sceNgsAT9GetSectionDetails", 512u, 256u, plain, info));
        Assert.Equal(0u, GuestMemory.R32(info + 12));
        Assert.Equal(2u * 0x100, GuestMemory.R32(info)); // frame 2
    }
}
