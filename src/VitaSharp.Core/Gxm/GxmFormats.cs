using VitaSharp.Core.Gxm.Shader;

namespace VitaSharp.Core.Gxm;

public enum TexBase : uint
{
    U8 = 0x00000000, S8 = 0x01000000, U4U4U4U4 = 0x02000000, U8U3U3U2 = 0x03000000, U1U5U5U5 = 0x04000000, U5U6U5 = 0x05000000,
    S5S5U6 = 0x06000000, U8U8 = 0x07000000, S8S8 = 0x08000000, U16 = 0x09000000, S16 = 0x0A000000, F16 = 0x0B000000,
    U8U8U8U8 = 0x0C000000, S8S8S8S8 = 0x0D000000, U2U10U10U10 = 0x0E000000, U16U16 = 0x0F000000, S16S16 = 0x10000000,
    F16F16 = 0x11000000, F32 = 0x12000000, F32M = 0x13000000, X8S8S8U8 = 0x14000000, X8U24 = 0x15000000, U32 = 0x17000000,
    S32 = 0x18000000, SE5M9M9M9 = 0x19000000, F11F11F10 = 0x1A000000, F16F16F16F16 = 0x1B000000, U16U16U16U16 = 0x1C000000,
    S16S16S16S16 = 0x1D000000, F32F32 = 0x1E000000, U32U32 = 0x1F000000, PVRT2BPP = 0x80000000, PVRT4BPP = 0x81000000,
    PVRTII2BPP = 0x82000000, PVRTII4BPP = 0x83000000, UBC1 = 0x85000000, UBC2 = 0x86000000, UBC3 = 0x87000000,
    UBC4 = 0x88000000, SBC4 = 0x89000000, UBC5 = 0x8A000000, SBC5 = 0x8B000000, YUV420P2 = 0x90000000, YUV420P3 = 0x91000000,
    YUV422 = 0x92000000, P4 = 0x94000000, P8 = 0x95000000, U8U8U8 = 0x98000000, S8S8S8 = 0x99000000, U2F10F10F10 = 0x9A000000,
}

public static class GxmFormats
{
    public const uint TextureBaseMask = 0x9F000000, TextureSwizzleMask = 0x00007000;

    public static TexBase BaseOf(uint textureFormat) => (TexBase)(textureFormat & TextureBaseMask);
    public static uint SwizzleOf(uint textureFormat) => textureFormat & TextureSwizzleMask;

    public static DataType TextureComponentType(uint format) => BaseOf(format) switch
    {
        TexBase.S8 or TexBase.S8S8 or TexBase.S8S8S8S8 or TexBase.X8S8S8U8 or TexBase.S8S8S8 => DataType.Int8,
        TexBase.U16 or TexBase.U2U10U10U10 or TexBase.U16U16 or TexBase.U16U16U16U16 => DataType.UInt16,
        TexBase.S16 or TexBase.S16S16 or TexBase.S16S16S16S16 => DataType.Int16,
        TexBase.F16 or TexBase.F16F16 or TexBase.SE5M9M9M9 or TexBase.F11F11F10 or TexBase.F16F16F16F16 or TexBase.U2F10F10F10 => DataType.F16,
        TexBase.F32 or TexBase.F32M or TexBase.X8U24 or TexBase.F32F32 or TexBase.U32 or TexBase.U32U32 or TexBase.S32 => DataType.F32,
        _ => DataType.UInt8,
    };

    public static int TextureComponentCount(uint format)
    {
        uint swz = SwizzleOf(format);
        switch (BaseOf(format))
        {
            case TexBase.U8: case TexBase.S8: case TexBase.U16: case TexBase.S16: case TexBase.F16:
            case TexBase.U32: case TexBase.S32: case TexBase.F32: case TexBase.F32M:
                return swz == 0 ? 1 : 4;
            case TexBase.U8U8: case TexBase.S8S8: case TexBase.U16U16: case TexBase.S16S16:
            case TexBase.F16F16: case TexBase.U32U32: case TexBase.F32F32:
                return swz == 0 ? 2 : 4;
            case TexBase.X8U24: return 1;
            default: return 4;
        }
    }
}
