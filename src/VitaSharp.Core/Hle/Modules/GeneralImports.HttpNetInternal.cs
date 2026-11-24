using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// Exports without public names that no installed module imports (the "General" group) of SceHttp and
// SceNetInternal (FW 3.74 libhttp and libnet read in their code, study only).

public sealed partial class Http
{
    sealed partial class Settings
    {
        /// <summary>libhttp's one-byte option 0xE (sceHttp_F30073EC sets it, NID_30F5D11F reads it), option 0x15 (one
        /// word, NID_C498765A) and the word at +0xD4 of a template or connection (NID_175685B8, 2 or 3).</summary>
        [SnapshotMissing] // states of builds before it: the calls did not keep them
        public int OptionE, Option15, OptionD4;
    }

    /// <summary>libhttp's loaded certificate store (NID_E0825488 starts it with the caller's allocator): the CA list,
    /// the client certificate and its key, kept as copies.</summary>
    [SnapshotMissing]
    bool _certStoreStarted;

    [SnapshotMissing]
    readonly List<byte[]> _loadedCaCerts = [];

    [SnapshotMissing]
    byte[]? _loadedClientCert, _loadedClientKey;

    /// <summary>NID_30F5D11F (SceHttp)(int id, int *value): reads option 0xE (before sceHttpInit 0x80431001, NULL
    /// 0x804311FE, an unknown id 0x80431100).</summary>
    [Hle("NID_30F5D11F", Nid = 0x30F5D11F, Library = "SceHttp")]
    int HttpGetOptionE(int id, uint value)
    {
        lock (_lock)
        {
            if (!_inited)
            {
                return ERR_BEFORE_INIT;
            }

            if (value == 0)
            {
                return ERR_INVALID_VALUE;
            }
        }

        return GetSetting(id, s => s.OptionE, value);
    }

    /// <summary>NID_C498765A (SceHttp)(int id, int value): sets option 0x15 (before sceHttpInit 0x80431001).</summary>
    [Hle("NID_C498765A", Nid = 0xC498765A, Library = "SceHttp")]
    int HttpSetOption15(int id, int value)
    {
        lock (_lock)
        {
            if (!_inited)
            {
                return ERR_BEFORE_INIT;
            }
        }

        return SetSetting(id, s => s.Option15 = value);
    }

    /// <summary>NID_175685B8 (SceHttp)(int id, int value): value 2 or 3 (else 0x804311FE) into +0xD4 of a template or a
    /// connection (a request 0x80431100).</summary>
    [Hle("NID_175685B8", Nid = 0x175685B8, Library = "SceHttp")]
    int HttpSetTemplateWordD4(int id, int value)
    {
        if (value is not (2 or 3))
        {
            return ERR_INVALID_VALUE;
        }

        lock (_lock)
        {
            int result = Find(id, out Obj o);
            if (result < 0)
            {
                return result;
            }

            if (o.Kind == Kind.Request)
            {
                return ERR_INVALID_ID;
            }

            o.S.OptionD4 = value;
            return 0;
        }
    }

    /// <summary>NID_54847CFE (SceHttp)(int reqId, uint32 info[4]) / NID_80E6013D (int connId, uint32 info[3]): the
    /// object's internal counters (a request: its finished flag, then three words of its connection; a connection:
    /// three words of its socket). NULL 0x804311FE; the offline objects have no socket: zeros (a default).</summary>
    [Hle("NID_54847CFE", Nid = 0x54847CFE, Library = "SceHttp", Default = true)]
    int HttpRequestInfo(int requestId, uint info)
    {
        if (info == 0)
        {
            return ERR_INVALID_VALUE;
        }

        return ObjectInfo(requestId, info, 16, Kind.Request);
    }

    [Hle("NID_80E6013D", Nid = 0x80E6013D, Library = "SceHttp", Default = true)]
    int HttpConnectionInfo(int connectionId, uint info) => ObjectInfo(connectionId, info, 12, Kind.Connection);

