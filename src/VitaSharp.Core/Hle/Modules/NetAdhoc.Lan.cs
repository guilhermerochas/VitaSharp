using System.Buffers.Binary;
using System.Text;
using VitaSharp.Core.Host;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// ScePspnetAdhoc on the ad hoc medium (<see cref="HostServices.Adhoc"/>, other emulator instances). FW 3.74's
/// sceNetAdhocInit opens a raw link-layer socket of EtherType 0x88C8 ("SceNetPspAdhoc", SceNetPs type 9) and runs PDP
/// and PTP over it in the library; a group is the IBSS whose SSID NID_F87F9518 makes (prefix, id type, ad hoc id,
/// "_L_", group name), which the network check dialog's PSP ad hoc modes create or join. Here a PDP datagram is one
/// frame of that EtherType: destination port, source port (big-endian), data. The PSP's own header on the air is not
/// reproduced (to confirm against a capture; it matters only to talk with a real PSP or Vita).
/// </summary>
public sealed partial class NetAdhoc
{
    /// <summary>The PSP ad hoc EtherType (pspnet_adhoc's raw socket).</summary>
    internal const ushort PspAdhocEtherType = 0x88C8;

    /// <summary>The ports before a PDP datagram's data in its frame.</summary>
    const int PdpHeaderSize = 4;

    /// <summary>A datagram received on a PDP socket, waiting for sceNetAdhocPdpRecv.</summary>
    sealed record PdpDatagram(byte[] Source, ushort SourcePort, byte[] Data);

    [SnapshotIgnore] // follows the host's medium
    AdhocMedium? _listening;

    /// <summary>The host's ad hoc medium, null when ad hoc is off.</summary>
    AdhocMedium? Medium => Emu?.Host?.Adhoc; // a test emulator shell may have no host services

    /// <summary>The unit's MAC on the ad hoc network: the medium's (one per instance), else the Wi-Fi's.</summary>
    internal byte[] UnitMac => Medium?.Mac ?? VitaSharp.Core.Hle.Modules.Net.MacAddress.ToArray();

    /// <summary>Subscribes to the frames of the host's medium (once per medium).</summary>
    void ListenToMedium()
    {
        AdhocMedium? medium = Medium;
        if (medium == null || ReferenceEquals(medium, _listening))
        {
            return;
        }

        _listening?.FrameReceived -= OnFrame;

        medium.FrameReceived += OnFrame;
        _listening = medium;
    }

