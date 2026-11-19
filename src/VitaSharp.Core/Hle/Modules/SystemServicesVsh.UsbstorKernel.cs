using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceUsbstorForDriver (os0:kd/usbstor.skprx SceUsbstorDriver 3.74, read in the module, study only): the USB mass
// storage function the Vita shows a PC (SceUsbstorVStorDriver and SceUsbstorMg plug their command handlers in). The
// module keeps up to eight handlers ordered by priority (byte 3; flags in byte 0, bits 0x80/0x20/0x10/0x08 refused;
// the word at +4 needed: else 0x80244103; a ninth 0x80244104, the same one twice 0x80244102, removing one not there
// 0x80244105), the maximum LUN (0..6, settable while the function is started and idle, else 0x8024410C), and per LUN
// (0..2 fit the module's 0x200-byte table) the inquiry strings, read back with the non-printable bytes as spaces. The
// data entries move a command's data through the USB device controller; the emulated unit is never plugged into a PC,
// so no command is in flight and a transfer answers the module's transfer error (0x80244109); size 0 moves nothing.
public sealed partial class SystemServicesVsh
{
    const int UsbstorHandlers = 8, UsbstorLunBytes = 0x9C, UsbstorLunTable = 0x200;

    /// <summary>SceUsbstor: an invalid argument (0x80244103).</summary>
    const int USBSTOR_ERR_INVALID = unchecked((int)0x80244103);

    /// <summary>SceUsbstor: the handler is registered already (0x80244102).</summary>
    const int USBSTOR_ERR_REGISTERED = unchecked((int)0x80244102);

    /// <summary>SceUsbstor: eight handlers are registered (0x80244104).</summary>
    const int USBSTOR_ERR_FULL = unchecked((int)0x80244104);

    /// <summary>SceUsbstor: the handler is not registered (0x80244105).</summary>
    const int USBSTOR_ERR_NOT_REGISTERED = unchecked((int)0x80244105);

    /// <summary>SceUsbstor: the transfer failed, no command in flight (0x80244109).</summary>
    const int USBSTOR_ERR_TRANSFER = unchecked((int)0x80244109);

    [SnapshotMissing] // states of builds before it: no handler, LUN 0, no inquiry strings
    readonly uint[] _usbstorHandlers = new uint[UsbstorHandlers];

    [SnapshotMissing]
    int _usbstorHandlerCount;

    [SnapshotMissing]
    uint _usbstorMaxLun;

    [SnapshotMissing]
    readonly byte[] _usbstorLuns = new byte[UsbstorLunTable];

    /// <summary>The registered handlers in order, for the tests.</summary>
    internal uint[] UsbstorHandlerOrder => _usbstorHandlers[.._usbstorHandlerCount];

    /// <summary>NID_C0944A04 (SceUsbstorForDriver)(SceUsbstorHandler *handler): inserted by priority (after the ones
    /// of the same priority; priority 0xFF last); its flags (0).</summary>
    [Hle("NID_C0944A04", Nid = 0xC0944A04, Library = "SceUsbstorForDriver")]
    int UsbstorRegisterHandler(uint handler)
    {
        if (handler == 0 || GuestMemory.R32(handler + 4) == 0 || (GuestMemory.Read<byte>(handler) & 0xB8) != 0)
        {
            return USBSTOR_ERR_INVALID;
        }

        if (_usbstorHandlerCount >= UsbstorHandlers)
        {
            return USBSTOR_ERR_FULL;
        }

        if (Array.IndexOf(_usbstorHandlers, handler, 0, _usbstorHandlerCount) >= 0)
        {
            return USBSTOR_ERR_REGISTERED;
        }

        byte priority = GuestMemory.Read<byte>(handler + 3);
        int at = _usbstorHandlerCount;
        if (priority != 0xFF)
        {
            for (int index = 0; index < _usbstorHandlerCount; index++)
            {
                if (priority < GuestMemory.Read<byte>(_usbstorHandlers[index] + 3))
                {
                    at = index;
                    break;
                }
            }
        }

        Array.Copy(_usbstorHandlers, at, _usbstorHandlers, at + 1, _usbstorHandlerCount - at);
        _usbstorHandlers[at] = handler;
        _usbstorHandlerCount++;
        return 0;
    }

    /// <summary>NID_EAC08C24 (SceUsbstorForDriver)(SceUsbstorHandler *handler): removes the handler; 0.</summary>
    [Hle("NID_EAC08C24", Nid = 0xEAC08C24, Library = "SceUsbstorForDriver")]
    int UsbstorUnregisterHandler(uint handler)
    {
        if (handler == 0 || _usbstorHandlerCount == 0)
        {
            return USBSTOR_ERR_INVALID;
        }

        int at = Array.IndexOf(_usbstorHandlers, handler, 0, _usbstorHandlerCount);
        if (at < 0)
        {
            return USBSTOR_ERR_NOT_REGISTERED;
        }

        Array.Copy(_usbstorHandlers, at + 1, _usbstorHandlers, at, _usbstorHandlerCount - at - 1);
        _usbstorHandlerCount--;
        _usbstorHandlers[_usbstorHandlerCount] = 0;
        return 0;
    }

