namespace VitaSharp.Core.Compat.Psp;

/// <summary>
/// The PSP's display scan-out channel of the DMACplus (PSP 0xBC800100, 0x14 bytes): where the LCD reads the picture
/// from. The PSP's display driver sets it with <c>sceDmacplusLcdcSetBaseAddr</c>/<c>SetFormat</c>/<c>Enable</c>.
/// <list type="table">
/// <item><term>+0x00</term><description>the framebuffer's PSP physical address (bits 0..28).</description></item>
/// <item><term>+0x04</term><description>the pixel format: 0 BGR565, 1 ABGR1555, 2 ABGR4444, 3 ABGR8888 (the PSP's
/// <c>PSP_DISPLAY_PIXEL_FORMAT_*</c>).</description></item>
/// <item><term>+0x08 / +0x0C</term><description>the width and the stride, in pixels.</description></item>
/// <item><term>+0x10</term><description>bit 0 scan-out enabled (bit 1 kept, meaning unknown).</description></item>
/// </list>
/// On the Vita the compat LCD DMA (<see cref="CompatLcdDma"/>) copies from this picture at every vertical blank.
/// Source: the PSP IPL SDK (<c>dmacplus.c</c>: the five registers and their use). That the Vita's compat LCD DMA takes
/// its source from these registers is inferred (SceCompat programs only the destination side, see
/// <see cref="CompatLcdDma"/>); the boot trace confirms it. The rest of the DMACplus block (0xBC800000) is not here.
/// </summary>
public sealed partial class PspLcdc : IPspMmioDevice
{
    /// <summary>PSP physical 0xBC800100.</summary>
    public const uint Base = 0x1C800100;

    public const uint BlockSize = 0x14;

    public const uint ScanOutEnabled = 1;

    private uint _address;
    private uint _format;
    private uint _width;
    private uint _stride;
    private uint _config;

    public uint PhysicalBase => Base;

    public uint Size => BlockSize;

    /// <summary>The framebuffer (PSP physical).</summary>
    public uint FramebufferAddress => _address;

    /// <summary>The pixel format (0..3).</summary>
    public uint PixelFormat => _format;

    public uint Width => _width;

    public uint Stride => _stride;

    /// <summary>The LCD reads a picture.</summary>
    public bool ScanningOut => (_config & ScanOutEnabled) != 0;

    public uint Read32(uint offset)
    {
        switch (offset)
        {
            case 0x00:
                return _address;
            case 0x04:
                return _format;
            case 0x08:
                return _width;
            case 0x0C:
                return _stride;
            default:
                return _config;
        }
    }

    public void Write32(uint offset, uint value)
    {
        switch (offset)
        {
            case 0x00:
                _address = value & 0x1FFFFFFF;
                break;
            case 0x04:
                _format = value & 3;
                break;
            case 0x08:
                _width = value & 0xFFF;
                break;
            case 0x0C:
                _stride = value & 0xFFF;
                break;
            default:
                _config = value & 3;
                break;
        }
    }

    /// <summary>No picture: every register 0.</summary>
    public void Reset()
    {
        _address = 0;
        _format = 0;
        _width = 0;
        _stride = 0;
        _config = 0;
    }
}
