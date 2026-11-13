/*
 * Graphics (sceGxm, drawn offscreen and read back by the CPU after sceGxmFinish):
 * clears, primitives (triangles, strips, fans, lines), vertex colour interpolation, culling, texture formats in
 * linear / strided / swizzled / tiled layouts (raw, packed, float, palette and block-compressed), samplers (filters,
 * address modes, mipmaps), blending and colour masks, depth, stencil, render-to-texture and 4x MSAA.
 *
 * Swizzled layout: Morton order with y in bit 0 and x in bit 1 (as Vita3K decodes it). Tiled layout: 32x32 texel
 * tiles stored row by row, texels inside a tile row by row. Both are what this test expects and are confirmed or
 * refuted by running it on a real console.
 */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "app.h"
#include "gfx.h"

#define AREA "gfx"
#define TEX_SIZE 64
#define BLOCK_SIZE 16
#define BLOCKS_PER_ROW (TEX_SIZE / BLOCK_SIZE)
#define BLOCK_COUNT (BLOCKS_PER_ROW * BLOCKS_PER_ROW)
#define STRIDED_TEXELS 80
#define TEXTURE_MEMORY_SIZE (TEX_SIZE * STRIDED_TEXELS * 8 + 4096)

typedef enum TextureLayout
{
    LAYOUT_LINEAR = 0,
    LAYOUT_STRIDED,
    LAYOUT_SWIZZLED,
    LAYOUT_TILED
} TextureLayout;

typedef enum TexelKind
{
    KIND_ABGR8 = 0,
    KIND_ARGB8,
    KIND_X8_1BGR,
    KIND_BGR8,
    KIND_565_RGB,
    KIND_565_BGR,
    KIND_4444_ARGB,
    KIND_1555_ARGB,
    KIND_2101010_ABGR,
    KIND_U8_1RRR,
    KIND_U8U8_00GR,
    KIND_F16X4_ABGR,
    KIND_P8_ABGR,
    KIND_P4_ABGR,
    KIND_BC1,
    KIND_BC2,
    KIND_BC3,
    KIND_ETC1
} TexelKind;

typedef struct TextureCase
{
    const char *name;
    SceGxmTextureFormat format;
    TexelKind kind;
    TextureLayout layout;
} TextureCase;

static const TextureCase TEXTURE_CASES[] = {
    {"tex_ABGR8_linear", SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR, KIND_ABGR8, LAYOUT_LINEAR},
    {"tex_ABGR8_strided", SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR, KIND_ABGR8, LAYOUT_STRIDED},
    {"tex_ABGR8_swizzled", SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR, KIND_ABGR8, LAYOUT_SWIZZLED},
    {"tex_ABGR8_tiled", SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR, KIND_ABGR8, LAYOUT_TILED},
    {"tex_ARGB8_linear", SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ARGB, KIND_ARGB8, LAYOUT_LINEAR},
    {"tex_X8_1BGR_linear", SCE_GXM_TEXTURE_FORMAT_X8U8U8U8_1BGR, KIND_X8_1BGR, LAYOUT_LINEAR},
    {"tex_U8U8U8_BGR_linear", SCE_GXM_TEXTURE_FORMAT_U8U8U8_BGR, KIND_BGR8, LAYOUT_LINEAR},
    {"tex_565_RGB_linear", SCE_GXM_TEXTURE_FORMAT_U5U6U5_RGB, KIND_565_RGB, LAYOUT_LINEAR},
    {"tex_565_RGB_swizzled", SCE_GXM_TEXTURE_FORMAT_U5U6U5_RGB, KIND_565_RGB, LAYOUT_SWIZZLED},
    {"tex_565_RGB_tiled", SCE_GXM_TEXTURE_FORMAT_U5U6U5_RGB, KIND_565_RGB, LAYOUT_TILED},
    {"tex_565_BGR_linear", SCE_GXM_TEXTURE_FORMAT_U5U6U5_BGR, KIND_565_BGR, LAYOUT_LINEAR},
    {"tex_4444_ARGB_linear", SCE_GXM_TEXTURE_FORMAT_U4U4U4U4_ARGB, KIND_4444_ARGB, LAYOUT_LINEAR},
    {"tex_1555_ARGB_linear", SCE_GXM_TEXTURE_FORMAT_U1U5U5U5_ARGB, KIND_1555_ARGB, LAYOUT_LINEAR},
    {"tex_2101010_ABGR_linear", SCE_GXM_TEXTURE_FORMAT_U2U10U10U10_ABGR, KIND_2101010_ABGR, LAYOUT_LINEAR},
    {"tex_U8_1RRR_linear", SCE_GXM_TEXTURE_FORMAT_U8_1RRR, KIND_U8_1RRR, LAYOUT_LINEAR},
    {"tex_U8_1RRR_swizzled", SCE_GXM_TEXTURE_FORMAT_U8_1RRR, KIND_U8_1RRR, LAYOUT_SWIZZLED},
    {"tex_U8_1RRR_tiled", SCE_GXM_TEXTURE_FORMAT_U8_1RRR, KIND_U8_1RRR, LAYOUT_TILED},
    {"tex_U8U8_00GR_linear", SCE_GXM_TEXTURE_FORMAT_U8U8_00GR, KIND_U8U8_00GR, LAYOUT_LINEAR},
    {"tex_F16x4_ABGR_linear", SCE_GXM_TEXTURE_FORMAT_F16F16F16F16_ABGR, KIND_F16X4_ABGR, LAYOUT_LINEAR},
    {"tex_F16x4_ABGR_swizzled", SCE_GXM_TEXTURE_FORMAT_F16F16F16F16_ABGR, KIND_F16X4_ABGR, LAYOUT_SWIZZLED},
    {"tex_P8_ABGR_linear", SCE_GXM_TEXTURE_FORMAT_P8_ABGR, KIND_P8_ABGR, LAYOUT_LINEAR},
    {"tex_P8_ABGR_swizzled", SCE_GXM_TEXTURE_FORMAT_P8_ABGR, KIND_P8_ABGR, LAYOUT_SWIZZLED},
    {"tex_P4_ABGR_linear", SCE_GXM_TEXTURE_FORMAT_P4_ABGR, KIND_P4_ABGR, LAYOUT_LINEAR},
    {"tex_UBC1_linear", SCE_GXM_TEXTURE_FORMAT_UBC1_ABGR, KIND_BC1, LAYOUT_LINEAR},
    {"tex_UBC1_swizzled", SCE_GXM_TEXTURE_FORMAT_UBC1_ABGR, KIND_BC1, LAYOUT_SWIZZLED},
    {"tex_UBC2_linear", SCE_GXM_TEXTURE_FORMAT_UBC2_ABGR, KIND_BC2, LAYOUT_LINEAR},
    {"tex_UBC3_linear", SCE_GXM_TEXTURE_FORMAT_UBC3_ABGR, KIND_BC3, LAYOUT_LINEAR},
    {"tex_ETC1_linear", SCE_GXM_TEXTURE_FORMAT_ETC1_1BGR, KIND_ETC1, LAYOUT_LINEAR},
};