    int ObjectInfo(int id, uint info, uint size, Kind kind)
    {
        lock (_lock)
        {
            int result = Find(id, out _, kind);
            if (result < 0)
            {
                return result;
            }
        }

        if (info != 0)
        {
            GuestMemory.Fill(info, 0, size);
        }

        return 0;
    }

    /// <summary>NID_0B0D8E7A / NID_88116DB8 / NID_B0E41827 / NID_FE60380A (SceHttp)(): libhttp's debug dumps (memory pool,
    /// connections, requests) to the TTY; here to the log.</summary>
    [Hle("NID_0B0D8E7A", Nid = 0x0B0D8E7A, Library = "SceHttp")]
    int HttpDebugDump()
    {
        HttpDebugPoolThroughEntry();
        HttpDebugConnectionsThroughEntry();
        HttpDebugRequestsThroughEntry();
        return 0;
    }

    [Hle("NID_88116DB8", Nid = 0x88116DB8, Library = "SceHttp")]
    int HttpDebugPool()
    {
        Log.Info($"libhttp memory pool: {_poolSize:x8}");
        return 0;
    }

    [Hle("NID_B0E41827", Nid = 0xB0E41827, Library = "SceHttp")]
    int HttpDebugConnections() => DebugCount(Kind.Connection, "connection");

    [Hle("NID_FE60380A", Nid = 0xFE60380A, Library = "SceHttp")]
    int HttpDebugRequests() => DebugCount(Kind.Request, "request");

    int DebugCount(Kind kind, string what)
    {
        int count;
        lock (_lock)
        {
            count = _objs.Values.Count(o => o.Kind == kind);
        }

        Log.Info($"libhttp: num. of active {what} = {count}");
        return 0;
    }

    /// <summary>NID_E0825488 (SceHttp)(alloc, free): starts the loaded certificate store.</summary>
    [Hle("NID_E0825488", Nid = 0xE0825488, Library = "SceHttp")]
    int HttpCertStoreStart(uint alloc, uint free)
    {
        _certStoreStarted = true;
        return 0;
    }

    /// <summary>NID_11C9BCE8 (SceHttp)(): ends the store: the CA list, the client certificate and key are dropped.</summary>
    [Hle("NID_11C9BCE8", Nid = 0x11C9BCE8, Library = "SceHttp")]
    int HttpCertStoreEnd()
    {
        _certStoreStarted = false;
        _loadedCaCerts.Clear();
        _loadedClientCert = _loadedClientKey = null;
        return 0;
    }

    /// <summary>NID_F8885CFD (SceHttp)(const void *cert, uint size): appends a CA certificate to the store's list.</summary>
    [Hle("NID_F8885CFD", Nid = 0xF8885CFD, Library = "SceHttp")]
    int HttpCertStoreAddCa(uint cert, uint size)
    {
        _loadedCaCerts.Add(GuestMemory.Span(cert, (int)size).ToArray());
        return 0;
    }

    /// <summary>NID_0CC59EF6 (SceHttp)(): empties the CA list.</summary>
    [Hle("NID_0CC59EF6", Nid = 0x0CC59EF6, Library = "SceHttp")]
    int HttpCertStoreClearCa()
    {
        _loadedCaCerts.Clear();
        return 0;
    }

    /// <summary>NID_031EBD2A / NID_802E3C30 (SceHttp)(const void *data, uint size): the client certificate / its key
    /// (each call replaces the last); NID_42108FBE (): drops both.</summary>
    [Hle("NID_031EBD2A", Nid = 0x031EBD2A, Library = "SceHttp")]
    int HttpCertStoreSetClientCert(uint data, uint size)
    {
        _loadedClientCert = GuestMemory.Span(data, (int)size).ToArray();
        return 0;
    }

    [Hle("NID_802E3C30", Nid = 0x802E3C30, Library = "SceHttp")]
    int HttpCertStoreSetClientKey(uint data, uint size)
    {
        _loadedClientKey = GuestMemory.Span(data, (int)size).ToArray();
        return 0;
    }

