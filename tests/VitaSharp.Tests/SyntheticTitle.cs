using System.Buffers.Binary;
using System.Text;

namespace VitaSharp.Tests;

/// <summary>
/// A tiny Vita executable written by the tests (no game data needed, also on CI): a fixed ET_SCE_EXEC ELF with one
/// segment at the start of user memory, a module_info and an import table for three firmware functions, and ARM code
/// that every frame reads the pad (sceCtrlPeekBufferPositive), fills a 480x272 A8B8G8R8 framebuffer with
/// <see cref="Idle"/> (no button held) or <see cref="Pressed"/> (any button), shows it (sceDisplaySetFrameBuf) and
/// waits for the next vblank. The front-end's runner boots it like a game, so its frame loop, the scripted input and
/// the frame dumps can be checked on pixels.
/// </summary>
public static class SyntheticTitle
{
    public const uint Base = 0x8100_0000;
    public const int Width = 480, Height = 272;
    /// <summary>The colours as R, G, B (the guest writes them as A8B8G8R8 words).</summary>
    public static readonly (byte R, byte G, byte B) Idle = (0xC0, 0x40, 0x20), Pressed = (0x20, 0xA0, 0xE0);

    const uint InfoOff = 0x0, ImportsOff = 0x60, StringsOff = 0x100, TablesOff = 0x140, StubsOff = 0x180, CodeOff = 0x200;
    const uint ParamOff = 0x400, CtrlOff = 0x420, FbOff = 0x1000;

    static uint Abgr((byte R, byte G, byte B) c) => 0xFF000000u | (uint)c.B << 16 | (uint)c.G << 8 | c.R;

    public const string Title = "Synthetic Test";

    /// <summary>Writes <c>&lt;fsRoot&gt;/ux0/app/&lt;titleId&gt;/eboot.bin</c> (and its param.sfo) and returns the app folder.</summary>
    public static string Install(string fsRoot, string titleId = "VSTS00001")
    {
        string app = Path.Combine(fsRoot, "ux0", "app", titleId);
        Directory.CreateDirectory(Path.Combine(app, "sce_sys"));
        File.WriteAllBytes(Path.Combine(app, "eboot.bin"), Elf());
        File.WriteAllBytes(Path.Combine(app, "sce_sys", "param.sfo"), ParamSfo(titleId));
        return app;
    }

    /// <summary>The title as a VPK (a zip of the app folder).</summary>
    public static void WriteVpk(string path, string titleId = "VSTS00001")
    {
        using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        void Add(string name, byte[] data) { using var s = zip.CreateEntry(name).Open(); s.Write(data); }
        Add("eboot.bin", Elf());
        Add("sce_sys/param.sfo", ParamSfo(titleId));
    }