/* 16 block colours (RGBA); the grey and two-channel formats use a ramp instead. */
static const uint8_t BLOCK_COLORS[BLOCK_COUNT][4] = {
    {255, 0, 0, 255},     {0, 255, 0, 255},     {0, 0, 255, 255},   {255, 255, 0, 255},
    {255, 0, 255, 255},   {0, 255, 255, 255},   {255, 255, 255, 255}, {0, 0, 0, 255},
    {128, 0, 0, 128},     {0, 128, 0, 128},     {0, 0, 128, 128},   {128, 128, 0, 255},
    {128, 0, 128, 255},   {0, 128, 128, 255},   {128, 128, 128, 128}, {64, 192, 32, 255},
};

static SceUID g_textureUid = -1;
static uint8_t *g_textureMemory;
static uint8_t g_linearElements[TEX_SIZE * TEX_SIZE * 8];

static bool uses_ramp(TexelKind kind)
{
    return kind == KIND_U8_1RRR || kind == KIND_U8U8_00GR;
}

static void source_color(TexelKind kind, int block, uint8_t rgba[4])
{
    if (uses_ramp(kind))
    {
        rgba[0] = (uint8_t)(block * 17);
        rgba[1] = (uint8_t)(255 - block * 17);
        rgba[2] = 0;
        rgba[3] = 255;
        return;
    }
    memcpy(rgba, BLOCK_COLORS[block], 4);
}

static int quantize(int value, int bits)
{
    int maximum = (1 << bits) - 1;
    return (value * maximum + 127) / 255;
}

static int expand(int quantized, int bits)
{
    int maximum = (1 << bits) - 1;
    return (quantized * 255 + maximum / 2) / maximum;
}

static uint16_t float_to_half(float value)
{
    uint32_t bits;
    memcpy(&bits, &value, sizeof(bits));
    uint32_t sign = (bits >> 16) & 0x8000u;
    int exponent = (int)((bits >> 23) & 0xFF) - 127 + 15;
    uint32_t mantissa = bits & 0x7FFFFFu;
    if (exponent <= 0)
    {
        return (uint16_t)sign;
    }
    if (exponent >= 31)
    {
        return (uint16_t)(sign | 0x7C00u);
    }
    return (uint16_t)(sign | ((uint32_t)exponent << 10) | (mantissa >> 13));
}

static uint16_t pack_565(const uint8_t rgba[4], bool redHigh)
{
    int r = quantize(rgba[0], 5);
    int g = quantize(rgba[1], 6);
    int b = quantize(rgba[2], 5);
    if (redHigh)
    {
        return (uint16_t)((r << 11) | (g << 5) | b);
    }
    return (uint16_t)((b << 11) | (g << 5) | r);
}

static void expected_565(const uint8_t rgba[4], uint8_t out[4])
{
    out[0] = (uint8_t)expand(quantize(rgba[0], 5), 5);
    out[1] = (uint8_t)expand(quantize(rgba[1], 6), 6);
    out[2] = (uint8_t)expand(quantize(rgba[2], 5), 5);
    out[3] = 255;
}

static int etc1_base(int value)
{
    int quantized = quantize(value, 4);
    return (quantized << 4) | quantized;
}

/* Bytes per element: a texel, or a 4x4 block for the compressed formats; 0 for P4 (two texels per byte). */
static int element_bytes(TexelKind kind)
{
    switch (kind)
    {
        case KIND_ABGR8:
        case KIND_ARGB8:
        case KIND_X8_1BGR:
        case KIND_2101010_ABGR:
            return 4;
        case KIND_BGR8:
            return 3;
        case KIND_565_RGB:
        case KIND_565_BGR:
        case KIND_4444_ARGB:
        case KIND_1555_ARGB:
        case KIND_U8U8_00GR:
            return 2;
        case KIND_U8_1RRR:
        case KIND_P8_ABGR:
            return 1;
        case KIND_F16X4_ABGR:
            return 8;
        case KIND_BC1:
        case KIND_ETC1:
            return 8;
        case KIND_BC2:
        case KIND_BC3:
            return 16;
        case KIND_P4_ABGR:
            return 0;
    }
    return 4;
}

static bool is_block_compressed(TexelKind kind)
{
    return kind == KIND_BC1 || kind == KIND_BC2 || kind == KIND_BC3 || kind == KIND_ETC1;
}

static void put16(uint8_t *out, uint16_t value)
{
    out[0] = (uint8_t)(value & 0xFF);
    out[1] = (uint8_t)(value >> 8);
}

static void put32(uint8_t *out, uint32_t value)
{
    out[0] = (uint8_t)(value & 0xFF);
    out[1] = (uint8_t)((value >> 8) & 0xFF);
    out[2] = (uint8_t)((value >> 16) & 0xFF);
    out[3] = (uint8_t)(value >> 24);
}

