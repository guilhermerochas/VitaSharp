using System.Security.Cryptography;

namespace VitaSharp.Core.Package.Pfs;

/// <summary>
/// The app0 of an application whose folder is still a PFS image (sce_pfs/files.db + unicv.db, its files encrypted):
/// on the console the application manager mounts app0 through ScePfsMgr with the title key it took from the licence
/// (FW 3.74 SceAppMgr, read in the module, study only), and every read of app0 comes out decrypted. The installers remove
/// the layer when they have the key; a folder that keeps it (the own runtime of a Unity PSM title, which the PSM
/// promote copies as it is, or a folder copied from a console) is opened here at launch: its encrypted files are
/// decrypted once into a view folder that the emulator lays over app0. The key opens the image only when every
/// unicv.db table finds its file by the HMAC of its first sector, the integrity check of the PFS image; a key that
/// does not (another title's) is <see cref="PfsKeyMismatchException"/>. The key is the one the firmware's licence path
/// gives (<see cref="LicenseTitleKey"/>).
/// </summary>
public static class PfsApp0View
{
    /// <summary>The file in the view folder that names the image and key it was made from.</summary>
    private const string StampFile = ".pfs-view";

    /// <summary>Whether the application in <paramref name="appDir"/> is a PFS image (its files still encrypted).</summary>
    public static bool IsImage(string appDir) => PfsDecryptor.HasPfs(appDir);

    /// <summary>
    /// The decrypted view of the PFS image in <paramref name="appDir"/> with the title key <paramref name="klicensee"/>,
    /// made in <paramref name="viewDir"/> (or kept from an earlier launch when it was made from the same image and
    /// key). Holds only the files the image encrypts, at their logical size; the rest of app0 is the folder itself.
    /// </summary>
    public static string Open(string appDir, byte[] klicensee, string viewDir, Action<string>? log = null)
    {
        byte[] filesDb = File.ReadAllBytes(Path.Combine(appDir, "sce_pfs", "files.db"));
        byte[] unicvDb = File.ReadAllBytes(Path.Combine(appDir, "sce_pfs", "unicv.db"));
        string stamp = Stamp(filesDb, unicvDb, klicensee);
        string stampPath = Path.Combine(viewDir, StampFile);
        if (File.Exists(stampPath) && File.ReadAllText(stampPath) == stamp)
        {
            return viewDir;
        }

        PfsPlan plan = new PfsDecryptor(klicensee).Plan(filesDb, unicvDb);
        List<(string RelPath, PfsTable Table, PfsNode Node)> encrypted = MatchFiles(appDir, plan);
        try
        {
            plan.EnsureAllMatched();
        }
        catch (InvalidDataException e)
        {
            throw new PfsKeyMismatchException(appDir, e);
        }

        string staging = viewDir + ".tmp";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }

        Directory.CreateDirectory(staging);
        foreach ((string relPath, PfsTable table, PfsNode node) in encrypted)
        {
            string target = Path.Combine(staging, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            DecryptFile(Path.Combine(appDir, relPath), target, plan.Cipher(table), node.Size);
        }

        File.WriteAllText(Path.Combine(staging, StampFile), stamp);
        if (Directory.Exists(viewDir))
        {
            Directory.Delete(viewDir, true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(viewDir))!);
        Directory.Move(staging, viewDir);
        log?.Invoke($"PFS: app0 of {appDir} opened with its licence's key ({encrypted.Count} encrypted files)");
        return viewDir;
    }

    /// <summary>Each file of the folder that a unicv.db table describes, found as the installers find it (sector
    /// count and the HMAC of its first sector), with the encrypted ones listed.</summary>
    private static List<(string RelPath, PfsTable Table, PfsNode Node)> MatchFiles(string appDir, PfsPlan plan)
    {
        var encrypted = new List<(string, PfsTable, PfsNode)>();
        var files = Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(appDir, f).Replace('\\', '/'))
            .Where(PfsPlan.IsCandidatePath)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (string relPath in files)
        {
            int headBytes = plan.HeadBytes;
            if (headBytes == 0)
            {
                break;
            }

            string path = Path.Combine(appDir, relPath);
            using FileStream stream = File.OpenRead(path);
            if (stream.Length == 0)
            {
                continue;
            }

            byte[] head = new byte[(int)Math.Min(stream.Length, headBytes)];
            stream.ReadExactly(head);
            if (plan.Match(stream.Length, head) is not { } table)
            {
                continue;
            }

            PfsNode node = plan.Node(relPath);
            if (node.Encrypted)
            {
                encrypted.Add((relPath, table, node));
            }
        }

        return encrypted;
    }

    private static void DecryptFile(string source, string target, PfsSectorCipher cipher, long logicalSize)
    {
        using FileStream input = File.OpenRead(source);
        using FileStream output = File.Create(target);
        int chunk = cipher.ChunkSize(PfsPlan.ChunkBytes);
        byte[] buffer = new byte[(int)Math.Min(chunk, Math.Max(input.Length, 1))];
        for (long position = 0; position < input.Length; position += chunk)
        {
            int count = (int)Math.Min(chunk, input.Length - position);
            Span<byte> span = buffer.AsSpan(0, count);
            input.ReadExactly(span);
            cipher.Decrypt(span, position);
            output.Write(span);
        }

        output.SetLength(Math.Min(output.Length, logicalSize));
    }

    /// <summary>The image and key a view was made from (a hash: the key itself is never written).</summary>
    private static string Stamp(byte[] filesDb, byte[] unicvDb, byte[] klicensee)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(filesDb);
        hash.AppendData(unicvDb);
        hash.AppendData(klicensee);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

/// <summary>The licence's key does not open the application's PFS image: no file of the folder verifies against a
/// unicv.db table with it.</summary>
public sealed class PfsKeyMismatchException(string appDir, Exception cause)
    : IOException($"PFS: the licence's key does not open the encrypted files of {appDir} ({cause.Message})", cause);
