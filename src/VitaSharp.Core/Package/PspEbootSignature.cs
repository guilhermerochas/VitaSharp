using System.Security.Cryptography;

namespace VitaSharp.Core.Package;

/// <summary>
/// The PSP emulator's eboot signature (ux0:pspemu/PSP/GAME/&lt;ID&gt;/__sce_ebootpbp): 0x200 bytes the security module
/// signs with the console's keys when a PSP or PSone title is installed (NpDrm EbootSigGenPsp/Ps1/MultiDisc), checked by
/// the shell and the PSP emulator before they show or start the title. Those keys are not here: the emulated signature is
/// the SHA-256 of the request (eboot file name and hash) under a fixed header, so the verify of the same request passes
/// and any other does not. The installer and the NpDrm HLE (VshBridge) make it the same way.
/// </summary>
public static class PspEbootSignature
{
    public const int Size = 0x200;
    public const int KindPsp = 1, KindPs1 = 2, KindMultiDisc = 3;

    /// <summary>The signature of <paramref name="path"/> (only its file name counts: "EBOOT.PBP") whose hash (or
    /// __sce_discinfo for <see cref="KindMultiDisc"/>) is <paramref name="hash"/>.</summary>
    public static byte[] Create(string path, ReadOnlySpan<byte> hash, int kind)
    {
        var sig = new byte[Size];
        "NPUMDSIG"u8.CopyTo(sig);
        sig[8] = (byte)kind;
        var h = SHA256.HashData([.. System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(path)), .. hash]);
        h.CopyTo(sig, 0x10);
        hash.CopyTo(sig.AsSpan(0x30));
        return sig;
    }

    /// <summary>The kind (1 PSP, 2 PS1, 3 multi-disc) of a signature made by <see cref="Create"/> for
    /// <paramref name="path"/>, or -1 when it does not verify.</summary>
    public static int Verify(string path, ReadOnlySpan<byte> sig)
    {
        if (sig.Length < Size || !sig[..8].SequenceEqual("NPUMDSIG"u8))
        {
            return -1;
        }

        var expect = Create(path, sig.Slice(0x30, sig[8] == KindMultiDisc ? 0x100 : 0x20), sig[8]);
        return sig.Slice(0x10, 0x20).SequenceEqual(expect.AsSpan(0x10, 0x20)) ? sig[8] : -1;
    }
}