    /// <summary>A frame of the joined network: a PDP datagram goes to the socket bound to its port, when its buffer
    /// has room (a datagram that does not fit is dropped, as a full socket buffer drops it).</summary>
    void OnFrame(AdhocFrame frame)
    {
        if (frame.EtherType != PspAdhocEtherType || frame.Payload.Length < PdpHeaderSize)
        {
            return;
        }

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload);
        ushort sourcePort = BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(2));
        byte[] data = frame.Payload.AsSpan(PdpHeaderSize).ToArray();
        lock (_lock)
        {
            PdpSocket? socket = _pdp.Values.FirstOrDefault(s => s.Port == port && !s.Deleted);
            if (socket == null || socket.QueuedBytes + data.Length > socket.BufferSize)
            {
                return;
            }

            socket.Inbox.Enqueue(new PdpDatagram(frame.Source, sourcePort, data));
            socket.QueuedBytes += data.Length;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Sends a PDP datagram on the joined network; false when there is no medium or no network.</summary>
    bool SendPdp(ReadOnlySpan<byte> destination, ushort port, ushort sourcePort, ReadOnlySpan<byte> data)
    {
        AdhocMedium? medium = Medium;
        if (medium == null)
        {
            return false;
        }

        byte[] payload = new byte[PdpHeaderSize + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, port);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2), sourcePort);
        data.CopyTo(payload.AsSpan(PdpHeaderSize));
        return medium.Send(destination, PspAdhocEtherType, payload);
    }

    /// <summary>The SSID of the group <paramref name="groupName"/> of the current ad hoc id (NID_F87F9518's), as the
    /// medium names networks (one character per byte); null before sceNetAdhocctlInit or for an invalid id type.</summary>
    internal string? GroupSsid(ReadOnlySpan<byte> groupName)
    {
        char letter;
        byte[] id = new byte[9];
        lock (_lock)
        {
            if (!_ctlInited)
            {
                return null;
            }

            switch (BinaryPrimitives.ReadUInt32LittleEndian(_adhocId))
            {
                case 0:
                    letter = 'A';
                    break;
                case 1:
                    letter = 'X';
                    break;
                case 2:
                    letter = 'S';
                    break;
                default:
                    return null;
            }

            _adhocId.AsSpan(4, 9).CopyTo(id);
        }

        string prefix = Emu.Hle.Modules.OfType<RegMgr>().First().Value(0x00134C03, out _) as string ?? "PSP";
        var ssid = new StringBuilder();
        for (int i = 0; i < 3; i++)
        {
            ssid.Append(i < prefix.Length ? prefix[i] : '\0');
        }

        ssid.Append('_').Append(letter);
        foreach (byte b in id)
        {
            ssid.Append((char)b);
        }

        ssid.Append("_L_");
        for (int i = 0; i < Math.Min(groupName.Length, SsidNameMax) && groupName[i] != 0; i++)
        {
            ssid.Append((char)groupName[i]);
        }

        return ssid.ToString();
    }

    /// <summary>The group name of the joined network (after "_L_" in its SSID), empty when none.</summary>
    static string GroupNameOf(string? ssid)
    {
        if (ssid == null)
        {
            return "";
        }

        int at = ssid.IndexOf("_L_", StringComparison.Ordinal);
        return at < 0 ? "" : ssid[(at + 3)..];
    }

    /// <summary>
    /// The network check dialog's PSP ad hoc modes (FW 3.74 libcdlg_netcheck): CREATE makes the group's IBSS, JOIN
    /// joins it once its beacon is heard, CONN joins it when heard and creates it otherwise. Runs on the dialog's own
    /// thread; returns the dialog result (OK, or user canceled when JOIN finds no such group before the timeout or ad
    /// hoc is off).
    /// </summary>
    internal int ConnectGroup(int mode, ReadOnlySpan<byte> groupName, long timeoutUs, Func<bool> aborted)
    {
        AdhocMedium? medium = Medium;
        string? ssid = GroupSsid(groupName);
        if (medium == null || ssid == null)
        {
            return CommonDialogState.ResultUserCanceled;
        }

        ListenToMedium();
        if (mode == CommonDialog.NetCheckModePspAdhocCreate)
        {
            medium.Create(ssid);
            Log.Info($"ad hoc: created the group '{GroupNameOf(ssid)}' (SSID {Printable(ssid)})");
            return CommonDialogState.ResultOk;
        }

        // listen for the group's beacons: JOIN waits up to the timeout, CONN one scan before creating it
        long waitUs = mode == CommonDialog.NetCheckModePspAdhocJoin ? timeoutUs : ScanUs;
        long deadline = Environment.TickCount64 + Math.Max(waitUs / 1000, AdhocMedium.BeaconIntervalMs * 3);
        while (!aborted())
        {
            if (medium.Join(ssid))
            {
                Log.Info($"ad hoc: joined the group '{GroupNameOf(ssid)}'");
                return CommonDialogState.ResultOk;
            }

            if (Environment.TickCount64 >= deadline)
            {
                break;
            }

            Thread.Sleep(AdhocMedium.BeaconIntervalMs / 2);
        }

        if (mode == CommonDialog.NetCheckModePspAdhocConn && !aborted())
        {
            medium.Create(ssid);
            Log.Info($"ad hoc: no group '{GroupNameOf(ssid)}' around, created it");
            return CommonDialogState.ResultOk;
        }

        Log.Info($"ad hoc: no group '{GroupNameOf(ssid)}' found");
        return CommonDialogState.ResultUserCanceled;
    }

    /// <summary>How long a scan listens for beacons (a little over three beacon intervals).</summary>
    const long ScanUs = 500_000;

    /// <summary>The joining dialog's wait when the game gives no timeout.</summary>
    internal const long DefaultJoinTimeoutUs = 30_000_000;

    static string Printable(string ssid) => new(ssid.Select(c => c is >= ' ' and <= '~' ? c : '.').ToArray());

    /// <summary>Leaves the joined network (sceNetAdhocctlTerm, sceNetCtlAdhocDisconnect).</summary>
    internal void LeaveGroup()
    {
        Medium?.Leave();
    }

    /// <summary>The size of a SceNetAdhocctlPeerInfo.</summary>
    const int PeerInfoSize = 0x98;

    /// <summary>Writes <paramref name="peers"/> as SceNetAdhocctlPeerInfo records (0x98: next, nickname[128], MAC,
    /// padding, last receive time) into <paramref name="buffer"/> up to <paramref name="size"/> bytes; returns the bytes
    /// they take (all of them, nothing written, when <paramref name="buffer"/> is 0).</summary>
    static int WritePeers(List<AdhocPeer> peers, uint buffer, int size)
    {
        if (buffer == 0)
        {
            return peers.Count * PeerInfoSize;
        }

        int count = Math.Min(peers.Count, Math.Max(size, 0) / PeerInfoSize);
        ulong now = (ulong)Environment.TickCount64 * 1000;
        for (int i = 0; i < count; i++)
        {
            uint record = buffer + (uint)(i * PeerInfoSize);
            GuestMemory.Fill(record, 0, PeerInfoSize);
            GuestMemory.W32(record, i + 1 < count ? record + PeerInfoSize : 0);
            WriteNickname(record + 4, peers[i].Nickname);
            peers[i].Mac.CopyTo(GuestMemory.Span(record + 0x84, 6));
            GuestMemory.Write(record + 0x90, now);
        }

        return count * PeerInfoSize;
    }

    /// <summary>A SceNetAdhocctlNickname (128 bytes, NUL-terminated UTF-8).</summary>
    static void WriteNickname(uint address, string nickname)
    {
        GuestMemory.Fill(address, 0, 128);
        byte[] bytes = Encoding.UTF8.GetBytes(nickname);
        bytes.AsSpan(0, Math.Min(bytes.Length, 127)).CopyTo(GuestMemory.Span(address, 127));
    }

    /// <summary>The peer of the joined network with MAC <paramref name="address"/>.</summary>
    AdhocPeer? PeerAt(uint address)
    {
        ReadOnlySpan<byte> mac = GuestMemory.ReadSpan(address, 6);
        foreach (AdhocPeer peer in Medium?.Peers() ?? [])
        {
            if (peer.Mac.AsSpan().SequenceEqual(mac))
            {
                return peer;
            }
        }

        return null;
    }
}
