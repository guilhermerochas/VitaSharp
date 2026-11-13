#include "gfx.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <psp2/common_dialog.h>
#include <psp2/display.h>
#include <psp2/kernel/sysmem.h>

#include "../third_party/font8x8/font8x8_basic.h"

#define FRAME_POOL_SIZE (8 * 1024 * 1024)
#define INDEX_COUNT 65535
#define PATCHER_BUFFER_SIZE (64 * 1024)
#define PATCHER_VERTEX_USSE_SIZE (64 * 1024)
#define PATCHER_FRAGMENT_USSE_SIZE (64 * 1024)
#define FONT_ATLAS_WIDTH 128
#define FONT_ATLAS_HEIGHT 64

extern const SceGxmProgram gxp_clear_v;
extern const SceGxmProgram gxp_clear_f;
extern const SceGxmProgram gxp_color_v;
extern const SceGxmProgram gxp_color_f;
extern const SceGxmProgram gxp_texture_v;
extern const SceGxmProgram gxp_texture_f;
extern const SceGxmProgram gxp_texture_tint_f;

typedef enum FragmentKind
{
    FRAGMENT_CLEAR = 0,
    FRAGMENT_COLOR,
    FRAGMENT_TEXTURE,
    FRAGMENT_TEXTURE_TINT,
    FRAGMENT_KIND_COUNT
} FragmentKind;

typedef struct DisplayData
{
    void *address;
} DisplayData;

typedef struct CurrentTarget
{
    int width;
    int height;
    bool msaa;
    bool inScene;
    bool isDisplay;
} CurrentTarget;

static SceGxmContext *g_context;
static SceGxmRenderTarget *g_displayRenderTarget;
static SceGxmShaderPatcher *g_patcher;

static SceUID g_vdmRingUid;
static SceUID g_vertexRingUid;
static SceUID g_fragmentRingUid;
static SceUID g_fragmentUsseRingUid;
static void *g_contextHostMemory;

static SceUID g_displayUid[DISPLAY_BUFFER_COUNT];
static void *g_displayData[DISPLAY_BUFFER_COUNT];
static SceGxmColorSurface g_displaySurface[DISPLAY_BUFFER_COUNT];
static SceGxmSyncObject *g_displaySync[DISPLAY_BUFFER_COUNT];
static int g_frontBuffer;
static int g_backBuffer;
static uint64_t g_frameCount;

static SceUID g_depthUid;
static void *g_depthData;
static SceGxmDepthStencilSurface g_depthSurface;

static SceUID g_patcherBufferUid;
static SceUID g_patcherVertexUsseUid;
static SceUID g_patcherFragmentUsseUid;

static SceGxmShaderPatcherId g_clearVertexId;
static SceGxmShaderPatcherId g_colorVertexId;
static SceGxmShaderPatcherId g_textureVertexId;
static SceGxmShaderPatcherId g_fragmentIds[FRAGMENT_KIND_COUNT];

static SceGxmVertexProgram *g_clearVertexProgram;
static SceGxmVertexProgram *g_colorVertexProgram;
static SceGxmVertexProgram *g_textureVertexProgram;
static SceGxmFragmentProgram *g_fragmentPrograms[FRAGMENT_KIND_COUNT][BLEND_COUNT][2];

static const SceGxmProgramParameter *g_colorWvpParam;
static const SceGxmProgramParameter *g_textureWvpParam;
static const SceGxmProgramParameter *g_clearColorParam;
static const SceGxmProgramParameter *g_tintColorParam;

static SceUID g_indexUid;
static uint16_t *g_indices;

static SceUID g_framePoolUid;
static uint8_t *g_framePool;
static size_t g_framePoolUsed;

static SceUID g_fontUid;
static void *g_fontData;
static SceGxmTexture g_fontTexture;

static CurrentTarget g_target;
static BlendMode g_blend = BLEND_ALPHA;

static const float IDENTITY_MATRIX[16] = {
    1.0f, 0.0f, 0.0f, 0.0f,
    0.0f, 1.0f, 0.0f, 0.0f,
    0.0f, 0.0f, 1.0f, 0.0f,
    0.0f, 0.0f, 0.0f, 1.0f,
};

SceGxmContext *gfx_context(void)
{
    return g_context;
}

uint64_t gfx_frame_count(void)
{
    return g_frameCount;
}

void *gfx_alloc_gpu(size_t size, bool cdram, SceUID *uid)
{
    SceKernelMemBlockType type;
    if (cdram)
    {
        type = SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW;
        size = ALIGN_UP(size, 256 * 1024);
    }
    else
    {
        type = SCE_KERNEL_MEMBLOCK_TYPE_USER_RW_UNCACHE;
        size = ALIGN_UP(size, 4 * 1024);
    }
    *uid = sceKernelAllocMemBlock("vstest_gpu", type, size, NULL);
    if (*uid < 0)
    {
        log_line("gfx: sceKernelAllocMemBlock(%u) failed: 0x%08X", (unsigned int)size, (unsigned int)*uid);
        return NULL;
    }
    void *data = NULL;
    sceKernelGetMemBlockBase(*uid, &data);
    int mapResult = sceGxmMapMemory(data, size, SCE_GXM_MEMORY_ATTRIB_READ | SCE_GXM_MEMORY_ATTRIB_WRITE);
    if (mapResult < 0)
    {
        log_line("gfx: sceGxmMapMemory failed: 0x%08X", (unsigned int)mapResult);
        sceKernelFreeMemBlock(*uid);
        *uid = -1;
        return NULL;
    }
    return data;
}

