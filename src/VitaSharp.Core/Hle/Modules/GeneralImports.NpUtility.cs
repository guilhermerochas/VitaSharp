using System.Text;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// Exports without public names that no installed module imports (the "General" group) of SceNpUtility (FW 3.74
// np_utility read in its code, study only): its text and number helpers, record set-ups, flag checks, worker and
// request plumbing.

public sealed partial class NpManager
{
    internal int XmlUnescape(uint destination, uint size, uint source, uint length, uint outLength) => NpCommonXmlUnescapeThroughEntry(destination, size, source, length, outLength);

    internal int XmlEscape(uint destination, uint size, uint source, uint length, uint outLength) => NpCommonXmlEscapeThroughEntry(destination, size, source, length, outLength);

    internal int DebugFlag() => NpCommonDebugFlag1ThroughEntry();

    internal int SystemSwVersionString(uint buffer) => sceNpCommonGetSystemSwVersionStringThroughEntry(buffer);
}

public sealed partial class NpUtility
{
    const int UTIL_FLAG_MISSING = unchecked((int)0x80550708),
        UTIL_BAD_KIND = unchecked((int)0x80550782), COMMON_NULL = unchecked((int)0x80550601), COMMON_BUSY = unchecked((int)0x8055060E),
        COMMON_FULL = unchecked((int)0x80550602);

    // ---- numbers and texts

    /// <summary>NID_4D52A224 (char *out, int value): the decimal digits of a signed value ('-' first), no terminator
    /// (the caller's buffer is cleared).</summary>
    [Hle("NID_4D52A224", Nid = 0x4D52A224, Library = "SceNpUtility")]
    int UtilFormatInt(uint output, int value) => WriteDigits(output, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>NID_FF9F1571 (char *out, ?, int64 value) / NID_2E4D8BCF (char *out, ?, uint64 value): the same for 64-bit
    /// values.</summary>
    [Hle("NID_FF9F1571", Nid = 0xFF9F1571, Library = "SceNpUtility")]
    int UtilFormatInt64(uint output, uint unused, long value) => WriteDigits(output, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Hle("NID_2E4D8BCF", Nid = 0x2E4D8BCF, Library = "SceNpUtility")]
    int UtilFormatUInt64(uint output, uint unused, ulong value) => WriteDigits(output, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    static int WriteDigits(uint output, string digits)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(digits);
        bytes.CopyTo(GuestMemory.Span(output, bytes.Length));
        return 0;
    }

    /// <summary>NID_BDFC2D58 (const char *text): the signed decimal value of up to 20 leading digits ('-' allowed);
    /// NULL is 0.</summary>
    [Hle("NID_BDFC2D58", Nid = 0xBDFC2D58, Library = "SceNpUtility")]
    long UtilParseInt64(uint text)
    {
        if (text == 0)
        {
            return 0;
        }

        string s = GuestMemory.ReadCString(text, 64);
        int start = s.StartsWith('-') ? 1 : 0;
        ulong value = 0;
        for (int i = start; i < s.Length && i < 20 && char.IsAsciiDigit(s[i]); i++)
        {
            value = unchecked(value * 10 + (ulong)(s[i] - '0'));
        }

        return start == 1 ? unchecked(-(long)value) : unchecked((long)value);
    }

    /// <summary>NID_475B35A8 (const char *onlineId): an online id's form: 3..16 characters, a letter first, then
    /// letters, digits, '-' or '_' (1), else 0.</summary>
    [Hle("NID_475B35A8", Nid = 0x475B35A8, Library = "SceNpUtility")]
    int UtilOnlineIdValid(uint text)
    {
        if (text == 0)
        {
            return 0;
        }

        string s = GuestMemory.ReadCString(text, 32);
        if (s.Length is < 3 or > 16 || !char.IsAsciiLetter(s[0]))
        {
            return 0;
        }

        foreach (char c in s.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return 0;
            }
        }

        return 1;
    }

    /// <summary>NID_89484A9C (a, b): answers b (the module's error mapping of an HTTP error leaves it as it is).</summary>
    [Hle("NID_89484A9C", Nid = 0x89484A9C, Library = "SceNpUtility")]
    int UtilMapHttpError(uint a, int error) => error;

