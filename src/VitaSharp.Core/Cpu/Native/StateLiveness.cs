using System.Diagnostics.CodeAnalysis;
using VitaSharp.Core.Cpu.Backends;

namespace VitaSharp.Core.Cpu.Native;

/// <summary>
/// Guest-state liveness across linked blocks: a static exit need not store a register or flag that every path of its
/// target block overwrites before reading it. Most blocks end in a compare and a branch whose flags only the branch
/// uses, and the next block sets its own flags first; without this every such exit materialises N Z C V (CSETs and
/// stores) for nobody. Dynarmic, FEX and Box64 drop dead flag computations within their (larger) blocks the same way.
/// <para>
/// The state in memory may then hold stale values of slots the guest can no longer observe: the next instruction to
/// run is the target, which writes them before any read, callout or exit. Nothing else reads them in between (an exit
/// returns to the dispatcher, which runs the target next; a block with a callout, which may raise an exit the
/// dispatcher handles with the state, keeps every store). The differential verifier and probes (which compare or show
/// the state at block boundaries) turn it off.
/// </para>
/// </summary>
public static class StateLiveness
{
    /// <summary><see cref="Block._deadIn"/> before it is computed.</summary>
    public const int Unknown = int.MinValue;
    /// <summary>The slots that can be dead: R0-R14 (bits 0-14; R15 and Thumb are what the dispatcher reads) and N Z C V
    /// (bits 16-19, <see cref="IrBuilder.SlotFlag0"/>).</summary>
    public const int Trackable = 0x7FFF | 0xF << IrBuilder.SlotFlag0;

    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static ArmFrontend? t_frontend; // its own IR buffers: the compiler's frontend holds the block being compiled
    [SuppressMessage("Style", "IDE1006", Justification = "[ThreadStatic]: t_camelCase; .editorconfig cannot select the attribute")]
    [ThreadStatic] private static int[]? t_liveAtLabel;

    /// <summary>R15 in the masks of <see cref="DropDeadStores"/>: dead at a static exit marked <see cref="ColdPc"/>.</summary>
    private const int Pc = 1 << 15;

    /// <summary>Imm3 of an <see cref="IrOp.ExitStatic"/>: the emitter stores R15 (the exit's target) itself, only on the
    /// paths that return to the dispatcher (<see cref="Arm64Emitter"/>'s patched exits): a linked exit never needs it.</summary>
    public const uint ColdPc = 1;

    private static int Bit(in IrInst x) => x.Op switch
    {
        IrOp.GetReg or IrOp.SetReg => 1 << (int)x.Imm,
        IrOp.GetFlag or IrOp.SetFlag => 1 << (IrBuilder.SlotFlag0 + (int)x.Imm),
        _ => 0,
    };

    /// <summary>
    /// The slots <paramref name="b"/> overwrites on every path before reading them (or before a callout or an exit),
    /// cached in the block. Translated with the frontend the compilers use, so it is exactly what the block's own code does.
    /// </summary>
    public static int DeadIn(Block b, ArmFrontend? like = null)
    {
        int cached = b._deadIn;
        if (cached != Unknown)
        {
            return cached;
        }

        ArmFrontend armFrontend = t_frontend ??= NativeArm64Compiler.NewFrontend(reuseIr: true);
        if (like != null)
        {
            armFrontend.HleCallouts = like.HleCallouts;
            armFrontend.DeferRegions = like.DeferRegions;
        }
        IrBlock ir = armFrontend.Translate(b);
        int written = 0, readFirst = 0, depth = 0;
        uint openLabel = 0;
        foreach (ref readonly IrInst x in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ir.Insts))
        {
            switch (x.Op)
            {
                case IrOp.GetReg:
                case IrOp.GetFlag:
                    readFirst |= Bit(x) & ~written;
                    break;
                case IrOp.SetReg:
                case IrOp.SetFlag:
                    if (depth == 0)
                    {
                        written |= Bit(x); // a store inside a conditional region happens on one path only
                    }

                    break;
                case IrOp.CondBranch:
                case IrOp.BranchIf:
                    depth = 1;
                    openLabel = x.Imm2;
                    break;
                case IrOp.Label:
                    if (depth > 0 && x.Imm == openLabel)
                    {
                        depth = 0;
                    }

                    break;
                case IrOp.Callout:
                case IrOp.ExitStatic:
                case IrOp.ExitDynamic:
                    readFirst |= ~written; // the handler or whatever runs next sees the state as it is
                    break;
            }
            if (depth == 0 && x.Op is IrOp.Callout or IrOp.ExitStatic or IrOp.ExitDynamic)
            {
                break;
            }
        }
        int dead = ~readFirst & Trackable;
        b._deadIn = dead;
        return dead;
    }

    /// <summary>
    /// Removes the stores of guest state no path reads again before the targets of the block's static exits overwrite it
    /// (<paramref name="deadAt"/>: the slots dead at a guest address, 0 when unknown). Run before
    /// <see cref="IrPasses.DeadCode"/>, which then drops the computations only those stores used (flag CSETs, ...).
    /// Static exits marked <see cref="ColdPc"/> also let the R15 stores go. Returns the number of stores removed.
    /// </summary>
    public static int DropDeadStores(IrBlock ir, Func<uint, bool, int> deadAt)
    {
        Span<IrInst> insts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ir.Insts);
        foreach (ref readonly IrInst x in insts)
        {
            if (x.Op == IrOp.Callout)
            {
                return 0; // a handler may raise an exit the dispatcher handles with the full state
            }
        }

        const int All = Trackable | Pc;
        int[] atLabel = t_liveAtLabel is { } l && l.Length >= ir.Labels ? l : (t_liveAtLabel = new int[Math.Max(16, ir.Labels * 2)]);
        Array.Fill(atLabel, All, 0, ir.Labels);
        int live = All, removed = 0;
        for (int i = insts.Length - 1; i >= 0; i--)
        {
            ref IrInst x = ref insts[i];
            switch (x.Op)
            {
                case IrOp.ExitStatic:
                    live = All & ~(deadAt(x.Imm, x.Imm2 != 0) & Trackable | (x.Imm3 == ColdPc ? Pc : 0));
                    break;
                case IrOp.ExitDynamic:
                    live = All;
                    break;
                case IrOp.Label:
                    atLabel[x.Imm] = live;
                    break;
                case IrOp.CondBranch:
                case IrOp.BranchIf:
                    live |= atLabel[x.Imm2];
                    break;
                case IrOp.GetReg:
                case IrOp.GetFlag:
                    live |= Bit(x);
                    break;
                case IrOp.SetReg:
                case IrOp.SetFlag:
                {
                    int bit = Bit(x);
                    if (bit == 0)
                    {
                        break;
                    }

                    if ((live & bit) == 0)
                    {
                        x = new IrInst(IrOp.Nop);
                        removed++;
                    }
                    else
                    {
                        live &= ~bit;
                    }

                    break;
                }
            }
        }
        return removed;
    }
}
