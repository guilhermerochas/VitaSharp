using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// SceVoiceQoS: voice-chat session layer over SceVoice (local/remote endpoints, connections, packets). There is no
/// header nor Vita3K implementation: prototypes follow the PS4 libSceVoiceQoS (same Sony API). VitaSharp has no real
/// online peers, so this is an offline session where nobody talks: ReadPacket yields nothing to send, WritePacket
/// consumes and drops, attributes are stored and read back, status reads as zeros. Error codes are unknown for the
/// Vita: <see cref="SceError.ERROR"/> is used provisionally for "not initialized"/"bad id" and INVALID_ARGUMENT for
/// null pointers.
/// </summary>
public sealed class VoiceQoS : HleModule
{
    internal const int ERR_GENERIC = SceError.ERROR, ERR_INVALID_ARGUMENT = SceError.INVALID_ARGUMENT;

    enum Kind { Local, Connection }

    readonly object _lock = new();
    bool _init;
    int _nextId = 1;
    readonly HashSet<int> _locals = new(), _remotes = new();
    readonly Dictionary<int, (int Local, int Remote)> _conns = new();
    readonly Dictionary<(Kind, int Id, uint Attr), byte[]> _attrs = new();

    int Fail(string what)
    {
        Log.WarnOnce("voiceqos-" + what, $"sceVoiceQoS: {what} (provisional error code 0x{ERR_GENERIC:X8})");
        return ERR_GENERIC;
    }

    /// <summary>Inferred (PS4): (void *pMemBlock, SceUInt32 memSize, SceInt32 appType). The block is not used.</summary>
    [Hle]
    int sceVoiceQoSInit(uint memBlock, uint memSize, uint appType)
    {
        Log.Debug($"sceVoiceQoSInit(mem=0x{memBlock:X8}, size=0x{memSize:X}, appType=0x{appType:X})");
        lock (_lock)
        {
            if (_init) return Fail("already initialized");
            _init = true;
            return 0;
        }
    }

    [Hle]
    int sceVoiceQoSEnd()
    {
        lock (_lock)
        {
            if (!_init) return Fail("not initialized");
            _init = false;
            _locals.Clear(); _remotes.Clear(); _conns.Clear(); _attrs.Clear();
            return 0;
        }
    }

    int CreateEndpoint(uint pId, HashSet<int> set)
    {
        lock (_lock)
        {
            if (!_init) return Fail("not initialized");
            if (pId == 0) return ERR_INVALID_ARGUMENT;
            int id = _nextId++;
            set.Add(id);
            GuestMemory.W32(pId, (uint)id);
            return 0;
        }
    }

    [Hle] int sceVoiceQoSCreateLocalEndpoint(uint pLocalId) => CreateEndpoint(pLocalId, _locals);
    [Hle] int sceVoiceQoSCreateRemoteEndpoint(uint pRemoteId) => CreateEndpoint(pRemoteId, _remotes);

    /// <summary>Removes the endpoint and the connections that use it (whether the hardware refuses instead: to confirm).</summary>
    int DeleteEndpoint(int id, HashSet<int> set, bool local)
    {
        lock (_lock)
        {
            if (!_init) return Fail("not initialized");
            if (!set.Remove(id)) return Fail("unknown endpoint");
            foreach (var c in _conns.Where(c => (local ? c.Value.Local : c.Value.Remote) == id).Select(c => c.Key).ToList())
                RemoveConnection(c);
            if (local) RemoveAttrs(Kind.Local, id);
            return 0;
        }
    }

    [Hle] int sceVoiceQoSDeleteLocalEndpoint(int localId) => DeleteEndpoint(localId, _locals, true);
    [Hle] int sceVoiceQoSDeleteRemoteEndpoint(int remoteId) => DeleteEndpoint(remoteId, _remotes, false);

    void RemoveConnection(int id)
    {
        _conns.Remove(id);
        RemoveAttrs(Kind.Connection, id);
    }

    void RemoveAttrs(Kind kind, int id)
    {
        foreach (var k in _attrs.Keys.Where(k => k.Item1 == kind && k.Id == id).ToList()) _attrs.Remove(k);
    }

    [Hle]
    int sceVoiceQoSConnect(uint pConnectionId, int localId, int remoteId)
    {
        lock (_lock)
        {
            if (!_init) return Fail("not initialized");
            if (pConnectionId == 0) return ERR_INVALID_ARGUMENT;
            if (!_locals.Contains(localId) || !_remotes.Contains(remoteId)) return Fail("unknown endpoint");
            if (_conns.ContainsValue((localId, remoteId))) return Fail("duplicate connection");
            int id = _nextId++;
            _conns[id] = (localId, remoteId);
            GuestMemory.W32(pConnectionId, (uint)id);
            return 0;
        }
    }