    /// <summary>NID_8CA24D19 (uint size, int slot): a block of a slot's heap (here the guest heap); NID_74B0D0AC (slot):
    /// the heap's reset; NID_E916C86A (list, slot): frees a list linked at +0xC.</summary>
    [Hle("NID_8CA24D19", Nid = 0x8CA24D19, Library = "SceNpUtility")]
    uint UtilHeapAlloc(uint size, int slot) => size == 0 ? 0 : Emu.Memory.Alloc(size, "np utility heap");

    [Hle("NID_74B0D0AC", Nid = 0x74B0D0AC, Library = "SceNpUtility", Default = true)]
    int UtilHeapResetSlot(int slot) => 0;

    [Hle("NID_E916C86A", Nid = 0xE916C86A, Library = "SceNpUtility")]
    int UtilFreeList(uint list, int slot)
    {
        while (list != 0)
        {
            uint next = GuestMemory.R32(list + 0xC);
            UtilHeapFreeThroughEntry(list, slot);
            list = next;
        }

        return 0;
    }

    /// <summary>NID_3A1BA624 (char **out, const void *data, uint size, int slot): the base64 text of the data in a new
    /// heap block.</summary>
    [Hle("NID_3A1BA624", Nid = 0x3A1BA624, Library = "SceNpUtility")]
    int UtilBase64(uint output, uint data, uint size, int slot)
    {
        string text = Convert.ToBase64String(GuestMemory.Span(data, (int)size));
        uint block = UtilHeapAllocThroughEntry((uint)text.Length + 1, slot);
        GuestMemory.WriteCString(block, text, text.Length + 1);
        GuestMemory.W32(output, block);
        return 0;
    }

    /// <summary>NID_15D37FB1 / NID_B8DFA4D1 (char **out, const char *text, int slot): the text with its XML entities
    /// decoded / encoded (SceNpCommon's helpers) in a new heap block (*out NULL on an error).</summary>
    [Hle("NID_15D37FB1", Nid = 0x15D37FB1, Library = "SceNpUtility")]
    int UtilXmlUnescape(uint output, uint text, int slot) => TransformText(output, text, slot, Np.XmlUnescape, false);

    [Hle("NID_B8DFA4D1", Nid = 0xB8DFA4D1, Library = "SceNpUtility")]
    int UtilXmlEscape(uint output, uint text, int slot) => TransformText(output, text, slot, Np.XmlEscape, true);

    int TransformText(uint output, uint text, int slot, Func<uint, uint, uint, uint, uint, int> transform, bool emptyAllowed)
    {
        uint length = (uint)GuestMemory.ReadCString(text, 0x100000).Length;
        if (length == 0 && emptyAllowed)
        {
            uint empty = UtilHeapAllocThroughEntry(1, slot);
            GuestMemory.Write<byte>(empty, 0);
            GuestMemory.W32(output, empty);
            return 0;
        }

        uint needed = Emu.Memory.Alloc(4, "np utility size");
        try
        {
            int result = transform(0, 0, text, length, needed);
            if (result < 0)
            {
                GuestMemory.W32(output, 0);
                return result;
            }

            uint size = GuestMemory.R32(needed);
            uint block = UtilHeapAllocThroughEntry(size, slot);
            GuestMemory.Fill(block, 0, size);
            result = transform(block, size, text, length, 0);
            GuestMemory.W32(output, result < 0 ? 0 : block);
            return result < 0 ? result : 0;
        }
        finally
        {
            Emu.Memory.Free(needed);
        }
    }

    /// <summary>NID_72FC55A7 (int request, uint length): the "Sce-Np-Info-Length" header of an HTTP request.</summary>
    [Hle("NID_72FC55A7", Nid = 0x72FC55A7, Library = "SceNpUtility")]
    int UtilAddInfoLengthHeader(int request, uint length)
    {
        uint name = Emu.Memory.Alloc(32, "np utility header");
        try
        {
            GuestMemory.WriteCString(name, "Sce-Np-Info-Length", 32);
            GuestMemory.WriteCString(name + 20, length.ToString(System.Globalization.CultureInfo.InvariantCulture), 12);
            return Emu.Hle.Modules.OfType<Http>().First().sceHttpAddRequestHeaderThroughEntry(request, name, name + 20, 0);
        }
        finally
        {
            Emu.Memory.Free(name);
        }
    }