void gfx_free_gpu(SceUID uid, void *data)
{
    if (uid < 0)
    {
        return;
    }
    sceGxmUnmapMemory(data);
    sceKernelFreeMemBlock(uid);
}

static void *alloc_usse(size_t size, bool vertex, SceUID *uid, unsigned int *offset)
{
    size = ALIGN_UP(size, 4 * 1024);
    *uid = sceKernelAllocMemBlock("vstest_usse", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW_UNCACHE, size, NULL);
    if (*uid < 0)
    {
        return NULL;
    }
    void *data = NULL;
    sceKernelGetMemBlockBase(*uid, &data);
    if (vertex)
    {
        sceGxmMapVertexUsseMemory(data, size, offset);
    }
    else
    {
        sceGxmMapFragmentUsseMemory(data, size, offset);
    }
    return data;
}

static void display_callback(const void *callbackData)
{
    const DisplayData *display = (const DisplayData *)callbackData;
    SceDisplayFrameBuf frameBuffer;
    memset(&frameBuffer, 0, sizeof(frameBuffer));
    frameBuffer.size = sizeof(frameBuffer);
    frameBuffer.base = display->address;
    frameBuffer.pitch = DISPLAY_STRIDE;
    frameBuffer.pixelformat = SCE_DISPLAY_PIXELFORMAT_A8B8G8R8;
    frameBuffer.width = DISPLAY_WIDTH;
    frameBuffer.height = DISPLAY_HEIGHT;
    sceDisplaySetFrameBuf(&frameBuffer, SCE_DISPLAY_SETBUF_NEXTFRAME);
    sceDisplayWaitVblankStart();
}

static void *patcher_host_alloc(void *userData, unsigned int size)
{
    (void)userData;
    return malloc(size);
}

static void patcher_host_free(void *userData, void *memory)
{
    (void)userData;
    free(memory);
}

static bool create_context(void)
{
    SceGxmInitializeParams initializeParams;
    memset(&initializeParams, 0, sizeof(initializeParams));
    initializeParams.flags = 0;
    initializeParams.displayQueueMaxPendingCount = DISPLAY_BUFFER_COUNT - 1;
    initializeParams.displayQueueCallback = display_callback;
    initializeParams.displayQueueCallbackDataSize = sizeof(DisplayData);
    initializeParams.parameterBufferSize = SCE_GXM_DEFAULT_PARAMETER_BUFFER_SIZE;
    int result = sceGxmInitialize(&initializeParams);
    if (result < 0)
    {
        log_line("gfx: sceGxmInitialize failed: 0x%08X", (unsigned int)result);
        return false;
    }

    void *vdmRing = gfx_alloc_gpu(SCE_GXM_DEFAULT_VDM_RING_BUFFER_SIZE, false, &g_vdmRingUid);
    void *vertexRing = gfx_alloc_gpu(SCE_GXM_DEFAULT_VERTEX_RING_BUFFER_SIZE, false, &g_vertexRingUid);
    void *fragmentRing = gfx_alloc_gpu(SCE_GXM_DEFAULT_FRAGMENT_RING_BUFFER_SIZE, false, &g_fragmentRingUid);
    unsigned int fragmentUsseOffset = 0;
    void *fragmentUsseRing = alloc_usse(SCE_GXM_DEFAULT_FRAGMENT_USSE_RING_BUFFER_SIZE, false,
                                        &g_fragmentUsseRingUid, &fragmentUsseOffset);
    g_contextHostMemory = malloc(SCE_GXM_MINIMUM_CONTEXT_HOST_MEM_SIZE);

    SceGxmContextParams contextParams;
    memset(&contextParams, 0, sizeof(contextParams));
    contextParams.hostMem = g_contextHostMemory;
    contextParams.hostMemSize = SCE_GXM_MINIMUM_CONTEXT_HOST_MEM_SIZE;
    contextParams.vdmRingBufferMem = vdmRing;
    contextParams.vdmRingBufferMemSize = SCE_GXM_DEFAULT_VDM_RING_BUFFER_SIZE;
    contextParams.vertexRingBufferMem = vertexRing;
    contextParams.vertexRingBufferMemSize = SCE_GXM_DEFAULT_VERTEX_RING_BUFFER_SIZE;
    contextParams.fragmentRingBufferMem = fragmentRing;
    contextParams.fragmentRingBufferMemSize = SCE_GXM_DEFAULT_FRAGMENT_RING_BUFFER_SIZE;
    contextParams.fragmentUsseRingBufferMem = fragmentUsseRing;
    contextParams.fragmentUsseRingBufferMemSize = SCE_GXM_DEFAULT_FRAGMENT_USSE_RING_BUFFER_SIZE;
    contextParams.fragmentUsseRingBufferOffset = fragmentUsseOffset;
    result = sceGxmCreateContext(&contextParams, &g_context);
    if (result < 0)
    {
        log_line("gfx: sceGxmCreateContext failed: 0x%08X", (unsigned int)result);
        return false;
    }
    return true;
}

static bool create_render_target(int width, int height, bool msaa, SceGxmRenderTarget **renderTarget)
{
    SceGxmRenderTargetParams params;
    memset(&params, 0, sizeof(params));
    params.flags = 0;
    params.width = width;
    params.height = height;
    params.scenesPerFrame = 1;
    params.multisampleMode = msaa ? SCE_GXM_MULTISAMPLE_4X : SCE_GXM_MULTISAMPLE_NONE;
    params.multisampleLocations = 0;
    params.driverMemBlock = -1;
    int result = sceGxmCreateRenderTarget(&params, renderTarget);
    if (result < 0)
    {
        log_line("gfx: sceGxmCreateRenderTarget(%dx%d msaa=%d) failed: 0x%08X", width, height, msaa,
                 (unsigned int)result);
        return false;
    }
    return true;
}

