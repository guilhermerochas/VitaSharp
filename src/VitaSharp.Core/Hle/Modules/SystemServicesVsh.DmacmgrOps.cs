using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceDmacmgrForDriver DMA operations (os0:kd/dmacmgr.skprx 3.74, read in the module, study only). An operation is a
// handle (Alloc: an object with an event flag named after it, "SceDmacDefault" without a name) that is assigned a
// controller (0x10, 0x11: DMAC0/1; 0x12 with a channel 0..15: DMAC4; 0x13: DMAC5, the crypto engine; 0x14: DMAC6, only
// mapped with a debug switch), given a chain of blocks (SetupChain, SetupDirect for one block, Concatenate to append),
// queued on the controller (EnQueue) and waited for (Sync). A chain block is eight words: [0] source, [1] destination,
// [2] length (low 24 bits), [3] control, [4..5] (DMAC5 key words), [6] burst sizes (low and high halfword: the length
// must be a multiple of each), [7] the next block, -1 for the last (ksceDmacMemcpy(dst, src, n) builds { src, dst, n }).
// The address mode says which addresses are virtual and translated page by page: 0 none, 1 the source, 0x10 the
// destination, 0x11 both; 0x100 is the two-block raw form. The options word block (0xC bytes, 0x5C for DMAC5) carries
// control bits and, at +8, the fill word: a block whose control has both bits 0xC set fills its destination with it
// (ksceDmacMemset passes { 0xC, 0x3E600, byte * 0x01010101 } and control 0x300C). When the transfer ends, the
// completion interrupt frees the chain, sets bit 0 of the event flag, marks the operation done and calls its callback
// (op, status, argument, the block an error stopped at, bytes moved). In VitaSharp guest addresses are physical, a
// transfer moves its bytes when it is queued and completes at once (the callback runs on the queuing thread, like an
// interrupt taken right away); DMAC5's cipher needs its keys from the secure processor, so a DMAC5 operation is refused
// at the queue (0x80020004).
public sealed partial class SystemServicesVsh
{
    /// <summary>SceDmacmgr: a bad handle, kind, channel, chain or mode (0x80020005).</summary>
    const int DMAOP_ERR_INVALID_ARGUMENT = unchecked((int)0x80020005);

    /// <summary>SceDmacmgr: an options block of the wrong size (0x80020009).</summary>
    const int DMAOP_ERR_INVALID_SIZE = unchecked((int)0x80020009);

    /// <summary>SceDmacmgr: the operation is queued or running (0x80027201).</summary>
    const int DMAOP_ERR_BUSY = unchecked((int)0x80027201);

    /// <summary>SceDmacmgr: DeQueue of an operation that is not queued (0x80027202).</summary>
    const int DMAOP_ERR_NOT_QUEUED = unchecked((int)0x80027202);

    /// <summary>SceDmacmgr: no chain to queue or wait for (0x80027203).</summary>
    const int DMAOP_ERR_NO_CHAIN = unchecked((int)0x80027203);

    /// <summary>SceDmacmgr: the operation is not allocated (0x80027205).</summary>
    const int DMAOP_ERR_NOT_ALLOCATED = unchecked((int)0x80027205);

    /// <summary>SceDmacmgr: EnQueue of a finished operation that was not set up again (0x80027206).</summary>
    const int DMAOP_ERR_FINISHED = unchecked((int)0x80027206);

    /// <summary>SceDmacmgr: the controller has no channel for it (0x80027207).</summary>
    const int DMAOP_ERR_NO_CHANNEL = unchecked((int)0x80027207);

    /// <summary>The crypto engine's cipher, whose keys live in the secure processor (0x80020004).</summary>
    const int DMAOP_ERR_SECURE = unchecked((int)0x80020004);

    const uint DmaKindDmac0 = 0x10, DmaKindDmac1 = 0x11, DmaKindDmac4 = 0x12, DmaKindDmac5 = 0x13, DmaKindDmac6 = 0x14;

    const uint DmaModePhysical = 0, DmaModeVirtualSource = 1, DmaModeVirtualDestination = 0x10;
    const uint DmaModeVirtualBoth = 0x11, DmaModeRaw = 0x100;

    /// <summary>The last block's next word.</summary>
    const uint DmaChainEnd = 0xFFFFFFFF;