    /// <summary>NID_2835BC9B () / NID_450C80FB (char buf[8]): SceNpCommon's debug flag / system version text.</summary>
    [Hle("NID_2835BC9B", Nid = 0x2835BC9B, Library = "SceNpUtility")]
    int UtilDebugFlag() => Np.DebugFlag();

    [Hle("NID_450C80FB", Nid = 0x450C80FB, Library = "SceNpUtility")]
    int UtilSystemVersion(uint buffer) => Np.SystemSwVersionString(buffer);

    /// <summary>NID_191D314C (): waits one second between retries (the HLE call returns at once: a default).</summary>
    [Hle("NID_191D314C", Nid = 0x191D314C, Library = "SceNpUtility", Default = true)]
    int UtilRetryWait() => 0;

    /// <summary>NID_37AD71B9 (): libhttp's pool must keep 8 KiB free (0x80431022); the HLE pool always has room.</summary>
    [Hle("NID_37AD71B9", Nid = 0x37AD71B9, Library = "SceNpUtility")]
    int UtilHttpPoolCheck() => 0;

    // ---- record set-ups and flag checks of the managers' requests

    /// <summary>NID_5D75C414 / NID_AB499EA4 / NID_A9D5542A / NID_F32ED701 / NID_A3767D45 (record): a field's flag bit
    /// (+4 bit 2, +0x18 bit 6, +0x10 bit 3, +0x10 bit 10, +0x14 bit 10) set: 0, else 0x80550708.</summary>
    [Hle("NID_5D75C414", Nid = 0x5D75C414, Library = "SceNpUtility")]
    int UtilFlag5D75C414(uint record) => FlagCheck(record, 4, 4);

    [Hle("NID_AB499EA4", Nid = 0xAB499EA4, Library = "SceNpUtility")]
    int UtilFlagAB499EA4(uint record) => FlagCheck(record, 0x18, 0x40);

    [Hle("NID_A9D5542A", Nid = 0xA9D5542A, Library = "SceNpUtility")]
    int UtilFlagA9D5542A(uint record) => FlagCheck(record, 0x10, 8);

    [Hle("NID_F32ED701", Nid = 0xF32ED701, Library = "SceNpUtility")]
    int UtilFlagF32ED701(uint record) => FlagCheck(record, 0x10, 0x400);

    [Hle("NID_A3767D45", Nid = 0xA3767D45, Library = "SceNpUtility")]
    int UtilFlagA3767D45(uint record) => FlagCheck(record, 0x14, 0x400);

    static int FlagCheck(uint record, uint offset, uint bit) => (GuestMemory.R32(record + offset) & bit) != 0 ? 0 : UTIL_FLAG_MISSING;

    /// <summary>NID_575BBBC4 (worker, a, b): two words of a worker (+0x1C, +0x20); NULL 0x80550601.</summary>
    [Hle("NID_575BBBC4", Nid = 0x575BBBC4, Library = "SceNpUtility")]
    int UtilWorkerSetWords(uint worker, uint a, uint b)
    {
        if (worker == 0)
        {
            return COMMON_NULL;
        }

        GuestMemory.W32(worker + 0x1C, a);
        GuestMemory.W32(worker + 0x20, b);
        return 0;
    }

    /// <summary>
    /// The managers' response parsers: their record (the utility's XML reader record, NID_31423835) is set up with
    /// the response's schema and np_utility's own callback, code of the firmware module that is not in guest memory
    /// here; the record's fields are set as the module sets them and the reader gets no schema (it then reports no
    /// element: a default). NID_04AA56CB / NID_2580CDAF / NID_67147C73 / NID_839F805E (record, response) and
    /// NID_15227842 (record, kind 1..2 else 0x80550782, value, response).
    /// </summary>
    [Hle("NID_04AA56CB", Nid = 0x04AA56CB, Library = "SceNpUtility", Default = true)]
    int UtilParser04AA56CB(uint record, uint response)
    {
        GuestMemory.W32(response + 0x20, 0xFFFFFFFF);
        GuestMemory.W32(response + 0x1C, 0);
        GuestMemory.W32(response + 0xC, 0);
        GuestMemory.W32(response + 0x14, 0);
        return UtilInitRecordThroughEntry(record, 0, 0, 0);
    }