static bool create_depth_surface(int width, int height, bool msaa, SceUID *uid, void **data,
                                 SceGxmDepthStencilSurface *surface)
{
    int sampleScale = msaa ? 2 : 1;
    unsigned int depthWidth = ALIGN_UP((unsigned int)(width * sampleScale), SCE_GXM_TILE_SIZEX);
    unsigned int depthHeight = ALIGN_UP((unsigned int)(height * sampleScale), SCE_GXM_TILE_SIZEY);
    *data = gfx_alloc_gpu(4 * depthWidth * depthHeight, false, uid);
    if (*data == NULL)
    {
        return false;
    }
    int result = sceGxmDepthStencilSurfaceInit(surface, SCE_GXM_DEPTH_STENCIL_FORMAT_S8D24,
                                               SCE_GXM_DEPTH_STENCIL_SURFACE_TILED, depthWidth, *data, NULL);
    if (result < 0)
    {
        log_line("gfx: sceGxmDepthStencilSurfaceInit failed: 0x%08X", (unsigned int)result);
        return false;
    }
    sceGxmDepthStencilSurfaceSetBackgroundDepth(surface, 1.0f);
    sceGxmDepthStencilSurfaceSetBackgroundStencil(surface, 0);
    return true;
}

static bool create_display_buffers(void)
{
    if (!create_render_target(DISPLAY_WIDTH, DISPLAY_HEIGHT, false, &g_displayRenderTarget))
    {
        return false;
    }
    for (int index = 0; index < DISPLAY_BUFFER_COUNT; index++)
    {
        g_displayData[index] = gfx_alloc_gpu(4 * DISPLAY_STRIDE * DISPLAY_HEIGHT, true, &g_displayUid[index]);
        if (g_displayData[index] == NULL)
        {
            return false;
        }
        memset(g_displayData[index], 0, 4 * DISPLAY_STRIDE * DISPLAY_HEIGHT);
        sceGxmColorSurfaceInit(&g_displaySurface[index], SCE_GXM_COLOR_FORMAT_A8B8G8R8, SCE_GXM_COLOR_SURFACE_LINEAR,
                               SCE_GXM_COLOR_SURFACE_SCALE_NONE, SCE_GXM_OUTPUT_REGISTER_SIZE_32BIT, DISPLAY_WIDTH,
                               DISPLAY_HEIGHT, DISPLAY_STRIDE, g_displayData[index]);
        sceGxmSyncObjectCreate(&g_displaySync[index]);
    }
    g_frontBuffer = DISPLAY_BUFFER_COUNT - 1;
    g_backBuffer = 0;
    return create_depth_surface(DISPLAY_WIDTH, DISPLAY_HEIGHT, false, &g_depthUid, &g_depthData, &g_depthSurface);
}

static bool register_program(const SceGxmProgram *program, const char *name, SceGxmShaderPatcherId *id)
{
    int result = sceGxmProgramCheck(program);
    if (result < 0)
    {
        log_line("gfx: sceGxmProgramCheck(%s) failed: 0x%08X", name, (unsigned int)result);
        return false;
    }
    result = sceGxmShaderPatcherRegisterProgram(g_patcher, program, id);
    if (result < 0)
    {
        log_line("gfx: register %s failed: 0x%08X", name, (unsigned int)result);
        return false;
    }
    return true;
}

static const SceGxmProgramParameter *find_parameter(const SceGxmProgram *program, const char *name)
{
    const SceGxmProgramParameter *parameter = sceGxmProgramFindParameterByName(program, name);
    if (parameter == NULL)
    {
        log_line("gfx: shader parameter %s not found", name);
    }
    return parameter;
}

