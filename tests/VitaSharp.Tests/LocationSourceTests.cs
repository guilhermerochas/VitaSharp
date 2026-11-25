using VitaSharp.Core.Capture;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Host;
using VitaSharp.Core.Memory;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceLibLocation with the host's location source (psp2/location.h; FW 3.74 liblocation.suprx): off by default (the
/// application is not allowed, as before); with a source the application is allowed, sceLocationGetLocation answers the
/// source's position with its accuracy, a source without a fix is "undetermined", and while "Gravar tudo" is armed or
/// recording without --capture-location the position is withheld. The --location= setting and what a capture keeps of
/// it. Synthetic: a fixed source and a source the test moves.
/// </summary>
[Collection("guest")]
public sealed class LocationSourceTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "location");

    public void Dispose() => _harness.Dispose();

    /// <summary>A source the test controls (null: no fix yet).</summary>
    private sealed class TestSource : ILocationSource
    {
        public LocationFix? Fix;

        public LocationFix? Current => Fix;

        public void Dispose()
        {
        }
    }

    private Location Module => _harness.Module<Location>();

    /// <summary>A handle opened with GPS + Wi-Fi and the output block (handle word, then 0x40 bytes).</summary>
    private (uint Handle, uint Block) Open()
    {
        uint block = _harness.Alloc(0x80);
        Assert.Equal(0, Call(Module, "sceLocationOpen", new Ptr<uint>(block), 2u, 1u));
        return (GuestMemory.R32(block), block + 0x10);
    }

    private static readonly LocationFix s_somewhere = new(-23.5613, -46.6565, 760.0, 25f, float.NaN, float.NaN);

    [Fact]
    public void WithASourceTheApplicationIsAllowedAndGetsItsPosition()
    {
        _harness.Emu.Host.Location = new FixedLocationSource(s_somewhere);
        (uint handle, uint info) = Open();
        GuestMemory.W32(info + 0x30, 0x12345678); // guard after the 0x30-byte structure

        Assert.Equal(0, Call(Module, "sceLocationGetLocation", handle, info));

        Assert.Equal(-23.5613, GuestMemory.Read<double>(info));
        Assert.Equal(-46.6565, GuestMemory.Read<double>(info + 0x08));
        Assert.Equal(760.0, GuestMemory.Read<double>(info + 0x10));
        Assert.Equal(25f, GuestMemory.Read<float>(info + 0x18));
        Assert.Equal((float)Location.DataInvalid, GuestMemory.Read<float>(info + 0x20)); // no direction known
        Assert.Equal((float)Location.DataInvalid, GuestMemory.Read<float>(info + 0x24)); // no speed known
        Assert.NotEqual(0ul, GuestMemory.Read<ulong>(info + 0x28));
        Assert.Equal(0x12345678u, GuestMemory.R32(info + 0x30));
        // the variant with the timeout second answers the same
        GuestMemory.Write(info, 0.0);
        Assert.Equal(0, Call(Module, "sceLocationGetLocationWithTimeout", handle, 10u, info));
        Assert.Equal(-23.5613, GuestMemory.Read<double>(info));

        // the permission says ALLOW and the confirmation dialog ends with "enable"
        Assert.Equal(0, Call(Module, "sceLocationGetPermission", handle, info));
        Assert.Equal((1u, 1u, Location.ApplicationAllow), (GuestMemory.R32(info), GuestMemory.R32(info + 4), GuestMemory.R32(info + 8)));
        Assert.Equal(0, Call(Module, "sceLocationConfirm", handle));
        Assert.Equal(0, Call(Module, "sceLocationConfirmGetResult", handle, new Ptr<uint>(info)));
        Assert.Equal(Location.ResultEnable, GuestMemory.R32(info));

        // no compass the service uses: the heading stays undetermined
        Assert.Equal(Location.INFO_UNDETERMINED_LOCATION, Call(Module, "sceLocationGetHeading", handle, info));
        Assert.Equal((float)Location.DataInvalid, GuestMemory.Read<float>(info));
    }

    [Fact]
    public void ASourceWithoutAFixIsUndeterminedAndFollowsTheSource()
    {
        var source = new TestSource();
        _harness.Emu.Host.Location = source;
        (uint handle, uint info) = Open();

        Assert.Equal(Location.INFO_UNDETERMINED_LOCATION, Call(Module, "sceLocationGetLocation", handle, info));
        Assert.Equal(Location.DataInvalid, GuestMemory.Read<double>(info));
        Assert.Equal(0, Call(Module, "sceLocationGetPermission", handle, info));
        Assert.Equal(Location.ApplicationAllow, GuestMemory.R32(info + 8)); // allowed, just no position yet

        source.Fix = s_somewhere;
        Assert.Equal(0, Call(Module, "sceLocationGetLocation", handle, info));
        Assert.Equal(-46.6565, GuestMemory.Read<double>(info + 0x08));

        source.Fix = s_somewhere with { Latitude = 51.5, Longitude = -0.12, Altitude = double.NaN };
        Assert.Equal(0, Call(Module, "sceLocationGetLocation", handle, info));
        Assert.Equal(51.5, GuestMemory.Read<double>(info));
        Assert.Equal(Location.DataInvalid, GuestMemory.Read<double>(info + 0x10)); // no altitude known
    }

    [Fact]
    public void WithoutASourceTheApplicationStaysDenied()
    {
        Assert.Null(_harness.Emu.Host.Location);
        (uint handle, uint info) = Open();

        Assert.Equal(Location.ERR_DISABLE_APPLICATION, Call(Module, "sceLocationGetLocation", handle, info));
        Assert.Equal(Location.DataInvalid, GuestMemory.Read<double>(info));
        Assert.Equal(0, Call(Module, "sceLocationGetPermission", handle, info));
        Assert.Equal(Location.ApplicationDeny, GuestMemory.R32(info + 8));
        Assert.Equal(Location.ERR_DISABLE_APPLICATION, Call(Module, "sceLocationStartLocationCallback", handle, 10u, 0x81000001u, 0u));
    }

    /// <summary>What a guest callback (a real guest function: an HLE stub) was called with.</summary>
    private sealed class CallbackLog
    {
        public readonly List<(int Result, uint Handle, double Latitude, double Longitude, uint Userdata)> Calls = new();

        public int Count
        {
            get
            {
                lock (Calls)
                {
                    return Calls.Count;
                }
            }
        }
    }

    /// <summary>A guest function that records its (result, handle, info, userdata) calls.</summary>
    private uint LocationCallback(CallbackLog log)
    {
        uint stub = _harness.Alloc(16);
        VitaSharp.Core.Loader.Linker.WriteHleStub(stub, _harness.Emu.Hle.RegisterHostFunction("location callback", context =>
        {
            uint info = context.Gpr(2);
            lock (log.Calls)
            {
                log.Calls.Add(((int)context.Gpr(0), context.Gpr(1), GuestMemory.Read<double>(info), GuestMemory.Read<double>(info + 8), context.Gpr(3)));
            }

            context.Return(0);
        }), 0);
        _harness.Emu.Code.Invalidate(stub, 16);
        return stub;
    }

    [Fact]
    public void ALocationCallbackRunsOncePerHandleUntilStopped()
    {
        _harness.Emu.Host.Location = new FixedLocationSource(s_somewhere);
        (uint handle, _) = Open();
        uint callback = LocationCallback(new CallbackLog());

        Assert.Equal(Location.ERR_INVALID_ADDRESS, Call(Module, "sceLocationStartLocationCallback", handle, 10u, 0u, 0u));
        Assert.Equal(Location.ERR_INVALID_ADDRESS, Call(Module, "sceLocationStartLocationCallback", 999u, 10u, 0u, 0u)); // the callback first
        Assert.Equal(0, Call(Module, "sceLocationStartLocationCallback", handle, 10u, callback, 0u));
        Assert.Equal(Location.ERR_MULTIPLE_CALLBACK, Call(Module, "sceLocationStartLocationCallback", handle, 10u, callback, 0u));
        Assert.Equal(0, Call(Module, "sceLocationStopLocationCallback", handle));
        Assert.Equal(Location.ERR_NOT_RUNNING_CALLBACK, Call(Module, "sceLocationStopLocationCallback", handle));
        Assert.Equal(0, Call(Module, "sceLocationStartLocationCallback", handle, 10u, callback, 0u));
        Assert.Equal(0, Call(Module, "sceLocationClose", handle)); // closing the handle ends its callback
        Until(() => !Module.LocationCallbackThreadRunning, "the callback thread to end with the last callback");
    }

    /// <summary>
    /// FW 3.74 liblocation: the "SceLocationCallback" thread calls callback(result, handle, &amp;info, userdata) for
    /// the service's reports. The first position is reported at once, then only a move of at least the distance;
    /// a source without a position reports nothing; stopping ends the reports and the thread. Before, the callback was
    /// accepted and never called.
    /// </summary>
    [Fact]
    public void TheCallbackGetsThePositionEveryDistance()
    {
        var source = new TestSource();
        _harness.Emu.Host.Location = source;
        (uint handle, _) = Open();
        var log = new CallbackLog();
        uint callback = LocationCallback(log);
        Assert.Equal(0, Call(Module, "sceLocationStartLocationCallback", handle, 100u, callback, 0x55u));

        WaitForPasses(2);
        Assert.Equal(0, log.Count); // no position yet: nothing to report

        source.Fix = s_somewhere;
        Module.WakeLocationCallbacks();
        Until(() => log.Count == 1, "the first report");
        Assert.Equal((0, handle, -23.5613, -46.6565, 0x55u), log.Calls[0]);

        source.Fix = s_somewhere with { Latitude = -23.5613 + 0.0001 }; // ~11 m: under the distance
        WaitForPasses(2);
        Assert.Equal(1, log.Count);

        source.Fix = s_somewhere with { Latitude = -23.5513 }; // ~1.1 km from the last report
        Module.WakeLocationCallbacks();
        Until(() => log.Count == 2, "the report after a move past the distance");
        Assert.Equal(-23.5513, log.Calls[1].Latitude);

        Assert.Equal(0, Call(Module, "sceLocationStopLocationCallback", handle));
        Until(() => !Module.LocationCallbackThreadRunning, "the callback thread to end");
        source.Fix = s_somewhere;
        Assert.Equal(2, log.Count);
    }

    /// <summary>Waits until the callback thread made <paramref name="passes"/> more passes (a whole one after now).</summary>
    private void WaitForPasses(int passes)
    {
        int target = Module.LocationCallbackPasses + passes;
        Until(() =>
        {
            Module.WakeLocationCallbacks();
            return Module.LocationCallbackPasses >= target;
        }, $"{passes} passes of the callback thread");
    }

    [Fact]
    public void DistancesAreGreatCircleMetres()
    {
        Assert.Equal(0.0, Location.DistanceMetres(10, 20, 10, 20));
        Assert.InRange(Location.DistanceMetres(0, 0, 0, 1), 111_190, 111_200);     // one degree on the equator
        Assert.InRange(Location.DistanceMetres(-23.5613, -46.6565, -23.5513, -46.6565), 1_100, 1_125);
    }

    /// <summary>A capture may go to a public report: while "Gravar tudo" is armed (its pre-roll keeps save states) or
    /// recording, the position is withheld unless --capture-location; disarmed, it comes back.</summary>
    [Fact]
    public void ACaptureArmedWithoutTheOptionWithholdsThePosition()
    {
        _harness.Emu.Host.Location = new FixedLocationSource(s_somewhere);
        (uint handle, uint info) = Open();
        var cap = new SessionCapture(_harness.Emu, new SessionCaptureOptions
        {
            Root = Path.Combine(_harness.Root, "captures"),
            SetPresentTap = _ => { },
            StartState = false,
            GpuDump = false,
            Zip = false,
            Threads = () => "no threads",
            Info = () => "test info",
        });
        try
        {
            cap.SetPreRoll(5);
            Assert.True(_harness.Emu.Host.LocationWithheld);
            GuestMemory.Write(info, 1.0);
            Assert.Equal(Location.INFO_UNDETERMINED_LOCATION, Call(Module, "sceLocationGetLocation", handle, info));
            Assert.Equal(Location.DataInvalid, GuestMemory.Read<double>(info));

            cap.SetPreRoll(0);
            Assert.False(_harness.Emu.Host.LocationWithheld);
            Assert.Equal(0, Call(Module, "sceLocationGetLocation", handle, info));
            Assert.Equal(-23.5613, GuestMemory.Read<double>(info));
        }
        finally
        {
            cap.Dispose();
        }

        Assert.False(_harness.Emu.Host.LocationWithheld);
    }

    [Fact]
    public void ACaptureWithTheOptionKeepsThePosition()
    {
        _harness.Emu.Host.Location = new FixedLocationSource(s_somewhere);
        (uint handle, uint info) = Open();
        var cap = new SessionCapture(_harness.Emu, new SessionCaptureOptions
        {
            Root = Path.Combine(_harness.Root, "captures_keep"),
            SetPresentTap = _ => { },
            StartState = false,
            GpuDump = false,
            Zip = false,
            Threads = () => "no threads",
            Info = () => "test info",
            KeepLocation = true,
        });
        try
        {
            cap.SetPreRoll(5);
            Assert.False(_harness.Emu.Host.LocationWithheld);
            Assert.Equal(0, Call(Module, "sceLocationGetLocation", handle, info));
            cap.SetPreRoll(0);
        }
        finally
        {
            cap.Dispose();
        }
    }

    [Fact]
    public void TheSettingIsOffByDefaultAndTakesAPositionOrTheSystem()
    {
        Assert.Equal(LocationSpecKind.None, LocationSpec.Parse(null).Kind);
        Assert.Equal(LocationSpecKind.None, LocationSpec.Parse("").Kind);
        Assert.Equal(LocationSpecKind.None, LocationSpec.Parse("none").Kind);
        Assert.Null(LocationSpec.Parse("").CreateSource(() => throw new InvalidOperationException("no system source when off")));
        Assert.Equal(LocationSpecKind.System, LocationSpec.Parse(" System ").Kind);

        var fixedSpec = LocationSpec.Parse("-23.5613, -46.6565");
        Assert.Equal(LocationSpecKind.Fixed, fixedSpec.Kind);
        Assert.Equal((-23.5613, -46.6565, LocationSpec.DefaultAccuracy), (fixedSpec.Fix.Latitude, fixedSpec.Fix.Longitude, fixedSpec.Fix.Accuracy));
        Assert.True(double.IsNaN(fixedSpec.Fix.Altitude));
        var full = LocationSpec.Parse("fixed:48.8584,2.2945,35,5");
        Assert.Equal((48.8584, 2.2945, 35.0, 5f), (full.Fix.Latitude, full.Fix.Longitude, full.Fix.Altitude, full.Fix.Accuracy));
        Assert.Equal(48.8584, full.CreateSource(() => null)!.Current!.Value.Latitude);

        // out of range, not numbers, a decimal comma, a bad accuracy: off
        foreach (string bad in new[] { "91,0", "0,181", "abc,1", "1", "1,2,3,4,5", "-23,5;-46,6", "1,2,3,0", "1,2,x" })
        {
            Assert.Equal(LocationSpecKind.None, LocationSpec.Parse(bad).Kind);
        }

        // the system's service where the host has none: off
        Assert.Null(LocationSpec.Parse("system").CreateSource(() => null));
    }

    /// <summary>session.txt keeps the run options for the re-run: the --location= value is withheld unless
    /// --capture-location, and the diagnostic command line never carries it.</summary>
    [Fact]
    public void CapturesWithholdTheLocationOption()
    {
        var privacy = new CapturePrivacy(null, null, null, null);
        Assert.Equal(["--location=<location>", "--cpu=native"], privacy.RedactOptions(["--location=-23.5613,-46.6565", "--cpu=native"]));
        Assert.Equal(["--location=-23.5613,-46.6565"], privacy.RedactOptions(["--location=-23.5613,-46.6565"], keepLocation: true));
        Assert.Equal("--location=<location>", CapturePrivacy.RedactLocation("--location=system"));
        Assert.Equal("--location=", CapturePrivacy.RedactLocation("--location="));
        Assert.Equal("--microphone=tone", CapturePrivacy.RedactLocation("--microphone=tone"));
    }
}