    [Hle("NID_2580CDAF", Nid = 0x2580CDAF, Library = "SceNpUtility", Default = true)]
    int UtilParser2580CDAF(uint record, uint response)
    {
        GuestMemory.W32(response + 4, 0);
        return UtilInitRecordThroughEntry(record, 0, 0, 0);
    }

    [Hle("NID_67147C73", Nid = 0x67147C73, Library = "SceNpUtility", Default = true)]
    int UtilParser67147C73(uint record, uint response)
    {
        GuestMemory.W32(response + 0xC, 0);
        GuestMemory.W32(response + 0x10, 0);
        return UtilInitRecordThroughEntry(record, 0, 0, 0);
    }

    [Hle("NID_839F805E", Nid = 0x839F805E, Library = "SceNpUtility", Default = true)]
    int UtilParser839F805E(uint record, uint value, uint response)
    {
        GuestMemory.W32(response, value);
        GuestMemory.W32(response + 0x10, 0);
        uint body = GuestMemory.R32(response + 4);
        if (body != 0)
        {
            GuestMemory.Fill(body, 0, 0x1C8);
            GuestMemory.W32(body + 0xE0, 0xFFFFFFFF);
            GuestMemory.W32(body + 0xE4, 0xFFFFFFFF);
            GuestMemory.W32(body + 0xDC, 0xFFFFFFFF);
        }

        return UtilInitRecordThroughEntry(record, 0, 0, response);
    }

    [Hle("NID_15227842", Nid = 0x15227842, Library = "SceNpUtility", Default = true)]
    int UtilParser15227842(uint record, int kind, uint value, uint response)
    {
        GuestMemory.W32(response + 4, value);
        GuestMemory.W32(response, (uint)kind);
        GuestMemory.W32(response + 0x18, 0);
        uint target = GuestMemory.R32(response + 8);
        if (target != 0)
        {
            GuestMemory.W32(target, 0);
        }

        GuestMemory.W32(response + 0xC, 0);
        return kind is 1 or 2 ? UtilInitRecordThroughEntry(record, 0, 0, 0) : UTIL_BAD_KIND;
    }

    /// <summary>NID_C02139E5 / NID_E9544993 (kind, ...): the parsers' element callbacks (kind 4: a text element)
    /// writing into the response; without a schema they are never reached: 0.</summary>
    [Hle("NID_C02139E5", Nid = 0xC02139E5, Library = "SceNpUtility", Default = true)]
    int UtilParserCallbackC02139E5(int kind, uint name, uint a, uint b, uint c, uint d, uint e) => 0;

    [Hle("NID_E9544993", Nid = 0xE9544993, Library = "SceNpUtility", Default = true)]
    int UtilParserCallbackE9544993(int kind, int level, int index, uint name, uint value) => 0;

    /// <summary>NID_C01F7C6A (record, data, size): the utility's copy of the XML reader (SceNpCommon NID_88B18A5B).</summary>
    [Hle("NID_C01F7C6A", Nid = 0xC01F7C6A, Library = "SceNpUtility")]
    int UtilXmlParseCopy(uint record, uint data, int size) => Np.XmlParse(record, data, size);

    // ---- the lookup worker (a thread, its event flag and lock, a queue of 16-byte jobs)

    /// <summary>NID_6EB4C8FE (worker, priority, stack, affinity, uint jobs, alloc, free) / NID_44FE1097 (worker, four
    /// words: a job) / NID_F73C4411 (worker: start) / NID_5631E596 (worker: stop): the worker is not run by the HLE
    /// (its thread function is the firmware module's): NULL 0x80550601, no allocator 0x8055060E; set-up, start and
    /// stop are accepted, a job queues nothing (a default).</summary>
    [Hle("NID_6EB4C8FE", Nid = 0x6EB4C8FE, Library = "SceNpUtility", Default = true)]
    int UtilWorkerCreate(uint worker, int priority, uint stack, int affinity, uint jobs, uint alloc, uint free)
    {
        if (worker == 0)
        {
            return COMMON_NULL;
        }

        GuestMemory.Fill(worker, 0, 0x28);
        if (alloc == 0)
        {
            GuestMemory.W32(worker, 0xFFFFFFFF);
            GuestMemory.W32(worker + 4, 0xFFFFFFFF);
            return COMMON_BUSY;
        }

        GuestMemory.W32(worker, 1);
        GuestMemory.W32(worker + 4, 1);
        GuestMemory.W32(worker + 0xC, jobs);
        GuestMemory.W32(worker + 0x14, alloc);
        GuestMemory.W32(worker + 0x18, free);
        return 0;
    }