static bool create_vertex_programs(void)
{
    const SceGxmProgramParameter *clearPosition = find_parameter(&gxp_clear_v, "aPosition");
    SceGxmVertexAttribute clearAttribute;
    clearAttribute.streamIndex = 0;
    clearAttribute.offset = 0;
    clearAttribute.format = SCE_GXM_ATTRIBUTE_FORMAT_F32;
    clearAttribute.componentCount = 2;
    clearAttribute.regIndex = sceGxmProgramParameterGetResourceIndex(clearPosition);
    SceGxmVertexStream clearStream;
    clearStream.stride = 2 * sizeof(float);
    clearStream.indexSource = SCE_GXM_INDEX_SOURCE_INDEX_16BIT;
    int result = sceGxmShaderPatcherCreateVertexProgram(g_patcher, g_clearVertexId, &clearAttribute, 1, &clearStream,
                                                        1, &g_clearVertexProgram);
    if (result < 0)
    {
        log_line("gfx: clear vertex program failed: 0x%08X", (unsigned int)result);
        return false;
    }

    const SceGxmProgramParameter *colorPosition = find_parameter(&gxp_color_v, "aPosition");
    const SceGxmProgramParameter *colorColor = find_parameter(&gxp_color_v, "aColor");
    SceGxmVertexAttribute colorAttributes[2];
    colorAttributes[0].streamIndex = 0;
    colorAttributes[0].offset = 0;
    colorAttributes[0].format = SCE_GXM_ATTRIBUTE_FORMAT_F32;
    colorAttributes[0].componentCount = 3;
    colorAttributes[0].regIndex = sceGxmProgramParameterGetResourceIndex(colorPosition);
    colorAttributes[1].streamIndex = 0;
    colorAttributes[1].offset = 3 * sizeof(float);
    colorAttributes[1].format = SCE_GXM_ATTRIBUTE_FORMAT_U8N;
    colorAttributes[1].componentCount = 4;
    colorAttributes[1].regIndex = sceGxmProgramParameterGetResourceIndex(colorColor);
    SceGxmVertexStream colorStream;
    colorStream.stride = sizeof(ColorVertex);
    colorStream.indexSource = SCE_GXM_INDEX_SOURCE_INDEX_16BIT;
    result = sceGxmShaderPatcherCreateVertexProgram(g_patcher, g_colorVertexId, colorAttributes, 2, &colorStream, 1,
                                                    &g_colorVertexProgram);
    if (result < 0)
    {
        log_line("gfx: color vertex program failed: 0x%08X", (unsigned int)result);
        return false;
    }

    const SceGxmProgramParameter *texturePosition = find_parameter(&gxp_texture_v, "aPosition");
    const SceGxmProgramParameter *textureCoord = find_parameter(&gxp_texture_v, "aTexcoord");
    SceGxmVertexAttribute textureAttributes[2];
    textureAttributes[0].streamIndex = 0;
    textureAttributes[0].offset = 0;
    textureAttributes[0].format = SCE_GXM_ATTRIBUTE_FORMAT_F32;
    textureAttributes[0].componentCount = 3;
    textureAttributes[0].regIndex = sceGxmProgramParameterGetResourceIndex(texturePosition);
    textureAttributes[1].streamIndex = 0;
    textureAttributes[1].offset = 3 * sizeof(float);
    textureAttributes[1].format = SCE_GXM_ATTRIBUTE_FORMAT_F32;
    textureAttributes[1].componentCount = 2;
    textureAttributes[1].regIndex = sceGxmProgramParameterGetResourceIndex(textureCoord);
    SceGxmVertexStream textureStream;
    textureStream.stride = sizeof(TextureVertex);
    textureStream.indexSource = SCE_GXM_INDEX_SOURCE_INDEX_16BIT;
    result = sceGxmShaderPatcherCreateVertexProgram(g_patcher, g_textureVertexId, textureAttributes, 2,
                                                    &textureStream, 1, &g_textureVertexProgram);
    if (result < 0)
    {
        log_line("gfx: texture vertex program failed: 0x%08X", (unsigned int)result);
        return false;
    }

    g_colorWvpParam = find_parameter(&gxp_color_v, "wvp");
    g_textureWvpParam = find_parameter(&gxp_texture_v, "wvp");
    g_clearColorParam = find_parameter(&gxp_clear_f, "uClearColor");
    g_tintColorParam = find_parameter(&gxp_texture_tint_f, "uTintColor");
    return true;
}

static void blend_info_for(BlendMode mode, SceGxmBlendInfo *info)
{
    memset(info, 0, sizeof(*info));
    info->colorMask = SCE_GXM_COLOR_MASK_ALL;
    info->colorFunc = SCE_GXM_BLEND_FUNC_ADD;
    info->alphaFunc = SCE_GXM_BLEND_FUNC_ADD;
    info->colorSrc = SCE_GXM_BLEND_FACTOR_ONE;
    info->colorDst = SCE_GXM_BLEND_FACTOR_ZERO;
    info->alphaSrc = SCE_GXM_BLEND_FACTOR_ONE;
    info->alphaDst = SCE_GXM_BLEND_FACTOR_ZERO;
    if (mode == BLEND_ALPHA)
    {
        info->colorSrc = SCE_GXM_BLEND_FACTOR_SRC_ALPHA;
        info->colorDst = SCE_GXM_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
        info->alphaSrc = SCE_GXM_BLEND_FACTOR_ONE;
        info->alphaDst = SCE_GXM_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA;
    }
    else if (mode == BLEND_ADD)
    {
        info->colorSrc = SCE_GXM_BLEND_FACTOR_ONE;
        info->colorDst = SCE_GXM_BLEND_FACTOR_ONE;
        info->alphaSrc = SCE_GXM_BLEND_FACTOR_ONE;
        info->alphaDst = SCE_GXM_BLEND_FACTOR_ONE;
    }
    else if (mode == BLEND_MULTIPLY)
    {
        info->colorSrc = SCE_GXM_BLEND_FACTOR_DST_COLOR;
        info->colorDst = SCE_GXM_BLEND_FACTOR_ZERO;
        info->alphaSrc = SCE_GXM_BLEND_FACTOR_ONE;
        info->alphaDst = SCE_GXM_BLEND_FACTOR_ZERO;
    }
    else if (mode == BLEND_MASK_RED)
    {
        info->colorMask = SCE_GXM_COLOR_MASK_R;
    }
    else if (mode == BLEND_MASK_NONE)
    {
        info->colorMask = SCE_GXM_COLOR_MASK_NONE;
    }
}

static const SceGxmProgram *vertex_gxp_for_fragment(FragmentKind kind)
{
    if (kind == FRAGMENT_CLEAR)
    {
        return &gxp_clear_v;
    }
    if (kind == FRAGMENT_COLOR)
    {
        return &gxp_color_v;
    }
    return &gxp_texture_v;
}