    /// <summary>The control bits of a fill block.</summary>
    const uint DmaControlFill = 0xC;

    /// <summary>The longest chain followed (a cyclic chain would run forever on the controller).</summary>
    const int DmaMaxBlocks = 0x10000;

    /// <summary>The bit the completion sets in the operation's event flag.</summary>
    const uint DmaDoneBit = 1;

    /// <summary>The wait mode of Sync: OR, clear the pattern.</summary>
    const uint DmaWaitMode = 5;

    /// <summary>One operation: its event flag, controller, callback, fill word, the chain set up (guest blocks with the
    /// address mode of each, kept apart) and the done flag.</summary>
    struct DmaOperation
    {
        public int EventFlag;
        public uint Kind;
        public uint Callback;
        public uint CallbackArgument;
        public uint FillWord;
        public bool Done;
    }

    [SnapshotMissing] // states of builds before it: no operation allocated
    readonly Dictionary<uint, DmaOperation> _dmaOperations = [];

    /// <summary>The chains of the operations: block address, address mode, two words each.</summary>
    [SnapshotMissing] // states of builds before it: no chain set up
    readonly Dictionary<uint, List<uint>> _dmaChains = [];

    [SnapshotMissing] // states of builds before it: the first handle
    uint _dmaNextHandle;

    ThreadMgr DmaThreads => Emu.Hle.Modules.OfType<ThreadMgr>().First();

    SysmemKernel DmaSysmem => Emu.Hle.Modules.OfType<SysmemKernel>().First();

    /// <summary>ksceKernelDmaOpAlloc(const char *name): a new operation with its event flag; the handle (odd).</summary>
    [Hle(Nid = 0x7CD5088A, Library = "SceDmacmgrForDriver")]
    int ksceKernelDmaOpAlloc(uint name)
    {
        string flagName = name == 0 ? "SceDmacDefault" : GuestMemory.ReadCString(name, 31);
        int eventFlag = DmaThreads.sceKernelCreateEventFlag(flagName, 0, 0, 0);
        if (eventFlag < 0)
        {
            return eventFlag;
        }

        _dmaNextHandle++;
        uint handle = (_dmaNextHandle << 1) | 1;
        _dmaOperations[handle] = new DmaOperation { EventFlag = eventFlag };
        return (int)handle;
    }

    /// <summary>ksceKernelDmaOpFree(op): the operation stopped, its chain and event flag released; 0.</summary>
    [Hle(Nid = 0xADFF1186, Library = "SceDmacmgrForDriver")]
    int ksceKernelDmaOpFree(uint handle)
    {
        if (!_dmaOperations.TryGetValue(handle, out DmaOperation operation))
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        DmaThreads.sceKernelDeleteEventFlagThroughEntry(operation.EventFlag);
        _dmaOperations.Remove(handle);
        _dmaChains.Remove(handle);
        return 0;
    }

    /// <summary>ksceKernelDmaOpAssign(op, int kind, int channel): the controller (0x10, 0x11, 0x12 with channel 0..15,
    /// 0x13, 0x14 when mapped; else 0x80020005); an operation with a chain queued is busy (0x80027201); clears done.</summary>
    [Hle(Nid = 0xFCE4171A, Library = "SceDmacmgrForDriver")]
    int ksceKernelDmaOpAssign(uint handle, uint kind, uint channel)
    {
        if (!_dmaOperations.TryGetValue(handle, out DmaOperation operation))
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        bool valid = kind switch
        {
            DmaKindDmac0 or DmaKindDmac1 or DmaKindDmac5 => true,
            DmaKindDmac4 => channel <= 0xF,
            DmaKindDmac6 => DmacSixthControllerMapped(),
            _ => false,
        };
        if (!valid)
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        operation.Kind = kind;
        operation.Done = false;
        _dmaOperations[handle] = operation;
        return 0;
    }

    /// <summary>ksceKernelDmaOpSetCallback(op, callback, void *argument): a NULL callback 0x80020005; 0.</summary>
    [Hle(Nid = 0xCF627CFD, Library = "SceDmacmgrForDriver")]
    int ksceKernelDmaOpSetCallback(uint handle, uint callback, uint argument)
    {
        if (callback == 0 || !_dmaOperations.TryGetValue(handle, out DmaOperation operation))
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        operation.Callback = callback;
        operation.CallbackArgument = argument;
        _dmaOperations[handle] = operation;
        return 0;
    }