    /// <summary>A param.sfo of a game application (UTF-8 string entries, sorted keys).</summary>
    public static byte[] ParamSfo(string titleId)
    {
        (string Key, string Value)[] entries =
        [
            ("APP_VER", "01.00"), ("ATTRIBUTE", ""), ("CATEGORY", "gd"), ("CONTENT_ID", $"UP0000-{titleId}_00-0000000000000000"),
            ("STITLE", Title), ("TITLE", Title), ("TITLE_ID", titleId),
        ];
        var keys = new MemoryStream();
        var data = new MemoryStream();
        var index = new List<(int KeyOff, int DataOff, int Len, int Max)>();
        foreach (var (k, v) in entries)
        {
            int ko = (int)keys.Length;
            keys.Write(Encoding.ASCII.GetBytes(k + "\0"));
            var bytes = Encoding.UTF8.GetBytes(v + "\0");
            int max = (bytes.Length + 3) & ~3;
            index.Add((ko, (int)data.Length, bytes.Length, max));
            data.Write(bytes);
            data.Write(new byte[max - bytes.Length]);
        }
        while (keys.Length % 4 != 0) keys.WriteByte(0);
        int keyStart = 20 + index.Count * 16, dataStart = keyStart + (int)keys.Length;
        var o = new MemoryStream();
        using (var w = new BinaryWriter(o, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(0x46535000u); w.Write(0x00000101u); w.Write(keyStart); w.Write(dataStart); w.Write(index.Count);
            foreach (var (ko, doff, len, max) in index) { w.Write((ushort)ko); w.Write((ushort)0x0204); w.Write(len); w.Write(max); w.Write(doff); }
            w.Write(keys.ToArray());
            w.Write(data.ToArray());
        }
        return o.ToArray();
    }

    /// <summary>The executable.</summary>
    public static byte[] Elf()
    {
        var seg = new byte[ParamOff + 0x40];
        void W32(uint off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(seg.AsSpan((int)off), v);
        void W16(uint off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(seg.AsSpan((int)off), v);

        // the imported libraries: name, library NID, function NIDs (one stub each)
        (string Lib, uint LibNid, uint Nid)[] imports =
        [
            ("SceCtrl", 0xD197E3C7, 0xA9C3CED6),        // sceCtrlPeekBufferPositive
            ("SceDisplayUser", 0x4FAACD11, 0x7A410B64), // sceDisplaySetFrameBuf
            ("SceDisplay", 0x5ED8F994, 0x5795E898),     // sceDisplayWaitVblankStart
        ];
        uint str = StringsOff;
        for (int i = 0; i < imports.Length; i++)
        {
            uint entry = ImportsOff + (uint)i * 0x34;
            uint nidTable = TablesOff + (uint)i * 8, stubTable = nidTable + 4, stub = StubsOff + (uint)i * 16;
            Encoding.ASCII.GetBytes(imports[i].Lib).CopyTo(seg, (int)str);
            W16(entry, 0x34);                      // size
            W16(entry + 2, 1);                     // version
            W16(entry + 6, 1);                     // functions
            W32(entry + 0x10, imports[i].LibNid);
            W32(entry + 0x14, Base + str);
            W32(entry + 0x1C, Base + nidTable);
            W32(entry + 0x20, Base + stubTable);
            W32(nidTable, imports[i].Nid);
            W32(stubTable, Base + stub);
            str += (uint)imports[i].Lib.Length + 1;
        }
        // 3 imports x 0x34 = 0x9C bytes from 0x60: the strings at 0x100 follow without overlap
        if (ImportsOff + imports.Length * 0x34 > StringsOff) throw new InvalidOperationException("import table overlaps the strings");

        // module_info
        Encoding.ASCII.GetBytes("VitaSharpSynthetic").CopyTo(seg, (int)InfoOff + 4);
        W16(InfoOff + 2, 0x0101);
        W32(InfoOff + 0x24, 0); W32(InfoOff + 0x28, 0); // no exports
        W32(InfoOff + 0x2C, ImportsOff); W32(InfoOff + 0x30, ImportsOff + (uint)imports.Length * 0x34);
        W32(InfoOff + 0x44, CodeOff); // module_start (ARM)
        W32(InfoOff + 0x48, 0xFFFFFFFF);

        // SceDisplayFrameBuf: size, base, pitch, format (A8B8G8R8), width, height
        W32(ParamOff, 0x18); W32(ParamOff + 4, Base + FbOff); W32(ParamOff + 8, Width); W32(ParamOff + 12, 0);
        W32(ParamOff + 16, Width); W32(ParamOff + 20, Height);

        var a = new ArmCode(Base + CodeOff);
        a.Label("loop");
        a.Emit(0xE3A00000);                        // mov r0, #0          port
        a.LdrLiteral(1, Base + CtrlOff);           // ldr r1, =ctrl data
        a.Emit(0xE3A02001);                        // mov r2, #1          one sample
        a.Bl(Base + StubsOff);                     // sceCtrlPeekBufferPositive
        a.LdrLiteral(1, Base + CtrlOff);
        a.Emit(0xE5911008);                        // ldr r1, [r1, #8]    buttons
        a.LdrLiteral(3, Abgr(Idle));
        a.Emit(0xE3510000);                        // cmp r1, #0
        a.LdrLiteral(3, Abgr(Pressed), cond: 0x1); // ldrne r3, =pressed
        a.LdrLiteral(2, Base + FbOff);
        a.LdrLiteral(4, Width * Height);
        a.Label("fill");
        a.Emit(0xE4823004);                        // str r3, [r2], #4
        a.Emit(0xE2544001);                        // subs r4, r4, #1
        a.Branch("fill", 0x1);                     // bne fill
        a.LdrLiteral(0, Base + ParamOff);
        a.Emit(0xE3A01001);                        // mov r1, #1          next vblank
        a.Bl(Base + StubsOff + 16);                // sceDisplaySetFrameBuf
        a.Bl(Base + StubsOff + 32);                // sceDisplayWaitVblankStart
        a.Branch("loop", 0xE);
        var code = a.Assemble();
        if (CodeOff + code.Length > ParamOff) throw new InvalidOperationException("code too long");
        code.CopyTo(seg, (int)CodeOff);

        // ELF header + one PT_LOAD (the framebuffer is the zero-filled rest of the segment)
        const int phoff = 0x34, dataOff = 0x100;
        var elf = new byte[dataOff + seg.Length];
        var e = elf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(e, 0x464C457F);
        e[4] = 1; e[5] = 1; e[6] = 1;                                // 32-bit, little endian, version 1
        BinaryPrimitives.WriteUInt16LittleEndian(e[16..], 0xFE00);  // ET_SCE_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(e[18..], 40);      // ARM
        BinaryPrimitives.WriteUInt32LittleEndian(e[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(e[24..], InfoOff); // segment 0, module_info offset
        BinaryPrimitives.WriteUInt32LittleEndian(e[28..], phoff);
        BinaryPrimitives.WriteUInt16LittleEndian(e[40..], 0x34);
        BinaryPrimitives.WriteUInt16LittleEndian(e[42..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(e[44..], 1);
        var ph = e.Slice(phoff, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(ph, 1);                          // PT_LOAD
        BinaryPrimitives.WriteUInt32LittleEndian(ph[4..], dataOff);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[8..], Base);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[12..], Base);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[16..], (uint)seg.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[20..], FbOff + Width * Height * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[24..], 7);                    // RWX
        BinaryPrimitives.WriteUInt32LittleEndian(ph[28..], 0x1000);
        seg.CopyTo(elf, dataOff);
        return elf;
    }

    /// <summary>A few ARM (A32) instructions with labels and a literal pool after the code.</summary>
    sealed class ArmCode(uint origin)
    {
        readonly List<Func<uint, uint>> _words = [];
        readonly Dictionary<string, int> _labels = [];
        readonly List<uint> _literals = [];

        public void Label(string name) => _labels[name] = _words.Count;
        public void Emit(uint word) => _words.Add(_ => word);

        public void Bl(uint target) => _words.Add(pc => 0xEB000000 | ((target - (pc + 8)) >> 2 & 0xFFFFFF));

        public void Branch(string label, uint cond) =>
            _words.Add(pc => cond << 28 | 0x0A000000 | ((origin + (uint)_labels[label] * 4 - (pc + 8)) >> 2 & 0xFFFFFF));

        /// <summary>ldr rd, [pc, #literal] (cond 0xE = always).</summary>
        public void LdrLiteral(int rd, uint value, uint cond = 0xE)
        {
            int index = _literals.IndexOf(value);
            if (index < 0) { index = _literals.Count; _literals.Add(value); }
            _words.Add(pc =>
            {
                uint lit = origin + (uint)(_words.Count + index) * 4;
                return cond << 28 | 0x059F0000 | (uint)rd << 12 | (lit - (pc + 8));
            });
        }

        public byte[] Assemble()
        {
            var bytes = new byte[(_words.Count + _literals.Count) * 4];
            for (int i = 0; i < _words.Count; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), _words[i](origin + (uint)i * 4));
            for (int i = 0; i < _literals.Count; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((_words.Count + i) * 4), _literals[i]);
            return bytes;
        }
    }
}