/* Encodes one texel (or one solid 4x4 block) of the given colour. */
static void encode_element(TexelKind kind, const uint8_t rgba[4], int paletteIndex, uint8_t *out)
{
    uint32_t r = rgba[0];
    uint32_t g = rgba[1];
    uint32_t b = rgba[2];
    uint32_t a = rgba[3];
    switch (kind)
    {
        case KIND_ABGR8:
            put32(out, r | (g << 8) | (b << 16) | (a << 24));
            break;
        case KIND_ARGB8:
            put32(out, b | (g << 8) | (r << 16) | (a << 24));
            break;
        case KIND_X8_1BGR:
            put32(out, r | (g << 8) | (b << 16));
            break;
        case KIND_BGR8:
            out[0] = (uint8_t)r;
            out[1] = (uint8_t)g;
            out[2] = (uint8_t)b;
            break;
        case KIND_565_RGB:
            put16(out, pack_565(rgba, true));
            break;
        case KIND_565_BGR:
            put16(out, pack_565(rgba, false));
            break;
        case KIND_4444_ARGB:
            put16(out, (uint16_t)((quantize((int)a, 4) << 12) | (quantize((int)r, 4) << 8) |
                                  (quantize((int)g, 4) << 4) | quantize((int)b, 4)));
            break;
        case KIND_1555_ARGB:
            put16(out, (uint16_t)((quantize((int)a, 1) << 15) | (quantize((int)r, 5) << 10) |
                                  (quantize((int)g, 5) << 5) | quantize((int)b, 5)));
            break;
        case KIND_2101010_ABGR:
            put32(out, ((uint32_t)quantize((int)a, 2) << 30) | ((uint32_t)quantize((int)b, 10) << 20) |
                           ((uint32_t)quantize((int)g, 10) << 10) | (uint32_t)quantize((int)r, 10));
            break;
        case KIND_U8_1RRR:
            out[0] = (uint8_t)r;
            break;
        case KIND_U8U8_00GR:
            out[0] = (uint8_t)r;
            out[1] = (uint8_t)g;
            break;
        case KIND_F16X4_ABGR:
            put16(out + 0, float_to_half((float)r / 255.0f));
            put16(out + 2, float_to_half((float)g / 255.0f));
            put16(out + 4, float_to_half((float)b / 255.0f));
            put16(out + 6, float_to_half((float)a / 255.0f));
            break;
        case KIND_P8_ABGR:
            out[0] = (uint8_t)paletteIndex;
            break;
        case KIND_P4_ABGR:
            break;
        case KIND_BC1:
        {
            uint16_t color = pack_565(rgba, true);
            put16(out + 0, color);
            put16(out + 2, color);
            put32(out + 4, 0);
            break;
        }
        case KIND_BC2:
        {
            uint8_t alphaNibble = (uint8_t)quantize((int)a, 4);
            uint8_t alphaByte = (uint8_t)((alphaNibble << 4) | alphaNibble);
            memset(out, alphaByte, 8);
            uint16_t color = pack_565(rgba, true);
            put16(out + 8, color);
            put16(out + 10, color);
            put32(out + 12, 0);
            break;
        }
        case KIND_BC3:
        {
            out[0] = (uint8_t)a;
            out[1] = (uint8_t)a;
            memset(out + 2, 0, 6);
            uint16_t color = pack_565(rgba, true);
            put16(out + 8, color);
            put16(out + 10, color);
            put32(out + 12, 0);
            break;
        }
        case KIND_ETC1:
        {
            /* Individual mode, both halves the same 4-bit colour, codeword 0, every pixel index 0 (+2). */
            int qr = quantize((int)r, 4);
            int qg = quantize((int)g, 4);
            int qb = quantize((int)b, 4);
            out[0] = (uint8_t)((qr << 4) | qr);
            out[1] = (uint8_t)((qg << 4) | qg);
            out[2] = (uint8_t)((qb << 4) | qb);
            out[3] = 0;
            out[4] = 0;
            out[5] = 0;
            out[6] = 0;
            out[7] = 0;
            break;
        }
    }
}

static int clamp_byte(int value)
{
    if (value < 0)
    {
        return 0;
    }
    if (value > 255)
    {
        return 255;
    }
    return value;
}

/* The colour the GPU should return for a texel encoded from rgba. */
static void expected_color(TexelKind kind, const uint8_t rgba[4], uint8_t out[4])
{
    switch (kind)
    {
        case KIND_ABGR8:
        case KIND_ARGB8:
        case KIND_P8_ABGR:
        case KIND_P4_ABGR:
        case KIND_F16X4_ABGR:
            memcpy(out, rgba, 4);
            break;
        case KIND_X8_1BGR:
        case KIND_BGR8:
            out[0] = rgba[0];
            out[1] = rgba[1];
            out[2] = rgba[2];
            out[3] = 255;
            break;
        case KIND_565_RGB:
        case KIND_565_BGR:
        case KIND_BC1:
            expected_565(rgba, out);
            break;
        case KIND_BC2:
            expected_565(rgba, out);
            out[3] = (uint8_t)expand(quantize(rgba[3], 4), 4);
            break;
        case KIND_BC3:
            expected_565(rgba, out);
            out[3] = rgba[3];
            break;
        case KIND_4444_ARGB:
            for (int channel = 0; channel < 4; channel++)
            {
                out[channel] = (uint8_t)expand(quantize(rgba[channel], 4), 4);
            }
            break;
        case KIND_1555_ARGB:
            for (int channel = 0; channel < 3; channel++)
            {
                out[channel] = (uint8_t)expand(quantize(rgba[channel], 5), 5);
            }
            out[3] = (uint8_t)expand(quantize(rgba[3], 1), 1);
            break;
        case KIND_2101010_ABGR:
            for (int channel = 0; channel < 3; channel++)
            {
                out[channel] = (uint8_t)expand(quantize(rgba[channel], 10), 10);
            }
            out[3] = (uint8_t)expand(quantize(rgba[3], 2), 2);
            break;
        case KIND_U8_1RRR:
            out[0] = rgba[0];
            out[1] = rgba[0];
            out[2] = rgba[0];
            out[3] = 255;
            break;
        case KIND_U8U8_00GR:
            out[0] = rgba[0];
            out[1] = rgba[1];
            out[2] = 0;
            out[3] = 0;
            break;
        case KIND_ETC1:
            out[0] = (uint8_t)clamp_byte(etc1_base(rgba[0]) + 2);
            out[1] = (uint8_t)clamp_byte(etc1_base(rgba[1]) + 2);
            out[2] = (uint8_t)clamp_byte(etc1_base(rgba[2]) + 2);
            out[3] = 255;
            break;
    }
}

static int tolerance_for(TexelKind kind)
{
    if (kind == KIND_F16X4_ABGR || kind == KIND_ETC1)
    {
        return 8;
    }
    return 4;
}

static uint32_t part1by1(uint32_t value)
{
    value &= 0x0000FFFFu;
    value = (value ^ (value << 8)) & 0x00FF00FFu;
    value = (value ^ (value << 4)) & 0x0F0F0F0Fu;
    value = (value ^ (value << 2)) & 0x33333333u;
    value = (value ^ (value << 1)) & 0x55555555u;
    return value;
}

/* Element index of (x, y) in a square power-of-two swizzled image: y in bit 0, x in bit 1. */
static uint32_t morton_index(uint32_t x, uint32_t y)
{
    return part1by1(y) | (part1by1(x) << 1);
}

static uint32_t tiled_index(uint32_t x, uint32_t y, uint32_t widthElements)
{
    uint32_t tilesPerRow = (widthElements + 31) / 32;
    uint32_t tile = (x / 32) + (y / 32) * tilesPerRow;
    uint32_t inside = (x % 32) + (y % 32) * 32;
    return tile * 1024 + inside;
}