    static bool WorkerReady(uint worker) => worker != 0 && GuestMemory.R32(worker) != 0xFFFFFFFF && GuestMemory.R32(worker + 4) != 0xFFFFFFFF;

    [Hle("NID_44FE1097", Nid = 0x44FE1097, Library = "SceNpUtility", Default = true)]
    int UtilWorkerQueue(uint worker, uint a, uint b, uint c, uint d)
    {
        if (!WorkerReady(worker))
        {
            return COMMON_NULL;
        }

        return GuestMemory.R32(worker + 0x10) >= GuestMemory.R32(worker + 0xC) ? COMMON_FULL : 0;
    }

    [Hle("NID_F73C4411", Nid = 0xF73C4411, Library = "SceNpUtility", Default = true)]
    int UtilWorkerStart(uint worker) => WorkerReady(worker) ? 0 : COMMON_NULL;

    [Hle("NID_5631E596", Nid = 0x5631E596, Library = "SceNpUtility", Default = true)]
    int UtilWorkerStop(uint worker)
    {
        if (!WorkerReady(worker))
        {
            return COMMON_NULL;
        }

        GuestMemory.Fill(worker, 0, 0x28);
        GuestMemory.W32(worker, 0xFFFFFFFF);
        GuestMemory.W32(worker + 4, 0xFFFFFFFF);
        return 0;
    }

    // ---- the managers' request plumbing: no requests are kept in HLE (the lookups answer 0x8055070E)

    [Hle("NID_8904FE15", Nid = 0x8904FE15, Library = "SceNpUtility", Default = true)]
    int UtilRequestState(uint id, uint state) => UnknownRequest("8904FE15");

    [Hle("NID_F5086697", Nid = 0xF5086697, Library = "SceNpUtility", Default = true)]
    int UtilRequestSetState(uint id, uint state) => UnknownRequest("F5086697");

    [Hle("NID_D03605CD", Nid = 0xD03605CD, Library = "SceNpUtility", Default = true)]
    int UtilFindManagedRequest(uint id, uint request) => UnknownRequest("D03605CD");

    [Hle("NID_F32ED20E", Nid = 0xF32ED20E, Library = "SceNpUtility", Default = true)]
    int UtilFindSlotRequestByKey(int key, uint request, int slot)
    {
        if (slot < UtilSlots && request != 0)
        {
            GuestMemory.W32(request, 0);
        }

        return UnknownRequest("F32ED20E");
    }

    [Hle("NID_D5BB8464", Nid = 0xD5BB8464, Library = "SceNpUtility", Default = true)]
    int UtilDeleteRequestOn(uint id, uint slot) => slot >= UtilSlots ? UTIL_INVALID_ARGUMENT : UnknownRequest("D5BB8464");

    [Hle("NID_C63A0F6B", Nid = 0xC63A0F6B, Library = "SceNpUtility", Default = true)]
    int UtilAbortHttpRequest(uint id) => UnknownRequest("C63A0F6B");

    [Hle("NID_B4423DAF", Nid = 0xB4423DAF, Library = "SceNpUtility", Default = true)]
    int UtilDeleteRequestB4423DAF(uint id, uint slot) => UtilDeleteRequestOnThroughEntry(id, slot);

    /// <summary>NID_46543307 / NID_A15150C9 (request, item): appends an item to a request's list (NULL request
    /// 0x80550704 for the second; no item: 0); NID_75FD803B (request): drops a request reference; NID_200BBAC5 (slot):
    /// the slot manager's cleanup.</summary>
    [Hle("NID_46543307", Nid = 0x46543307, Library = "SceNpUtility", Default = true)]
    int UtilRequestAddItem(uint request, uint item) => 0;

    [Hle("NID_A15150C9", Nid = 0xA15150C9, Library = "SceNpUtility", Default = true)]
    int UtilRequestAddItem2(uint request, uint item) => request == 0 ? UTIL_NOT_EMPTY : 0;

    [Hle("NID_75FD803B", Nid = 0x75FD803B, Library = "SceNpUtility", Default = true)]
    int UtilRequestRelease(uint request) => 0;