static SceGxmFragmentProgram *fragment_program(FragmentKind kind, BlendMode blend, bool msaa)
{
    int msaaIndex = msaa ? 1 : 0;
    SceGxmFragmentProgram *program = g_fragmentPrograms[kind][blend][msaaIndex];
    if (program != NULL)
    {
        return program;
    }
    SceGxmBlendInfo blendInfo;
    blend_info_for(blend, &blendInfo);
    int result = sceGxmShaderPatcherCreateFragmentProgram(
        g_patcher, g_fragmentIds[kind], SCE_GXM_OUTPUT_REGISTER_FORMAT_UCHAR4,
        msaa ? SCE_GXM_MULTISAMPLE_4X : SCE_GXM_MULTISAMPLE_NONE, blend == BLEND_NONE ? NULL : &blendInfo,
        vertex_gxp_for_fragment(kind), &program);
    if (result < 0)
    {
        log_line("gfx: fragment program kind %d blend %d msaa %d failed: 0x%08X", kind, blend, msaa,
                 (unsigned int)result);
        return NULL;
    }
    g_fragmentPrograms[kind][blend][msaaIndex] = program;
    return program;
}

static bool create_patcher(void)
{
    void *patcherBuffer = gfx_alloc_gpu(PATCHER_BUFFER_SIZE, false, &g_patcherBufferUid);
    unsigned int vertexUsseOffset = 0;
    unsigned int fragmentUsseOffset = 0;
    void *vertexUsse = alloc_usse(PATCHER_VERTEX_USSE_SIZE, true, &g_patcherVertexUsseUid, &vertexUsseOffset);
    void *fragmentUsse = alloc_usse(PATCHER_FRAGMENT_USSE_SIZE, false, &g_patcherFragmentUsseUid,
                                    &fragmentUsseOffset);

    SceGxmShaderPatcherParams params;
    memset(&params, 0, sizeof(params));
    params.userData = NULL;
    params.hostAllocCallback = patcher_host_alloc;
    params.hostFreeCallback = patcher_host_free;
    params.bufferAllocCallback = NULL;
    params.bufferFreeCallback = NULL;
    params.bufferMem = patcherBuffer;
    params.bufferMemSize = PATCHER_BUFFER_SIZE;
    params.vertexUsseAllocCallback = NULL;
    params.vertexUsseFreeCallback = NULL;
    params.vertexUsseMem = vertexUsse;
    params.vertexUsseMemSize = PATCHER_VERTEX_USSE_SIZE;
    params.vertexUsseOffset = vertexUsseOffset;
    params.fragmentUsseAllocCallback = NULL;
    params.fragmentUsseFreeCallback = NULL;
    params.fragmentUsseMem = fragmentUsse;
    params.fragmentUsseMemSize = PATCHER_FRAGMENT_USSE_SIZE;
    params.fragmentUsseOffset = fragmentUsseOffset;
    int result = sceGxmShaderPatcherCreate(&params, &g_patcher);
    if (result < 0)
    {
        log_line("gfx: sceGxmShaderPatcherCreate failed: 0x%08X", (unsigned int)result);
        return false;
    }

    bool registered = register_program(&gxp_clear_v, "clear_v", &g_clearVertexId);
    registered = registered && register_program(&gxp_color_v, "color_v", &g_colorVertexId);
    registered = registered && register_program(&gxp_texture_v, "texture_v", &g_textureVertexId);
    registered = registered && register_program(&gxp_clear_f, "clear_f", &g_fragmentIds[FRAGMENT_CLEAR]);
    registered = registered && register_program(&gxp_color_f, "color_f", &g_fragmentIds[FRAGMENT_COLOR]);
    registered = registered && register_program(&gxp_texture_f, "texture_f", &g_fragmentIds[FRAGMENT_TEXTURE]);
    registered = registered &&
                 register_program(&gxp_texture_tint_f, "texture_tint_f", &g_fragmentIds[FRAGMENT_TEXTURE_TINT]);
    return registered && create_vertex_programs();
}

static bool create_font(void)
{
    size_t size = FONT_ATLAS_WIDTH * FONT_ATLAS_HEIGHT * 4;
    g_fontData = gfx_alloc_gpu(size, false, &g_fontUid);
    if (g_fontData == NULL)
    {
        return false;
    }
    uint32_t *texels = (uint32_t *)g_fontData;
    for (int character = 0; character < 128; character++)
    {
        int cellX = (character % 16) * 8;
        int cellY = (character / 16) * 8;
        for (int row = 0; row < 8; row++)
        {
            unsigned char bits = (unsigned char)font8x8_basic[character][row];
            for (int column = 0; column < 8; column++)
            {
                bool set = (bits >> column) & 1;
                texels[(cellY + row) * FONT_ATLAS_WIDTH + cellX + column] = set ? 0xFFFFFFFFu : 0x00FFFFFFu;
            }
        }
    }
    sceGxmTextureInitLinear(&g_fontTexture, g_fontData, SCE_GXM_TEXTURE_FORMAT_U8U8U8U8_ABGR, FONT_ATLAS_WIDTH,
                            FONT_ATLAS_HEIGHT, 0);
    sceGxmTextureSetMinFilter(&g_fontTexture, SCE_GXM_TEXTURE_FILTER_POINT);
    sceGxmTextureSetMagFilter(&g_fontTexture, SCE_GXM_TEXTURE_FILTER_POINT);
    return true;
}

