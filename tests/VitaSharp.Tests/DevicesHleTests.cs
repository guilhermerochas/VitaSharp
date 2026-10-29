using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>Package F HLE (touch, ctrl, motion, audio out/in, power, sulpha) against guest memory.</summary>
[Collection("guest")]
public sealed class DevicesHleTests(GuestMemoryFixture fx)
{
    static object? Invoke<T>(T module, string name, params object[] args) =>
        typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);
    static int Call<T>(T module, string name, params object[] args) => (int)Invoke(module, name, args)!;

    static Ptr<T> P<T>(uint a) where T : unmanaged => new(a);

    [Fact]
    public void TouchSamplingStateAndForce()
    {
        var t = new Touch();
        uint p = fx.Memory.Alloc(0x10, "touch");
        GuestMemory.W32(p, 0xFF);
        Assert.Equal(0, Call(t, "sceTouchGetSamplingState", 0, P<uint>(p)));
        Assert.Equal(0u, GuestMemory.R32(p)); // hardware default: stopped
        Assert.Equal(0, Call(t, "sceTouchSetSamplingState", 1, 1u));
        Assert.Equal(0, Call(t, "sceTouchGetSamplingState", 1, P<uint>(p)));
        Assert.Equal(1u, GuestMemory.R32(p));
        Assert.Equal(0, Call(t, "sceTouchSetSamplingState", 1, 0u));
        Assert.Equal(0u, t.SamplingState(1));
        Assert.Equal(unchecked((int)0x80350001), Call(t, "sceTouchSetSamplingState", 2, 1u));
        Assert.Equal(unchecked((int)0x80350001), Call(t, "sceTouchSetSamplingState", 0, 2u));
        Assert.Equal(unchecked((int)0x80350001), Call(t, "sceTouchGetSamplingState", 0, P<uint>(0)));
        Assert.Equal(0, Call(t, "sceTouchEnableTouchForce", 0));
        Assert.Equal(unchecked((int)0x80350001), Call(t, "sceTouchDisableTouchForce", 5));
        // region variants reject region > 0xFF before touching the panel
        Assert.Equal(unchecked((int)0x80350001), Call(t, "sceTouchPeekRegion", 0, p, 1u, 0x100));
        Assert.Equal(unchecked((int)0x80350001), Call(t, "sceTouchPeek", 0, p, 65u));
    }

    [Fact]
    public void TouchPixelDensity()
    {
        var t = new Touch();
        uint p = fx.Memory.Alloc(0x10, "density");
        Assert.Equal(0, Call(t, "sceTouchGetPixelDensity", P<float>(p), P<float>(p + 4)));
        Assert.Equal(22.0f, GuestMemory.Read<float>(p));
        Assert.Equal(22.0f, GuestMemory.Read<float>(p + 4));
        Assert.Equal(unchecked((int)0x80350001), Call(t, "sceTouchGetPixelDensity", P<float>(0), P<float>(p)));
    }

    [Fact]
    public void CtrlRapidFire()
    {
        var c = new Ctrl();
        uint rule = fx.Memory.Alloc(0x20, "rapid");
        const uint Cross = 0x4000;
        uint[] r = [Cross, Cross, Cross, 0, 2, 2];
        for (int i = 0; i < 6; i++) GuestMemory.W32(rule + (uint)i * 4, r[i]);
        Assert.Equal(0, Call(c, "sceCtrlSetRapidFire", 0, 0, rule));
        var seen = new List<uint>();
        for (long v = 100; v < 108; v++) seen.Add(c.ApplyRapidFire(Cross, v) & Cross);
        Assert.Equal(new uint[] { Cross, Cross, 0, 0, Cross, Cross, 0, 0 }, seen);
        Assert.Equal(0u, c.ApplyRapidFire(0, 108)); // trigger released: untouched
        Assert.Equal(unchecked((int)0x80340001), Call(c, "sceCtrlSetRapidFire", 0, 16, rule));
        Assert.Equal(unchecked((int)0x80340001), Call(c, "sceCtrlSetRapidFire", 0, 0, 0u));
        Assert.Equal(0, Call(c, "sceCtrlClearRapidFire", 0, 0));
        Assert.Equal(Cross, c.ApplyRapidFire(Cross, 110));
    }

    [Fact]
    public void CtrlShoulderButtonsPerApiFamily()
    {
        // host L/R arrive as LTRIGGER/RTRIGGER; L3/R3 and the PS button may also be set by a DualShock
        const uint L = 0x100, R = 0x200, L1 = 0x400, R1 = 0x800, L3 = 0x2, R3 = 0x4, Cross = 0x4000, Ps = 0x10000;
        uint host = L | R | L3 | R3 | Cross | Ps;
        // original API (sceCtrlPeekBufferPositive): shoulders = LTRIGGER/RTRIGGER, no L3/R3, standard bits only
        Assert.Equal(L | R | Cross, Ctrl.MapButtons(host, Ctrl.Api.Standard));
        // *Ext: same buttons, system bits kept
        Assert.Equal(L | R | Cross | Ps, Ctrl.MapButtons(host, Ctrl.Api.Ext));
        // "2" API (sceCtrlReadBufferPositive2): shoulders = L1/R1 (vita-headers, Vita3K is_v2), L3/R3 reported
        Assert.Equal(L1 | R1 | L3 | R3 | Cross | Ps, Ctrl.MapButtons(host, Ctrl.Api.Ext | Ctrl.Api.V2));
        Assert.Equal(L1, Ctrl.MapButtons(L, Ctrl.Api.Ext | Ctrl.Api.V2));
        Assert.Equal(0u, Ctrl.MapButtons(0, Ctrl.Api.Ext | Ctrl.Api.V2));
    }

    [Fact]
    public void CtrlAnalogStickCheck()
    {
        var c = new Ctrl();
        uint p = fx.Memory.Alloc(0x10, "stick");
        Assert.Equal(0, Call(c, "sceCtrlSetAnalogStickCheckMode", 3));
        Assert.Equal(0, Call(c, "sceCtrlGetAnalogStickCheckMode", P<int>(p)));
        Assert.Equal(3u, GuestMemory.R32(p));
        Assert.Equal(0, Call(c, "sceCtrlSetAnalogStickCheckTarget", 7));
        Assert.Equal(0, Call(c, "sceCtrlGetAnalogStickCheckTarget", P<int>(p)));
        Assert.Equal(7u, GuestMemory.R32(p));
        Assert.Equal(unchecked((int)0x80340001), Call(c, "sceCtrlGetAnalogStickCheckTarget", P<int>(0)));
    }

    [Fact]
    public void MotionStateAndFilters()
    {
        var s = new Sensors();
        Assert.Equal(unchecked((int)0x80360206), Call(s, "sceMotionStopSampling"));
        Assert.Equal(0, Call(s, "sceMotionStartSampling"));
        Assert.Equal(unchecked((int)0x80360207), Call(s, "sceMotionStartSampling"));
        Assert.Equal(1, Call(s, "sceMotionGetTiltCorrection"));
        Assert.Equal(1, Call(s, "sceMotionGetDeadband"));
        Assert.Equal(0, Call(s, "sceMotionSetDeadband", 0));
        Assert.Equal(0, Call(s, "sceMotionGetDeadband"));
        Assert.Equal(0, Call(s, "sceMotionGetMagnetometerState"));
        Assert.Equal(0, Call(s, "sceMotionMagnetometerOn"));
        Assert.Equal(1, Call(s, "sceMotionGetMagnetometerState"));
        Assert.Equal(1, Call(s, "sceMotionGetGyroBiasCorrection"));
        Assert.Equal(20.0f, (float)Invoke(s, "sceMotionGetAngleThreshold")!);
        Assert.Equal(unchecked((int)0x8036020A), Call(s, "sceMotionSetAngleThreshold", 50.0f));
        Assert.Equal(unchecked((int)0x8036020A), Call(s, "sceMotionSetAngleThreshold", float.NaN));
        Assert.Equal(0, Call(s, "sceMotionSetAngleThreshold", 30.0f));
        Assert.Equal(30.0f, (float)Invoke(s, "sceMotionGetAngleThreshold")!);

        uint v = fx.Memory.Alloc(0x40, "basicori");
        Assert.Equal(0, Call(s, "sceMotionGetBasicOrientation", v));
        Assert.Equal(1.0f, GuestMemory.Read<float>(v + 8));
        Assert.Equal(0, Call(s, "sceMotionGetDeviceLocation", v));
        Assert.Equal(-32.0f, GuestMemory.Read<float>(v));
        Assert.Equal(-33.0f, GuestMemory.Read<float>(v + 12));
        Assert.Equal(unchecked((int)0x80360209), Call(s, "sceMotionGetSensorState", 0u, 64));
        Assert.Equal(unchecked((int)0x80360205), Call(s, "sceMotionGetSensorState", v, 65));
    }

    [Fact]
    public void MotionSensorRecords()
    {
        uint p = fx.Memory.Alloc(64 * 0x40, "sensors");
        Sensors.WriteSensorStates(p, 64, 2_000_000, 100);
        for (uint i = 0; i < 64; i++)
        {
            uint r = p + i * 0x40;
            Assert.Equal(-1.0f, GuestMemory.Read<float>(r + 8));
            Assert.Equal(0.0f, GuestMemory.Read<float>(r + 0x0C));
            Assert.Equal(100u - (63 - i), GuestMemory.R32(r + 0x28));
        }
        Assert.Equal(2_000_000u, GuestMemory.R32(p + 63 * 0x40 + 0x24)); // newest last
        Assert.True(GuestMemory.R32(p + 0x24) < GuestMemory.R32(p + 0x40 + 0x24));
    }

    [Fact]
    public void AudioOutPortsAndProfiles()
    {
        var a = new Audio();
        for (int i = 0; i < 8; i++) Assert.Equal(i, Call(a, "sceAudioOutOpenPort", 0, 256, 48000, 1));
        Assert.Equal(unchecked((int)0x80260005), Call(a, "sceAudioOutOpenPort", 1, 256, 48000, 1));
        Assert.Equal(8, Call(a, "sceAudioOutOpenExtPort", 256, 1));
        Assert.Equal(unchecked((int)0x80260005), Call(a, "sceAudioOutOpenExtPort", 256, 1));
        Assert.Equal(0, Call(a, "sceAudioOutReleasePort", 3));
        Assert.Equal(3, Call(a, "sceAudioOutOpenPort", 2, 256, 48000, 0)); // lowest free handle
        Assert.Equal(unchecked((int)0x8026000A), Call(a, "sceAudioOutSetCompress", 0, 1)); // MAIN
        Assert.Equal(0, Call(a, "sceAudioOutSetCompress", 3, 1));                          // VOICE
        Assert.Equal(unchecked((int)0x80260003), Call(a, "sceAudioOutSetCompress", 42, 1));
        Assert.Equal(256, Call(a, "sceAudioOutGetPortVolume_forUser", 0));
        Assert.Equal(0, Call(a, "sceAudioOutSetPortVolume_forUser", 2, 128u, 0));
        Assert.Equal(128, Call(a, "sceAudioOutGetPortVolume_forUser", 0));
        Assert.Equal(256, Call(a, "sceAudioOutGetPortVolume_forUser", 1));
        Assert.Equal(unchecked((int)0x80260009), Call(a, "sceAudioOutSetPortVolume_forUser", 4, 300u, 0));
        Assert.Equal(1, Call(a, "sceAudioOutGetAdopt", 1));
        Assert.Equal(0, Call(a, "sceAudioOutSetAdopt_forUser", 1, 0, 0, 0));
        Assert.Equal(0, Call(a, "sceAudioOutGetAdopt", 1));
    }

    [Fact]
    public void AudioInVirtualMicrophone()
    {
        var a = new Audio();
        Assert.Equal(0, Call(a, "sceAudioInGetStatus", 1));
        Assert.Equal(unchecked((int)0x8026010B), Call(a, "sceAudioInGetStatus", 0));
        Assert.Equal(unchecked((int)0x80260103), Call(a, "sceAudioInOpenPort", 0, 256, 48000, 0));
        Assert.Equal(unchecked((int)0x80260102), Call(a, "sceAudioInOpenPort", 0, 300, 16000, 0));
        Assert.Equal(unchecked((int)0x80260106), Call(a, "sceAudioInOpenPort", 0, 256, 16000, 1));
        Assert.Equal(unchecked((int)0x80260104), Call(a, "sceAudioInOpenPort", 5, 256, 16000, 0));
        Assert.Equal(0, Call(a, "sceAudioInGetAdopt", 0));
        Assert.Equal(0x80, Call(a, "sceAudioInOpenPort", 2, 768, 48000, 0));
        Assert.Equal(unchecked((int)0x80260107), Call(a, "sceAudioInOpenPort", 0, 256, 16000, 0));
        Assert.Equal(1, Call(a, "sceAudioInGetAdopt", 2));
        Assert.Equal(0x1060, Call(a, "sceAudioInGetMicGain", 0x80));
        Assert.Equal(0, Call(a, "sceAudioInSetMicGain", 0x80, 0x12345));
        Assert.Equal(0x2345, Call(a, "sceAudioInGetMicGain", 0x80));
        Assert.Equal(unchecked((int)0x80260109), Call(a, "sceAudioInInput", 0x81, 0u));
        Assert.Equal(0, Call(a, "sceAudioInReleasePort", 0x80));
        Assert.Equal(unchecked((int)0x80260109), Call(a, "sceAudioInReleasePort", 0x80));
        Assert.Equal(0x80, Call(a, "sceAudioInOpenPort", 0, 512, 16000, 0));
        Assert.Equal(1, Call(a, "sceAudioInSetMute", 1));
        Assert.Equal(1, Call(a, "sceAudioInGetStatus", 1));
        Assert.Equal(0, Call(a, "sceAudioInSetMute", 2));
        Assert.Equal(7, Call(a, "sceAudioInSetMute", 7));
        Assert.Equal(0, Call(a, "sceAudioInGetInput"));
        Assert.Equal(0, Call(a, "sceAudioInSelectInput", 1));
        Assert.Equal(1, Call(a, "sceAudioInGetInput"));
    }

    [Fact]
    public void PowerAndSulpha()
    {
        var m = new SystemMisc();
        Assert.Equal(0, Call(m, "scePowerGetUsingWireless"));
        Assert.Equal(0, Call(m, "scePowerSetUsingWireless", 1));
        Assert.Equal(1, Call(m, "scePowerGetUsingWireless"));
        Assert.Equal(unchecked((int)0x802B0000), Call(m, "scePowerSetUsingWireless", 2));
        Assert.Equal(4150, Call(m, "scePowerGetBatteryVolt"));

        var n = new Ngs();
        uint cfg = fx.Memory.Alloc(0x10, "sulpha");
        Assert.Equal(0, Call(n, "sceSulphaNgsGetDefaultConfig", cfg));
        Assert.Equal(1024u, GuestMemory.R32(cfg));
        Assert.Equal(4096u, GuestMemory.R32(cfg + 4));
        Assert.Equal(0, Call(n, "sceSulphaNgsGetNeededMemory", cfg, P<uint>(cfg + 8)));
        Assert.Equal(20496u, GuestMemory.R32(cfg + 8));
        Assert.NotEqual(0, Call(n, "sceSulphaNgsSetSampleName", 0u, 4u, 0u));
    }

    [Fact]
    public void NgsSystemLockHasNoOwner()
    {
        // Sengoku Musou 2 Empires: the first of its lock users calls sceNgsSystemLock and the last one out calls
        // sceNgsSystemUnlock, from whichever thread. An owned lock (host Monitor) ignored that unlock and deadlocked.
        var n = new Ngs();
        var sys = new VitaSharp.Core.Hle.Audio.NgsSystem { Handle = 0x1234 };
        var systems = (System.Collections.Concurrent.ConcurrentDictionary<uint, VitaSharp.Core.Hle.Audio.NgsSystem>)
            typeof(Ngs).GetField("_systems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(n)!;
        systems[sys.Handle] = sys;

        var t = new Thread(() => Call(n, "sceNgsSystemLock", sys.Handle));
        t.Start(); t.Join();
        Assert.Equal(1, sys.GuestLockCount);

        var waiter = new Thread(() => Call(n, "sceNgsSystemLock", sys.Handle)); // held by the first thread: blocks
        waiter.Start();
        Assert.False(waiter.Join(200));
        Assert.Equal(0, Call(n, "sceNgsSystemUnlock", sys.Handle));                // released by another thread
        Assert.True(waiter.Join(5000));
        Assert.Equal(1, sys.GuestLockCount);
        Assert.Equal(0, Call(n, "sceNgsSystemUnlock", sys.Handle));
        Assert.Equal(0, sys.GuestLockCount);
        Assert.Equal(0, Call(n, "sceNgsSystemUnlock", sys.Handle));                // unbalanced unlock is harmless
        Assert.Equal(0, sys.GuestLockCount);
    }
}