    /// <summary>ksceKernelDmaOpSetupChain(op, chain, options, int mode): the options block 0xC bytes (0x5C on DMAC5;
    /// else 0x80020009), its fill word kept; the chain checked (NULL or -1, a bad mode, a length not a multiple of the
    /// burst sizes: 0x80020005; a virtual range that does not translate: 0x80022007) and replacing the one set up;
    /// clears done.</summary>
    [Hle(Nid = 0x167079FC, Library = "SceDmacmgrForDriver")]
    int ksceKernelDmaOpSetupChain(uint handle, uint chain, uint options, uint mode)
    {
        if (!_dmaOperations.TryGetValue(handle, out DmaOperation operation))
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        uint fillWord = 0;
        if (options != 0)
        {
            uint size = operation.Kind == DmaKindDmac5 ? 0x5Cu : 0xCu;
            if (GuestMemory.R32(options) != size)
            {
                return DMAOP_ERR_INVALID_SIZE;
            }

            fillWord = GuestMemory.R32(options + 8);
        }

        var blocks = new List<uint>();
        int built = DmaBuildChain(operation.Kind, chain, mode, blocks);
        if (built < 0)
        {
            return built;
        }

        operation.FillWord = fillWord;
        operation.Done = false;
        _dmaOperations[handle] = operation;
        _dmaChains[handle] = blocks;
        return 0;
    }

    /// <summary>ksceKernelDmaOpSetupDirect(op, block, int mode): one block { source, destination, length, control,
    /// burst sizes, options control, fill word } as a chain of one; NULL 0x80020005.</summary>
    [Hle(Nid = 0x01A599E0, Library = "SceDmacmgrForDriver")]
    int ksceKernelDmaOpSetupDirect(uint handle, uint block, uint mode)
    {
        if (block == 0 || !_dmaOperations.TryGetValue(handle, out DmaOperation operation))
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        if (!DmaValidMode(mode))
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        int checkedBlock = DmaCheckBlock(operation.Kind, block, mode, GuestMemory.R32(block + 0x10));
        if (checkedBlock < 0)
        {
            return checkedBlock;
        }

        operation.FillWord = operation.Kind == DmaKindDmac5 ? 0 : GuestMemory.R32(block + 0x18);
        operation.Done = false;
        _dmaOperations[handle] = operation;
        _dmaChains[handle] = [block, mode | DmaDirectFlag];
        return 0;
    }

    /// <summary>A block of SetupDirect: its burst sizes are word 4, not word 6.</summary>
    const uint DmaDirectFlag = 0x80000000;

    /// <summary>ksceKernelDmaOpConcatenate(op, chain, int mode): a chain appended to the one set up, or, on a finished
    /// operation, queued again at once; nothing set up and not finished 0x80027203.</summary>
    [Hle(Nid = 0xA4454DEA, Library = "SceDmacmgrForDriver")]
    int ksceKernelDmaOpConcatenate(uint handle, uint chain, uint mode)
    {
        if (!_dmaOperations.TryGetValue(handle, out DmaOperation operation))
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        bool hasChain = _dmaChains.ContainsKey(handle);
        if (!operation.Done && !hasChain)
        {
            return DMAOP_ERR_NO_CHAIN;
        }

        var blocks = new List<uint>();
        int built = DmaBuildChain(operation.Kind, chain, mode, blocks);
        if (built < 0)
        {
            return built;
        }

        if (hasChain)
        {
            _dmaChains[handle].AddRange(blocks);
            return 0;
        }

        _dmaChains[handle] = blocks;
        operation.Done = false;
        _dmaOperations[handle] = operation;
        return DmaRun(handle);
    }

