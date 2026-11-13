/*
 * UI helpers shared by the screens: pad input, progress frames, result lists, and the test entry points.
 */
#ifndef VSTEST_APP_H
#define VSTEST_APP_H

#include "common.h"
#include "results.h"

#include <psp2/ctrl.h>

#define COLOR_BACKGROUND 0xFF302018u
#define COLOR_TEXT 0xFFFFFFFFu
#define COLOR_DIM 0xFFB0B0B0u
#define COLOR_OK 0xFF40E040u
#define COLOR_FAIL 0xFF4040FFu
#define COLOR_SKIP 0xFF40E0E0u
#define COLOR_HIGHLIGHT 0xFF805020u

typedef struct PadState
{
    uint32_t held;
    uint32_t pressed;
    SceCtrlData raw;
} PadState;

/* Reads the pad (positive logic); pressed holds the buttons that went down since the last call. */
void app_read_pad(PadState *state);

/* Draws one frame with a title and a status line (used while checks run). */
void app_show_progress(const char *title, const char *message);

/* Draws the results of one area (or all when area is NULL) starting at a scroll offset. */
void app_draw_result_list(const char *area, int firstLine, float top);

/* Shows the results of an area until Circle is pressed (Up/Down scroll). */
void app_show_results_screen(const char *title, const char *area);

/*
 * Renders frames (so the system dialog can draw) while getStatus() reports RUNNING, up to timeoutSeconds.
 * Returns the last status.
 */
int app_run_dialog(const char *title, int (*getStatus)(void), int timeoutSeconds);

/* Test areas. */
void test_graphics_run(void);
void test_input_run(int timeoutSeconds);
void test_input_live_screen(void);
void test_audio_run(void);
void test_files_run(void);
void test_threads_run(void);
void test_system_run(bool withDialogs);
void test_perf_run(int seconds, bool interactive);

#endif
