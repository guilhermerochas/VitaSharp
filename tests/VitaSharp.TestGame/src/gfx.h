/*
 * Raw sceGxm renderer: display buffers, shader patcher, the four shader pairs, offscreen targets with CPU
 * readback, and simple 2D drawing (rectangles, 8x8 bitmap text) in pixel coordinates of the current target.
 */
#ifndef VSTEST_GFX_H
#define VSTEST_GFX_H

#include "common.h"

#include <psp2/gxm.h>
#include <psp2/types.h>

#define DISPLAY_WIDTH 960
#define DISPLAY_HEIGHT 544
#define DISPLAY_STRIDE 960
#define DISPLAY_BUFFER_COUNT 3

/* Colours are packed as 0xAABBGGRR (byte 0 is red), the layout of U8U8U8U8_ABGR / A8B8G8R8. */
#define RGBA(r, g, b, a) \
    ((uint32_t)(r) | ((uint32_t)(g) << 8) | ((uint32_t)(b) << 16) | ((uint32_t)(a) << 24))
#define COLOR_R(c) ((int)((c) & 0xFF))
#define COLOR_G(c) ((int)(((c) >> 8) & 0xFF))
#define COLOR_B(c) ((int)(((c) >> 16) & 0xFF))
#define COLOR_A(c) ((int)(((c) >> 24) & 0xFF))

typedef struct ColorVertex
{
    float x;
    float y;
    float z;
    uint32_t color;
} ColorVertex;

typedef struct TextureVertex
{
    float x;
    float y;
    float z;
    float u;
    float v;
} TextureVertex;

typedef enum BlendMode
{
    BLEND_NONE = 0,
    BLEND_ALPHA,
    BLEND_ADD,
    BLEND_MULTIPLY,
    BLEND_MASK_RED,
    BLEND_MASK_NONE,
    BLEND_COUNT
} BlendMode;

typedef struct Offscreen
{
    int width;
    int height;
    bool msaa;
    SceUID colorUid;
    void *colorData;
    SceUID depthUid;
    void *depthData;
    SceGxmColorSurface colorSurface;
    SceGxmDepthStencilSurface depthSurface;
    SceGxmRenderTarget *renderTarget;
} Offscreen;

bool gfx_init(void);
void gfx_shutdown(void);
SceGxmContext *gfx_context(void);

/* GPU-visible memory, mapped for reads and writes (cdram: video memory, else uncached main memory). */
void *gfx_alloc_gpu(size_t size, bool cdram, SceUID *uid);
void gfx_free_gpu(SceUID uid, void *data);

/* Per-frame scratch memory (vertices); reset after each finished scene. */
void *gfx_frame_alloc(size_t size, size_t alignment);

/* Display frame: begins the scene and clears it; frame_end also lets the common dialogs draw. */
void gfx_frame_begin(uint32_t clearColor);
void gfx_frame_end(void);
uint64_t gfx_frame_count(void);

/* Offscreen target (A8B8G8R8 linear colour, S8D24 depth/stencil); begin/end wrap one scene and end waits for the GPU. */
bool gfx_offscreen_create(Offscreen *target, int width, int height, bool msaa);
void gfx_offscreen_destroy(Offscreen *target);
void gfx_offscreen_begin(Offscreen *target);
void gfx_offscreen_end(Offscreen *target);
uint32_t gfx_offscreen_pixel(const Offscreen *target, int x, int y);

/* State of the current scene. */
void gfx_set_blend(BlendMode mode);
void gfx_reset_state(void);

/* Drawing in pixel coordinates of the current target; z is in normalised device units (-1 near .. 1 far). */
void gfx_clear(uint32_t color);
void gfx_draw_color(const ColorVertex *vertices, int count, SceGxmPrimitiveType primitive);
void gfx_draw_textured(const TextureVertex *vertices, int count, SceGxmPrimitiveType primitive,
                       const SceGxmTexture *texture, bool tinted, uint32_t tintColor);
void gfx_draw_rect(float x, float y, float width, float height, uint32_t color);
void gfx_draw_rect_z(float x, float y, float width, float height, float z, uint32_t color);
void gfx_draw_text(float x, float y, int scale, uint32_t color, const char *text);
void gfx_printf(float x, float y, int scale, uint32_t color, const char *format, ...)
    __attribute__((format(printf, 5, 6)));

#endif