bool gfx_init(void)
{
    if (!create_context())
    {
        return false;
    }
    if (!create_display_buffers())
    {
        return false;
    }
    if (!create_patcher())
    {
        return false;
    }
    g_indices = (uint16_t *)gfx_alloc_gpu(INDEX_COUNT * sizeof(uint16_t), false, &g_indexUid);
    g_framePool = (uint8_t *)gfx_alloc_gpu(FRAME_POOL_SIZE, false, &g_framePoolUid);
    if (g_indices == NULL || g_framePool == NULL)
    {
        return false;
    }
    for (int index = 0; index < INDEX_COUNT; index++)
    {
        g_indices[index] = (uint16_t)index;
    }
    g_framePoolUsed = 0;
    return create_font();
}

void gfx_shutdown(void)
{
    sceGxmFinish(g_context);
    sceGxmDisplayQueueFinish();
}

void *gfx_frame_alloc(size_t size, size_t alignment)
{
    size_t start = ALIGN_UP(g_framePoolUsed, alignment);
    if (start + size > FRAME_POOL_SIZE)
    {
        log_line("gfx: frame pool exhausted (%u bytes wanted)", (unsigned int)size);
        return NULL;
    }
    g_framePoolUsed = start + size;
    return g_framePool + start;
}

static void set_viewport(int width, int height)
{
    float halfWidth = (float)width * 0.5f;
    float halfHeight = (float)height * 0.5f;
    sceGxmSetViewport(g_context, halfWidth, halfWidth, halfHeight, -halfHeight, 0.5f, 0.5f);
    sceGxmSetRegionClip(g_context, SCE_GXM_REGION_CLIP_OUTSIDE, 0, 0, width - 1, height - 1);
}

void gfx_reset_state(void)
{
    set_viewport(g_target.width, g_target.height);
    sceGxmSetCullMode(g_context, SCE_GXM_CULL_NONE);
    sceGxmSetFrontDepthFunc(g_context, SCE_GXM_DEPTH_FUNC_ALWAYS);
    sceGxmSetBackDepthFunc(g_context, SCE_GXM_DEPTH_FUNC_ALWAYS);
    sceGxmSetFrontDepthWriteEnable(g_context, SCE_GXM_DEPTH_WRITE_DISABLED);
    sceGxmSetBackDepthWriteEnable(g_context, SCE_GXM_DEPTH_WRITE_DISABLED);
    sceGxmSetFrontStencilFunc(g_context, SCE_GXM_STENCIL_FUNC_ALWAYS, SCE_GXM_STENCIL_OP_KEEP,
                              SCE_GXM_STENCIL_OP_KEEP, SCE_GXM_STENCIL_OP_KEEP, 0xFF, 0xFF);
    sceGxmSetBackStencilFunc(g_context, SCE_GXM_STENCIL_FUNC_ALWAYS, SCE_GXM_STENCIL_OP_KEEP,
                             SCE_GXM_STENCIL_OP_KEEP, SCE_GXM_STENCIL_OP_KEEP, 0xFF, 0xFF);
    sceGxmSetFrontPolygonMode(g_context, SCE_GXM_POLYGON_MODE_TRIANGLE_FILL);
    sceGxmSetBackPolygonMode(g_context, SCE_GXM_POLYGON_MODE_TRIANGLE_FILL);
    g_blend = BLEND_ALPHA;
}

void gfx_frame_begin(uint32_t clearColor)
{
    g_target.width = DISPLAY_WIDTH;
    g_target.height = DISPLAY_HEIGHT;
    g_target.msaa = false;
    g_target.inScene = true;
    g_target.isDisplay = true;
    sceGxmBeginScene(g_context, 0, g_displayRenderTarget, NULL, NULL, g_displaySync[g_backBuffer],
                     &g_displaySurface[g_backBuffer], &g_depthSurface);
    gfx_reset_state();
    gfx_clear(clearColor);
}

void gfx_frame_end(void)
{
    sceGxmEndScene(g_context, NULL, NULL);
    g_target.inScene = false;

    SceCommonDialogUpdateParam updateParam;
    memset(&updateParam, 0, sizeof(updateParam));
    updateParam.renderTarget.colorFormat = SCE_GXM_COLOR_FORMAT_A8B8G8R8;
    updateParam.renderTarget.surfaceType = SCE_GXM_COLOR_SURFACE_LINEAR;
    updateParam.renderTarget.width = DISPLAY_WIDTH;
    updateParam.renderTarget.height = DISPLAY_HEIGHT;
    updateParam.renderTarget.strideInPixels = DISPLAY_STRIDE;
    updateParam.renderTarget.colorSurfaceData = g_displayData[g_backBuffer];
    updateParam.renderTarget.depthSurfaceData = g_depthData;
    updateParam.displaySyncObject = g_displaySync[g_backBuffer];
    sceCommonDialogUpdate(&updateParam);

    DisplayData displayData;
    displayData.address = g_displayData[g_backBuffer];
    sceGxmDisplayQueueAddEntry(g_displaySync[g_frontBuffer], g_displaySync[g_backBuffer], &displayData);
    g_frontBuffer = g_backBuffer;
    g_backBuffer = (g_backBuffer + 1) % DISPLAY_BUFFER_COUNT;

    sceGxmFinish(g_context);
    g_framePoolUsed = 0;
    g_frameCount++;
}