    [Hle("NID_200BBAC5", Nid = 0x200BBAC5, Library = "SceNpUtility", Default = true)]
    int UtilSlotCleanup(int slot) => 0;

    /// <summary>NID_23414285 (context, a, b, c, d): queues a callout on a context's callout list (+0x60).</summary>
    [Hle("NID_23414285", Nid = 0x23414285, Library = "SceNpUtility", Default = true)]
    int UtilQueue23414285(uint context, uint a, uint b, uint c, uint d) => 0;

    /// <summary>The managers' server requests (lookups of profiles and avatars, title storage, presence, word
    /// filter): they build an XML or JSON body, look the request id up and send it; no requests are kept and no
    /// server can be reached: 0x8055070E or the network's error (defaults).</summary>
    [Hle("NID_1392F18F", Nid = 0x1392F18F, Library = "SceNpUtility", Default = true)]
    int UtilServer1392F18F(uint id, uint a, uint b, uint c, uint d, uint e) => UnknownRequest("1392F18F");

    [Hle("NID_4B30CAEF", Nid = 0x4B30CAEF, Library = "SceNpUtility", Default = true)]
    int UtilServer4B30CAEF(uint id, uint a, uint b, uint c, uint d, uint e) => UnknownRequest("4B30CAEF");

    [Hle("NID_9EEDD7F8", Nid = 0x9EEDD7F8, Library = "SceNpUtility", Default = true)]
    int UtilServer9EEDD7F8(uint id, uint a, uint b, uint c, uint d, uint e) => UnknownRequest("9EEDD7F8");

    [Hle("NID_B418204E", Nid = 0xB418204E, Library = "SceNpUtility", Default = true)]
    int UtilServerB418204E(uint id, uint a) => UnknownRequest("B418204E");

    [Hle("NID_60D70B4A", Nid = 0x60D70B4A, Library = "SceNpUtility", Default = true)]
    int UtilServer60D70B4A(uint id, uint a, uint b, uint c, uint d, uint e, uint f, uint g) => UnknownRequest("60D70B4A");

    [Hle("NID_DFFF438A", Nid = 0xDFFF438A, Library = "SceNpUtility", Default = true)]
    int UtilServerDFFF438A(uint a, uint b, uint c, uint d, uint e, uint f, uint g, uint h)
    {
        if (e != 0)
        {
            GuestMemory.W32(e, 0);
        }

        return UnknownRequest("DFFF438A");
    }

    [Hle("NID_8857763D", Nid = 0x8857763D, Library = "SceNpUtility", Default = true)]
    int UtilServer8857763D(uint id, uint a, uint b, uint c, uint d, uint e, uint f) => UnknownRequest("8857763D");

    [Hle("NID_8951DA6B", Nid = 0x8951DA6B, Library = "SceNpUtility", Default = true)]
    int UtilServer8951DA6B(uint a, uint b, uint npId, uint c, uint d, int slot) => PsnAccount.Current.ServerError;

    [Hle("NID_C4FE3A57", Nid = 0xC4FE3A57, Library = "SceNpUtility", Default = true)]
    int UtilServerC4FE3A57(uint a, uint kind, uint b, uint c) => PsnAccount.Current.ServerError;

    [Hle("NID_A48B44C9", Nid = 0xA48B44C9, Library = "SceNpUtility", Default = true)]
    int UtilServerA48B44C9(uint request, uint a, uint b, uint c, uint d) => PsnAccount.Current.ServerError;

    [Hle("NID_4FAC4566", Nid = 0x4FAC4566, Library = "SceNpUtility", Default = true)]
    int UtilServer4FAC4566(uint id, uint a, uint b, uint c, uint d, uint e, uint f) => UnknownRequest("4FAC4566");

    [Hle("NID_83445FED", Nid = 0x83445FED, Library = "SceNpUtility", Default = true)]
    int UtilServer83445FED(uint buffer, uint size, uint a) => PsnAccount.Current.ServerError;

    [Hle("NID_60CC5AFB", Nid = 0x60CC5AFB, Library = "SceNpUtility", Default = true)]
    int UtilServer60CC5AFB(uint a, int kind, uint b, uint c, uint d, uint result, uint e)
    {
        if (result != 0)
        {
            GuestMemory.W32(result, 0);
        }

        return 0;
    }
}
