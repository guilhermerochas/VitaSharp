using System.Buffers.Binary;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// The rest of SceGpuEs4User (os0:us/libgpu_es4.suprx 3.74, read in its code, study only): the device-data bridge-call
// wrappers (refused by the driver before its SGX init server ran, see GpuEs4.User.Connection.cs), libgpu's heap and its
// USSE/PDS code writers. Two writers copy instruction templates from libgpu's own code segment; those words are read
// from the installed firmware's module at run time (never kept in the repository), next to the export as in 3.74.
public sealed partial class GpuEs4
{
    /// <summary>libgpu heap blocks (NID_7AF40138 / NID_1BE5985A).</summary>
    readonly HashSet<uint> _userHeapBlocks = new();

    /// <summary>Tests: a stand-in for the firmware module's bytes (export NID, offset from the export, length).</summary>
    internal Func<uint, int, int, byte[]?>? LibgpuCodeSource;

    byte[]? _libgpuElf;
    bool _libgpuElfRead;

    /// <summary>NID_804BA13D (device, value, uint *out1, uint *out2): bridge 0x1089; on PVRSRV_OK its words to out1 and
    /// (when given) out2.</summary>
    [Hle("NID_804BA13D", Nid = 0x804BA13D, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User804BA13D(uint device, uint value, uint output1, uint output2)
    {
        return BridgeWithWord(ServicesOfDevice(device), 0x89, 8, output1);
    }

    /// <summary>NID_83D972DF (device, value, uint out[4]): bridge 0x108B, four words back on PVRSRV_OK.</summary>
    [Hle("NID_83D972DF", Nid = 0x83D972DF, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User83D972DF(uint device, uint value, uint output)
    {
        return ServicesBridgeResult(ServicesOfDevice(device), 0x8B, 8, 0x14);
    }

    /// <summary>NID_8A997DBB (device, const uint in[9], uint *out1, uint *out2): bridge 0x1084; on PVRSRV_OK its word
    /// to out1 and 0 to out2.</summary>
    [Hle("NID_8A997DBB", Nid = 0x8A997DBB, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User8A997DBB(uint device, uint input, uint output1, uint output2)
    {
        int result = BridgeWithWord(ServicesOfDevice(device), 0x84, 0x2C, output1);
        if (result == 0)
        {
            GuestMemory.W32(output2, 0);
        }

        return result;
    }

    /// <summary>NID_8ED7D698 (device, a, b): bridge 0x108E.</summary>
    [Hle("NID_8ED7D698", Nid = 0x8ED7D698, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User8ED7D698(uint device, uint first, uint second)
    {
        return ServicesBridgeResult(ServicesOfDevice(device), 0x8E, 0xC, 8);
    }

    /// <summary>NID_94286660 (device, uint *out): NULLs 3; bridge 0x1055, its word to out on PVRSRV_OK.</summary>
    [Hle("NID_94286660", Nid = 0x94286660, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User94286660(uint device, uint output)
    {
        return output == 0 || device == 0 ? PvrInvalidParams : BridgeWithWord(ServicesOfDevice(device), 0x55, 8, output);
    }

    /// <summary>NID_9D5C4326 (device): bridge 0x1093.</summary>
    [Hle("NID_9D5C4326", Nid = 0x9D5C4326, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User9D5C4326(uint device)
    {
        return ServicesBridgeResult(ServicesOfDevice(device), 0x93, 8, 8);
    }

    /// <summary>NID_AD20D1A0 (device, value, a, b): NULL device or value 0 3; bridge 0x101E.</summary>
    [Hle("NID_AD20D1A0", Nid = 0xAD20D1A0, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserAD20D1A0(uint device, uint value, uint first, uint second)
    {
        return value == 0 || device == 0 ? PvrInvalidParams : ServicesBridgeResult(ServicesOfDevice(device), 0x1E, 0x10, 8);
    }

    /// <summary>NID_B000CA65 (device, info *out): NULLs 3; bridge 0x105E (0x104 bytes back, copied on PVRSRV_OK).</summary>
    [Hle("NID_B000CA65", Nid = 0xB000CA65, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserB000CA65(uint device, uint output)
    {
        return output == 0 || device == 0 ? PvrInvalidParams : ServicesBridgeResult(ServicesOfDevice(device), 0x5E, 8, 0x104);
    }

    /// <summary>NID_B26DF3D2 (device, value): NULL device or value 0 3; bridge 0x1056.</summary>
    [Hle("NID_B26DF3D2", Nid = 0xB26DF3D2, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserB26DF3D2(uint device, uint value)
    {
        return value == 0 || device == 0 ? PvrInvalidParams : ServicesBridgeResult(ServicesOfDevice(device), 0x56, 8, 8);
    }

    /// <summary>NID_B6CFD213 (device, memory *memory): NULLs 3; the memory's +4 is cleared, bridge 0x1007 frees it
    /// on the device, then libgpu frees the record (on PVRSRV_OK).</summary>
    [Hle("NID_B6CFD213", Nid = 0xB6CFD213, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserB6CFD213(uint device, uint memory)
    {
        if (memory == 0 || device == 0)
        {
            return PvrInvalidParams;
        }

        GuestMemory.W32(memory + 4, 0);
        int result = ServicesBridgeResult(ServicesOfDevice(device), 0x07, 0x28, 8);
        if (result == 0)
        {
            GpuEs4UserHeapFree(memory);
        }

        return result;
    }

    /// <summary>NID_B9178277 (device, value): NULL device or value 0 3; bridge 0x1004.</summary>
    [Hle("NID_B9178277", Nid = 0xB9178277, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserB9178277(uint device, uint value)
    {
        return value == 0 || device == 0 ? PvrInvalidParams : ServicesBridgeResult(ServicesOfDevice(device), 0x04, 0xC, 8);
    }

    /// <summary>NID_BDE2B9E1 (device, value, const uint in[2], uint *out): bridge 0x108D, its word to out on PVRSRV_OK.</summary>
    [Hle("NID_BDE2B9E1", Nid = 0xBDE2B9E1, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserBDE2B9E1(uint device, uint value, uint input, uint output)
    {
        return BridgeWithWord(ServicesOfDevice(device), 0x8D, 0x14, output);
    }

    /// <summary>NID_D90B0707 (device, a, b): bridge 0x1087.</summary>
    [Hle("NID_D90B0707", Nid = 0xD90B0707, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserD90B0707(uint device, uint first, uint second)
    {
        return ServicesBridgeResult(ServicesOfDevice(device), 0x87, 0x10, 8);
    }

    /// <summary>NID_F0CCE9DB (device, const block[0x2F4], uint out[6]): bridge 0x108C, six words back on PVRSRV_OK.</summary>
    [Hle("NID_F0CCE9DB", Nid = 0xF0CCE9DB, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserF0CCE9DB(uint device, uint block, uint output)
    {
        return ServicesBridgeResult(ServicesOfDevice(device), 0x8C, 0x2FC, 0x1C);
    }

    /// <summary>NID_7AF40138 (uint size): a block of libgpu's heap (sceKernelAllocHeapMemory); NULL for 0 bytes.</summary>
    [Hle("NID_7AF40138", Nid = 0x7AF40138, Library = "SceGpuEs4User")]
    uint GpuEs4UserHeapAlloc(uint size)
    {
        if (size == 0)
        {
            return 0;
        }

        uint block = Emu.Memory.Alloc(size, "libgpu_es4 heap", MemoryRegion.User, 8);
        lock (_lock)
        {
            _userHeapBlocks.Add(block);
        }

        return block;
    }

    /// <summary>NID_1BE5985A (void *block): back to libgpu's heap (a block it did not hand out: nothing).</summary>
    [Hle("NID_1BE5985A", Nid = 0x1BE5985A, Library = "SceGpuEs4User")]
    void GpuEs4UserHeapFree(uint block)
    {
        lock (_lock)
        {
            if (!_userHeapBlocks.Remove(block))
            {
                return;
            }
        }

        Emu.Memory.Free(block);
    }

    /// <summary>NID_1A575DFD (uint *code): writes the two-word instruction { 0, 0xFA4C0700 }; the address after it.</summary>
    [Hle("NID_1A575DFD", Nid = 0x1A575DFD, Library = "SceGpuEs4User")]
    uint GpuEs4UserWriteNop(uint code)
    {
        GuestMemory.W32(code, 0);
        GuestMemory.W32(code + 4, 0xFA4C0700);
        return code + 8;
    }

    /// <summary>NID_53CACDAB (uint *code, const uint source[6], uint value): { 0, 0xFA440700 }, six two-word loads of the
    /// source words (the low 21 bits with the slot in bits 21..23; bits 21..25 and 26..31 of the word in the second
    /// word), then the two words built from value; the address after the 0x40 bytes.</summary>
    [Hle("NID_53CACDAB", Nid = 0x53CACDAB, Library = "SceGpuEs4User")]
    uint GpuEs4UserWriteLoads(uint code, uint source, uint value)
    {
        Span<uint> words = stackalloc uint[6];
        for (int i = 0; i < 6; i++)
        {
            words[i] = GuestMemory.R32(source + (uint)i * 4);
        }

        GuestMemory.W32(code, 0);
        GuestMemory.W32(code + 4, 0xFA440700);
        for (int i = 0; i < 6; i++)
        {
            uint word = words[i];
            uint slot = code + 8 + (uint)i * 8;
            GuestMemory.W32(slot, (word & 0x1FFFFF) | ((uint)i << 21));
            GuestMemory.W32(slot + 4, ((word >> 17) & 0x1F0) | ((word >> 26) << 12) | 0xFCA00000);
        }

        GuestMemory.W32(code + 0x38, ((value & 0x3F) << 22) | 0x200082);
        GuestMemory.W32(code + 0x3C, ((value & 0x3000) << 10) | ((value >> 3) & 0x1F8) | 0xFB240000);
        return code + 0x40;
    }

    /// <summary>NID_A5E331FA (state *state, void *buffer): the 0x48-byte program at the buffer's next 16-byte boundary,
    /// built from the state's words 2..5 (words 3, 6, 7, 10 and 11 left as they are); state[0] = the program, state[1] = 0x30; the address after it.</summary>
    [Hle("NID_A5E331FA", Nid = 0xA5E331FA, Library = "SceGpuEs4User")]
    uint GpuEs4UserWriteProgram(uint state, uint buffer)
    {
        uint program = (buffer + 15) & ~15u;
        GuestMemory.W32(program, 0x2000);
        GuestMemory.W32(program + 4, 0);
        GuestMemory.W32(program + 8, GuestMemory.R32(state + 16));
        GuestMemory.W32(program + 16, GuestMemory.R32(state + 20));
        GuestMemory.W32(program + 20, 0x100);
        GuestMemory.W32(program + 32, GuestMemory.R32(state + 8));
        GuestMemory.W32(program + 36, GuestMemory.R32(state + 12));
        GuestMemory.W32(program + 48, 0x070181A6);
        GuestMemory.W32(program + 52, 0x070581A6);
        GuestMemory.W32(program + 56, 0x070581A6);
        GuestMemory.W32(program + 60, 0x070581A6);
        GuestMemory.W32(program + 64, 0x070801A5);
        GuestMemory.W32(program + 68, 0xAF000000);
        GuestMemory.W32(state, program);
        GuestMemory.W32(state + 4, 0x30);
        return program + 0x48;
    }

    /// <summary>NID_86FC3202 (uint *code): copies libgpu's 32-byte code template (the export's +0x20 in 3.74); the
    /// address after it.</summary>
    [Hle("NID_86FC3202", Nid = 0x86FC3202, Library = "SceGpuEs4User")]
    uint GpuEs4UserWriteTemplate(uint code)
    {
        CopyLibgpuCode(0x86FC3202, 0x20, code, 0x20);
        return code + 0x20;
    }

    /// <summary>
    /// NID_E98914E4 (state *state, uint *program, int kind, int textured, int format): copies one of libgpu's five
    /// 0x90-byte program templates (3.74: the export's +0x950 untextured, +0xA48 / +0xB40 textured with format 2 /
    /// other for kind 0; +0x9B8 / +0xAB0 for format 2 / other otherwise), patches the state's words into it (word 10
    /// at +0x24 for kind 0, +0x30 otherwise) and sets state[1] = 0x30 / 0x40 and state[0] = the program's 16-byte
    /// aligned address; the address after the 0x90 bytes.
    /// </summary>
    [Hle("NID_E98914E4", Nid = 0xE98914E4, Library = "SceGpuEs4User")]
    uint GpuEs4UserWriteProgramTemplate(uint state, uint program, int kind, int textured, int format)
    {
        int offset = kind != 0 ? (format == 2 ? 0x9B8 : 0xAB0) : textured == 0 ? 0x950 : format == 2 ? 0xA48 : 0xB40;
        CopyLibgpuCode(0xE98914E4, offset, program, 0x90);
        GuestMemory.W32(state + 4, kind == 0 ? 0x30u : 0x40u);
        GuestMemory.W32(program, GuestMemory.R32(state + 8));
        GuestMemory.W32(program + 4, GuestMemory.R32(state + 12));
        GuestMemory.W32(program + 16, GuestMemory.R32(state + 20));
        GuestMemory.W32(program + 20, GuestMemory.R32(state + 24));
        GuestMemory.W32(program + 24, GuestMemory.R32(state + 28));
        GuestMemory.W32(program + 8, GuestMemory.R32(state + 32));
        GuestMemory.W32(program + 12, GuestMemory.R32(state + 36));
        GuestMemory.W32(program + (kind == 0 ? 36u : 48u), GuestMemory.R32(state + 40));
        GuestMemory.W32(state, (program + 15) & ~15u);
        return program + 0x90;
    }

    /// <summary>Copies <paramref name="length"/> bytes of libgpu's code segment at the export's +offset into guest
    /// memory; without the firmware module the bytes stay zero (logged once).</summary>
    void CopyLibgpuCode(uint nid, int offset, uint destination, int length)
    {
        byte[]? bytes = LibgpuCodeSource != null ? LibgpuCodeSource(nid, offset, length) : ReadLibgpuCode(nid, offset, length);
        if (bytes == null || bytes.Length < length)
        {
            Log.InfoOnce("libgpu-code", $"SceGpuEs4User: {Io.FirmwareLayout.UserModules.Guest("libgpu_es4.suprx")} not readable: its code templates are written as zeros (default)");
            GuestMemory.Fill(destination, 0, (uint)length);
            return;
        }

        bytes.AsSpan(0, length).CopyTo(GuestMemory.Span(destination, length));
    }

    byte[]? ReadLibgpuCode(uint nid, int offset, int length)
    {
        lock (_lock)
        {
            if (!_libgpuElfRead)
            {
                _libgpuElfRead = true;
                try
                {
                    if (Emu.Vfs.Resolve(Io.FirmwareLayout.UserModules.Guest("libgpu_es4.suprx")) is { } host && File.Exists(host))
                    {
                        _libgpuElf = SelfDecryptor.ToElf(File.ReadAllBytes(host), null);
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
                    or System.Security.Cryptography.CryptographicException)
                {
                    Log.Info($"SceGpuEs4User: {Io.FirmwareLayout.UserModules.Guest("libgpu_es4.suprx")}: {e.Message}");
                }
            }

            return _libgpuElf == null ? null : ExportRelativeBytes(_libgpuElf, nid, offset, length);
        }
    }

    /// <summary>The bytes at an export's address (Thumb bit cleared) + offset in a module ELF: the export tables of its
    /// sce_module_info (at e_entry in the first segment), the address mapped through the program headers.</summary>
    internal static byte[]? ExportRelativeBytes(byte[] elf, uint nid, int offset, int length)
    {
        ReadOnlySpan<byte> data = elf;
        if (data.Length < 0x34)
        {
            return null;
        }

        uint entry = BinaryPrimitives.ReadUInt32LittleEndian(data[0x18..]);
        uint programHeaders = BinaryPrimitives.ReadUInt32LittleEndian(data[0x1C..]);
        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data[0x2A..]);
        int headerCount = BinaryPrimitives.ReadUInt16LittleEndian(data[0x2C..]);
        var segments = new List<(uint Offset, uint Address, uint Size)>();
        for (int i = 0; i < headerCount; i++)
        {
            int header = (int)programHeaders + i * headerSize;
            if (header + 0x14 > data.Length)
            {
                return null;
            }

            segments.Add((BinaryPrimitives.ReadUInt32LittleEndian(data[(header + 4)..]), BinaryPrimitives.ReadUInt32LittleEndian(data[(header + 8)..]),
                BinaryPrimitives.ReadUInt32LittleEndian(data[(header + 0x10)..])));
        }

        if (segments.Count == 0)
        {
            return null;
        }

        long FileOffset(uint address)
        {
            foreach ((uint segmentOffset, uint segmentAddress, uint segmentSize) in segments)
            {
                if (address >= segmentAddress && address - segmentAddress < segmentSize)
                {
                    return segmentOffset + (address - segmentAddress);
                }
            }

            return -1;
        }

        uint first = segments[0].Offset;
        long info = first + (entry & 0x3FFFFFFF);
        if (info + 0x2C > data.Length)
        {
            return null;
        }

        uint exportsTop = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(info + 0x24)..]);
        uint exportsEnd = BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(info + 0x28)..]);
        for (long position = first + exportsTop; position + 0x20 <= first + exportsEnd && position + 0x20 <= data.Length;)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)position..]);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(position + 6)..]) + BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(position + 8)..]);
            long nids = FileOffset(BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(position + 0x18)..]));
            long addresses = FileOffset(BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(position + 0x1C)..]));
            for (int i = 0; nids >= 0 && addresses >= 0 && i < count && addresses + i * 4 + 4 <= data.Length; i++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(nids + i * 4)..]) != nid)
                {
                    continue;
                }

                uint address = (BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(addresses + i * 4)..]) & ~1u) + (uint)offset;
                long start = FileOffset(address);
                return start < 0 || start + length > data.Length ? null : data.Slice((int)start, length).ToArray();
            }

            if (size == 0)
            {
                break;
            }

            position += size;
        }

        return null;
    }

    /// <summary>
    /// NID_09BB98B2 (device, info *info): NULL info 3. An info of type 9 (+0) is answered by libgpu itself: with a
    /// memory record at +8, its info block (+0x74) words +0xC / +0x10 go to +0xC / +0x10, and the call answers 3 either
    /// way as 3.74 does; any other type goes to bridge 0x106C { cookie, info }.
    /// </summary>
    [Hle("NID_09BB98B2", Nid = 0x09BB98B2, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User09BB98B2(uint device, uint info)
    {
        if (info == 0)
        {
            return PvrInvalidParams;
        }

        if (GuestMemory.R32(info) != 9)
        {
            return ServicesBridgeResult(ServicesOfDevice(device), 0x6C, 0xC, 8);
        }

        uint memory = GuestMemory.R32(info + 8);
        if (memory != 0)
        {
            uint block = GuestMemory.R32(memory + 0x74);
            GuestMemory.W32(info + 0xC, GuestMemory.R32(block + 0xC));
            GuestMemory.W32(info + 0x10, GuestMemory.R32(block + 0x10));
        }

        return PvrInvalidParams;
    }

    /// <summary>NID_768482F6 (device, uint out[9]): bridge 0x1091, nine words back on PVRSRV_OK.</summary>
    [Hle("NID_768482F6", Nid = 0x768482F6, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User768482F6(uint device, uint output)
    {
        return ServicesBridgeResult(ServicesOfDevice(device), 0x91, 4, 0x28);
    }

    /// <summary>NID_9A4A5DAE (device, value, uint out[3]): NULLs 3; bridge 0x105C, three words back on PVRSRV_OK.</summary>
    [Hle("NID_9A4A5DAE", Nid = 0x9A4A5DAE, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User9A4A5DAE(uint device, uint value, uint output)
    {
        return output == 0 || device == 0 ? PvrInvalidParams : ServicesBridgeResult(ServicesOfDevice(device), 0x5C, 8, 0x10);
    }

    /// <summary>NID_9E8E2957 (device, value, uint *out): bridge 0x108A, its word to out on PVRSRV_OK.</summary>
    [Hle("NID_9E8E2957", Nid = 0x9E8E2957, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4User9E8E2957(uint device, uint value, uint output)
    {
        return BridgeWithWord(ServicesOfDevice(device), 0x8A, 8, output);
    }

    /// <summary>NID_AAD85460 (services **record, value, reserved): a NULL record or services, or reserved not 0, 3;
    /// bridge 0x1053 { value }.</summary>
    [Hle("NID_AAD85460", Nid = 0xAAD85460, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserAAD85460(uint record, uint value, uint reserved)
    {
        uint services = ServicesOfConnection(record);
        return record == 0 || services == 0 || reserved != 0 ? PvrInvalidParams : ServicesBridgeResult(services, 0x53, 8, 8);
    }

    /// <summary>NID_C0D16CE5 (device, info *out): bridge 0x1092 { cookie } (0x120 bytes back, copied on PVRSRV_OK).</summary>
    [Hle("NID_C0D16CE5", Nid = 0xC0D16CE5, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserC0D16CE5(uint device, uint output)
    {
        return ServicesBridgeResult(ServicesOfDevice(device), 0x92, 4, 0x124);
    }

    /// <summary>NID_CCEE0336 (device, value, info *out): bridge 0x1088 (0x4C bytes back, copied on PVRSRV_OK).</summary>
    [Hle("NID_CCEE0336", Nid = 0xCCEE0336, Library = "SceGpuEs4User", Default = true)]
    int GpuEs4UserCCEE0336(uint device, uint value, uint output)
    {
        return ServicesBridgeResult(ServicesOfDevice(device), 0x88, 8, 0x50);
    }
}
