using System.Security.Cryptography;
using System.Text;
using VitaSharp.Core.Package.Pfs;

namespace VitaSharp.Tests;

/// <summary>
/// Writes a folder as a read-only PFS image (the "unicv" kind retail applications have), encrypted with a test key:
/// sce_pfs/files.db lists the files (the plain ones with the NENC attribute), sce_pfs/unicv.db has one table per file
/// (its sectors and the HMAC-SHA1 of each sector of the stored file), and every encrypted file is stored as the
/// console stores it (AES-CBC per sector with the table's tweak). The keys come from the same derivation the
/// decryption uses (<see cref="PfsDecryptor.Plan"/>), so the image is opened only with the key it was made with.
/// </summary>
internal static class PfsImageBuilder
{
    private const int PageSize = 0x400;
    private const uint SectorSize = 0x8000;
    private const int EntriesPerPage = 9, EntrySize = 72, InfoSize = 16;
    private const ushort TypeFile = 0x0001, AttrNenc = 0x4000, AttrDir = 0x8000;
    private const uint FilesSalt = 0x5A17;

    /// <summary>A file of the image: its path in the folder ('/'-separated, at most one folder deep), its contents and
    /// whether the image encrypts it.</summary>
    internal readonly record struct Entry(string RelPath, byte[] Data, bool Encrypted);

    /// <summary>Writes <paramref name="entries"/> into <paramref name="appDir"/> as a PFS image with title key
    /// <paramref name="klicensee"/>.</summary>
    public static void Write(string appDir, byte[] klicensee, IReadOnlyList<Entry> entries)
    {
        byte[] filesDb = FilesDb(entries);
        byte[] unicvDb = UnicvDb(entries);
        PfsPlan plan = new PfsDecryptor(klicensee).Plan(filesDb, unicvDb);
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            PfsTable table = plan.Tables[i];
            byte[] stored = (byte[])entry.Data.Clone();
            if (entry.Encrypted)
            {
                plan.Cipher(table).Encrypt(stored, 0);
            }

            int signatures = TablePage(i) + 1;
            for (int sector = 0; sector < table.NumSectors; sector++)
            {
                int start = (int)(sector * SectorSize);
                int length = (int)Math.Min(SectorSize, stored.Length - start);
                byte[] icv = HMACSHA1.HashData(table.SigKey, stored.AsSpan(start, length));
                icv.CopyTo(unicvDb, signatures * PageSize + 16 + sector * 20);
            }

            string path = Path.Combine(appDir, entry.RelPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, stored);
        }

        string pfs = Path.Combine(appDir, "sce_pfs");
        Directory.CreateDirectory(pfs);
        File.WriteAllBytes(Path.Combine(pfs, "files.db"), filesDb);
        File.WriteAllBytes(Path.Combine(pfs, "unicv.db"), unicvDb);
    }

    /// <summary>The unicv.db page of file <paramref name="index"/>'s table (its signature page follows it).</summary>
    private static int TablePage(int index) => 1 + index * 2;

    private static byte[] FilesDb(IReadOnlyList<Entry> entries)
    {
        var rows = new List<(uint Parent, string Name, uint Index, ushort Type, uint Size)>();
        var folders = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint next = 1;
        foreach (Entry entry in entries)
        {
            string[] parts = entry.RelPath.Split('/');
            uint parent = 0;
            if (parts.Length == 2)
            {
                if (!folders.TryGetValue(parts[0], out parent))
                {
                    parent = next++;
                    folders[parts[0]] = parent;
                    rows.Add((0, parts[0], parent, AttrDir, 0));
                }
            }

            ushort type = entry.Encrypted ? TypeFile : (ushort)(TypeFile | AttrNenc);
            rows.Add((parent, parts[^1], next++, type, (uint)entry.Data.Length));
        }

        int pages = (rows.Count + EntriesPerPage - 1) / EntriesPerPage;
        byte[] db = new byte[PageSize * (1 + pages)];
        Encoding.ASCII.GetBytes("SCENGPFS").CopyTo(db, 0);
        BitConverter.TryWriteBytes(db.AsSpan(0xC), (ushort)1);
        BitConverter.TryWriteBytes(db.AsSpan(0x10), (uint)PageSize);
        BitConverter.TryWriteBytes(db.AsSpan(0x1C), FilesSalt);
        for (int i = 0; i < rows.Count; i++)
        {
            Span<byte> page = db.AsSpan(PageSize * (1 + i / EntriesPerPage), PageSize);
            int slot = i % EntriesPerPage;
            BitConverter.TryWriteBytes(page[8..], (uint)Math.Min(EntriesPerPage, rows.Count - (i - slot)));
            (uint parent, string name, uint index, ushort type, uint size) = rows[i];
            int header = 16 + slot * EntrySize;
            BitConverter.TryWriteBytes(page[header..], parent);
            Encoding.ASCII.GetBytes(name).CopyTo(page[(header + 4)..]);
            int info = 16 + EntriesPerPage * EntrySize + slot * InfoSize;
            BitConverter.TryWriteBytes(page[info..], index);
            BitConverter.TryWriteBytes(page[(info + 4)..], type);
            BitConverter.TryWriteBytes(page[(info + 8)..], size);
        }

        return db;
    }

    private static byte[] UnicvDb(IReadOnlyList<Entry> entries)
    {
        byte[] db = new byte[PageSize * (1 + entries.Count * 2)];
        Encoding.ASCII.GetBytes("SCEIRODB").CopyTo(db, 0);
        BitConverter.TryWriteBytes(db.AsSpan(0xC), (uint)PageSize);
        for (int i = 0; i < entries.Count; i++)
        {
            uint sectors = PfsPlan.SectorCount(entries[i].Data.Length);
            Span<byte> table = db.AsSpan(TablePage(i) * PageSize, PageSize);
            Encoding.ASCII.GetBytes("SCEIFTBL").CopyTo(table);
            BitConverter.TryWriteBytes(table[8..], 2u);
            BitConverter.TryWriteBytes(table[0x10..], 50u);
            BitConverter.TryWriteBytes(table[0x14..], sectors);
            BitConverter.TryWriteBytes(table[0x18..], SectorSize);
            SHA1.HashData(Encoding.ASCII.GetBytes("dbseed " + entries[i].RelPath)).CopyTo(table[0x34..]);
            Span<byte> signatures = db.AsSpan((TablePage(i) + 1) * PageSize, PageSize);
            BitConverter.TryWriteBytes(signatures[8..], sectors);
        }

        return db;
    }
}