bool gfx_offscreen_create(Offscreen *target, int width, int height, bool msaa)
{
    memset(target, 0, sizeof(*target));
    target->width = width;
    target->height = height;
    target->msaa = msaa;
    target->colorUid = -1;
    target->depthUid = -1;
    size_t colorSize = (size_t)width * (size_t)height * 4;
    target->colorData = gfx_alloc_gpu(colorSize, false, &target->colorUid);
    if (target->colorData == NULL)
    {
        return false;
    }
    memset(target->colorData, 0, colorSize);
    int result = sceGxmColorSurfaceInit(&target->colorSurface, SCE_GXM_COLOR_FORMAT_A8B8G8R8,
                                        SCE_GXM_COLOR_SURFACE_LINEAR,
                                        msaa ? SCE_GXM_COLOR_SURFACE_SCALE_MSAA_DOWNSCALE
                                             : SCE_GXM_COLOR_SURFACE_SCALE_NONE,
                                        SCE_GXM_OUTPUT_REGISTER_SIZE_32BIT, width, height, width, target->colorData);
    if (result < 0)
    {
        log_line("gfx: offscreen colour surface failed: 0x%08X", (unsigned int)result);
        return false;
    }
    if (!create_depth_surface(width, height, msaa, &target->depthUid, &target->depthData, &target->depthSurface))
    {
        return false;
    }
    return create_render_target(width, height, msaa, &target->renderTarget);
}

void gfx_offscreen_destroy(Offscreen *target)
{
    if (target->renderTarget != NULL)
    {
        sceGxmDestroyRenderTarget(target->renderTarget);
        target->renderTarget = NULL;
    }
    gfx_free_gpu(target->colorUid, target->colorData);
    gfx_free_gpu(target->depthUid, target->depthData);
    target->colorUid = -1;
    target->depthUid = -1;
}

void gfx_offscreen_begin(Offscreen *target)
{
    g_target.width = target->width;
    g_target.height = target->height;
    g_target.msaa = target->msaa;
    g_target.inScene = true;
    g_target.isDisplay = false;
    sceGxmBeginScene(g_context, 0, target->renderTarget, NULL, NULL, NULL, &target->colorSurface,
                     &target->depthSurface);
    gfx_reset_state();
}

void gfx_offscreen_end(Offscreen *target)
{
    (void)target;
    sceGxmEndScene(g_context, NULL, NULL);
    sceGxmFinish(g_context);
    g_target.inScene = false;
    g_framePoolUsed = 0;
}

uint32_t gfx_offscreen_pixel(const Offscreen *target, int x, int y)
{
    const volatile uint32_t *pixels = (const volatile uint32_t *)target->colorData;
    return pixels[y * target->width + x];
}

void gfx_set_blend(BlendMode mode)
{
    g_blend = mode;
}

static float to_ndc_x(float x)
{
    return 2.0f * x / (float)g_target.width - 1.0f;
}

static float to_ndc_y(float y)
{
    return 1.0f - 2.0f * y / (float)g_target.height;
}

static void set_identity_wvp(const SceGxmProgramParameter *parameter)
{
    void *uniformBuffer = NULL;
    sceGxmReserveVertexDefaultUniformBuffer(g_context, &uniformBuffer);
    sceGxmSetUniformDataF(uniformBuffer, parameter, 0, 16, IDENTITY_MATRIX);
}

static void set_fragment_color(const SceGxmProgramParameter *parameter, uint32_t color)
{
    float values[4];
    values[0] = (float)COLOR_R(color) / 255.0f;
    values[1] = (float)COLOR_G(color) / 255.0f;
    values[2] = (float)COLOR_B(color) / 255.0f;
    values[3] = (float)COLOR_A(color) / 255.0f;
    void *uniformBuffer = NULL;
    sceGxmReserveFragmentDefaultUniformBuffer(g_context, &uniformBuffer);
    sceGxmSetUniformDataF(uniformBuffer, parameter, 0, 4, values);
}

static void draw_sequential(SceGxmPrimitiveType primitive, int count)
{
    int result = sceGxmDraw(g_context, primitive, SCE_GXM_INDEX_FORMAT_U16, g_indices, (unsigned int)count);
    if (result < 0)
    {
        log_line("gfx: sceGxmDraw failed: 0x%08X", (unsigned int)result);
    }
}

void gfx_clear(uint32_t color)
{
    float *positions = (float *)gfx_frame_alloc(6 * 2 * sizeof(float), 16);
    SceGxmFragmentProgram *fragment = fragment_program(FRAGMENT_CLEAR, BLEND_NONE, g_target.msaa);
    if (positions == NULL || fragment == NULL)
    {
        return;
    }
    const float corners[12] = {-1.0f, -1.0f, 1.0f, -1.0f, -1.0f, 1.0f, 1.0f, -1.0f, 1.0f, 1.0f, -1.0f, 1.0f};
    memcpy(positions, corners, sizeof(corners));
    sceGxmSetVertexProgram(g_context, g_clearVertexProgram);
    sceGxmSetFragmentProgram(g_context, fragment);
    set_fragment_color(g_clearColorParam, color);
    sceGxmSetVertexStream(g_context, 0, positions);
    draw_sequential(SCE_GXM_PRIMITIVE_TRIANGLES, 6);
}