/* Builds the texture memory for a case; returns false when the case cannot be built. */
static bool build_texture(const TextureCase *testCase, SceGxmTexture *texture, uint32_t *palette)
{
    TexelKind kind = testCase->kind;
    memset(g_textureMemory, 0, TEXTURE_MEMORY_SIZE);
    bool compressed = is_block_compressed(kind);
    int elementSize = element_bytes(kind);
    int elementsPerRow = compressed ? TEX_SIZE / 4 : TEX_SIZE;
    int elementTexels = compressed ? 4 : 1;

    if (kind == KIND_P8_ABGR || kind == KIND_P4_ABGR)
    {
        for (int block = 0; block < BLOCK_COUNT; block++)
        {
            uint8_t rgba[4];
            source_color(kind, block, rgba);
            palette[block] = RGBA(rgba[0], rgba[1], rgba[2], rgba[3]);
        }
    }

    if (kind == KIND_P4_ABGR)
    {
        for (int y = 0; y < TEX_SIZE; y++)
        {
            for (int x = 0; x < TEX_SIZE; x++)
            {
                int block = (y / BLOCK_SIZE) * BLOCKS_PER_ROW + (x / BLOCK_SIZE);
                uint8_t *byte = &g_textureMemory[(y * TEX_SIZE + x) / 2];
                if ((x & 1) == 0)
                {
                    *byte = (uint8_t)((*byte & 0xF0) | block);
                }
                else
                {
                    *byte = (uint8_t)((*byte & 0x0F) | (block << 4));
                }
            }
        }
    }
    else
    {
        for (int elementY = 0; elementY < elementsPerRow; elementY++)
        {
            for (int elementX = 0; elementX < elementsPerRow; elementX++)
            {
                int texelX = elementX * elementTexels;
                int texelY = elementY * elementTexels;
                int block = (texelY / BLOCK_SIZE) * BLOCKS_PER_ROW + (texelX / BLOCK_SIZE);
                uint8_t rgba[4];
                source_color(kind, block, rgba);
                uint8_t *linear = &g_linearElements[(elementY * elementsPerRow + elementX) * elementSize];
                encode_element(kind, rgba, block, linear);
            }
        }
        int strideElements = testCase->layout == LAYOUT_STRIDED ? STRIDED_TEXELS : elementsPerRow;
        for (int elementY = 0; elementY < elementsPerRow; elementY++)
        {
            for (int elementX = 0; elementX < elementsPerRow; elementX++)
            {
                uint32_t destination;
                if (testCase->layout == LAYOUT_SWIZZLED)
                {
                    destination = morton_index((uint32_t)elementX, (uint32_t)elementY);
                }
                else if (testCase->layout == LAYOUT_TILED)
                {
                    destination = tiled_index((uint32_t)elementX, (uint32_t)elementY, (uint32_t)elementsPerRow);
                }
                else
                {
                    destination = (uint32_t)(elementY * strideElements + elementX);
                }
                memcpy(&g_textureMemory[destination * (uint32_t)elementSize],
                       &g_linearElements[(elementY * elementsPerRow + elementX) * elementSize], (size_t)elementSize);
            }
        }
    }

    int initResult;
    if (testCase->layout == LAYOUT_SWIZZLED)
    {
        initResult = sceGxmTextureInitSwizzled(texture, g_textureMemory, testCase->format, TEX_SIZE, TEX_SIZE, 0);
    }
    else if (testCase->layout == LAYOUT_TILED)
    {
        initResult = sceGxmTextureInitTiled(texture, g_textureMemory, testCase->format, TEX_SIZE, TEX_SIZE, 0);
    }
    else if (testCase->layout == LAYOUT_STRIDED)
    {
        initResult = sceGxmTextureInitLinearStrided(texture, g_textureMemory, testCase->format, TEX_SIZE, TEX_SIZE,
                                                    STRIDED_TEXELS * (unsigned int)elementSize);
    }
    else
    {
        initResult = sceGxmTextureInitLinear(texture, g_textureMemory, testCase->format, TEX_SIZE, TEX_SIZE, 0);
    }
    if (initResult < 0)
    {
        result_add(AREA, testCase->name, RESULT_FAIL, "texture init 0x%08X", (unsigned int)initResult);
        return false;
    }
    if (kind == KIND_P8_ABGR || kind == KIND_P4_ABGR)
    {
        sceGxmTextureSetPalette(texture, palette);
    }
    sceGxmTextureSetMinFilter(texture, SCE_GXM_TEXTURE_FILTER_POINT);
    sceGxmTextureSetMagFilter(texture, SCE_GXM_TEXTURE_FILTER_POINT);
    return true;
}

static void draw_textured_quad(float x, float y, float width, float height, float u0, float v0, float u1,
                               float v1, const SceGxmTexture *texture)
{
    TextureVertex vertices[6];
    vertices[0] = (TextureVertex){x, y, 0.0f, u0, v0};
    vertices[1] = (TextureVertex){x + width, y, 0.0f, u1, v0};
    vertices[2] = (TextureVertex){x, y + height, 0.0f, u0, v1};
    vertices[3] = (TextureVertex){x + width, y, 0.0f, u1, v0};
    vertices[4] = (TextureVertex){x + width, y + height, 0.0f, u1, v1};
    vertices[5] = (TextureVertex){x, y + height, 0.0f, u0, v1};
    gfx_draw_textured(vertices, 6, SCE_GXM_PRIMITIVE_TRIANGLES, texture, false, 0);
}

static bool color_near(uint32_t actual, int r, int g, int b, int a, int tolerance)
{
    return abs(COLOR_R(actual) - r) <= tolerance && abs(COLOR_G(actual) - g) <= tolerance &&
           abs(COLOR_B(actual) - b) <= tolerance && (a < 0 || abs(COLOR_A(actual) - a) <= tolerance);
}

static void run_texture_case(Offscreen *target, const TextureCase *testCase, uint32_t *palette)
{
    SceGxmTexture texture;
    if (!build_texture(testCase, &texture, palette))
    {
        return;
    }
    gfx_offscreen_begin(target);
    gfx_clear(RGBA(1, 2, 3, 4));
    gfx_set_blend(BLEND_NONE);
    draw_textured_quad(0, 0, TEX_SIZE, TEX_SIZE, 0.0f, 0.0f, 1.0f, 1.0f, &texture);
    gfx_offscreen_end(target);

    int mismatches = 0;
    int firstBlock = -1;
    uint32_t firstActual = 0;
    uint8_t firstWanted[4] = {0, 0, 0, 0};
    for (int block = 0; block < BLOCK_COUNT; block++)
    {
        int x = (block % BLOCKS_PER_ROW) * BLOCK_SIZE + BLOCK_SIZE / 2;
        int y = (block / BLOCKS_PER_ROW) * BLOCK_SIZE + BLOCK_SIZE / 2;
        uint8_t rgba[4];
        uint8_t wanted[4];
        source_color(testCase->kind, block, rgba);
        expected_color(testCase->kind, rgba, wanted);
        uint32_t actual = gfx_offscreen_pixel(target, x, y);
        if (!color_near(actual, wanted[0], wanted[1], wanted[2], wanted[3], tolerance_for(testCase->kind)))
        {
            if (firstBlock < 0)
            {
                firstBlock = block;
                firstActual = actual;
                memcpy(firstWanted, wanted, 4);
            }
            mismatches++;
        }
    }
    if (mismatches == 0)
    {
        result_add(AREA, testCase->name, RESULT_OK, "16/16 blocks");
    }
    else
    {
        result_add(AREA, testCase->name, RESULT_FAIL, "%d/16 wrong; b%d got %08X want %02X%02X%02X%02X", mismatches,
                   firstBlock, (unsigned int)firstActual, firstWanted[3], firstWanted[2], firstWanted[1],
                   firstWanted[0]);
    }
}

