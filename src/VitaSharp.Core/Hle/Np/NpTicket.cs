using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Np;

/// <summary>
/// A PSN auth ticket issued locally (sceNpAuthCreateStartRequest / sceNpAuthGetTicket), in the PS3 ticket 2.1 format
/// (RPCS3 np_handler.cpp, what RPCN issues): big-endian header u32 version 0x21010000, u32 size of the rest, then two
/// blobs of typed nodes (u16 type, u16 length, data; blob types 0x30xx):
/// <code>
/// 0x3000 body: [8] serial id (20)  [1] issuer id  [7] issued date  [7] expire date  [2] account id
///              [4] online id (32)  [8] region (4)  [4] domain (4)  [8] service id (24)  [1] status
///              [0] status duration  [0] date of birth  0x3010 entitlements { 0x3011 { [4] id (32) [7] created
///              [7] expire [1] type [1] remaining count [1] consumed count } ... }
/// 0x3002 footer: [8] cipher id (4)  [8] signature (56, zero: nothing can verify it offline)
/// </code>
/// Dates are SceNpTime (milliseconds since the Unix epoch). The game only reads it through sceNpAuthGetTicketParam
/// and sceNpAuthGetEntitlement*, which parse this same layout.
/// </summary>
public sealed class NpTicket
{
    public const uint Version = 0x21010000;
    const ushort TypeEmpty = 0, TypeU32 = 1, TypeU64 = 2, TypeString = 4, TypeTime = 7, TypeBinary = 8;
    const ushort BlobBody = 0x3000, BlobFooter = 0x3002, BlobEntitlements = 0x3010, BlobEntitlement = 0x3011;

    /// <summary>An owned add-on (SceNpEntitlement, 64 bytes: id[32] +0, created +32, expireDate +40, type +48,
    /// remainingCount +52, consumedCount +56).</summary>
    public sealed record Entitlement(string Id, string ContentId, ulong Created, ulong Expire, uint Type, int Remaining, uint Consumed)
    {
        /// <summary>SCE_NP_ENTITLEMENT_TYPE_NON_CONSUMABLE (PS3 value).</summary>
        public const uint NonConsumable = 0;

        /// <summary>From an installed add-on's CONTENT_ID ("UP0001-PCSE00914_00-LABEL"): the id drops the service prefix.</summary>
        public static Entitlement FromContent(string? contentId, string titleId, string folder)
        {
            string cid = string.IsNullOrEmpty(contentId) ? $"XX0000-{titleId}_00-{folder}" : contentId;
            int dash = cid.IndexOf('-');
            string id = dash >= 0 && dash + 1 < cid.Length ? cid[(dash + 1)..] : cid;
            if (id.Length > 32) id = id[..32];
            return new Entitlement(id, cid, 0, 0, NonConsumable, -1, 0);
        }

        public string Label => Id.Contains('-') ? Id[(Id.LastIndexOf('-') + 1)..] : Id;

        public bool Matches(string id) => id == Id || id == ContentId || id == Label;

        public void Write(uint p)
        {
            GuestMemory.Fill(p, 0, 64);
            WriteId(p, Id);
            GuestMemory.Write(p + 32, Created);
            GuestMemory.Write(p + 40, Expire);
            GuestMemory.W32(p + 48, Type);
            GuestMemory.W32(p + 52, (uint)Remaining);
            GuestMemory.W32(p + 56, Consumed);
        }
    }

    public byte[] Serial { get; private set; } = [];
    public uint IssuerId { get; private set; }
    public ulong IssuedDate { get; private set; }
    public ulong ExpireDate { get; private set; }
    public ulong AccountId { get; private set; }
    public string OnlineId { get; private set; } = "";
    public byte[] Region { get; private set; } = new byte[4];
    public byte[] Domain { get; private set; } = new byte[4];
    public byte[] ServiceId { get; private set; } = new byte[24];
    public uint Status { get; private set; }
    public List<Entitlement> Entitlements { get; } = new();

