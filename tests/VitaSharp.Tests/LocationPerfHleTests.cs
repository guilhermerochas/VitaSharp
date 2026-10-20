using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>G2 sections 5 and 6: SceLibLocation, SceGps, SceNear*, ScePerf/Razor, SceDeci4pUserp, backtrace, RNG.</summary>
[Collection("guest")]
public sealed class LocationPerfHleTests(GuestMemoryFixture fx)
{
    static object? Invoke<T>(T module, string name, params object[] args) =>
        typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args);
    static int Call<T>(T module, string name, params object[] args) => (int)Invoke(module, name, args)!;
    static Ptr<T> P<T>(uint a) where T : unmanaged => new(a);

    [Fact]
    public void LocationHandlesAndMethods()
    {
        var l = new Location();
        uint p = fx.Memory.Alloc(0x10, "loc-h");
        Assert.Equal(Location.ERR_INVALID_ADDRESS, Call(l, "sceLocationOpen", P<uint>(0), 1u, 1u));
        Assert.Equal(Location.ERR_INVALID_LOCATION_METHOD, Call(l, "sceLocationOpen", P<uint>(p), 6u, 1u));
        Assert.Equal(Location.ERR_INVALID_HEADING_METHOD, Call(l, "sceLocationOpen", P<uint>(p), 5u, 5u));
        Assert.Equal(0, Call(l, "sceLocationOpen", P<uint>(p), 2u, 3u));
        uint h = GuestMemory.R32(p);
        Assert.NotEqual(0u, h);
        Assert.Equal(0, Call(l, "sceLocationGetMethod", h, P<uint>(p + 4), P<uint>(p + 8)));
        Assert.Equal(2u, GuestMemory.R32(p + 4));
        Assert.Equal(3u, GuestMemory.R32(p + 8));
        Assert.Equal(Location.ERR_INVALID_ADDRESS, Call(l, "sceLocationGetMethod", h, P<uint>(0), P<uint>(p + 8)));
        Assert.Equal(Location.ERR_INVALID_LOCATION_METHOD, Call(l, "sceLocationReopen", h, 9u, 0u));
        Assert.Equal(0, Call(l, "sceLocationReopen", h, 5u, 4u));
        Assert.Equal(0, Call(l, "sceLocationGetMethod", h, P<uint>(p + 4), P<uint>(p + 8)));
        Assert.Equal(5u, GuestMemory.R32(p + 4));
        Assert.Equal(4u, GuestMemory.R32(p + 8));
        Assert.Equal(0, Call(l, "sceLocationClose", h));
        Assert.Equal(Location.ERR_INVALID_HANDLE, Call(l, "sceLocationClose", h));
        Assert.Equal(Location.ERR_INVALID_HANDLE, Call(l, "sceLocationReopen", h, 1u, 1u));
        Assert.Equal(0, l.OpenHandles);
    }

    [Fact]
    public void LocationMeasurementsAreDenied()
    {
        var l = new Location();
        uint p = fx.Memory.Alloc(0x80, "loc-m");
        Assert.Equal(0, Call(l, "sceLocationOpen", P<uint>(p), 1u, 1u));
        uint h = GuestMemory.R32(p);
        uint info = p + 0x10;
        GuestMemory.Span(info, 0x30).Fill(0xAA);
        GuestMemory.W32(info + 0x30, 0x12345678); // guard after the 0x30-byte struct
        Assert.Equal(Location.ERR_DISABLE_APPLICATION, Call(l, "sceLocationGetLocation", h, info));
        Assert.Equal(Location.DataInvalid, GuestMemory.Read<double>(info));
        Assert.Equal(Location.DataInvalid, GuestMemory.Read<double>(info + 0x10));
        Assert.Equal((float)Location.DataInvalid, GuestMemory.Read<float>(info + 0x24));
        Assert.NotEqual(0ul, GuestMemory.Read<ulong>(info + 0x28));
        Assert.Equal(0x12345678u, GuestMemory.R32(info + 0x30));
        Assert.Equal(Location.ERR_DISABLE_APPLICATION, Call(l, "sceLocationGetLocationWithTimeout", h, info, 1000ul));
        Assert.Equal(Location.ERR_INVALID_ADDRESS, Call(l, "sceLocationGetLocation", h, 0u));
        Assert.Equal(Location.ERR_INVALID_HANDLE, Call(l, "sceLocationGetLocation", 999u, info));

        GuestMemory.W32(info + 0x20, 0x87654321);
        Assert.Equal(Location.ERR_DISABLE_APPLICATION, Call(l, "sceLocationGetHeading", h, info));
        Assert.Equal((float)Location.DataInvalid, GuestMemory.Read<float>(info));
        Assert.Equal(0x87654321u, GuestMemory.R32(info + 0x20)); // SceLocationHeadingInfo is 0x20 bytes

        Assert.Equal(Location.ERR_INVALID_ADDRESS, Call(l, "sceLocationStartLocationCallback", h, 10u, 0u, 0u));
        Assert.Equal(Location.ERR_DISABLE_APPLICATION, Call(l, "sceLocationStartLocationCallback", h, 10u, 0x81000001u, 0u));
        Assert.Equal(Location.ERR_NOT_RUNNING_CALLBACK, Call(l, "sceLocationStopLocationCallback", h));
        Assert.Equal(Location.ERR_DISABLE_APPLICATION, Call(l, "sceLocationStartHeadingCallback", h, 10u, 0x81000001u, 0u));
        Assert.Equal(Location.ERR_NOT_RUNNING_CALLBACK, Call(l, "sceLocationStopHeadingCallback", h));
        Assert.Equal(0, Call(l, "sceLocationCancelGetLocation", h));
    }

    [Fact]
    public void LocationConfirmDialogAndPermission()
    {
        var l = new Location();
        uint p = fx.Memory.Alloc(0x40, "loc-c");
        Assert.Equal(0, Call(l, "sceLocationOpen", P<uint>(p), 0u, 0u));
        uint h = GuestMemory.R32(p);
        Assert.Equal(0, Call(l, "sceLocationConfirmGetStatus", h, P<uint>(p + 4)));
        Assert.Equal(Location.DialogIdle, GuestMemory.R32(p + 4));
        Assert.Equal(Location.ERR_DIALOG_RESULT_NONE, Call(l, "sceLocationConfirmGetResult", h, P<uint>(p + 8)));
        Assert.Equal(0, Call(l, "sceLocationConfirm", h));
        Assert.Equal(0, Call(l, "sceLocationConfirmGetStatus", h, P<uint>(p + 4)));
        Assert.Equal(Location.DialogFinished, GuestMemory.R32(p + 4));
        Assert.Equal(0, Call(l, "sceLocationConfirmGetResult", h, P<uint>(p + 8)));
        Assert.Equal(Location.ResultDisable, GuestMemory.R32(p + 8));
        Assert.Equal(0, Call(l, "sceLocationConfirmAbort", h));

        uint info = p + 0x10;
        GuestMemory.W32(info + 0x14, 0xCAFEBABE);
        Assert.Equal(0, Call(l, "sceLocationGetPermission", h, info));
        Assert.Equal(1u, GuestMemory.R32(info));
        Assert.Equal(1u, GuestMemory.R32(info + 4));
        Assert.Equal(2u, GuestMemory.R32(info + 8));
        Assert.Equal(0xCAFEBABEu, GuestMemory.R32(info + 0x14));
        Assert.Equal(Location.ERR_INVALID_HANDLE, Call(l, "sceLocationGetPermission", 77u, info));
        Assert.Equal(0, Call(l, "sceLocationTerm"));
        Assert.Equal(Location.ERR_INVALID_HANDLE, Call(l, "sceLocationConfirm", h));
    }

    [Fact]
    public void GpsHasNoDevice()
    {
        var g = new Gps();
        Assert.Equal(0, Call(g, "_sceGpsIsDevice"));
        Assert.Equal(Gps.ERR_NO_DEVICE, Call(g, "_sceGpsOpen", 0));
        Assert.Equal(Gps.ERR_NO_DEVICE, Call(g, "_sceGpsGetData", 0u, 0u));
        Assert.Equal(0, Call(g, "_sceGpsResumeCallback"));
    }

    [Fact]
    public void NearIsUnavailableAndDialogFinishesAborted()
    {
        var n = new NearUtil();
        Assert.Equal(NearUtil.ERR_NOT_SUPPORTED, Call(n, "sceNearInitialize"));
        Assert.Equal(NearUtil.ERR_NOT_SUPPORTED, Call(n, "sceNearUtilityInitialize"));
        Assert.Equal(NearUtil.ERR_NOT_RUNNING, Call(n, "sceNearGetNeighbors"));
        Assert.Equal(0, Call(n, "sceNearFinalize"));
        // early-SDK NIDs: Initialize fails, the rest report "nothing" without writing
        Assert.Equal(NearUtil.ERR_NOT_SUPPORTED, Call(n, "NearA412E9CA", 0u, 0u, 0u));
        Assert.Equal(0, Call(n, "NearE608000B", 0u));
        Assert.Equal(0, Call(n, "NearEarlyNothing"));

        uint r = fx.Memory.Alloc(0x40, "near-dlg");
        GuestMemory.W32(r, 0xFFFFFFFF);
        GuestMemory.W32(r + 4, 0x11223344);
        Assert.Equal(NearUtil.DialogNone, Call(n, "sceNearDialogGetStatus"));
        Assert.Equal(NearUtil.ERR_NOT_RUNNING, Call(n, "sceNearDialogGetResult", r));
        Assert.Equal(0, Call(n, "sceNearDialogInit", 0u));
        Assert.Equal(NearUtil.DialogFinished, Call(n, "sceNearDialogGetStatus"));
        Assert.Equal(0, Call(n, "sceNearDialogGetResult", r));
        Assert.Equal(2u, GuestMemory.R32(r));            // SCE_COMMON_DIALOG_RESULT_ABORTED
        Assert.Equal(0x11223344u, GuestMemory.R32(r + 4)); // nothing else written
        Assert.Equal(0, Call(n, "sceNearDialogTerm"));
        Assert.Equal(NearUtil.DialogNone, Call(n, "sceNearDialogGetStatus"));
    }

    [Fact]
    public void PerfAndRazorQueries()
    {
        var d = new DebugPerf();
        uint p = fx.Memory.Alloc(0x40, "perf");
        GuestMemory.W32(p, 0xFFFFFFFF);
        Assert.Equal(0, Call(d, "scePerfArmPmonGetCounterValue", 0, 0u, P<uint>(p)));
        Assert.Equal(0u, GuestMemory.R32(p));
        Assert.Equal(0, Call(d, "scePerfArmPmonGetCounterValue", 0, 0u, P<uint>(0)));
        Assert.Equal(DebugPerf.TimebaseFrequency, (uint)Invoke(d, "scePerfGetTimebaseFrequency")!);
        Assert.Equal(0, Call(d, "sceRazorCpuIsCapturing"));

        uint info = p + 8;
        GuestMemory.Span(info, 0x28).Fill(0xAA);
        GuestMemory.W32(info + 0x28, 0x5A5A5A5A);
        Assert.Equal(0, Call(d, "sceRazorGpuLiveSetBuffer", 0x81100000u, 0x2000u, info));
        Assert.Equal(0x81100000u, GuestMemory.R32(info));
        Assert.Equal(0u, GuestMemory.R32(info + 4));   // entry_count
        Assert.Equal(0x2000u, GuestMemory.R32(info + 0x0C));
        Assert.Equal(0ul, GuestMemory.Read<ulong>(info + 0x18));
        Assert.Equal(0x5A5A5A5Au, GuestMemory.R32(info + 0x28));
        Assert.Equal(0, Call(d, "sceRazorGpuLiveSetBuffer", 0u, 0u, 0u));
    }

    [Fact]
    public void Deci4pHasNoHost()
    {
        var d = new DebugPerf();
        uint name = fx.Memory.Alloc(0x10, "deci");
        GuestMemory.WriteCString(name, "proto");
        Assert.Equal(SceError.ERROR, Call(d, "sceKernelDeci4pOpen", name, 1u, 0x100u));
        Assert.Equal(SceError.UNKNOWN_UID, Call(d, "sceKernelDeci4pRead", 1, 0u, 0u, 0u));
        Assert.Equal(0, Call(d, "sceKernelDeci4pIsProcessAttached"));
    }

    [Fact]
    public void BacktraceArgsValidation()
    {
        var pm = new ProcessMgr();
        uint args = fx.Memory.Alloc(0x10, "bt-args");
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(pm, "_sceKernelBacktrace", 0, 0u, 0u, 0u));
        GuestMemory.W32(args, 0);
        GuestMemory.W32(args + 4, 1); // SCE_KERNEL_BACKTRACE_MODE_KERNEL
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(pm, "_sceKernelBacktrace", 0, 0u, 0u, args));
        GuestMemory.W32(args + 4, 2);
        Assert.Equal(SceError.INVALID_ARGUMENT, Call(pm, "_sceKernelBacktrace", 0, args, 4u, args));
    }

    [Fact]
    public void RandomNumberLimits()
    {
        var s = new Security();
        uint p = fx.Memory.Alloc(0x80, "rng");
        GuestMemory.Span(p, 0x41).Clear();
        Assert.Equal(0, Call(s, "sceKernelGetRandomNumber", p, 64u));
        Assert.Contains(GuestMemory.Span(p, 64).ToArray(), b => b != 0);
        Assert.Equal(0, GuestMemory.Read<byte>(p + 64));
        Assert.Equal(Security.SCE_RNG_ERROR_INVALID_ARGUMENT, Call(s, "sceKernelGetRandomNumber", p, 65u));
        Assert.Equal(Security.SCE_RNG_ERROR_INVALID_ARGUMENT, Call(s, "sceKernelGetRandomNumber", 0u, 4u));
        Assert.Equal(0, Call(s, "sceKernelGetRandomNumber", 0u, 0u));
    }
}