    [Hle("NID_42108FBE", Nid = 0x42108FBE, Library = "SceHttp")]
    int HttpCertStoreClearClient()
    {
        _loadedClientCert = _loadedClientKey = null;
        return 0;
    }

    /// <summary>Tests: what the store holds.</summary>
    internal (int CaCount, bool HasClientCert, bool HasClientKey, bool Started) CertStore =>
        (_loadedCaCerts.Count, _loadedClientCert != null, _loadedClientKey != null, _certStoreStarted);
}

public sealed partial class Net
{
    /// <summary>libnet's debug print callback NID_A64CFBF5 sets (NID_694F8996 prints through it; 0 = none).</summary>
    [SnapshotMissing]
    uint _libnetWord;

    int NotInitialized() => NetErr(ENOTINIT);

    /// <summary>NID_65AFF9BE (SceNetInternal)(const SceNetInitParam *param, ...): libnet's init with the internal flag
    /// 0x2000; the same checks and set-up as sceNetInit.</summary>
    [Hle("NID_65AFF9BE", Nid = 0x65AFF9BE, Library = "SceNetInternal")]
    int NetInternalInit(uint param, uint a, uint b, uint c) => sceNetInitThroughEntry(param);

    /// <summary>NID_E2F6CAEA / NID_873DE778 / NID_0F83B7A9 (SceNetInternal): sceNetSyscallDescriptorCreate (name,
    /// flags) / Close (id) / Ctl (id, op, arg, len), each after sceNetInit (0x804101C8), the kernel's -errno as
    /// 0x80410100 | errno.</summary>
    [Hle("NID_E2F6CAEA", Nid = 0xE2F6CAEA, Library = "SceNetInternal")]
    int NetInternalDescriptorCreate(uint name, int flags) =>
        Initialized ? KernelToNet(sceNetSyscallDescriptorCreate(name == 0 ? "" : GuestMemory.ReadCString(name, 32), flags)) : NotInitialized();

    [Hle("NID_873DE778", Nid = 0x873DE778, Library = "SceNetInternal")]
    int NetInternalDescriptorClose(int id) => Initialized ? KernelToNet(sceNetSyscallDescriptorCloseThroughEntry(id)) : NotInitialized();

    [Hle("NID_0F83B7A9", Nid = 0x0F83B7A9, Library = "SceNetInternal")]
    int NetInternalDescriptorCtl(int id, int op, uint argument, int length) =>
        Initialized ? KernelToNet(sceNetSyscallDescriptorCtlThroughEntry(id, op, argument, length)) : NotInitialized();

    /// <summary>NID_FE8C89AE (SceNetInternal)(name, namelen, old, oldlen, new, newlen): sceNetSyscallSysctl with the six
    /// words in a block.</summary>
    [Hle("NID_FE8C89AE", Nid = 0xFE8C89AE, Library = "SceNetInternal")]
    int NetInternalSysctl(uint name, uint nameLength, uint old, uint oldLength, uint value, uint valueLength)
    {
        if (!Initialized)
        {
            return NotInitialized();
        }

        lock (_netLock)
        {
            uint block = ControlBlock();
            uint[] words = [name, nameLength, old, oldLength, value, valueLength];
            for (int i = 0; i < words.Length; i++)
            {
                GuestMemory.W32(block + (uint)i * 4, words[i]);
            }

            return KernelToNet(sceNetSyscallSysctlThroughEntry(block));
        }
    }

    /// <summary>NID_7FD92AE7 (SceNetInternal)(int ifIndex): the kernel index of a libnet interface (-1 0x80410116, not
    /// found 0x80410159).</summary>
    [Hle("NID_7FD92AE7", Nid = 0x7FD92AE7, Library = "SceNetInternal")]
    int NetInternalKernelIndex(int ifIndex)
    {
        if (!Initialized)
        {
            return NotInitialized();
        }

        int index = LibnetKernelIndex(ifIndex);
        return index < 0 ? NetErr(index) : index;
    }

