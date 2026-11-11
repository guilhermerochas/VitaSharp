using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The system's certificate authorities as libssl (vs0:sys/external/libssl.suprx, FW 3.74) hands them out: the PEM blocks
/// of vs0:data/external/cert/CA_LIST.cer (69 roots in 3.74, each "-----BEGIN CERTIFICATE-----" through the CRLF after
/// "-----END CERTIFICATE-----", the "#name" comment lines left out). The WebKit process (ScePsp2Compat) asks for all of
/// them once, size first, and concatenates the texts into the root store its TLS verification uses: without them every
/// HTTPS page asks "the system did not receive a root CA certificate" and its sub-resources (images) never load.
/// </summary>
public sealed partial class Ssl
{
    const int ERR_NO_MEMORY = unchecked((int)0x8001000C), ERR_NO_ENTRY = unchecked((int)0x80010002);
    const string CaListPath = "vs0:data/external/cert/CA_LIST.cer";

    byte[][]? _caCerts;

    /// <summary>The PEM blocks of CA_LIST.cer in file order (empty when the firmware is not installed).</summary>
    internal static byte[][] ParseCaList(ReadOnlySpan<byte> file)
    {
        var begin = "-----BEGIN CERTIFICATE-----"u8;
        var end = "-----END CERTIFICATE-----"u8;
        var certs = new List<byte[]>();
        int pos = 0;
        while (pos < file.Length)
        {
            int b = file[pos..].IndexOf(begin);
            if (b < 0) break;
            b += pos;
            int e = file[b..].IndexOf(end);
            if (e < 0) break;
            e += b + end.Length;
            if (e < file.Length && file[e] == '\r') e++;
            if (e < file.Length && file[e] == '\n') e++;
            certs.Add(file[b..e].ToArray());
            pos = e;
        }
        return certs.ToArray();
    }

    byte[][]? CaCerts()
    {
        if (_caCerts != null) return _caCerts;
        string? host = Emu?.Vfs.Resolve(CaListPath);
        if (host == null || !File.Exists(host)) return null;
        return _caCerts = ParseCaList(File.ReadAllBytes(host));
    }

    /// <summary>Bytes one certificate takes in the caller's buffer: its text and a NUL, padded to 4, then the 8-byte node.</summary>
    static uint EntrySize(byte[] cert) => (((uint)cert.Length + 1 + 3) & ~3u) + 8;

    /// <summary>
    /// int sceSslInternalGetCertificateAuthority(int category, uint mask, SceSslCaNode **list, void *buf, uint size,
    /// uint *usedSize). Category 0 with mask 0xFFFFFFFF is every root (the only call the system makes); libssl's per-category
    /// bit table is not reproduced, every request gets the whole list. Without <paramref name="list"/> or
    /// <paramref name="buf"/> only *usedSize is written (the size the data needs). Otherwise each certificate's text is
    /// copied into <paramref name="buf"/>, NUL-terminated, followed (4-aligned) by its node {const char *pem; node *next}
    /// appended to *list (after the nodes already there); ENOMEM when <paramref name="size"/> is too small. Returns the number of certificates.
    /// </summary>
    [Hle]
    int sceSslInternalGetCertificateAuthority(int category, uint mask, uint list, uint buf, uint size, uint usedSize)
    {
        var certs = CaCerts();
        if (certs == null) return ERR_NO_ENTRY;
        bool fill = list != 0 && buf != 0;
        uint total = 0;
        uint tail = 0;
        if (fill)
        {
            // appended to the list the caller passes in, as libssl does
            for (uint n = GuestMemory.R32(list); n != 0; n = GuestMemory.R32(n + 4)) tail = n;
        }
        foreach (var cert in certs)
        {
            uint need = EntrySize(cert);
            if (fill)
            {
                if (size - total < need) return ERR_NO_MEMORY;
                uint text = buf + total;
                cert.CopyTo(GuestMemory.Span(text, cert.Length));
                GuestMemory.Write<byte>(text + (uint)cert.Length, 0);
                uint node = text + need - 8;
                GuestMemory.W32(node, text);
                GuestMemory.W32(node + 4, 0);
                if (tail == 0) GuestMemory.W32(list, node);
                else GuestMemory.W32(tail + 4, node);
                tail = node;
            }
            total += need;
        }
        if (usedSize != 0) GuestMemory.W32(usedSize, total);
        Log.Debug($"sceSslInternalGetCertificateAuthority({category}, 0x{mask:X}): {certs.Length} roots, {total} bytes{(fill ? "" : " (size)")}");
        return certs.Length;
    }
}
