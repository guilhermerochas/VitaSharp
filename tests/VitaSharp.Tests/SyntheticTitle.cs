using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace VitaSharp.Tests;

/// <summary>
/// A tiny Vita executable written by the tests (no game data needed, also on CI): a fixed ET_SCE_EXEC ELF with one
/// segment at the start of user memory, a module_info and an import table for three firmware functions, and ARM code
/// that every frame reads the pad (sceCtrlPeekBufferPositive), fills one of two 480x272 A8B8G8R8 framebuffers (in turn,
/// as games double-buffer: the one not on screen) with
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

    private static uint Abgr((byte R, byte G, byte B) c) => 0xFF000000u | (uint)c.B << 16 | (uint)c.G << 8 | c.R;

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
        using ZipArchive zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        void Add(string name, byte[] data)
        {
            using Stream s = zip.CreateEntry(name).Open();
            s.Write(data);
        }
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
        var index = new List<(int KeyOff, int DataOffset, int Len, int Max)>();
        foreach ((string? k, string? v) in entries)
        {
            int ko = (int)keys.Length;
            keys.Write(Encoding.ASCII.GetBytes(k + "\0"));
            byte[] bytes = Encoding.UTF8.GetBytes(v + "\0");
            int max = (bytes.Length + 3) & ~3;
            index.Add((ko, (int)data.Length, bytes.Length, max));
            data.Write(bytes);
            data.Write(new byte[max - bytes.Length]);
        }
        while (keys.Length % 4 != 0)
        {
            keys.WriteByte(0);
        }

        int keyStart = 20 + index.Count * 16, dataStart = keyStart + (int)keys.Length;
        var o = new MemoryStream();
        using (var w = new BinaryWriter(o, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(0x46535000u);
            w.Write(0x00000101u);
            w.Write(keyStart);
            w.Write(dataStart);
            w.Write(index.Count);
            foreach ((int ko, int doff, int len, int max) in index)
            {
                w.Write((ushort)ko);
                w.Write((ushort)0x0204);
                w.Write(len);
                w.Write(max);
                w.Write(doff);
            }
            w.Write(keys.ToArray());
            w.Write(data.ToArray());
        }
        return o.ToArray();
    }

    /// <summary>The executable.</summary>
    public static byte[] Elf() => Build(readsFile: false);

    /// <summary>The variant of <see cref="InstallFileReader"/>: every frame it opens <see cref="ColourFile"/> on app0
    /// and reads the colour of the frame from it (sceIoOpen, sceIoRead, sceIoClose).</summary>
    public static byte[] FileReaderElf() => Build(readsFile: true);

    /// <summary>The file the <see cref="FileReaderElf"/> variant reads every frame.</summary>
    public const string ColourFile = "colour.bin";

    /// <summary>A file the <see cref="FileReaderElf"/> variant cannot read draws this.</summary>
    public static readonly (byte R, byte G, byte B) Missing = (0x10, 0x10, 0x10);

    /// <summary>Size of <see cref="ColourFile"/>: the two colours are its first 8 bytes, the rest is never read.</summary>
    public const int ColourFileSize = 64 * 1024;

    /// <summary>The title of <see cref="Install"/> that takes its colours from app0:<see cref="ColourFile"/> every frame
    /// (the idle colour from bytes 0-3, with a button held a second read gives the pressed colour from bytes 4-7):
    /// a game reading its own files while it runs.</summary>
    public static string InstallFileReader(string fsRoot, string titleId = "VSTS00001")
    {
        string app = Install(fsRoot, titleId);
        File.WriteAllBytes(Path.Combine(app, "eboot.bin"), FileReaderElf());
        byte[] colours = new byte[ColourFileSize];
        for (int i = 8; i < colours.Length; i++)
        {
            colours[i] = (byte)(i * 7);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(colours, Abgr(Idle));
        BinaryPrimitives.WriteUInt32LittleEndian(colours.AsSpan(4), Abgr(Pressed));
        File.WriteAllBytes(Path.Combine(app, ColourFile), colours);
        return app;
    }

    private static byte[] Build(bool readsFile)
    {
        // the layout: module_info, imports, their strings, NID and stub tables, stubs, code, then the data
        const uint InfoOff = 0x0, ImportsOff = 0x60;
        uint stringsOff = readsFile ? 0x200u : 0x100u, tablesOff = stringsOff + (readsFile ? 0x60u : 0x40u), stubsOff = tablesOff + 0x40, codeOff = readsFile ? 0x300u : 0x200u;
        uint paramOff = readsFile ? 0x600u : 0x400u, ctrlOff = paramOff + 0x20, param2Off = paramOff + 0x60;
        uint pathOff = paramOff - 0x40, readBufOff = paramOff - 0x10;
        const uint FbOff = 0x1000, Fb2Off = FbOff + Width * Height * 4;

        byte[] seg = new byte[param2Off + 0x20];
        void W32(uint off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(seg.AsSpan((int)off), v);
        void W16(uint off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(seg.AsSpan((int)off), v);

        // the imported libraries: name, library NID, function NIDs (one stub each)
        List<(string Lib, uint LibNid, uint Nid)> imports =
        [
            ("SceCtrl", 0xD197E3C7, 0xA9C3CED6),        // sceCtrlPeekBufferPositive
            ("SceDisplayUser", 0x4FAACD11, 0x7A410B64), // sceDisplaySetFrameBuf
            ("SceDisplay", 0x5ED8F994, 0x5795E898),     // sceDisplayWaitVblankStart
        ];
        if (readsFile)
        {
            imports.Add(("SceLibKernel", 0xCAE9ACE6, 0x6C60AC61)); // sceIoOpen
            imports.Add(("SceIofilemgr", 0xF2FF276E, 0xFDB32293)); // sceIoRead
            imports.Add(("SceIofilemgr", 0xF2FF276E, 0xC70B8886)); // sceIoClose
        }

        uint str = stringsOff;
        for (int i = 0; i < imports.Count; i++)
        {
            uint entry = ImportsOff + (uint)i * 0x34;
            uint nidTable = tablesOff + (uint)i * 8, stubTable = nidTable + 4, stub = stubsOff + (uint)i * 16;
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
        if (ImportsOff + imports.Count * 0x34 > stringsOff || str > tablesOff || stubsOff + imports.Count * 16 > codeOff)
        {
            throw new InvalidOperationException("import tables overlap");
        }

        // module_info
        Encoding.ASCII.GetBytes("VitaSharpSynthetic").CopyTo(seg, (int)InfoOff + 4);
        W16(InfoOff + 2, 0x0101);
        W32(InfoOff + 0x24, 0);
        W32(InfoOff + 0x28, 0); // no exports
        W32(InfoOff + 0x2C, ImportsOff);
        W32(InfoOff + 0x30, ImportsOff + (uint)imports.Count * 0x34);
        W32(InfoOff + 0x44, codeOff); // module_start (ARM)
        W32(InfoOff + 0x48, 0xFFFFFFFF);

        // SceDisplayFrameBuf: size, base, pitch, format (A8B8G8R8), width, height
        W32(paramOff, 0x18);
        W32(paramOff + 4, Base + FbOff);
        W32(paramOff + 8, Width);
        W32(paramOff + 12, 0);
        W32(paramOff + 16, Width);
        W32(paramOff + 20, Height);
        // the second framebuffer's
        W32(param2Off, 0x18);
        W32(param2Off + 4, Base + Fb2Off);
        W32(param2Off + 8, Width);
        W32(param2Off + 12, 0);
        W32(param2Off + 16, Width);
        W32(param2Off + 20, Height);
        if (readsFile)
        {
            Encoding.ASCII.GetBytes("app0:" + ColourFile).CopyTo(seg, (int)pathOff);
        }

        var a = new ArmCode(Base + codeOff);
        a.Emit(0xE3A05000);                        // mov r5, #0          r5: which framebuffer (kept across calls)
        a.Label("loop");
        a.Emit(0xE3A00000);                        // mov r0, #0          port
        a.LdrLiteral(1, Base + ctrlOff);           // ldr r1, =ctrl data
        a.Emit(0xE3A02001);                        // mov r2, #1          one sample
        a.Bl(Base + stubsOff);                     // sceCtrlPeekBufferPositive
        a.LdrLiteral(1, Base + ctrlOff);
        a.Emit(0xE5911008);                        // ldr r1, [r1, #8]    buttons
        if (readsFile)
        {
            a.Emit(0xE1A07001);                    // mov r7, r1          buttons (kept across calls)
            a.LdrLiteral(0, Base + pathOff);       // ldr r0, ="app0:colour.bin"
            a.Emit(0xE3A01001);                    // mov r1, #1          SCE_O_RDONLY
            a.Emit(0xE3A02000);                    // mov r2, #0
            a.Bl(Base + stubsOff + 48);            // sceIoOpen
            a.Emit(0xE1A08000);                    // mov r8, r0          fd
            a.LdrLiteral(3, Abgr(Missing));
            a.Emit(0xE3580000);                    // cmp r8, #0
            a.Branch("colour", 0xB);               // blt colour          not opened: the missing colour
            a.Emit(0xE1A00008);                    // mov r0, r8
            a.LdrLiteral(1, Base + readBufOff);
            a.Emit(0xE3A02004);                    // mov r2, #4
            a.Bl(Base + stubsOff + 64);            // sceIoRead           bytes 0-3: the idle colour
            a.Emit(0xE3570000);                    // cmp r7, #0
            a.Branch("close", 0x0);                // beq close
            a.Emit(0xE1A00008);                    // mov r0, r8
            a.LdrLiteral(1, Base + readBufOff);
            a.Emit(0xE3A02004);                    // mov r2, #4
            a.Bl(Base + stubsOff + 64);            // sceIoRead           bytes 4-7: the pressed colour
            a.Label("close");
            a.Emit(0xE1A00008);                    // mov r0, r8
            a.Bl(Base + stubsOff + 80);            // sceIoClose
            a.LdrLiteral(3, Base + readBufOff);
            a.Emit(0xE5933000);                    // ldr r3, [r3]        the colour read
        }
        else
        {
            a.LdrLiteral(3, Abgr(Idle));
            a.Emit(0xE3510000);                    // cmp r1, #0
            a.LdrLiteral(3, Abgr(Pressed), cond: 0x1); // ldrne r3, =pressed
        }

        a.Label("colour");
        a.LdrLiteral(2, Base + FbOff);
        a.LdrLiteral(6, Base + paramOff);
        a.Emit(0xE3150001);                        // tst r5, #1
        a.LdrLiteral(2, Base + Fb2Off, cond: 0x1); // ldrne r2, =second framebuffer
        a.LdrLiteral(6, Base + param2Off, cond: 0x1);
        a.LdrLiteral(4, Width * Height);
        a.Label("fill");
        a.Emit(0xE4823004);                        // str r3, [r2], #4
        a.Emit(0xE2544001);                        // subs r4, r4, #1
        a.Branch("fill", 0x1);                     // bne fill
        a.Emit(0xE1A00006);                        // mov r0, r6          the filled one's SceDisplayFrameBuf
        a.Emit(0xE3A01001);                        // mov r1, #1          next vblank
        a.Bl(Base + stubsOff + 16);                // sceDisplaySetFrameBuf
        a.Bl(Base + stubsOff + 32);                // sceDisplayWaitVblankStart
        a.Emit(0xE2255001);                        // eor r5, r5, #1      the other framebuffer next
        a.Branch("loop", 0xE);
        byte[] code = a.Assemble();
        if (codeOff + code.Length > (readsFile ? pathOff : paramOff))
        {
            throw new InvalidOperationException("code too long");
        }

        code.CopyTo(seg, (int)codeOff);

        // ELF header + one PT_LOAD (the framebuffers are the zero-filled rest of the segment)
        const int ProgramHeaderOffset = 0x34, DataOffset = 0x100;
        byte[] elf = new byte[DataOffset + seg.Length];
        Span<byte> e = elf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(e, 0x464C457F);
        e[4] = 1;
        e[5] = 1;
        e[6] = 1;                                // 32-bit, little endian, version 1
        BinaryPrimitives.WriteUInt16LittleEndian(e[16..], 0xFE00);  // ET_SCE_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(e[18..], 40);      // ARM
        BinaryPrimitives.WriteUInt32LittleEndian(e[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(e[24..], InfoOff); // segment 0, module_info offset
        BinaryPrimitives.WriteUInt32LittleEndian(e[28..], ProgramHeaderOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(e[40..], 0x34);
        BinaryPrimitives.WriteUInt16LittleEndian(e[42..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(e[44..], 1);
        Span<byte> ph = e.Slice(ProgramHeaderOffset, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(ph, 1);                          // PT_LOAD
        BinaryPrimitives.WriteUInt32LittleEndian(ph[4..], DataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[8..], Base);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[12..], Base);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[16..], (uint)seg.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[20..], Fb2Off + Width * Height * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(ph[24..], 5);                    // R+X, as a module's text (its import stubs must be there)
        BinaryPrimitives.WriteUInt32LittleEndian(ph[28..], 0x1000);
        seg.CopyTo(elf, DataOffset);
        return elf;
    }

    /// <summary>A few ARM (A32) instructions with labels and a literal pool after the code.</summary>
    private sealed class ArmCode(uint origin)
    {
        private readonly List<Func<uint, uint>> _words = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<uint> _literals = [];

        public void Label(string name) => _labels[name] = _words.Count;
        public void Emit(uint word) => _words.Add(_ => word);

        public void Bl(uint target) => _words.Add(pc => 0xEB000000 | ((target - (pc + 8)) >> 2 & 0xFFFFFF));

        public void Branch(string label, uint cond) =>
            _words.Add(pc => cond << 28 | 0x0A000000 | ((origin + (uint)_labels[label] * 4 - (pc + 8)) >> 2 & 0xFFFFFF));

        /// <summary>ldr rd, [pc, #literal] (cond 0xE = always).</summary>
        public void LdrLiteral(int rd, uint value, uint cond = 0xE)
        {
            int index = _literals.IndexOf(value);
            if (index < 0)
            {
                index = _literals.Count;
                _literals.Add(value);
            }
            _words.Add(pc =>
            {
                uint lit = origin + (uint)(_words.Count + index) * 4;
                return cond << 28 | 0x059F0000 | (uint)rd << 12 | (lit - (pc + 8));
            });
        }

        public byte[] Assemble()
        {
            byte[] bytes = new byte[(_words.Count + _literals.Count) * 4];
            for (int i = 0; i < _words.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), _words[i](origin + (uint)i * 4));
            }

            for (int i = 0; i < _literals.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((_words.Count + i) * 4), _literals[i]);
            }

            return bytes;
        }
    }
}
