using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceI2cForDriver (os0:kd/lowio.skprx 3.74, read in the module, study only): the two I2C buses (bus 0..1, else
// 0x803F0300) the codec, HDMI, camera, clock generator, LCD and motion sensor drivers talk through. A transfer checks
// the device address (0..0xFF, else 0x803F0303) and the length (a write 0..64, a read 1..64, else 0x803F0302), calls
// the bus's debug handlers (ksceI2cSetDebugHandlers: +4 before with the bus, address, buffer and length, +8 after with
// the bus and the result), starts the controller and turns its status into the result: 0, or 0x803F0380 + the
// index of the first error bit (status bits 8..15). No device sits on the emulated buses, so the controller reports
// the first error (the address phase is not acknowledged): every transfer answers 0x803F0380 and a read leaves the
// buffer as it was. Init, the speed setting and NID_E449AC6E's word only program the controller: 0.
public sealed partial class SystemServicesVsh
{
    const int I2cBuses = 2, I2cMaxLength = 64;

    /// <summary>SceI2c: a bus above 1 (0x803F0300).</summary>
    const int I2C_ERR_BUS = unchecked((int)0x803F0300);

    /// <summary>SceI2c: a length out of range (0x803F0302).</summary>
    const int I2C_ERR_LENGTH = unchecked((int)0x803F0302);

    /// <summary>SceI2c: a device address above 0xFF (0x803F0303).</summary>
    const int I2C_ERR_ADDRESS = unchecked((int)0x803F0303);

    /// <summary>SceI2c: the controller's first error status (bit 8): no device answered (0x803F0380).</summary>
    const int I2C_ERR_NO_ACK = unchecked((int)0x803F0380);

    [SnapshotMissing] // states of builds before it: nothing set
    readonly uint[] _i2cDebugHandlers = new uint[I2cBuses];

    [SnapshotMissing]
    readonly uint[] _i2cWord = new uint[I2cBuses];

    [SnapshotMissing]
    readonly uint[] _i2cSpeed = new uint[I2cBuses * 2];

    [SnapshotMissing(true)] // the reset line is released at boot
    bool _i2cReleased = true;

    /// <summary>The kept values of a bus, for the tests.</summary>
    internal (uint Handlers, uint Word, uint SpeedLow, uint SpeedHigh, bool Released) I2cKernelValues(int bus) =>
        (_i2cDebugHandlers[bus], _i2cWord[bus], _i2cSpeed[bus * 2], _i2cSpeed[bus * 2 + 1], _i2cReleased);

    /// <summary>ksceI2cInit(int bus): programs the controller; 0.</summary>
    [Hle(Nid = 0x9CF8F3D6, Library = "SceI2cForDriver")]
    int ksceI2cInit(uint bus)
    {
        return bus >= I2cBuses ? I2C_ERR_BUS : 0;
    }

    /// <summary>ksceI2cSetDebugHandlers(int bus, handlers): the handlers called around each transfer (NULL: none); 0.</summary>
    [Hle(Nid = 0xA2C7CE62, Library = "SceI2cForDriver")]
    int ksceI2cSetDebugHandlers(uint bus, uint handlers)
    {
        if (bus >= I2cBuses)
        {
            return I2C_ERR_BUS;
        }

        _i2cDebugHandlers[bus] = handlers;
        return 0;
    }

    /// <summary>NID_E449AC6E (SceI2cForDriver)(int bus, uint value): a word of the bus kept; 0.</summary>
    [Hle("NID_E449AC6E", Nid = 0xE449AC6E, Library = "SceI2cForDriver")]
    int I2cSetBusWord(uint bus, uint value)
    {
        if (bus >= I2cBuses)
        {
            return I2C_ERR_BUS;
        }

        _i2cWord[bus] = value;
        return 0;
    }

    /// <summary>NID_30CF9469 (SceI2cForDriver)(int bus, uint low, uint high): the bus timing kept and the controller's
    /// speed programmed from the product; 0.</summary>
    [Hle("NID_30CF9469", Nid = 0x30CF9469, Library = "SceI2cForDriver")]
    int I2cSetBusSpeed(uint bus, uint low, uint high)
    {
        if (bus >= I2cBuses)
        {
            return I2C_ERR_BUS;
        }

        _i2cSpeed[bus * 2] = low;
        _i2cSpeed[bus * 2 + 1] = high;
        return 0;
    }

    /// <summary>ksceI2cReset(int release): 0 holds the bus reset line (bit 31 of the bus event flag cleared), else
    /// releases it.</summary>
    [Hle(Nid = 0x76D277AB, Library = "SceI2cForDriver")]
    int ksceI2cReset(int release)
    {
        _i2cReleased = release != 0;
        return 0;
    }

    /// <summary>ksceI2cTransferWrite(int bus, uint addr, const void *buffer, uint size): size 0..64.</summary>
    [Hle(Nid = 0xCA94A759, Library = "SceI2cForDriver")]
    int ksceI2cTransferWrite(uint bus, uint address, uint buffer, uint size)
    {
        if (bus >= I2cBuses)
        {
            return I2C_ERR_BUS;
        }

        if (address > 0xFF)
        {
            return I2C_ERR_ADDRESS;
        }

        return size > I2cMaxLength ? I2C_ERR_LENGTH : I2cTransfer(bus, address, buffer, size);
    }

    /// <summary>ksceI2cTransferRead(int bus, uint addr, void *buffer, uint size): size 1..64.</summary>
    [Hle(Nid = 0xD1D0A9A4, Library = "SceI2cForDriver")]
    int ksceI2cTransferRead(uint bus, uint address, uint buffer, uint size)
    {
        if (bus >= I2cBuses)
        {
            return I2C_ERR_BUS;
        }

        if (address > 0xFF)
        {
            return I2C_ERR_ADDRESS;
        }

        return size - 1 >= I2cMaxLength ? I2C_ERR_LENGTH : I2cTransfer(bus, address, buffer, size);
    }

    /// <summary>ksceI2cTransferWriteRead(int bus, uint writeAddr, const void *writeBuffer, uint writeSize, uint
    /// readAddr, void *readBuffer, uint readSize): both sizes 1..64.</summary>
    [Hle(Nid = 0x0A40B7BF, Library = "SceI2cForDriver")]
    int ksceI2cTransferWriteRead(uint bus, uint writeAddress, uint writeBuffer, uint writeSize, uint readAddress,
        uint readBuffer, uint readSize)
    {
        if (bus >= I2cBuses)
        {
            return I2C_ERR_BUS;
        }

        if (writeAddress > 0xFF || readAddress > 0xFF)
        {
            return I2C_ERR_ADDRESS;
        }

        if (writeSize - 1 >= I2cMaxLength || readSize - 1 >= I2cMaxLength)
        {
            return I2C_ERR_LENGTH;
        }

        return I2cTransfer(bus, writeAddress, writeBuffer, writeSize);
    }

    /// <summary>One transfer on a bus without devices: the debug handlers around it, the controller's no-answer status.</summary>
    int I2cTransfer(uint bus, uint address, uint buffer, uint size)
    {
        uint handlers = _i2cDebugHandlers[bus];
        GuestThread? thread = GuestThread.Current;
        if (handlers != 0 && thread != null)
        {
            uint before = GuestMemory.R32(handlers + 4);
            if (before != 0)
            {
                thread.CallGuest(before, bus, address, buffer, size);
            }
        }

        int result = I2C_ERR_NO_ACK;
        if (handlers != 0 && thread != null)
        {
            uint after = GuestMemory.R32(handlers + 8);
            if (after != 0)
            {
                thread.CallGuest(after, bus, (uint)result);
            }
        }

        return result;
    }
}
