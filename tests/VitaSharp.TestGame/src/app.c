#include "app.h"

#include <string.h>

#include <psp2/common_dialog.h>

#include "gfx.h"

#define LIST_LINE_HEIGHT 18
#define LIST_LINES 24

static uint32_t g_previousButtons = 0;

void app_read_pad(PadState *state)
{
    memset(&state->raw, 0, sizeof(state->raw));
    sceCtrlPeekBufferPositive(0, &state->raw, 1);
    state->held = state->raw.buttons;
    state->pressed = state->held & ~g_previousButtons;
    g_previousButtons = state->held;
}

void app_show_progress(const char *title, const char *message)
{
    gfx_frame_begin(COLOR_BACKGROUND);
    gfx_draw_text(24, 20, 3, COLOR_TEXT, title);
    gfx_draw_text(24, 80, 2, COLOR_DIM, message);
    gfx_frame_end();
}

static uint32_t status_color(ResultStatus status)
{
    if (status == RESULT_OK)
    {
        return COLOR_OK;
    }
    if (status == RESULT_FAIL)
    {
        return COLOR_FAIL;
    }
    return COLOR_SKIP;
}

static int count_lines(const char *area)
{
    int lines = 0;
    for (int index = 0; index < results_count(); index++)
    {
        const ResultEntry *entry = results_get(index);
        if (area == NULL || strcmp(entry->area, area) == 0)
        {
            lines++;
        }
    }
    return lines;
}

void app_draw_result_list(const char *area, int firstLine, float top)
{
    int line = 0;
    int drawn = 0;
    for (int index = 0; index < results_count() && drawn < LIST_LINES; index++)
    {
        const ResultEntry *entry = results_get(index);
        if (area != NULL && strcmp(entry->area, area) != 0)
        {
            continue;
        }
        if (line < firstLine)
        {
            line++;
            continue;
        }
        float y = top + (float)(drawn * LIST_LINE_HEIGHT);
        gfx_printf(24, y, 2, status_color(entry->status), "%-4s", result_status_name(entry->status));
        if (area == NULL)
        {
            gfx_printf(104, y, 2, COLOR_TEXT, "%-8.8s %-22.22s %.14s", entry->area, entry->check, entry->value);
        }
        else
        {
            gfx_printf(104, y, 2, COLOR_TEXT, "%-24.24s %.30s", entry->check, entry->value);
        }
        line++;
        drawn++;
    }
}

void app_show_results_screen(const char *title, const char *area)
{
    int firstLine = 0;
    int totalLines = count_lines(area);
    while (true)
    {
        PadState pad;
        app_read_pad(&pad);
        if (pad.pressed & SCE_CTRL_CIRCLE)
        {
            return;
        }
        if ((pad.held & SCE_CTRL_DOWN) && firstLine + LIST_LINES < totalLines)
        {
            firstLine++;
        }
        if ((pad.held & SCE_CTRL_UP) && firstLine > 0)
        {
            firstLine--;
        }
        int okCount;
        int failCount;
        int skipCount;
        results_totals(area, &okCount, &failCount, &skipCount);
        gfx_frame_begin(COLOR_BACKGROUND);
        gfx_draw_text(24, 12, 3, COLOR_TEXT, title);
        gfx_printf(24, 46, 2, COLOR_DIM, "OK %d  FAIL %d  SKIP %d   (Up/Down scroll, Circle back)", okCount, failCount,
                   skipCount);
        app_draw_result_list(area, firstLine, 76);
        gfx_frame_end();
    }
}

int app_run_dialog(const char *title, int (*getStatus)(void), int timeoutSeconds)
{
    uint64_t start = time_now_us();
    int status = getStatus();
    while (status == SCE_COMMON_DIALOG_STATUS_RUNNING)
    {
        if (time_now_us() - start > (uint64_t)timeoutSeconds * 1000000ull)
        {
            break;
        }
        gfx_frame_begin(COLOR_BACKGROUND);
        gfx_draw_text(24, 20, 3, COLOR_TEXT, title);
        gfx_draw_text(24, 80, 2, COLOR_DIM, "System dialog open: answer it to continue.");
        gfx_frame_end();
        status = getStatus();
    }
    return status;
}