    public static void WriteId(uint p, string id)
    {
        GuestMemory.Fill(p, 0, 32);
        var b = Encoding.ASCII.GetBytes(id);
        b.AsSpan(0, Math.Min(b.Length, 32)).CopyTo(GuestMemory.Span(p, 32));
    }

    /// <summary>The 64-bit account id of an online id (stable across runs).</summary>
    public static ulong AccountIdOf(string onlineId) =>
        BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("VitaSharp:" + onlineId))) & 0x7FFF_FFFF_FFFF_FFFF;

    static byte[] Fixed(string s, int len)
    {
        var b = new byte[len];
        var src = Encoding.ASCII.GetBytes(s);
        src.AsSpan(0, Math.Min(src.Length, len)).CopyTo(b);
        return b;
    }

    sealed class Writer
    {
        readonly List<byte> _b = new();
        public byte[] ToArray() => _b.ToArray();
        void Header(ushort type, int len)
        {
            _b.Add((byte)(type >> 8)); _b.Add((byte)type);
            _b.Add((byte)(len >> 8)); _b.Add((byte)len);
        }
        public Writer Bytes(ushort type, byte[] data) { Header(type, data.Length); _b.AddRange(data); return this; }
        public Writer U32(uint v) { var d = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(d, v); return Bytes(TypeU32, d); }
        public Writer U64(ushort type, ulong v) { var d = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(d, v); return Bytes(type, d); }
        public Writer Empty() { Header(TypeEmpty, 0); return this; }
        public Writer Blob(ushort type, Writer inner) => Bytes(type, inner.ToArray());
    }

    /// <summary>A ticket for <paramref name="onlineId"/> and <paramref name="serviceId"/>, valid for a day, owning
    /// <paramref name="entitlements"/>.</summary>
    public static byte[] Build(string onlineId, string serviceId, IReadOnlyList<Entitlement> entitlements, DateTime? now = null)
    {
        ulong issued = (ulong)new DateTimeOffset(now ?? DateTime.UtcNow).ToUnixTimeMilliseconds();
        var serial = SHA256.HashData(Encoding.UTF8.GetBytes($"{onlineId}|{serviceId}|{issued}"))[..20];
        var ents = new Writer();
        foreach (var e in entitlements)
            ents.Blob(BlobEntitlement, new Writer()
                .Bytes(TypeString, Fixed(e.Id, 32))
                .U64(TypeTime, e.Created != 0 ? e.Created : issued)
                .U64(TypeTime, e.Expire)
                .U32(e.Type)
                .U32((uint)e.Remaining)
                .U32(e.Consumed));
        var body = new Writer()
            .Bytes(TypeBinary, serial)
            .U32(0x100) // issuer: the local "server"
            .U64(TypeTime, issued)
            .U64(TypeTime, issued + 24UL * 3600 * 1000)
            .U64(TypeU64, AccountIdOf(onlineId))
            .Bytes(TypeString, Fixed(onlineId, 32))
            .Bytes(TypeBinary, Fixed("us", 4))
            .Bytes(TypeString, Fixed("un", 4))
            .Bytes(TypeBinary, Fixed(serviceId, 24))
            .U32(0)
            .Empty()
            .Empty()
            .Blob(BlobEntitlements, ents);
        var footer = new Writer().Bytes(TypeBinary, Fixed("VSHP", 4)).Bytes(TypeBinary, new byte[56]);
        var rest = new Writer().Blob(BlobBody, body).Blob(BlobFooter, footer).ToArray();
        var ticket = new byte[8 + rest.Length];
        BinaryPrimitives.WriteUInt32BigEndian(ticket, Version);
        BinaryPrimitives.WriteUInt32BigEndian(ticket.AsSpan(4), (uint)rest.Length);
        rest.CopyTo(ticket, 8);
        return ticket;
    }

    readonly record struct Node(ushort Type, int Offset, int Length);

    static List<Node>? Nodes(ReadOnlySpan<byte> d, int start, int end)
    {
        var list = new List<Node>();
        int i = start;
        while (i < end)
        {
            if (i + 4 > end) return null;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(d[i..]);
            int len = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 2)..]);
            if (i + 4 + len > end) return null;
            list.Add(new Node(type, i + 4, len));
            i += 4 + len;
        }
        return list;
    }

    /// <summary>Parses a ticket in this format; null when it is not one.</summary>
    public static NpTicket? Parse(byte[] d)
    {
        if (d.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(d) != Version) return null;
        int size = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4));
        if (size + 8 > d.Length) return null;
        var top = Nodes(d, 8, 8 + size);
        if (top == null || top.Count < 1 || top[0].Type != BlobBody) return null;
        var body = Nodes(d, top[0].Offset, top[0].Offset + top[0].Length);
        if (body == null || body.Count < 10) return null;
        var t = new NpTicket();
        byte[] Data(Node n) => d.AsSpan(n.Offset, n.Length).ToArray();
        uint U32(Node n) => n.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(n.Offset)) : 0;
        ulong U64(Node n) => n.Length == 8 ? BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(n.Offset)) : 0;
        t.Serial = Data(body[0]);
        t.IssuerId = U32(body[1]);
        t.IssuedDate = U64(body[2]);
        t.ExpireDate = U64(body[3]);
        t.AccountId = U64(body[4]);
        t.OnlineId = Encoding.ASCII.GetString(Data(body[5])).TrimEnd('\0');
        t.Region = Data(body[6]);
        t.Domain = Data(body[7]);
        t.ServiceId = Data(body[8]);
        t.Status = U32(body[9]);
        foreach (var blob in body.Where(n => n.Type == BlobEntitlements))
            foreach (var e in Nodes(d, blob.Offset, blob.Offset + blob.Length) ?? [])
            {
                if (e.Type != BlobEntitlement) continue;
                var f = Nodes(d, e.Offset, e.Offset + e.Length);
                if (f == null || f.Count < 6) continue;
                string id = Encoding.ASCII.GetString(Data(f[0])).TrimEnd('\0');
                t.Entitlements.Add(new Entitlement(id, id, U64(f[1]), U64(f[2]), U32(f[3]), (int)U32(f[4]), U32(f[5])));
            }
        return t;
    }

    /// <summary>Writes SCE_NP_TICKET_PARAM_* <paramref name="paramId"/> into a SceNpTicketParam union (false: unknown id).</summary>
    public bool WriteParam(int paramId, uint p)
    {
        void Bytes(byte[] b) => b.CopyTo(GuestMemory.Span(p, b.Length));
        switch (paramId)
        {
            case 0: Bytes(Serial); break;                           // SERIAL_ID (20)
            case 1: GuestMemory.W32(p, IssuerId); break;            // ISSUER_ID
            case 2: GuestMemory.Write(p, IssuedDate); break;        // ISSUED_DATE
            case 3: GuestMemory.Write(p, ExpireDate); break;        // EXPIRE_DATE
            case 4: GuestMemory.Write(p, AccountId); break;         // SUBJECT_ACCOUNT_ID
            case 5: Bytes(Fixed(OnlineId, 32)); break;              // SUBJECT_ONLINE_ID
            case 6: Bytes(Region); break;                           // SUBJECT_REGION
            case 7: Bytes(Domain); break;                           // SUBJECT_DOMAIN
            case 8: Bytes(ServiceId); break;                        // SERVICE_ID
            case 9: GuestMemory.W32(p, Status); break;              // SUBJECT_STATUS
            case 10: case 11: GuestMemory.Write(p, 0UL); break;     // STATUS_DURATION, SUBJECT_DOB
            default: return false;
        }
        return true;
    }
}