    /// <summary>NID_77B7E69E (SceUsbstorForDriver)(): the OR of the handlers' byte 2.</summary>
    [Hle("NID_77B7E69E", Nid = 0x77B7E69E, Library = "SceUsbstorForDriver")]
    int UsbstorHandlerBits()
    {
        int bits = 0;
        for (int index = 0; index < _usbstorHandlerCount; index++)
        {
            bits |= GuestMemory.Read<byte>(_usbstorHandlers[index] + 2);
        }

        return bits;
    }

    /// <summary>NID_BEF6B5AE (SceUsbstorForDriver)(): the maximum LUN.</summary>
    [Hle("NID_BEF6B5AE", Nid = 0xBEF6B5AE, Library = "SceUsbstorForDriver")]
    int UsbstorGetMaxLun()
    {
        return (int)_usbstorMaxLun;
    }

    /// <summary>NID_73F2FF9D (SceUsbstorForDriver)(uint maxLun): 0..6; the function is started and idle here.</summary>
    [Hle("NID_73F2FF9D", Nid = 0x73F2FF9D, Library = "SceUsbstorForDriver")]
    int UsbstorSetMaxLun(uint maxLun)
    {
        if (maxLun >= 7)
        {
            return USBSTOR_ERR_INVALID;
        }

        _usbstorMaxLun = maxLun;
        return 0;
    }

    /// <summary>NID_ADB2AAF2 (SceUsbstorForDriver)(const char *product, const char *revision, uint lun): the LUN's
    /// inquiry product (16 characters) and revision (4).</summary>
    [Hle("NID_ADB2AAF2", Nid = 0xADB2AAF2, Library = "SceUsbstorForDriver")]
    int UsbstorSetInquiry(uint product, uint revision, uint lun)
    {
        if (lun >= UsbstorHandlers || !UsbstorLunFits(lun))
        {
            return USBSTOR_ERR_INVALID;
        }

        int at = (int)lun * UsbstorLunBytes;
        Array.Clear(_usbstorLuns, at, 0x14);
        CopyCString(product, at, 0x10);
        CopyCString(revision, at + 0x10, 4);
        return 0;
    }

    /// <summary>NID_B6689597 (SceUsbstorForDriver)(uint lun, int field, void *out, uint size): a field of the LUN's
    /// inquiry data (0 vendor, 8 bytes; 1 product, 16; 2 revision, 4; 3 the rest, up to 0x80 as is), non-printable bytes
    /// as spaces; the bytes copied.</summary>
    [Hle("NID_B6689597", Nid = 0xB6689597, Library = "SceUsbstorForDriver")]
    int UsbstorGetInquiry(uint lun, uint field, uint output, uint size)
    {
        if (output == 0 || lun >= UsbstorHandlers || !UsbstorLunFits(lun))
        {
            return USBSTOR_ERR_INVALID;
        }

        int at = (int)lun * UsbstorLunBytes;
        (int offset, uint length) = field switch
        {
            0 => (0, 8u),
            1 => (8, 16u),
            2 => (0x18, 4u),
            3 => (0x1C, 0x80u),
            _ => (-1, 0u),
        };
        if (offset < 0)
        {
            return USBSTOR_ERR_INVALID;
        }

        uint count = Math.Min(size, length);
        for (uint index = 0; index < count; index++)
        {
            byte value = _usbstorLuns[at + offset + (int)index];
            bool printable = value is >= 0x20 and < 0x7F;
            GuestMemory.Write(output + index, field == 3 || printable ? value : (byte)0x20);
        }

        return (int)count;
    }

    /// <summary>NID_D3C7EE70 (SceUsbstorForDriver)(const void *data, uint size, int timeout): sends a command's data
    /// to the PC; no command is in flight.</summary>
    [Hle("NID_D3C7EE70", Nid = 0xD3C7EE70, Library = "SceUsbstorForDriver")]
    int UsbstorSendData(uint data, uint size, int timeout)
    {
        return UsbstorTransfer(data, size);
    }

    /// <summary>NID_F1A690DC (SceUsbstorForDriver)(void *data, uint size, int timeout): receives a command's data from
    /// the PC; no command is in flight.</summary>
    [Hle("NID_F1A690DC", Nid = 0xF1A690DC, Library = "SceUsbstorForDriver")]
    int UsbstorReceiveData(uint data, uint size, int timeout)
    {
        return UsbstorTransfer(data, size);
    }

    static int UsbstorTransfer(uint data, uint size)
    {
        if (data == 0)
        {
            return USBSTOR_ERR_INVALID;
        }

        return size == 0 ? 0 : USBSTOR_ERR_TRANSFER;
    }

    /// <summary>Whether the LUN's entry lies in the module's 0x200-byte table (it checks lun * 0x9C + 0xA0).</summary>
    static bool UsbstorLunFits(uint lun)
    {
        return lun * UsbstorLunBytes + 0xA0 <= UsbstorLunTable;
    }

    void CopyCString(uint source, int at, int limit)
    {
        if (source == 0)
        {
            return;
        }

        for (int index = 0; index < limit; index++)
        {
            byte value = GuestMemory.Read<byte>(source + (uint)index);
            if (value == 0)
            {
                break;
            }

            _usbstorLuns[at + index] = value;
        }
    }
}
