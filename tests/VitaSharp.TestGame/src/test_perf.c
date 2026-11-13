/*
 * Performance: a heavy GXM scene (about 6000 depth-tested rotating triangles, full-screen alpha layers for fill
 * rate, and text) with an fps / frame-time counter. The automatic run renders it for a few seconds and records the
 * average fps and the worst frame; the menu entry runs it for 60 s (Circle stops).
 */
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "app.h"
#include "gfx.h"

#define AREA "perf"
#define GRID_COLUMNS 48
#define GRID_ROWS 21
#define QUAD_COUNT (GRID_COLUMNS * GRID_ROWS * 3)
#define FILL_LAYERS 4

static ColorVertex g_vertices[QUAD_COUNT * 6];

static void build_scene(float time)
{
    int vertex = 0;
    float cellWidth = (float)DISPLAY_WIDTH / (float)GRID_COLUMNS;
    float cellHeight = (float)(DISPLAY_HEIGHT - 80) / (float)GRID_ROWS;
    for (int layer = 0; layer < 3; layer++)
    {
        for (int row = 0; row < GRID_ROWS; row++)
        {
            for (int column = 0; column < GRID_COLUMNS; column++)
            {
                float centerX = ((float)column + 0.5f) * cellWidth;
                float centerY = 80.0f + ((float)row + 0.5f) * cellHeight;
                float angle = time * (1.0f + (float)layer) + (float)(row * GRID_COLUMNS + column) * 0.07f;
                float size = cellWidth * (0.9f - 0.2f * (float)layer);
                float cosine = cosf(angle) * size * 0.5f;
                float sine = sinf(angle) * size * 0.5f;
                float z = -0.6f + 0.5f * (float)layer + 0.1f * sinf(angle * 0.5f);
                uint8_t red = (uint8_t)(128 + 127 * sinf(angle));
                uint8_t green = (uint8_t)(128 + 127 * sinf(angle + 2.1f));
                uint8_t blue = (uint8_t)(128 + 127 * sinf(angle + 4.2f));
                uint32_t color = RGBA(red, green, blue, 255);
                float cornersX[4] = {-cosine + sine, cosine + sine, cosine - sine, -cosine - sine};
                float cornersY[4] = {-sine - cosine, sine - cosine, sine + cosine, -sine + cosine};
                const int order[6] = {0, 1, 3, 1, 2, 3};
                for (int index = 0; index < 6; index++)
                {
                    int corner = order[index];
                    g_vertices[vertex] = (ColorVertex){centerX + cornersX[corner], centerY + cornersY[corner], z, color};
                    vertex++;
                }
            }
        }
    }
}

static void draw_scene(float time, double fps, double frameMs, double worstMs, int secondsLeft)
{
    SceGxmContext *context = gfx_context();
    gfx_frame_begin(0xFF000000u);
    build_scene(time);
    gfx_set_blend(BLEND_NONE);
    sceGxmSetFrontDepthFunc(context, SCE_GXM_DEPTH_FUNC_LESS);
    sceGxmSetFrontDepthWriteEnable(context, SCE_GXM_DEPTH_WRITE_ENABLED);
    int total = QUAD_COUNT * 6;
    int chunk = 6 * 4096;
    for (int first = 0; first < total; first += chunk)
    {
        int count = total - first < chunk ? total - first : chunk;
        gfx_draw_color(&g_vertices[first], count, SCE_GXM_PRIMITIVE_TRIANGLES);
    }
    sceGxmSetFrontDepthFunc(context, SCE_GXM_DEPTH_FUNC_ALWAYS);
    sceGxmSetFrontDepthWriteEnable(context, SCE_GXM_DEPTH_WRITE_DISABLED);
    gfx_set_blend(BLEND_ALPHA);
    for (int layer = 0; layer < FILL_LAYERS; layer++)
    {
        float offset = 40.0f * sinf(time + (float)layer);
        gfx_draw_rect(offset, 80, DISPLAY_WIDTH, DISPLAY_HEIGHT - 80, RGBA(255, 255, 255, 12));
    }
    gfx_draw_rect(0, 0, DISPLAY_WIDTH, 78, 0xC0000000u);
    gfx_printf(16, 8, 3, COLOR_TEXT, "%.1f fps", fps);
    gfx_printf(16, 44, 2, COLOR_DIM, "frame %.2f ms  worst %.2f ms  %d triangles  %d s left", frameMs, worstMs,
               QUAD_COUNT * 2, secondsLeft);
    gfx_frame_end();
}

void test_perf_run(int seconds, bool interactive)
{
    results_clear_area(AREA);
    uint64_t start = time_now_us();
    uint64_t deadline = start + (uint64_t)seconds * 1000000ull;
    uint64_t previous = start;
    uint64_t windowStart = start;
    int windowFrames = 0;
    double fps = 0.0;
    double frameMs = 0.0;
    double worstMs = 0.0;
    int frames = 0;
    int slowFrames = 0;
    while (time_now_us() < deadline)
    {
        if (interactive)
        {
            PadState pad;
            app_read_pad(&pad);
            if (pad.pressed & SCE_CTRL_CIRCLE)
            {
                break;
            }
        }
        uint64_t now = time_now_us();
        float time = (float)(now - start) / 1000000.0f;
        int secondsLeft = (int)((deadline - now) / 1000000ull);
        draw_scene(time, fps, frameMs, worstMs, secondsLeft);
        uint64_t after = time_now_us();
        frameMs = (double)(after - previous) / 1000.0;
        previous = after;
        frames++;
        /* The first frames include program patching; they do not count for the worst frame. */
        if (frames > 10 && frameMs > worstMs)
        {
            worstMs = frameMs;
        }
        if (frames > 10 && frameMs > 34.0)
        {
            slowFrames++;
        }
        windowFrames++;
        if (after - windowStart >= 500000)
        {
            fps = (double)windowFrames * 1000000.0 / (double)(after - windowStart);
            windowFrames = 0;
            windowStart = after;
        }
    }
    double elapsedSeconds = (double)(time_now_us() - start) / 1000000.0;
    double averageFps = (double)frames / elapsedSeconds;
    result_check(AREA, "heavy_scene_avg_fps", averageFps >= 29.0, "%.1f fps over %.1f s (%d frames)", averageFps,
                 elapsedSeconds, frames);
    result_check(AREA, "heavy_scene_worst_frame", worstMs < 50.0, "%.2f ms", worstMs);
    result_check(AREA, "heavy_scene_slow_frames", frames > 0 && slowFrames * 20 <= frames, "%d of %d frames over 34 ms",
                 slowFrames, frames);
}