    [Hle]
    int sceVoiceQoSDisconnect(int connectionId)
    {
        lock (_lock)
        {
            if (!_init) return Fail("not initialized");
            if (!_conns.ContainsKey(connectionId)) return Fail("unknown connection");
            RemoveConnection(connectionId);
            return 0;
        }
    }

    [Hle]
    int sceVoiceQoSGetLocalEndpoint(int connectionId, uint pLocalId) => GetEndpoint(connectionId, pLocalId, true);
    [Hle]
    int sceVoiceQoSGetRemoteEndpoint(int connectionId, uint pRemoteId) => GetEndpoint(connectionId, pRemoteId, false);

    int GetEndpoint(int connectionId, uint pOut, bool local)
    {
        lock (_lock)
        {
            if (!_init) return Fail("not initialized");
            if (pOut == 0) return ERR_INVALID_ARGUMENT;
            if (!_conns.TryGetValue(connectionId, out var c)) return Fail("unknown connection");
            GuestMemory.W32(pOut, (uint)(local ? c.Local : c.Remote));
            return 0;
        }
    }

    /// <summary>Nothing to send: no local voice reaches the network in the offline session.</summary>
    [Hle]
    int sceVoiceQoSReadPacket(int connectionId, uint pData, uint pSize)
    {
        lock (_lock)
        {
            if (!_init) return Fail("not initialized");
            if (pData == 0 || pSize == 0) return ERR_INVALID_ARGUMENT;
            if (!_conns.ContainsKey(connectionId)) return Fail("unknown connection");
            GuestMemory.W32(pSize, 0);
            return 0;
        }
    }

    /// <summary>Received packets are consumed whole (*pSize unchanged) and dropped.</summary>
    [Hle]
    int sceVoiceQoSWritePacket(int connectionId, uint pData, uint pSize)
    {
        lock (_lock)
        {
            if (!_init) return Fail("not initialized");
            if (pData == 0 || pSize == 0) return ERR_INVALID_ARGUMENT;
            if (!_conns.ContainsKey(connectionId)) return Fail("unknown connection");
            return 0;
        }
    }

    int SetAttr(Kind kind, int id, uint attr, uint value, int size)
    {
        lock (_lock)
        {
            if (!_init) return Fail("not initialized");
            if (value == 0 || size is <= 0 or > 0x10000) return ERR_INVALID_ARGUMENT;
            if (!(kind == Kind.Local ? _locals.Contains(id) : _conns.ContainsKey(id))) return Fail("unknown id");
            _attrs[(kind, id, attr)] = GuestMemory.Span(value, size).ToArray();
            return 0;
        }
    }

    int GetAttr(Kind kind, int id, uint attr, uint value, int size)
    {
        lock (_lock)
        {
            if (!_init) return Fail("not initialized");
            if (value == 0 || size is <= 0 or > 0x10000) return ERR_INVALID_ARGUMENT;
            if (!(kind == Kind.Local ? _locals.Contains(id) : _conns.ContainsKey(id))) return Fail("unknown id");
            var dst = GuestMemory.Span(value, size);
            dst.Clear();
            if (_attrs.TryGetValue((kind, id, attr), out var b)) b.AsSpan(0, Math.Min(b.Length, size)).CopyTo(dst);
            return 0;
        }
    }

    [Hle] int sceVoiceQoSSetLocalEndpointAttribute(int localId, uint attributeId, uint pValue, int size) => SetAttr(Kind.Local, localId, attributeId, pValue, size);
    [Hle] int sceVoiceQoSGetLocalEndpointAttribute(int localId, uint attributeId, uint pValue, int size) => GetAttr(Kind.Local, localId, attributeId, pValue, size);
    [Hle] int sceVoiceQoSSetConnectionAttribute(int connectionId, uint attributeId, uint pValue, int size) => SetAttr(Kind.Connection, connectionId, attributeId, pValue, size);
    [Hle] int sceVoiceQoSGetConnectionAttribute(int connectionId, uint attributeId, uint pValue, int size) => GetAttr(Kind.Connection, connectionId, attributeId, pValue, size);

    /// <summary>No traffic and no voice activity: every status reads as zeros.</summary>
    [Hle]
    int sceVoiceQoSGetStatus(int connectionId, uint statusId, uint pValue, int size)
    {
        lock (_lock)
        {
            if (!_init) return Fail("not initialized");
            if (pValue == 0 || size is <= 0 or > 0x10000) return ERR_INVALID_ARGUMENT;
            if (!_conns.ContainsKey(connectionId)) return Fail("unknown connection");
            GuestMemory.Span(pValue, size).Clear();
            return 0;
        }
    }
}