    /// <summary>NID_F9E3A8B7 / NID_853EEABE (SceNetInternal)(int ifIndex, void *value, int reserved): read / write an
    /// interface's 8-byte value (commands 0x13 / 0x20000013): value non-NULL and reserved 0 (0x80410116), the
    /// interface found (0x80410113).</summary>
    [Hle("NID_F9E3A8B7", Nid = 0xF9E3A8B7, Library = "SceNetInternal")]
    int NetInternalGetInterfaceValue(int ifIndex, uint value, int reserved) => InterfaceValueCommand(ifIndex, value, reserved, 0x13);

    [Hle("NID_853EEABE", Nid = 0x853EEABE, Library = "SceNetInternal")]
    int NetInternalSetInterfaceValue(int ifIndex, uint value, int reserved) => InterfaceValueCommand(ifIndex, value, reserved, 0x20000013);

    int InterfaceValueCommand(int ifIndex, uint value, int reserved, int command)
    {
        if (!Initialized)
        {
            return NotInitialized();
        }

        if (value == 0 || reserved != 0)
        {
            return NetErr(EINVAL);
        }

        int kernelIndex = LibnetKernelIndex(ifIndex);
        if (kernelIndex < 0)
        {
            return NetErr(LIBNET_ENODEV);
        }

        return LibnetControl(kernelIndex, command, value, 8);
    }

    /// <summary>NID_AAA34850 (SceNetInternal)(struct { int id; uint8 mac[6]; ... } *entry, uint flags): fills an entry
    /// of the kernel's neighbour table by id (flags 0..7, else 0x80410116); the emulated kernel's table is empty:
    /// 0x80410102 (a default).</summary>
    [Hle("NID_AAA34850", Nid = 0xAAA34850, Library = "SceNetInternal", Default = true)]
    int NetInternalNeighbourEntry(uint entry, uint flags)
    {
        if (!Initialized)
        {
            return NotInitialized();
        }

        return NetErr((flags & ~7u) != 0 ? EINVAL : ENOENT);
    }

    /// <summary>NID_A64CFBF5 (SceNetInternal)(callback): libnet's debug print callback, set under its lock.</summary>
    [Hle("NID_A64CFBF5", Nid = 0xA64CFBF5, Library = "SceNetInternal")]
    int NetInternalSetWord(uint value)
    {
        if (!Initialized)
        {
            return NotInitialized();
        }

        _libnetWord = value;
        return 0;
    }

    /// <summary>NID_4054D2D8 (SceNetInternal)(struct { int id; uint bits; } *sync, uint init, const char *name) /
    /// NID_97527BDB (sync): libnet's event flag pair (sceKernelCreateEventFlag name, attr 0x1000, the initial bits;
    /// sceKernelDeleteEventFlag), after sceNetInit.</summary>
    [Hle("NID_4054D2D8", Nid = 0x4054D2D8, Library = "SceNetInternal")]
    int NetInternalSyncCreate(uint sync, uint init, uint name)
    {
        if (!Initialized)
        {
            return NotInitialized();
        }

        ThreadMgr threads = Emu.Hle.Modules.OfType<ThreadMgr>().First();
        int id = threads.sceKernelCreateEventFlag(name == 0 ? "SceNetSync" : GuestMemory.ReadCString(name, 32), 0x1000, init, 0);
        if (id < 0)
        {
            return id;
        }

        GuestMemory.W32(sync, (uint)id);
        GuestMemory.W32(sync + 4, init);
        return 0;
    }

    [Hle("NID_97527BDB", Nid = 0x97527BDB, Library = "SceNetInternal")]
    int NetInternalSyncDelete(uint sync)
    {
        if (!Initialized)
        {
            return NotInitialized();
        }

        Emu.Hle.Modules.OfType<ThreadMgr>().First().sceKernelDeleteEventFlagThroughEntry((int)GuestMemory.R32(sync));
        return 0;
    }
}
