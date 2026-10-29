using System.Text;
using VitaSharp.Core.Firmware;

namespace VitaSharp.Tests;

/// <summary>Pieces that run the firmware's own system software: the registry template and store.</summary>
public class SystemSoftwareTests
{
    static readonly byte[] XorKey = [0x89, 0xFA, 0x95, 0x48, 0xCB, 0x6D, 0x77, 0x9D, 0xA2, 0x25, 0x34, 0xFD, 0xA9, 0x35, 0x59, 0x6E];

    /// <summary>A registry.db0 in the firmware's layout: 138-byte header, then the XOR-obfuscated template text.</summary>
    static string FsWithTemplate(string text)
    {
        string root = Path.Combine(Path.GetTempPath(), "vs-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "os0", "kd"));
        var body = Encoding.ASCII.GetBytes(text);
        var file = new byte[138 + body.Length];
        for (int i = 0; i < body.Length; i++) file[138 + i] = (byte)(body[i] ^ XorKey[i & 0xF]);
        File.WriteAllBytes(Path.Combine(root, "os0", "kd", "registry.db0"), file);
        return root;
    }

    const string Template =
        "[BASE-0\n1=CONFIG/\n2=SYSTEM/\n3=language\n4=username\n5=NET/\n6=01-03/\n7=conf_flag\n" +
        "[REG-BAS\n1/2/3=0:4:1\n1/2/4=1:17:Vita\n1/5/6/7=0:4:0\n[REG-J1\n";

    [Fact]
    public void Template_defines_keys_with_types_defaults_and_ranges()
    {
        string root = FsWithTemplate(Template);
        try
        {
            var reg = Registry.Open(root);
            Assert.True(reg.TryGetInt("/CONFIG/SYSTEM/", "language", out int lang));
            Assert.Equal(1, lang);
            Assert.True(reg.TryGetInt("/CONFIG/SYSTEM", "language", out _)); // category without the trailing '/'
            Assert.Equal("Vita", Encoding.UTF8.GetString(reg.Get("/CONFIG/SYSTEM/", "username")!).TrimEnd('\0'));
            Assert.Equal(Registry.KeyType.Str, reg.Find("/CONFIG/SYSTEM/", "username")!.Type);
            // "01-03" expands to one category per number
            Assert.NotNull(reg.Find("/CONFIG/NET/02/", "conf_flag"));
            Assert.NotNull(reg.Find("/CONFIG/NET/03/", "conf_flag"));
            Assert.Null(reg.Find("/CONFIG/NET/04/", "conf_flag"));
            Assert.Null(reg.Get("/CONFIG/SYSTEM/", "nope"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Written_values_persist_and_unknown_keys_are_refused()
    {
        string root = FsWithTemplate(Template);
        try
        {
            var reg = Registry.Open(root);
            Assert.True(reg.SetInt("/CONFIG/SYSTEM/", "language", 17));
            Assert.True(reg.Set("/CONFIG/SYSTEM/", "username", Encoding.UTF8.GetBytes("Bob\0")));
            Assert.False(reg.SetInt("/CONFIG/SYSTEM/", "nope", 1));

            var again = Registry.Open(root);
            Assert.True(again.TryGetInt("/CONFIG/SYSTEM/", "language", out int lang));
            Assert.Equal(17, lang);
            Assert.StartsWith("Bob", Encoding.UTF8.GetString(again.Get("/CONFIG/SYSTEM/", "username")!));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Dynamic_defaults_follow_the_emulator_until_written()
    {
        string root = FsWithTemplate(Template);
        try
        {
            var reg = Registry.Open(root);
            int option = 5;
            reg.Define("/CONFIG/SYSTEM/language", Registry.KeyType.Int, 4, () => option);
            Assert.True(reg.TryGetInt("/CONFIG/SYSTEM/", "language", out int v));
            Assert.Equal(5, v);
            option = 6;
            Assert.True(reg.TryGetInt("/CONFIG/SYSTEM/", "language", out v));
            Assert.Equal(6, v);
            reg.SetInt("/CONFIG/SYSTEM/", "language", 2);
            Assert.True(reg.TryGetInt("/CONFIG/SYSTEM/", "language", out v));
            Assert.Equal(2, v);
        }
        finally { Directory.Delete(root, true); }
    }
}