static void check_textures(Offscreen *target)
{
    /* The palette lives after the texel data in the same GPU block (64-byte aligned). */
    uint32_t *palette = (uint32_t *)(g_textureMemory + TEXTURE_MEMORY_SIZE - 1024);
    for (size_t index = 0; index < ARRAY_COUNT(TEXTURE_CASES); index++)
    {
        run_texture_case(target, &TEXTURE_CASES[index], palette);
    }
    result_add(AREA, "tex_PVRT_YUV_cube", RESULT_SKIP, "not covered yet (PVRTC, YUV420, cube maps)");
}

static void check_clear_and_primitives(Offscreen *target)
{
    gfx_offscreen_begin(target);
    gfx_clear(RGBA(255, 0, 255, 255));
    gfx_offscreen_end(target);
    uint32_t corner = gfx_offscreen_pixel(target, 0, 0);
    uint32_t opposite = gfx_offscreen_pixel(target, TEX_SIZE - 1, TEX_SIZE - 1);
    result_check(AREA, "clear_readback", color_near(corner, 255, 0, 255, 255, 0) &&
                                            color_near(opposite, 255, 0, 255, 255, 0),
                 "%08X %08X", (unsigned int)corner, (unsigned int)opposite);

    gfx_offscreen_begin(target);
    gfx_clear(RGBA(0, 0, 0, 255));
    gfx_set_blend(BLEND_NONE);
    ColorVertex triangle[3] = {
        {8, 8, 0, RGBA(255, 0, 0, 255)},
        {56, 8, 0, RGBA(255, 0, 0, 255)},
        {8, 56, 0, RGBA(255, 0, 0, 255)},
    };
    gfx_draw_color(triangle, 3, SCE_GXM_PRIMITIVE_TRIANGLES);
    gfx_offscreen_end(target);
    uint32_t inside = gfx_offscreen_pixel(target, 16, 16);
    uint32_t outside = gfx_offscreen_pixel(target, 50, 50);
    result_check(AREA, "triangle_flat", color_near(inside, 255, 0, 0, 255, 0) && color_near(outside, 0, 0, 0, 255, 0),
                 "in %08X out %08X", (unsigned int)inside, (unsigned int)outside);

    gfx_offscreen_begin(target);
    gfx_clear(RGBA(0, 0, 0, 255));
    gfx_set_blend(BLEND_NONE);
    ColorVertex gouraud[3] = {
        {0, 0, 0, RGBA(255, 0, 0, 255)},
        {96, 0, 0, RGBA(0, 255, 0, 255)},
        {0, 96, 0, RGBA(0, 0, 255, 255)},
    };
    gfx_draw_color(gouraud, 3, SCE_GXM_PRIMITIVE_TRIANGLES);
    gfx_offscreen_end(target);
    uint32_t centroid = gfx_offscreen_pixel(target, 32, 32);
    result_check(AREA, "vertex_color_interpolation", color_near(centroid, 85, 85, 85, 255, 12), "%08X",
                 (unsigned int)centroid);

    gfx_offscreen_begin(target);
    gfx_clear(RGBA(0, 0, 0, 255));
    gfx_set_blend(BLEND_NONE);
    ColorVertex strip[4] = {
        {16, 16, 0, RGBA(0, 255, 0, 255)},
        {48, 16, 0, RGBA(0, 255, 0, 255)},
        {16, 48, 0, RGBA(0, 255, 0, 255)},
        {48, 48, 0, RGBA(0, 255, 0, 255)},
    };
    gfx_draw_color(strip, 4, SCE_GXM_PRIMITIVE_TRIANGLE_STRIP);
    gfx_offscreen_end(target);
    uint32_t stripCenter = gfx_offscreen_pixel(target, 40, 40);
    uint32_t stripOutside = gfx_offscreen_pixel(target, 8, 8);
    result_check(AREA, "triangle_strip", color_near(stripCenter, 0, 255, 0, 255, 0) &&
                                            color_near(stripOutside, 0, 0, 0, 255, 0),
                 "%08X %08X", (unsigned int)stripCenter, (unsigned int)stripOutside);

    gfx_offscreen_begin(target);
    gfx_clear(RGBA(0, 0, 0, 255));
    gfx_set_blend(BLEND_NONE);
    ColorVertex fan[5] = {
        {32, 32, 0, RGBA(0, 0, 255, 255)},
        {8, 8, 0, RGBA(0, 0, 255, 255)},
        {56, 8, 0, RGBA(0, 0, 255, 255)},
        {56, 56, 0, RGBA(0, 0, 255, 255)},
        {8, 56, 0, RGBA(0, 0, 255, 255)},
    };
    gfx_draw_color(fan, 5, SCE_GXM_PRIMITIVE_TRIANGLE_FAN);
    gfx_offscreen_end(target);
    uint32_t fanRight = gfx_offscreen_pixel(target, 50, 32);
    uint32_t fanLeft = gfx_offscreen_pixel(target, 14, 32);
    result_check(AREA, "triangle_fan", color_near(fanRight, 0, 0, 255, 255, 0) && color_near(fanLeft, 0, 0, 0, 255, 0),
                 "right %08X left %08X", (unsigned int)fanRight, (unsigned int)fanLeft);

    gfx_offscreen_begin(target);
    gfx_clear(RGBA(0, 0, 0, 255));
    gfx_set_blend(BLEND_NONE);
    sceGxmSetFrontPolygonMode(gfx_context(), SCE_GXM_POLYGON_MODE_LINE);
    sceGxmSetBackPolygonMode(gfx_context(), SCE_GXM_POLYGON_MODE_LINE);
    sceGxmSetFrontPointLineWidth(gfx_context(), 1);
    sceGxmSetBackPointLineWidth(gfx_context(), 1);
    ColorVertex line[2] = {
        {4, 32.5f, 0, RGBA(255, 255, 255, 255)},
        {60, 32.5f, 0, RGBA(255, 255, 255, 255)},
    };
    gfx_draw_color(line, 2, SCE_GXM_PRIMITIVE_LINES);
    gfx_offscreen_end(target);
    int litPixels = 0;
    for (int y = 30; y <= 34; y++)
    {
        for (int x = 0; x < TEX_SIZE; x++)
        {
            if (COLOR_R(gfx_offscreen_pixel(target, x, y)) > 128)
            {
                litPixels++;
            }
        }
    }
    result_check(AREA, "lines", litPixels >= 50 && litPixels <= 120, "%d lit pixels (56 expected)", litPixels);

    bool visible[2][2];
    const SceGxmCullMode modes[2] = {SCE_GXM_CULL_CW, SCE_GXM_CULL_CCW};
    for (int mode = 0; mode < 2; mode++)
    {
        gfx_offscreen_begin(target);
        gfx_clear(RGBA(0, 0, 0, 255));
        gfx_set_blend(BLEND_NONE);
        sceGxmSetCullMode(gfx_context(), modes[mode]);
        ColorVertex first[3] = {
            {2, 2, 0, RGBA(255, 255, 255, 255)},
            {30, 2, 0, RGBA(255, 255, 255, 255)},
            {2, 30, 0, RGBA(255, 255, 255, 255)},
        };
        ColorVertex second[3] = {
            {34, 2, 0, RGBA(255, 255, 255, 255)},
            {34, 30, 0, RGBA(255, 255, 255, 255)},
            {62, 2, 0, RGBA(255, 255, 255, 255)},
        };
        gfx_draw_color(first, 3, SCE_GXM_PRIMITIVE_TRIANGLES);
        gfx_draw_color(second, 3, SCE_GXM_PRIMITIVE_TRIANGLES);
        gfx_offscreen_end(target);
        visible[mode][0] = COLOR_R(gfx_offscreen_pixel(target, 8, 8)) > 128;
        visible[mode][1] = COLOR_R(gfx_offscreen_pixel(target, 40, 8)) > 128;
    }
    bool cullOk = visible[0][0] != visible[0][1] && visible[1][0] != visible[1][1] && visible[0][0] != visible[1][0];
    result_check(AREA, "cull_cw_ccw", cullOk, "CW keeps %d%d, CCW keeps %d%d", visible[0][0], visible[0][1],
                 visible[1][0], visible[1][1]);
}

