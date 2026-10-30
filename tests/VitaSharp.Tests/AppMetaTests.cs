using VitaSharp.Core.Kernel;
using VitaSharp.Core.Package;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>ux0:appmeta, what the shell's database rebuild reads instead of scanning ux0:app, and the per-process
/// ownership of kernel objects in the shared table.</summary>
public class AppMetaTests
{
    static byte[] Sfo(string titleId, string category)
    {
        // a minimal PARAM.SFO with TITLE_ID and CATEGORY (UTF-8 strings)
        var keys = new[] { ("CATEGORY", category), ("TITLE_ID", titleId) };
        var keyTable = new List<byte>(); var dataTable = new List<byte>(); var index = new List<byte>();
        foreach (var (k, v) in keys)
        {
            var data = System.Text.Encoding.UTF8.GetBytes(v + "\0");
            index.AddRange(BitConverter.GetBytes((ushort)keyTable.Count));
            index.AddRange(BitConverter.GetBytes((ushort)0x0204));
            index.AddRange(BitConverter.GetBytes((uint)data.Length));
            index.AddRange(BitConverter.GetBytes((uint)data.Length));
            index.AddRange(BitConverter.GetBytes((uint)dataTable.Count));
            keyTable.AddRange(System.Text.Encoding.ASCII.GetBytes(k + "\0"));
            dataTable.AddRange(data);
        }
        while (keyTable.Count % 4 != 0) keyTable.Add(0);
        uint keyOff = (uint)(0x14 + index.Count), dataOff = keyOff + (uint)keyTable.Count;
        var sfo = new List<byte>();
        sfo.AddRange(BitConverter.GetBytes(0x46535000u)); sfo.AddRange(BitConverter.GetBytes(0x101u));
        sfo.AddRange(BitConverter.GetBytes(keyOff)); sfo.AddRange(BitConverter.GetBytes(dataOff));
        sfo.AddRange(BitConverter.GetBytes((uint)keys.Length));
        sfo.AddRange(index); sfo.AddRange(keyTable); sfo.AddRange(dataTable);
        return sfo.ToArray();
    }

    [Fact]
    public void SyncWritesAppmetaAndResetsTheCardIdOnlyWhenSomethingChanged()
    {
        string ux0 = Path.Combine(Path.GetTempPath(), "vitasharp-appmeta-" + Guid.NewGuid().ToString("N"));
        try
        {
            string app = Path.Combine(ux0, "app", "ABCD12345");
            Directory.CreateDirectory(Path.Combine(app, "sce_sys", "livearea", "contents"));
            File.WriteAllBytes(Path.Combine(app, "eboot.bin"), [1]);
            File.WriteAllBytes(Path.Combine(app, "sce_sys", "param.sfo"), Sfo("ABCD12345", "gd"));
            File.WriteAllBytes(Path.Combine(app, "sce_sys", "icon0.png"), [2]);
            File.WriteAllText(Path.Combine(app, "sce_sys", "livearea", "contents", "template.xml"), "<livearea/>");
            File.WriteAllText(Path.Combine(ux0, "id.dat"), "MID=x");

            Assert.Equal(1, AppMeta.Sync(ux0));
            string meta = Path.Combine(ux0, "appmeta", "ABCD12345");
            Assert.True(File.Exists(Path.Combine(meta, "param.sfo")));
            Assert.True(File.Exists(Path.Combine(meta, "icon0.png")));
            Assert.True(File.Exists(Path.Combine(meta, "livearea", "contents", "template.xml")));
            Assert.False(File.Exists(Path.Combine(ux0, "id.dat")));

            File.WriteAllText(Path.Combine(ux0, "id.dat"), "MID=x");
            Assert.Equal(0, AppMeta.Sync(ux0)); // nothing changed: the card keeps its id
            Assert.True(File.Exists(Path.Combine(ux0, "id.dat")));

            Directory.Delete(app, true); // uninstalled: its appmeta goes too
            Assert.Equal(1, AppMeta.Sync(ux0));
            Assert.False(Directory.Exists(meta));
        }
        finally { if (Directory.Exists(ux0)) Directory.Delete(ux0, true); }
    }

    sealed class Dummy : KernelObject;

    [Fact]
    public void ObjectsBelongToTheProcessThatCreatedThem()
    {
        var table = new ObjectTable();
        int a, b;
        using (ProcessScope.Enter(0x1001)) a = table.Register(new Dummy { Name = "pipeA" });
        using (ProcessScope.Enter(0x2001)) b = table.Register(new Dummy { Name = "pipeB" });
        Assert.Equal(0x1001, table.Get<Dummy>(a)!.OwnerPid);
        Assert.NotNull(table.FindByName<Dummy>("pipeB")); // one namespace for every process
        Assert.Equal(1, table.RemoveOwnedBy(0x1001));
        Assert.Null(table.Get<Dummy>(a));
        Assert.NotNull(table.Get<Dummy>(b));
    }
}
