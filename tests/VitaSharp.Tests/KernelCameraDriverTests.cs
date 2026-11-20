using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// SceCameraForDriver (FW 3.74) with the two sensors, on the user camera state.
/// </summary>
[Collection("guest")]
public sealed class KernelCameraDriverTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint CameraParam = 0x802E0000;

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

    private static uint Nid(string name) => VitaSharp.Core.Hle.NidDatabase.LookupName(name)[0].Nid;

    [Fact]
    public void TheKernelDriverSharesTheUserCameraState()
    {
        _harness.OnGuest(t =>
        {
            const uint NotOpen = 0x802E0004, NotActive = 0x802E000E, AlreadyOpen = 0x802E0003;
            uint buffer = _harness.Alloc(320 * 240 * 4);
            uint info = _harness.Alloc(0x30);
            GuestMemory.Fill(info, 0, 0x30);
            GuestMemory.W32(info, 0x30);
            GuestMemory.Write<ushort>(info + 0x06, 5);
            GuestMemory.Write<ushort>(info + 0x08, 2);
            GuestMemory.Write<ushort>(info + 0x0A, 30);
            GuestMemory.W32(info + 0x14, 320 * 240 * 4);
            GuestMemory.W32(info + 0x20, buffer);
            uint pid = (uint)t.Pid;
            uint active = _harness.Word(0xFF);

            Assert.Equal(CameraParam, R0(t, 0x0E9B4B9E, 2));
            Assert.Equal(NotOpen, R0(t, 0x0E9B4B9E, 0));
            Assert.Equal(0u, R0(t, 0xEB1CC2CA, pid, active));
            Assert.Equal(0u, GuestMemory.R32(active));
            Assert.Equal(CameraParam, R0(t, 0xE487870A, 0, _harness.Alloc(0x20)));
            Assert.Equal(0u, R0(t, 0xE487870A, 0, info)); // the kernel opens the process's camera
            Assert.Equal(AlreadyOpen, R0(t, Nid("sceCameraOpen"), 0, info)); // one camera state for both sides
            Assert.Equal(0u, R0(t, 0x0E9B4B9E, 0));
            Assert.Equal(NotActive, R0(t, 0x3590BB9B, 0, 5));
            Assert.Equal(0u, R0(t, 0x22E70D83, 0, 0, 0));

            Assert.Equal(0u, R0(t, Nid("sceCameraStart"), 0));
            Assert.Equal(1u, R0(t, 0xEB1CC2CA, pid, active));
            Assert.Equal(1u, GuestMemory.R32(active));
            Assert.Equal(1u, R0(t, 0x22E70D83, 0, 0, 0));
            Assert.Equal(0u, R0(t, 0x22E70D83, 1, 0, 0));
            Assert.Equal(0u, R0(t, 0x3590BB9B, 0, 5));
            uint state = _harness.Alloc(12);
            Assert.Equal(0u, R0(t, 0xEE3D4B1C, 0, state));
            Assert.Equal(1u, GuestMemory.R32(state));
            Assert.Equal(1u, GuestMemory.R32(state + 4));
            Assert.Equal(320u | (240u << 16), GuestMemory.R32(state + 8));

            uint value = _harness.Word(0);
            Assert.Equal(CameraParam, R0(t, 0xB312CE90, 0, 0x20, 0, 0));
            Assert.Equal(0u, R0(t, 0xB312CE90, 0, 0x0D, 0x1234, 0));
            Assert.Equal(0u, R0(t, 0xFE3C170C, 0, 0x0D, value, 0));
            Assert.Equal(0x1234u, GuestMemory.Read<ushort>(value));
            Assert.Equal(CameraParam, R0(t, 0xD56A12BD, 0, 0x0D, 0xF000, 0));
            Assert.Equal(0u, R0(t, 0xD56A12BD, 0, 0x30, 7, 0));
            Assert.Equal(0u, R0(t, 0xC2E71BE8, 0, 0x30, value, 0));
            Assert.Equal(7u, GuestMemory.Read<ushort>(value));

            Assert.Equal(0u, R0(t, Nid("sceCameraStop"), 0));
            Assert.Equal(0u, R0(t, 0xEB1CC2CA, pid, active));
            Assert.Equal(0u, R0(t, Nid("sceCameraClose"), 0));
            Assert.Equal(CameraParam, R0(t, 0xD259707C, 0, 2));
            Assert.Equal(0xFFFFFFFFu, R0(t, 0xE81EADE1, 0xFFFFFFFF));
        });
    }
}