static void build_sampler_texture(SceGxmTexture *texture)
{
    uint32_t *texels = (uint32_t *)g_textureMemory;
    memset(g_textureMemory, 0, TEXTURE_MEMORY_SIZE);
    for (int y = 0; y < 8; y++)
    {
        for (int x = 0; x < 8; x++)
        {
            texels[y * 8 + x] = x < 4 ? RGBA(0, 0, 0, 255) : RGBA(255, 255, 255, 255);
        }
    }
    sceGxmTextureInitLinear(texture, texels, SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR, 8, 8, 0);
}

static void check_samplers(Offscreen *target)
{
    SceGxmTexture texture;
    build_sampler_texture(&texture);

    sceGxmTextureSetMinFilter(&texture, SCE_GXM_TEXTURE_FILTER_POINT);
    sceGxmTextureSetMagFilter(&texture, SCE_GXM_TEXTURE_FILTER_POINT);
    gfx_offscreen_begin(target);
    gfx_set_blend(BLEND_NONE);
    draw_textured_quad(0, 0, TEX_SIZE, TEX_SIZE, 0.0f, 0.0f, 1.0f, 1.0f, &texture);
    gfx_offscreen_end(target);
    int pointLeft = COLOR_R(gfx_offscreen_pixel(target, 31, 10));
    int pointRight = COLOR_R(gfx_offscreen_pixel(target, 32, 10));
    result_check(AREA, "sampler_point", pointLeft == 0 && pointRight == 255, "x31 %d x32 %d", pointLeft, pointRight);

    sceGxmTextureSetMagFilter(&texture, SCE_GXM_TEXTURE_FILTER_LINEAR);
    sceGxmTextureSetMinFilter(&texture, SCE_GXM_TEXTURE_FILTER_LINEAR);
    gfx_offscreen_begin(target);
    gfx_set_blend(BLEND_NONE);
    draw_textured_quad(0, 0, TEX_SIZE, TEX_SIZE, 0.0f, 0.0f, 1.0f, 1.0f, &texture);
    gfx_offscreen_end(target);
    int linearLeft = COLOR_R(gfx_offscreen_pixel(target, 31, 10));
    int linearRight = COLOR_R(gfx_offscreen_pixel(target, 32, 10));
    result_check(AREA, "sampler_linear", linearLeft >= 96 && linearLeft <= 128 && linearRight >= 128 &&
                                            linearRight <= 160,
                 "x31 %d x32 %d (112, 143 expected)", linearLeft, linearRight);

    sceGxmTextureSetMagFilter(&texture, SCE_GXM_TEXTURE_FILTER_POINT);
    sceGxmTextureSetMinFilter(&texture, SCE_GXM_TEXTURE_FILTER_POINT);
    const SceGxmTextureAddrMode modes[3] = {SCE_GXM_TEXTURE_ADDR_REPEAT, SCE_GXM_TEXTURE_ADDR_MIRROR,
                                            SCE_GXM_TEXTURE_ADDR_CLAMP};
    const char *names[3] = {"sampler_addr_repeat", "sampler_addr_mirror", "sampler_addr_clamp"};
    const int wantedAt39[3] = {0, 255, 255};
    const int wantedAt55[3] = {255, 0, 255};
    for (int mode = 0; mode < 3; mode++)
    {
        sceGxmTextureSetUAddrMode(&texture, modes[mode]);
        gfx_offscreen_begin(target);
        gfx_set_blend(BLEND_NONE);
        draw_textured_quad(0, 0, TEX_SIZE, TEX_SIZE, 0.0f, 0.0f, 2.0f, 1.0f, &texture);
        gfx_offscreen_end(target);
        int at39 = COLOR_R(gfx_offscreen_pixel(target, 39, 10));
        int at55 = COLOR_R(gfx_offscreen_pixel(target, 55, 10));
        result_check(AREA, names[mode], at39 == wantedAt39[mode] && at55 == wantedAt55[mode],
                     "u1.23 %d u1.73 %d (want %d %d)", at39, at55, wantedAt39[mode], wantedAt55[mode]);
    }

    /* Mipmaps: 16x16 red level 0, 8x8 green level 1 straight after it. */
    uint32_t *levels = (uint32_t *)g_textureMemory;
    for (int index = 0; index < 16 * 16; index++)
    {
        levels[index] = RGBA(255, 0, 0, 255);
    }
    for (int index = 0; index < 8 * 8; index++)
    {
        levels[256 + index] = RGBA(0, 255, 0, 255);
    }
    SceGxmTexture mipTexture;
    sceGxmTextureInitLinear(&mipTexture, levels, SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR, 16, 16, 2);
    sceGxmTextureSetMinFilter(&mipTexture, SCE_GXM_TEXTURE_FILTER_POINT);
    sceGxmTextureSetMagFilter(&mipTexture, SCE_GXM_TEXTURE_FILTER_POINT);
    sceGxmTextureSetMipFilter(&mipTexture, SCE_GXM_TEXTURE_MIP_FILTER_ENABLED);
    gfx_offscreen_begin(target);
    gfx_clear(RGBA(0, 0, 0, 255));
    gfx_set_blend(BLEND_NONE);
    draw_textured_quad(0, 0, 16, 16, 0.0f, 0.0f, 1.0f, 1.0f, &mipTexture);
    draw_textured_quad(32, 0, 8, 8, 0.0f, 0.0f, 1.0f, 1.0f, &mipTexture);
    gfx_offscreen_end(target);
    uint32_t level0 = gfx_offscreen_pixel(target, 8, 8);
    uint32_t level1 = gfx_offscreen_pixel(target, 36, 4);
    result_check(AREA, "mipmap_levels", color_near(level0, 255, 0, 0, 255, 2) && color_near(level1, 0, 255, 0, 255, 2),
                 "1:1 %08X 1:2 %08X", (unsigned int)level0, (unsigned int)level1);
}