    /// <summary>ksceKernelDmaOpEnQueue(op): no chain 0x80027203, finished and not set up again 0x80027206, no
    /// controller 0x80027207; the transfer runs and completes.</summary>
    [Hle(Nid = 0x543F54CF, Library = "SceDmacmgrForDriver")]
    int ksceKernelDmaOpEnQueue(uint handle)
    {
        if (!_dmaOperations.TryGetValue(handle, out DmaOperation operation))
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        if (!_dmaChains.ContainsKey(handle))
        {
            return DMAOP_ERR_NO_CHAIN;
        }

        if (operation.Done)
        {
            return DMAOP_ERR_FINISHED;
        }

        if (operation.Kind == 0)
        {
            return DMAOP_ERR_NO_CHANNEL;
        }

        return DmaRun(handle);
    }

    /// <summary>ksceKernelDmaOpDeQueue(op): a transfer completes when it is queued, so none is ever waiting in the
    /// queue (0x80027202).</summary>
    [Hle(Nid = 0x7433F70F, Library = "SceDmacmgrForDriver")]
    int ksceKernelDmaOpDeQueue(uint handle)
    {
        return _dmaOperations.ContainsKey(handle) ? DMAOP_ERR_NOT_QUEUED : DMAOP_ERR_INVALID_ARGUMENT;
    }

    /// <summary>ksceKernelDmaOpQuit(op): the chain set up dropped (nothing runs); 0.</summary>
    [Hle(Nid = 0x03052D0D, Library = "SceDmacmgrForDriver")]
    int ksceKernelDmaOpQuit(uint handle)
    {
        if (!_dmaOperations.ContainsKey(handle))
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        _dmaChains.Remove(handle);
        return 0;
    }

    /// <summary>ksceKernelDmaOpSync(op, int mode, SceUInt32 timeout, int *status): nothing set up nor finished
    /// 0x80027203; mode 1 polls (not finished: 1), 2 waits, 3 waits up to the timeout, else 0x80020005; the completion
    /// bit is consumed and *status gets the block an error stopped at (0: transfers here complete whole).</summary>
    [Hle(Nid = 0x397A917C, Library = "SceDmacmgrForDriver", Cost = HleCost.SchedulerWait)]
    int ksceKernelDmaOpSync(uint handle, uint mode, uint timeout, uint status)
    {
        if (!_dmaOperations.TryGetValue(handle, out DmaOperation operation))
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        if (!operation.Done && !_dmaChains.ContainsKey(handle))
        {
            return DMAOP_ERR_NO_CHAIN;
        }

        int result;
        switch (mode)
        {
            case 1:
                if (!operation.Done)
                {
                    return 1;
                }

                EventFlag? flag = Emu.Kernel.Objects.Get<EventFlag>(operation.EventFlag);
                result = flag?.Poll(DmaDoneBit, DmaWaitMode, new Ptr<uint>(0)) ?? SceError.UNKNOWN_EVF_ID;
                break;
            case 2:
                result = DmaThreads.sceKernelWaitEventFlagThroughEntry(operation.EventFlag, DmaDoneBit, DmaWaitMode, new Ptr<uint>(0), new Ptr<uint>(0));
                break;
            case 3:
                uint timeoutWord = Mem.Alloc(4, "SceDmacSync timeout", MemoryRegion.Kernel, 4);
                GuestMemory.W32(timeoutWord, timeout);
                result = DmaThreads.sceKernelWaitEventFlagThroughEntry(operation.EventFlag, DmaDoneBit, DmaWaitMode, new Ptr<uint>(0), new Ptr<uint>(timeoutWord));
                Mem.Free(timeoutWord);
                break;
            default:
                return DMAOP_ERR_INVALID_ARGUMENT;
        }

        if (result < 0)
        {
            return result;
        }

        if (status != 0)
        {
            GuestMemory.W32(status, 0);
        }

        return 0;
    }

    static bool DmaValidMode(uint mode)
    {
        return mode is DmaModePhysical or DmaModeVirtualSource or DmaModeVirtualDestination or DmaModeVirtualBoth or DmaModeRaw;
    }

