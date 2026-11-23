using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// np_common.suprx 3.74 (decompiled, study only): the library heap's own create/destroy (NID_619FB198, NID_622F004B;
/// the A385C8CD/BA6310A1 pair calls them) and the UTF-8 text checks the NP modules use. A character is one valid
/// UTF-8 sequence: an ASCII byte other than the control characters (all of 0x01..0x1F except tab, line feed and
/// carriage return, and 0x7F), 0xC2..0xDF plus one continuation byte, 0xE0..0xEF plus two, or 0xF0..0xF7 plus three;
/// anything else is 0x80550608, a NULL text 0x80550601.
/// </summary>
public sealed partial class NpManager
{
    const int COMMON_INVALID_TEXT = unchecked((int)0x80550608);

    /// <summary>NID_619FB198(size, base): the library heap made on <paramref name="memory"/> (sceClibMspaceCreate,
    /// no checks); 0.</summary>
    [Hle(Nid = 0x619FB198, Library = "SceNpCommon")]
    int NID_619FB198(uint size, uint memory)
    {
        _commonHeap = ClibModule.sceClibMspaceCreateThroughEntry(memory, size);
        return 0;
    }

    /// <summary>NID_622F004B(): the library heap destroyed (sceClibMspaceDestroy).</summary>
    [Hle(Nid = 0x622F004B, Library = "SceNpCommon")]
    void NID_622F004B()
    {
        if (_commonHeap != 0)
        {
            ClibModule.sceClibMspaceDestroyThroughEntry(_commonHeap);
        }

        _commonHeap = 0;
    }

    /// <summary>NID_73BDFF32(text): the number of UTF-8 characters up to the terminating NUL.</summary>
    [Hle(Nid = 0x73BDFF32, Library = "SceNpCommon")]
    int NID_73BDFF32(uint text)
    {
        if (text == 0)
        {
            return COMMON_NULL;
        }

        int count = 0;
        while (GuestMemory.Read<byte>(text) != 0)
        {
            int length = Utf8CharLength(text);
            if (length == 0)
            {
                return COMMON_INVALID_TEXT;
            }

            text += (uint)length;
            count++;
        }

        return count;
    }

    /// <summary>NID_A04C77A1(count, text, const char **end): steps over at most <paramref name="count"/> UTF-8
    /// characters (stopping at the NUL) and gives where it stopped.</summary>
    [Hle(Nid = 0xA04C77A1, Library = "SceNpCommon")]
    int NID_A04C77A1(uint count, uint text, uint endOut)
    {
        if (text == 0)
        {
            return COMMON_NULL;
        }

        for (; count != 0 && GuestMemory.Read<byte>(text) != 0; count--)
        {
            int length = Utf8CharLength(text);
            if (length == 0)
            {
                return COMMON_INVALID_TEXT;
            }

            text += (uint)length;
        }

        if (endOut != 0)
        {
            GuestMemory.W32(endOut, text);
        }

        return 0;
    }

    /// <summary>The bytes of the UTF-8 character at <paramref name="text"/> (not NUL), 0 when it is not one np_common
    /// accepts.</summary>
    static int Utf8CharLength(uint text)
    {
        byte lead = GuestMemory.Read<byte>(text);
        if ((lead & 0x80) == 0)
        {
            bool control = lead < 0x20 && lead is not (0x09 or 0x0A or 0x0D);
            return control || lead == 0x7F ? 0 : 1;
        }

        if ((lead & 0xE0) == 0xC0 && lead > 0xC1 && IsContinuation(text + 1))
        {
            return 2;
        }

        if ((lead & 0xF0) == 0xE0 && IsContinuation(text + 1) && IsContinuation(text + 2))
        {
            return 3;
        }

        if ((lead & 0xF8) == 0xF0 && IsContinuation(text + 1) && IsContinuation(text + 2) && IsContinuation(text + 3))
        {
            return 4;
        }

        return 0;
    }

    static bool IsContinuation(uint address) => (GuestMemory.Read<byte>(address) & 0xC0) == 0x80;
}