static void check_blending(Offscreen *target)
{
    typedef struct BlendCase
    {
        const char *name;
        BlendMode mode;
        uint32_t background;
        uint32_t source;
        int wanted[3];
    } BlendCase;
    const BlendCase cases[] = {
        {"blend_alpha", BLEND_ALPHA, RGBA(0, 0, 255, 255), RGBA(255, 0, 0, 128), {128, 0, 127}},
        {"blend_add", BLEND_ADD, RGBA(0, 0, 255, 255), RGBA(0, 128, 0, 255), {0, 128, 255}},
        {"blend_multiply", BLEND_MULTIPLY, RGBA(255, 0, 255, 255), RGBA(128, 128, 128, 255), {128, 0, 128}},
        {"color_mask_red", BLEND_MASK_RED, RGBA(0, 0, 0, 255), RGBA(255, 255, 255, 255), {255, 0, 0}},
        {"color_mask_none", BLEND_MASK_NONE, RGBA(10, 20, 30, 255), RGBA(255, 255, 255, 255), {10, 20, 30}},
    };
    for (size_t index = 0; index < ARRAY_COUNT(cases); index++)
    {
        gfx_offscreen_begin(target);
        gfx_clear(cases[index].background);
        gfx_set_blend(cases[index].mode);
        gfx_draw_rect(8, 8, 48, 48, cases[index].source);
        gfx_offscreen_end(target);
        uint32_t pixel = gfx_offscreen_pixel(target, 32, 32);
        result_check(AREA, cases[index].name,
                     color_near(pixel, cases[index].wanted[0], cases[index].wanted[1], cases[index].wanted[2], -1, 3),
                     "%08X want %d,%d,%d", (unsigned int)pixel, cases[index].wanted[0], cases[index].wanted[1],
                     cases[index].wanted[2]);
    }
}

static void check_depth_stencil(Offscreen *target)
{
    SceGxmContext *context = gfx_context();
    gfx_offscreen_begin(target);
    gfx_clear(RGBA(0, 0, 0, 255));
    gfx_set_blend(BLEND_NONE);
    sceGxmSetFrontDepthFunc(context, SCE_GXM_DEPTH_FUNC_LESS);
    sceGxmSetBackDepthFunc(context, SCE_GXM_DEPTH_FUNC_LESS);
    sceGxmSetFrontDepthWriteEnable(context, SCE_GXM_DEPTH_WRITE_ENABLED);
    sceGxmSetBackDepthWriteEnable(context, SCE_GXM_DEPTH_WRITE_ENABLED);
    gfx_draw_rect_z(0, 0, 64, 64, 0.0f, RGBA(0, 255, 0, 255));
    gfx_draw_rect_z(0, 0, 64, 32, 0.5f, RGBA(255, 0, 0, 255));
    gfx_draw_rect_z(0, 32, 64, 32, -0.5f, RGBA(0, 0, 255, 255));
    gfx_offscreen_end(target);
    uint32_t farRejected = gfx_offscreen_pixel(target, 32, 16);
    uint32_t nearAccepted = gfx_offscreen_pixel(target, 32, 48);
    result_check(AREA, "depth_less", color_near(farRejected, 0, 255, 0, 255, 0) &&
                                        color_near(nearAccepted, 0, 0, 255, 255, 0),
                 "far %08X near %08X", (unsigned int)farRejected, (unsigned int)nearAccepted);

    gfx_offscreen_begin(target);
    gfx_clear(RGBA(0, 0, 0, 255));
    gfx_set_blend(BLEND_NONE);
    sceGxmSetFrontDepthFunc(context, SCE_GXM_DEPTH_FUNC_LESS);
    sceGxmSetFrontDepthWriteEnable(context, SCE_GXM_DEPTH_WRITE_ENABLED);
    gfx_draw_rect_z(0, 0, 64, 64, 0.0f, RGBA(0, 255, 0, 255));
    sceGxmSetFrontDepthFunc(context, SCE_GXM_DEPTH_FUNC_GREATER);
    gfx_draw_rect_z(0, 0, 32, 64, 0.5f, RGBA(255, 0, 0, 255));
    sceGxmSetFrontDepthFunc(context, SCE_GXM_DEPTH_FUNC_LESS);
    sceGxmSetFrontDepthWriteEnable(context, SCE_GXM_DEPTH_WRITE_DISABLED);
    gfx_draw_rect_z(32, 0, 32, 64, -0.5f, RGBA(0, 0, 255, 255));
    gfx_draw_rect_z(32, 0, 32, 32, -0.2f, RGBA(255, 255, 0, 255));
    gfx_offscreen_end(target);
    uint32_t greater = gfx_offscreen_pixel(target, 16, 32);
    uint32_t noWriteTop = gfx_offscreen_pixel(target, 48, 16);
    uint32_t noWriteBottom = gfx_offscreen_pixel(target, 48, 48);
    result_check(AREA, "depth_greater", color_near(greater, 255, 0, 0, 255, 0), "%08X", (unsigned int)greater);
    result_check(AREA, "depth_write_disabled", color_near(noWriteTop, 255, 255, 0, 255, 0) &&
                                                  color_near(noWriteBottom, 0, 0, 255, 255, 0),
                 "top %08X bottom %08X", (unsigned int)noWriteTop, (unsigned int)noWriteBottom);

    gfx_offscreen_begin(target);
    gfx_clear(RGBA(0, 0, 255, 255));
    sceGxmSetFrontStencilRef(context, 1);
    sceGxmSetBackStencilRef(context, 1);
    sceGxmSetFrontStencilFunc(context, SCE_GXM_STENCIL_FUNC_ALWAYS, SCE_GXM_STENCIL_OP_KEEP, SCE_GXM_STENCIL_OP_KEEP,
                              SCE_GXM_STENCIL_OP_REPLACE, 0xFF, 0xFF);
    sceGxmSetBackStencilFunc(context, SCE_GXM_STENCIL_FUNC_ALWAYS, SCE_GXM_STENCIL_OP_KEEP, SCE_GXM_STENCIL_OP_KEEP,
                             SCE_GXM_STENCIL_OP_REPLACE, 0xFF, 0xFF);
    gfx_set_blend(BLEND_MASK_NONE);
    gfx_draw_rect(0, 0, 32, 64, RGBA(255, 255, 255, 255));
    sceGxmSetFrontStencilFunc(context, SCE_GXM_STENCIL_FUNC_EQUAL, SCE_GXM_STENCIL_OP_KEEP, SCE_GXM_STENCIL_OP_KEEP,
                              SCE_GXM_STENCIL_OP_KEEP, 0xFF, 0xFF);
    sceGxmSetBackStencilFunc(context, SCE_GXM_STENCIL_FUNC_EQUAL, SCE_GXM_STENCIL_OP_KEEP, SCE_GXM_STENCIL_OP_KEEP,
                             SCE_GXM_STENCIL_OP_KEEP, 0xFF, 0xFF);
    gfx_set_blend(BLEND_NONE);
    gfx_draw_rect(0, 0, 64, 64, RGBA(255, 0, 0, 255));
    gfx_offscreen_end(target);
    uint32_t masked = gfx_offscreen_pixel(target, 16, 32);
    uint32_t unmasked = gfx_offscreen_pixel(target, 48, 32);
    result_check(AREA, "stencil_replace_equal", color_near(masked, 255, 0, 0, 255, 0) &&
                                                   color_near(unmasked, 0, 0, 255, 255, 0),
                 "inside %08X outside %08X", (unsigned int)masked, (unsigned int)unmasked);
}