    /// <summary>Follows a chain from <paramref name="chain"/> into <paramref name="blocks"/> (block, mode pairs), each
    /// block checked; 0 or the error.</summary>
    int DmaBuildChain(uint kind, uint chain, uint mode, List<uint> blocks)
    {
        if (chain == 0 || chain == DmaChainEnd || !DmaValidMode(mode))
        {
            return DMAOP_ERR_INVALID_ARGUMENT;
        }

        if (mode == DmaModeRaw)
        {
            blocks.Add(chain);
            blocks.Add(mode);
            return 0;
        }

        uint block = chain;
        for (int count = 0; block != DmaChainEnd; count++)
        {
            if (block == 0 || count == DmaMaxBlocks)
            {
                return DMAOP_ERR_INVALID_ARGUMENT;
            }

            int checkedBlock = DmaCheckBlock(kind, block, mode, GuestMemory.R32(block + 0x18));
            if (checkedBlock < 0)
            {
                return checkedBlock;
            }

            blocks.Add(block);
            blocks.Add(mode);
            block = GuestMemory.R32(block + 0x1C);
        }

        return 0;
    }

    /// <summary>The checks of one block: a translated block's length a multiple of the burst sizes (outside DMAC5) and
    /// its virtual ranges translating.</summary>
    int DmaCheckBlock(uint kind, uint block, uint mode, uint burst)
    {
        uint length = GuestMemory.R32(block + 8) & 0xFFFFFF;
        if (mode != DmaModePhysical && kind != DmaKindDmac5)
        {
            uint low = burst & 0xFFFF;
            uint high = burst >> 16;
            uint mask = (low == 0 ? 0 : low - 1) | (high == 0 ? 0 : high - 1);
            if ((length & mask) != 0)
            {
                return DMAOP_ERR_INVALID_ARGUMENT;
            }
        }

        bool fill = (GuestMemory.R32(block + 0xC) & DmaControlFill) == DmaControlFill;
        if ((mode & DmaModeVirtualSource) != 0 && !fill)
        {
            int walked = DmaSysmem.MmuWalk(GuestMemory.R32(block), length);
            if (walked < 0)
            {
                return walked;
            }
        }

        if ((mode & DmaModeVirtualDestination) != 0)
        {
            int walked = DmaSysmem.MmuWalk(GuestMemory.R32(block + 4), length);
            if (walked < 0)
            {
                return walked;
            }
        }

        return 0;
    }

    /// <summary>Runs the chain set up and completes the operation: the chain released, the completion bit set, done,
    /// then the callback (op, status 0, argument, the block an error stopped at, bytes moved).</summary>
    int DmaRun(uint handle)
    {
        DmaOperation operation = _dmaOperations[handle];
        if (operation.Kind == DmaKindDmac5)
        {
            return DMAOP_ERR_SECURE;
        }

        List<uint> blocks = _dmaChains[handle];
        uint moved = 0;
        for (int i = 0; i < blocks.Count; i += 2)
        {
            uint block = blocks[i];
            if ((blocks[i + 1] & ~DmaDirectFlag) == DmaModeRaw)
            {
                moved += DmaTransfer(block, operation.FillWord);
                moved += DmaTransfer(block + 0x20, operation.FillWord);
            }
            else
            {
                moved += DmaTransfer(block, operation.FillWord);
            }
        }

        _dmaChains.Remove(handle);
        operation.Done = true;
        _dmaOperations[handle] = operation;
        DmaThreads.sceKernelSetEventFlagThroughEntry(operation.EventFlag, DmaDoneBit);
        if (operation.Callback != 0 && GuestThread.Current is { } thread)
        {
            thread.CallGuest(operation.Callback, handle, 0, operation.CallbackArgument, 0, moved);
        }

        return 0;
    }

    /// <summary>One block's transfer: a fill of the destination with the fill word, or a copy (overlap safe, the
    /// controller reads ahead of its writes in bursts); the bytes moved.</summary>
    static uint DmaTransfer(uint block, uint fillWord)
    {
        uint source = GuestMemory.R32(block);
        uint destination = GuestMemory.R32(block + 4);
        uint length = GuestMemory.R32(block + 8) & 0xFFFFFF;
        if (length == 0)
        {
            return 0;
        }

        if ((GuestMemory.R32(block + 0xC) & DmaControlFill) == DmaControlFill)
        {
            Span<byte> target = GuestMemory.Span(destination, (int)length);
            for (int i = 0; i < target.Length; i++)
            {
                target[i] = (byte)(fillWord >> ((i & 3) * 8));
            }

            return length;
        }

        GuestMemory.Span(source, (int)length).CopyTo(GuestMemory.Span(destination, (int)length));
        return length;
    }
}