void gfx_draw_color(const ColorVertex *vertices, int count, SceGxmPrimitiveType primitive)
{
    if (count <= 0 || count > INDEX_COUNT)
    {
        return;
    }
    ColorVertex *gpuVertices = (ColorVertex *)gfx_frame_alloc((size_t)count * sizeof(ColorVertex), 16);
    SceGxmFragmentProgram *fragment = fragment_program(FRAGMENT_COLOR, g_blend, g_target.msaa);
    if (gpuVertices == NULL || fragment == NULL)
    {
        return;
    }
    for (int index = 0; index < count; index++)
    {
        gpuVertices[index].x = to_ndc_x(vertices[index].x);
        gpuVertices[index].y = to_ndc_y(vertices[index].y);
        gpuVertices[index].z = vertices[index].z;
        gpuVertices[index].color = vertices[index].color;
    }
    sceGxmSetVertexProgram(g_context, g_colorVertexProgram);
    sceGxmSetFragmentProgram(g_context, fragment);
    set_identity_wvp(g_colorWvpParam);
    sceGxmSetVertexStream(g_context, 0, gpuVertices);
    draw_sequential(primitive, count);
}

void gfx_draw_textured(const TextureVertex *vertices, int count, SceGxmPrimitiveType primitive,
                       const SceGxmTexture *texture, bool tinted, uint32_t tintColor)
{
    if (count <= 0 || count > INDEX_COUNT)
    {
        return;
    }
    TextureVertex *gpuVertices = (TextureVertex *)gfx_frame_alloc((size_t)count * sizeof(TextureVertex), 16);
    FragmentKind kind = tinted ? FRAGMENT_TEXTURE_TINT : FRAGMENT_TEXTURE;
    SceGxmFragmentProgram *fragment = fragment_program(kind, g_blend, g_target.msaa);
    if (gpuVertices == NULL || fragment == NULL)
    {
        return;
    }
    for (int index = 0; index < count; index++)
    {
        gpuVertices[index].x = to_ndc_x(vertices[index].x);
        gpuVertices[index].y = to_ndc_y(vertices[index].y);
        gpuVertices[index].z = vertices[index].z;
        gpuVertices[index].u = vertices[index].u;
        gpuVertices[index].v = vertices[index].v;
    }
    sceGxmSetVertexProgram(g_context, g_textureVertexProgram);
    sceGxmSetFragmentProgram(g_context, fragment);
    set_identity_wvp(g_textureWvpParam);
    if (tinted)
    {
        set_fragment_color(g_tintColorParam, tintColor);
    }
    sceGxmSetFragmentTexture(g_context, 0, texture);
    sceGxmSetVertexStream(g_context, 0, gpuVertices);
    draw_sequential(primitive, count);
}

void gfx_draw_rect_z(float x, float y, float width, float height, float z, uint32_t color)
{
    ColorVertex vertices[6];
    float left = x;
    float right = x + width;
    float top = y;
    float bottom = y + height;
    vertices[0] = (ColorVertex){left, top, z, color};
    vertices[1] = (ColorVertex){right, top, z, color};
    vertices[2] = (ColorVertex){left, bottom, z, color};
    vertices[3] = (ColorVertex){right, top, z, color};
    vertices[4] = (ColorVertex){right, bottom, z, color};
    vertices[5] = (ColorVertex){left, bottom, z, color};
    gfx_draw_color(vertices, 6, SCE_GXM_PRIMITIVE_TRIANGLES);
}

void gfx_draw_rect(float x, float y, float width, float height, uint32_t color)
{
    gfx_draw_rect_z(x, y, width, height, 0.0f, color);
}

void gfx_draw_text(float x, float y, int scale, uint32_t color, const char *text)
{
    size_t length = strlen(text);
    if (length == 0)
    {
        return;
    }
    if (length > 1000)
    {
        length = 1000;
    }
    TextureVertex *vertices = (TextureVertex *)malloc(length * 6 * sizeof(TextureVertex));
    if (vertices == NULL)
    {
        return;
    }
    float glyphSize = 8.0f * (float)scale;
    float penX = x;
    float penY = y;
    int vertexCount = 0;
    for (size_t index = 0; index < length; index++)
    {
        unsigned char character = (unsigned char)text[index];
        if (character == '\n')
        {
            penX = x;
            penY += glyphSize + (float)scale;
            continue;
        }
        if (character >= 128)
        {
            character = '?';
        }
        float u0 = (float)((character % 16) * 8) / (float)FONT_ATLAS_WIDTH;
        float v0 = (float)((character / 16) * 8) / (float)FONT_ATLAS_HEIGHT;
        float u1 = u0 + 8.0f / (float)FONT_ATLAS_WIDTH;
        float v1 = v0 + 8.0f / (float)FONT_ATLAS_HEIGHT;
        float left = penX;
        float right = penX + glyphSize;
        float top = penY;
        float bottom = penY + glyphSize;
        vertices[vertexCount + 0] = (TextureVertex){left, top, 0.0f, u0, v0};
        vertices[vertexCount + 1] = (TextureVertex){right, top, 0.0f, u1, v0};
        vertices[vertexCount + 2] = (TextureVertex){left, bottom, 0.0f, u0, v1};
        vertices[vertexCount + 3] = (TextureVertex){right, top, 0.0f, u1, v0};
        vertices[vertexCount + 4] = (TextureVertex){right, bottom, 0.0f, u1, v1};
        vertices[vertexCount + 5] = (TextureVertex){left, bottom, 0.0f, u0, v1};
        vertexCount += 6;
        penX += glyphSize;
    }
    BlendMode previous = g_blend;
    g_blend = BLEND_ALPHA;
    gfx_draw_textured(vertices, vertexCount, SCE_GXM_PRIMITIVE_TRIANGLES, &g_fontTexture, true, color);
    g_blend = previous;
    free(vertices);
}

void gfx_printf(float x, float y, int scale, uint32_t color, const char *format, ...)
{
    char buffer[512];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    gfx_draw_text(x, y, scale, color, buffer);
}
