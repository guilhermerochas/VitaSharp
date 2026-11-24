namespace VitaSharp.Core.Compat.Ge;

/// <summary>
/// The PSP GE's display list commands: each list word is the command in bits 24..31 and its argument in bits 0..23.
/// The names follow the PSP SDK's (GE_CMD_* in pspge.h, the same set PPSSPP and Jpcsp document).
/// </summary>
public static class GeCommand
{
    public const int Nop = 0x00;
    public const int VertexAddress = 0x01;
    public const int IndexAddress = 0x02;
    public const int Primitive = 0x04;
    public const int Bezier = 0x05;
    public const int Spline = 0x06;
    public const int BoundingBox = 0x07;
    public const int Jump = 0x08;
    public const int BoundingBoxJump = 0x09;
    public const int Call = 0x0A;
    public const int Return = 0x0B;
    public const int End = 0x0C;
    public const int Signal = 0x0E;
    public const int Finish = 0x0F;
    public const int Base = 0x10;
    public const int VertexType = 0x12;
    public const int OffsetAddress = 0x13;
    public const int Origin = 0x14;
    public const int Region1 = 0x15;
    public const int Region2 = 0x16;
    public const int LightingEnable = 0x17;
    public const int LightEnable0 = 0x18;
    public const int DepthClampEnable = 0x1C;
    public const int CullFaceEnable = 0x1D;
    public const int TextureMapEnable = 0x1E;
    public const int FogEnable = 0x1F;
    public const int DitherEnable = 0x20;
    public const int AlphaBlendEnable = 0x21;
    public const int AlphaTestEnable = 0x22;
    public const int DepthTestEnable = 0x23;
    public const int StencilTestEnable = 0x24;
    public const int AntiAliasEnable = 0x25;
    public const int PatchCullEnable = 0x26;
    public const int ColorTestEnable = 0x27;
    public const int LogicOpEnable = 0x28;
    public const int BoneMatrixNumber = 0x2A;
    public const int BoneMatrixData = 0x2B;
    public const int MorphWeight0 = 0x2C;
    public const int PatchDivision = 0x36;
    public const int PatchPrimitive = 0x37;
    public const int PatchFacing = 0x38;
    public const int WorldMatrixNumber = 0x3A;
    public const int WorldMatrixData = 0x3B;
    public const int ViewMatrixNumber = 0x3C;
    public const int ViewMatrixData = 0x3D;
    public const int ProjectionMatrixNumber = 0x3E;
    public const int ProjectionMatrixData = 0x3F;
    public const int TexGenMatrixNumber = 0x40;
    public const int TexGenMatrixData = 0x41;
    public const int ViewportXScale = 0x42;
    public const int ViewportYScale = 0x43;
    public const int ViewportZScale = 0x44;
    public const int ViewportXCenter = 0x45;
    public const int ViewportYCenter = 0x46;
    public const int ViewportZCenter = 0x47;
    public const int TexScaleU = 0x48;
    public const int TexScaleV = 0x49;
    public const int TexOffsetU = 0x4A;
    public const int TexOffsetV = 0x4B;
    public const int OffsetX = 0x4C;
    public const int OffsetY = 0x4D;
    public const int ShadeMode = 0x50;
    public const int ReverseNormal = 0x51;
    public const int MaterialUpdate = 0x53;
    public const int MaterialEmissive = 0x54;
    public const int MaterialAmbient = 0x55;
    public const int MaterialDiffuse = 0x56;
    public const int MaterialSpecular = 0x57;
    public const int MaterialAlpha = 0x58;
    public const int MaterialSpecularCoefficient = 0x5B;
    public const int AmbientColor = 0x5C;
    public const int AmbientAlpha = 0x5D;
    public const int LightMode = 0x5E;

    /// <summary>LIGHTTYPE0..3: bits 0-1 the computation (0 diffuse, 1 diffuse and specular, 2 powered diffuse), 8-9 the
    /// type (0 directional, 1 point, 2 and 3 spot).</summary>
    public const int LightType0 = 0x5F;

    /// <summary>LX0..: three commands (x, y, z) per light from here.</summary>
    public const int LightPosition0 = 0x63;
    public const int LightDirection0 = 0x6F;

    /// <summary>LKA0..: the constant, linear and quadratic attenuation, three per light.</summary>
    public const int LightAttenuation0 = 0x7B;
    public const int LightSpotExponent0 = 0x87;
    public const int LightSpotCutoff0 = 0x8B;

    /// <summary>LAC0..: the ambient, diffuse and specular colour, three per light.</summary>
    public const int LightColor0 = 0x8F;
    public const int Cull = 0x9B;
    public const int FrameBufferPointer = 0x9C;
    public const int FrameBufferWidth = 0x9D;
    public const int DepthBufferPointer = 0x9E;
    public const int DepthBufferWidth = 0x9F;
    public const int TextureAddress0 = 0xA0;
    public const int TextureBufferWidth0 = 0xA8;
    public const int ClutAddress = 0xB0;
    public const int ClutAddressUpper = 0xB1;
    public const int TransferSource = 0xB2;
    public const int TransferSourceWidth = 0xB3;
    public const int TransferDestination = 0xB4;
    public const int TransferDestinationWidth = 0xB5;
    public const int TextureSize0 = 0xB8;
    public const int TextureMapMode = 0xC0;
    public const int TextureShadeLights = 0xC1;
    public const int TextureMode = 0xC2;
    public const int TextureFormat = 0xC3;
    public const int LoadClut = 0xC4;
    public const int ClutFormat = 0xC5;
    public const int TextureFilter = 0xC6;
    public const int TextureWrap = 0xC7;
    public const int TextureLevel = 0xC8;
    public const int TextureFunction = 0xC9;
    public const int TextureEnvironmentColor = 0xCA;
    public const int TextureFlush = 0xCB;
    public const int TextureSync = 0xCC;
    public const int Fog1 = 0xCD;
    public const int Fog2 = 0xCE;
    public const int FogColor = 0xCF;
    public const int TextureLodSlope = 0xD0;
    public const int FrameBufferPixelFormat = 0xD2;
    public const int ClearMode = 0xD3;
    public const int Scissor1 = 0xD4;
    public const int Scissor2 = 0xD5;
    public const int MinZ = 0xD6;
    public const int MaxZ = 0xD7;
    public const int ColorTest = 0xD8;
    public const int ColorReference = 0xD9;
    public const int ColorTestMask = 0xDA;
    public const int AlphaTest = 0xDB;
    public const int StencilTest = 0xDC;
    public const int StencilOperation = 0xDD;
    public const int DepthTest = 0xDE;
    public const int BlendMode = 0xDF;
    public const int BlendFixedA = 0xE0;
    public const int BlendFixedB = 0xE1;
    public const int Dither0 = 0xE2;
    public const int LogicOp = 0xE6;
    public const int DepthWriteDisable = 0xE7;
    public const int MaskRgb = 0xE8;
    public const int MaskAlpha = 0xE9;
    public const int TransferStart = 0xEA;
    public const int TransferSourcePosition = 0xEB;
    public const int TransferDestinationPosition = 0xEC;
    public const int TransferSize = 0xEE;

    /// <summary>The primitive types of <see cref="Primitive"/> (argument bits 16..18).</summary>
    public const int PrimitivePoints = 0, PrimitiveLines = 1, PrimitiveLineStrip = 2, PrimitiveTriangles = 3,
        PrimitiveTriangleStrip = 4, PrimitiveTriangleFan = 5, PrimitiveRectangles = 6, PrimitiveKeepPrevious = 7;
}
