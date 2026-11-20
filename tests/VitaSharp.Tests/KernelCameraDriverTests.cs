using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// SceCameraForDriver without a sensor. (FW 3.74)
/// </summary>
[Collection("guest")]
public sealed class KernelCameraDriverTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint CameraParam = 0x802E0000, CameraNotInitialised = 0x802E0002;

    private readonly HleHarness _harness = new(fixture, "cameradriv");

    public void Dispose() => _harness.Dispose();

    private unsafe uint R0(GuestThread thread, uint nid, params uint[] args)
    {
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        for (int i = 0; i < 4; i++)
        {
            thread.Core.State->R[i] = i < args.Length ? args[i] : 0xA5A5A5A5;
        }

        uint stack = thread.Core.State->R[13];
        thread.Core.State->R[13] = stack - 0x40;
        for (int i = 4; i < args.Length; i++)
        {
            GuestMemory.W32(stack - 0x40 + (uint)(i - 4) * 4, args[i]);
        }

        implementation(thread.Hle);
        thread.Core.State->R[13] = stack;
        return thread.Core.State->R[0];
    }

    private uint Sized(uint size)
    {
        uint record = _harness.Alloc(size);
        GuestMemory.W32(record, size);
        return record;
    }

    [Fact]
    public void TheCameraDriverHasNoSensor()
    {
        _harness.OnGuest(t =>
        {
            Assert.Equal(CameraParam, R0(t, 0x0E9B4B9E, 2));
            Assert.Equal(CameraNotInitialised, R0(t, 0x0E9B4B9E, 1));
            Assert.Equal(CameraParam, R0(t, 0xB312CE90, 0, 0x20, 0, 0));
            Assert.Equal(CameraNotInitialised, R0(t, 0xB312CE90, 0, 0x0D, 0, 0));
            Assert.Equal(CameraParam, R0(t, 0xD56A12BD, 0, 0x0D, 0xF000, 0));
            Assert.Equal(CameraParam, R0(t, 0xE487870A, 0, Sized(0x20)));
            Assert.Equal(CameraNotInitialised, R0(t, 0xE487870A, 0, Sized(0x2C)));
            uint active = _harness.Word(0xFF);
            Assert.Equal(CameraParam, R0(t, 0xEB1CC2CA, (uint)t.Pid, 0));
            Assert.Equal(0u, R0(t, 0xEB1CC2CA, (uint)t.Pid, active));
            Assert.Equal(0u, GuestMemory.R32(active));
            Assert.Equal(CameraParam, R0(t, 0xD259707C, 0, 2));
            Assert.Equal(0u, R0(t, 0xD259707C, 0, 1));
            Assert.Equal(0xFFFFFFFFu, R0(t, 0xE81EADE1, 0xFFFFFFFF));
        });
    }
}
