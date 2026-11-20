using System.Reflection;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The kernel twins (<see cref="KernelTwins"/>): each <c>*ForDriver</c>/<c>*ForKernel</c> entry the firmware exports as
/// the same function as a user syscall runs that syscall's HLE. Every twin is checked to forward to the user function
/// of its own name (read from the forwarding call), and one twin per library is run by its kernel NID and compared
/// with the user function run by its user NID on the same registers and memory (result and what it wrote).
/// </summary>
[Collection("guest")]
public sealed class KernelTwinsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint BufferSize = 0x400;

    private readonly HleHarness _harness = new(fixture, "ktwins");

    public void Dispose() => _harness.Dispose();

    private static IEnumerable<MethodInfo> Twins() => typeof(KernelTwins)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Where(m => m.GetCustomAttributes<HleAttribute>().Any());

    private static readonly MethodInfo s_forward = typeof(KernelTwins).GetMethod("Forward", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>The user NID a twin forwards to: the constant of its <c>Forward(context, nid)</c> call.</summary>
    private static uint ForwardedNid(MethodInfo twin) => ConstantPassedTo(twin, s_forward);

    /// <summary>The 32-bit constant <paramref name="method"/> passes as the last argument of its call to
    /// <paramref name="callee"/> (IL <c>ldc.i4 constant</c> right before <c>call callee</c>). The call is matched by its
    /// method token, not taken as the first <c>ldc.i4</c>/<c>call</c> pair: a coverage run (coverlet) instruments every
    /// sequence point with <c>ldc.i4 hitIndex; call RecordHit</c> ahead of the method's own code.</summary>
    private static uint ConstantPassedTo(MethodInfo method, MethodInfo callee)
    {
        if (method.Module != callee.Module)
        {
            throw new ArgumentException($"{callee.Name} is not defined in the module of {method.Name}", nameof(callee));
        }

        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        for (int i = 5; i + 5 <= il.Length; i++)
        {
            if (il[i] == 0x28 && il[i - 5] == 0x20 && BitConverter.ToInt32(il, i + 1) == callee.MetadataToken)
            {
                return BitConverter.ToUInt32(il, i - 4);
            }
        }

        throw new InvalidOperationException($"{method.Name} has no {callee.Name} call with a constant");
    }

    /// <summary>Stand-in for an instrumented twin: a call with a constant ahead of the forwarding call, the shape
    /// coverlet gives every method.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining | System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
    private static void InstrumentedShape()
    {
        RecordHitStandIn(0x0001B667);
        ForwardStandIn(null, 0xDCC90EDB);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RecordHitStandIn(int hitIndex) => GC.KeepAlive(hitIndex);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ForwardStandIn(object? context, uint userNid) => GC.KeepAlive(context ?? userNid);

    [Fact]
    public void ForwardedNidIsTheForwardCallsConstantEvenWithACallAheadOfIt()
    {
        MethodInfo shape = typeof(KernelTwinsTests).GetMethod(nameof(InstrumentedShape), BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo forward = typeof(KernelTwinsTests).GetMethod(nameof(ForwardStandIn), BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo hit = typeof(KernelTwinsTests).GetMethod(nameof(RecordHitStandIn), BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(0xDCC90EDBu, ConstantPassedTo(shape, forward));
        Assert.Equal(0x0001B667u, ConstantPassedTo(shape, hit));
    }

    /// <summary>The user function a twin's name stands for: the name without the kernel prefix "k"; the SceCpuForDriver
    /// atomics are named ksceKernelCpuAtomic* for the user sceKernelAtomic*, the CPU id ksceKernelCpuId for
    /// sceKernelGetCpuId.</summary>
    private static string UserNameOf(string twin)
    {
        string user = twin.Substring(1);
        if (user.StartsWith("sceKernelCpuAtomic", StringComparison.Ordinal))
        {
            return "sceKernelAtomic" + user.Substring("sceKernelCpuAtomic".Length);
        }

        return user == "sceKernelCpuId" ? "sceKernelGetCpuId" : user;
    }

    private static uint KernelNidOf(string twin) => NidDatabase.LookupName(twin)
        .First(i => i.Library.Contains("ForDriver", StringComparison.Ordinal) || i.Library.Contains("ForKernel", StringComparison.Ordinal)).Nid;

    [Fact]
    public void EveryTwinForwardsToTheImplementedUserFunctionOfItsName()
    {
        var wrong = new List<string>();
        int count = 0;
        foreach (MethodInfo twin in Twins())
        {
            count++;
            uint userNid = ForwardedNid(twin);
            string expected = UserNameOf(twin.Name);
            if (NidDatabase.NameOf(userNid) != expected || !_harness.Emu.Hle.IsImplemented(userNid))
            {
                wrong.Add($"{twin.Name} -> 0x{userNid:X8} {NidDatabase.NameOf(userNid)} (expected {expected})");
            }

            if (!NidDatabase.LookupName(twin.Name).Any(i => _harness.Emu.Hle.IsImplemented(i.Nid)))
            {
                wrong.Add($"{twin.Name}: no kernel NID bound");
            }
        }

        // 398 since ksceZlibDecompress follows SceKernelUtils' own codes (SysmemKernel.Utils), not libpaf's
        Assert.True(count >= 398, $"only {count} twins");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    /// <summary>One twin per library and the registers it runs with (buffer = a 0x400-byte guest buffer, refilled
    /// with the same bytes before each run).</summary>
    private static readonly (string Twin, Func<uint, uint[]> Args)[] s_samples =
    [
        ("ksceAudioOutSetAdoptMode", _ => [1]),
        ("ksceAudioInReleasePort", _ => [99]),
        ("ksceJpegEncoderGetContextSize", b => [b]),
        ("ksceKernelBacktrace", _ => [0, 0, 0, 0]),
        ("ksceBtGetConfiguration", b => [b]),
        ("ksceKernelCpuAtomicAddAndGet32", b => [b, 5]),
        ("ksceCtrlGetSamplingMode", b => [b]),
        ("ksceDisplayGetMaximumFrameBufResolution", b => [b, b + 4]),
        ("ksceDmacMemset", b => [b, 0x5A, 0x10]),
        ("ksceHidKeyboardGetIntercept", b => [b]),
        ("ksceIoGetThreadDefaultPriorityForSystem", _ => []),
        ("ksceKernelPowerTick", _ => [0]),
        ("ksceMotionDevGetDeviceInfo", b => [b]),
        ("ksceNetGetsockname", b => [0xFFFFFFFF, b, b + 0x20]),
        ("ksceKernelIsGameBudget", _ => []),
        ("ksceKernelLibcTime", _ => [0]),
        ("kscePsmDrmRemoveActData", _ => []),
        ("ksceRtcFormatRFC3339", b => [b + 0x200, b, 0]),
        ("ksceSblGcAuthMgrPkgVry", _ => [0]),
        ("ksceSblLicMgrGetLicenseStatus", _ => []),
        ("ksceKernelGetThreadId", _ => []),
        ("ksceTouchGetPanelInfo", b => [0, b]),
        ("ksceUdcdGetDeviceInfo", b => [b]),
        ("ksceUsbServAccessoryActivate", _ => [0]),
    ];

    [Fact]
    public void OneTwinPerLibraryMatchesItsUserFunction()
    {
        // every library with twins has a sample
        var libraries = Twins().Select(t => NidDatabase.Lookup(KernelNidOf(t.Name))!.Library).ToHashSet();
        var sampled = s_samples.Select(s => NidDatabase.Lookup(KernelNidOf(s.Twin))!.Library).ToHashSet();
        Assert.Empty(libraries.Except(sampled));

        uint buffer = _harness.Alloc(BufferSize);
        byte[] pattern = Enumerable.Range(0, (int)BufferSize).Select(i => (byte)(i * 7 + 1)).ToArray();
        _harness.OnGuest(t =>
        {
            foreach ((string twin, Func<uint, uint[]> args) in s_samples)
            {
                MethodInfo method = typeof(KernelTwins).GetMethod(twin, BindingFlags.Instance | BindingFlags.NonPublic)!;
                uint userNid = ForwardedNid(method);
                (uint, uint, byte[]) kernel = Run(t, KernelNidOf(twin), args(buffer), buffer, pattern);
                (uint, uint, byte[]) user = Run(t, userNid, args(buffer), buffer, pattern);
                if (twin == "ksceKernelLibcTime")
                {
                    // the time in seconds: the two runs may straddle a second
                    Assert.InRange(user.Item1 - kernel.Item1, 0u, 1u);
                    continue;
                }

                Assert.True(kernel.Item1 == user.Item1, $"{twin}: r0 0x{kernel.Item1:X8} != user 0x{user.Item1:X8}");
                Assert.True(kernel.Item3.AsSpan().SequenceEqual(user.Item3), $"{twin}: wrote different memory");
            }
        });
    }

    private (uint R0, uint R1, byte[] Memory) Run(VitaSharp.Core.Kernel.GuestThread thread, uint nid, uint[] args, uint buffer, byte[] pattern)
    {
        pattern.CopyTo(GuestMemory.Span(buffer, pattern.Length));
        Action<HleContext> implementation = _harness.Emu.Hle.ImplOf(nid) ?? throw new InvalidOperationException($"0x{nid:X8} not implemented");
        unsafe
        {
            for (int i = 0; i < 4; i++)
            {
                thread.Core.State->R[i] = i < args.Length ? args[i] : 0;
            }

            implementation(thread.Hle);
            return (thread.Core.State->R[0], thread.Core.State->R[1], GuestMemory.Span(buffer, pattern.Length).ToArray());
        }
    }
}
