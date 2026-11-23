using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The kernel memory of a process's hooks (thunk, trampoline, records, counting stubs) is freed when the process ends
/// only while it is still the block the framework allocated: when the kernel ended first, that memory went with it and
/// can already be another system's (or another hook's), which a late free would take away.
/// </summary>
public sealed partial class PluginHookCallLogTests
{
    [Fact]
    public void AProcessEndingAfterItsHookMemoryWasReusedLeavesTheNewOwnersBlock()
    {
        using var rig = new Rig(fixture, "interp");
        (uint target, _) = rig.LoadTarget();
        var hooks = new SceModuleBuilder("ReuseHook");
        int answers = hooks.ReturnConstant(3);
        uint hook = hooks.AddressOf(rig.Load("reuse.skprx", hooks).Segments[0].Address, answers);
        uint hookRef = rig.Harness.Word(0);
        int pid = rig.Harness.Emu.Pid;
        int uid = Convert.ToInt32(Invoke(rig.Framework, "HookAbsolute", pid, hookRef, target, hook, 0u, 0u));
        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        uint record = GuestMemory.R32(hookRef);
        GuestMemory memory = rig.Harness.Emu.Memory;

        // the kernel's end frees the record's block, and someone else gets the same memory
        GuestMemory.Block? reused;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            memory.Free(record);
            var others = new List<uint>();
            uint other = 0;
            while (other != record && others.Count < 4096)
            {
                other = memory.Alloc(16, "test:another owner", MemoryRegion.Kernel, 16);
                others.Add(other);
            }

            Assert.Equal(record, other);
            foreach (uint extra in others.Where(a => a != record))
            {
                memory.Free(extra);
            }

            reused = memory.FindBlock(record);
        }

        // the process ends afterwards: its patch goes, the other owner's block stays
        rig.Framework.ForgetProcess(pid);
        Assert.NotNull(reused);
        Assert.Same(reused, memory.FindBlock(record));
    }
}