static void check_render_to_texture(Offscreen *target)
{
    Offscreen source;
    if (!gfx_offscreen_create(&source, TEX_SIZE, TEX_SIZE, false))
    {
        result_add(AREA, "render_to_texture", RESULT_FAIL, "cannot create source target");
        return;
    }
    gfx_offscreen_begin(&source);
    gfx_clear(RGBA(0, 0, 0, 255));
    gfx_set_blend(BLEND_NONE);
    gfx_draw_rect(0, 0, 32, 32, RGBA(255, 0, 0, 255));
    gfx_draw_rect(32, 0, 32, 32, RGBA(0, 255, 0, 255));
    gfx_draw_rect(0, 32, 32, 32, RGBA(0, 0, 255, 255));
    gfx_draw_rect(32, 32, 32, 32, RGBA(255, 255, 255, 255));
    gfx_offscreen_end(&source);

    SceGxmTexture texture;
    sceGxmTextureInitLinear(&texture, source.colorData, SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR, TEX_SIZE, TEX_SIZE, 0);
    sceGxmTextureSetMinFilter(&texture, SCE_GXM_TEXTURE_FILTER_POINT);
    sceGxmTextureSetMagFilter(&texture, SCE_GXM_TEXTURE_FILTER_POINT);
    gfx_offscreen_begin(target);
    gfx_clear(RGBA(9, 9, 9, 255));
    gfx_set_blend(BLEND_NONE);
    draw_textured_quad(0, 0, TEX_SIZE, TEX_SIZE, 0.0f, 0.0f, 1.0f, 1.0f, &texture);
    gfx_offscreen_end(target);
    bool quadrants = color_near(gfx_offscreen_pixel(target, 16, 16), 255, 0, 0, 255, 0) &&
                     color_near(gfx_offscreen_pixel(target, 48, 16), 0, 255, 0, 255, 0) &&
                     color_near(gfx_offscreen_pixel(target, 16, 48), 0, 0, 255, 255, 0) &&
                     color_near(gfx_offscreen_pixel(target, 48, 48), 255, 255, 255, 255, 0);
    result_check(AREA, "render_to_texture", quadrants, "%08X %08X %08X %08X",
                 (unsigned int)gfx_offscreen_pixel(target, 16, 16), (unsigned int)gfx_offscreen_pixel(target, 48, 16),
                 (unsigned int)gfx_offscreen_pixel(target, 16, 48), (unsigned int)gfx_offscreen_pixel(target, 48, 48));
    gfx_offscreen_destroy(&source);
}

static int count_edge_pixels(const Offscreen *target)
{
    int partial = 0;
    for (int y = 0; y < TEX_SIZE; y++)
    {
        for (int x = 0; x < TEX_SIZE; x++)
        {
            int red = COLOR_R(gfx_offscreen_pixel(target, x, y));
            if (red > 20 && red < 235)
            {
                partial++;
            }
        }
    }
    return partial;
}

static void draw_edge_triangle(void)
{
    ColorVertex triangle[3] = {
        {0, 0, 0, RGBA(255, 255, 255, 255)},
        {64, 0, 0, RGBA(255, 255, 255, 255)},
        {0, 50, 0, RGBA(255, 255, 255, 255)},
    };
    gfx_draw_color(triangle, 3, SCE_GXM_PRIMITIVE_TRIANGLES);
}

static void check_msaa(Offscreen *target)
{
    gfx_offscreen_begin(target);
    gfx_clear(RGBA(0, 0, 0, 255));
    gfx_set_blend(BLEND_NONE);
    draw_edge_triangle();
    gfx_offscreen_end(target);
    int plainEdges = count_edge_pixels(target);

    Offscreen msaaTarget;
    if (!gfx_offscreen_create(&msaaTarget, TEX_SIZE, TEX_SIZE, true))
    {
        result_add(AREA, "msaa_4x_edges", RESULT_FAIL, "cannot create 4x MSAA target");
        return;
    }
    gfx_offscreen_begin(&msaaTarget);
    gfx_clear(RGBA(0, 0, 0, 255));
    gfx_set_blend(BLEND_NONE);
    draw_edge_triangle();
    gfx_offscreen_end(&msaaTarget);
    int msaaEdges = count_edge_pixels(&msaaTarget);
    uint32_t inside = gfx_offscreen_pixel(&msaaTarget, 8, 8);
    result_check(AREA, "no_msaa_hard_edges", plainEdges == 0, "%d partial pixels", plainEdges);
    result_check(AREA, "msaa_4x_edges", msaaEdges >= 20 && color_near(inside, 255, 255, 255, 255, 0),
                 "%d partial pixels, inside %08X", msaaEdges, (unsigned int)inside);
    gfx_offscreen_destroy(&msaaTarget);
}

void test_graphics_run(void)
{
    results_clear_area(AREA);
    app_show_progress("Graphics", "sceGxm checks (offscreen + readback) ...");
    g_textureMemory = (uint8_t *)gfx_alloc_gpu(TEXTURE_MEMORY_SIZE, false, &g_textureUid);
    Offscreen target;
    bool created = g_textureMemory != NULL && gfx_offscreen_create(&target, TEX_SIZE, TEX_SIZE, false);
    result_check(AREA, "offscreen_target", created, "%dx%d A8B8G8R8 + S8D24", TEX_SIZE, TEX_SIZE);
    if (!created)
    {
        return;
    }
    check_clear_and_primitives(&target);
    check_textures(&target);
    check_samplers(&target);
    check_blending(&target);
    check_depth_stencil(&target);
    check_render_to_texture(&target);
    check_msaa(&target);
    gfx_offscreen_destroy(&target);
    gfx_free_gpu(g_textureUid, g_textureMemory);
    g_textureUid = -1;
    g_textureMemory = NULL;
}
