/*
 * VitaSharp test game: runs every automatic check once at boot (writing ux0:data/VSTEST/results.txt), then shows
 * a menu to rerun an area, open the live input screen, or browse the results.
 */
#include <stdio.h>
#include <string.h>

#include <psp2/apputil.h>
#include <psp2/common_dialog.h>
#include <psp2/system_param.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/sysmodule.h>
#include <psp2/touch.h>
#include <psp2/motion.h>

#include "app.h"
#include "gfx.h"

/* newlib heap for malloc (the GPU memory comes from sceKernelAllocMemBlock). */
int _newlib_heap_size_user = 32 * 1024 * 1024;

typedef enum MenuAction
{
    MENU_RUN_ALL = 0,
    MENU_GRAPHICS,
    MENU_INPUT_LIVE,
    MENU_INPUT_CHECKS,
    MENU_AUDIO,
    MENU_FILES,
    MENU_THREADS,
    MENU_SYSTEM,
    MENU_PERFORMANCE,
    MENU_RESULTS,
    MENU_EXIT,
    MENU_COUNT
} MenuAction;

static const char *MENU_LABELS[MENU_COUNT] = {
    "Run all checks",
    "Graphics (sceGxm)",
    "Input: live values",
    "Input: checks",
    "Audio (sceAudioOut)",
    "Files and saves",
    "Threads and sync",
    "System and dialogs",
    "Performance (60 s scene)",
    "All results",
    "Exit",
};

static const char *MENU_AREAS[MENU_COUNT] = {
    NULL, "gfx", NULL, "input", "audio", "files", "threads", "system", "perf", NULL, NULL,
};

static void init_system(void)
{
    sceCtrlSetSamplingMode(SCE_CTRL_MODE_ANALOG_WIDE);
    sceTouchSetSamplingState(SCE_TOUCH_PORT_FRONT, SCE_TOUCH_SAMPLING_STATE_START);
    sceTouchSetSamplingState(SCE_TOUCH_PORT_BACK, SCE_TOUCH_SAMPLING_STATE_START);
    sceMotionStartSampling();

    SceAppUtilInitParam initParam;
    SceAppUtilBootParam bootParam;
    memset(&initParam, 0, sizeof(initParam));
    memset(&bootParam, 0, sizeof(bootParam));
    int appUtilResult = sceAppUtilInit(&initParam, &bootParam);
    log_line("VSTEST sceAppUtilInit: 0x%08X", (unsigned int)appUtilResult);

    SceCommonDialogConfigParam config;
    sceCommonDialogConfigParamInit(&config);
    sceAppUtilSystemParamGetInt(SCE_SYSTEM_PARAM_ID_LANG, (int *)&config.language);
    sceAppUtilSystemParamGetInt(SCE_SYSTEM_PARAM_ID_ENTER_BUTTON, (int *)&config.enterButtonAssign);
    sceCommonDialogSetConfigParam(&config);
}

static void run_all(void)
{
    uint64_t start = time_now_us();
    test_system_run(false);
    results_write_file(false);
    test_graphics_run();
    results_write_file(false);
    test_files_run();
    results_write_file(false);
    test_threads_run();
    results_write_file(false);
    test_audio_run();
    results_write_file(false);
    test_system_run(true);
    results_write_file(false);
    test_input_run(20);
    results_write_file(false);
    test_perf_run(5, false);
    log_line("VSTEST run all took %llu ms", (unsigned long long)((time_now_us() - start) / 1000));
    results_write_file(true);
}

static void run_menu_action(MenuAction action)
{
    if (action == MENU_RUN_ALL)
    {
        run_all();
        app_show_results_screen("All results", NULL);
        return;
    }
    if (action == MENU_GRAPHICS)
    {
        test_graphics_run();
    }
    else if (action == MENU_INPUT_LIVE)
    {
        test_input_live_screen();
        return;
    }
    else if (action == MENU_INPUT_CHECKS)
    {
        test_input_run(30);
    }
    else if (action == MENU_AUDIO)
    {
        test_audio_run();
    }
    else if (action == MENU_FILES)
    {
        test_files_run();
    }
    else if (action == MENU_THREADS)
    {
        test_threads_run();
    }
    else if (action == MENU_SYSTEM)
    {
        test_system_run(false);
        test_system_run(true);
    }
    else if (action == MENU_PERFORMANCE)
    {
        test_perf_run(60, true);
    }
    else if (action == MENU_RESULTS)
    {
        app_show_results_screen("All results", NULL);
        return;
    }
    results_write_file(false);
    app_show_results_screen(MENU_LABELS[action], MENU_AREAS[action]);
}

static void draw_menu(int selected)
{
    int okCount;
    int failCount;
    int skipCount;
    results_totals(NULL, &okCount, &failCount, &skipCount);
    gfx_frame_begin(COLOR_BACKGROUND);
    gfx_draw_text(24, 16, 3, COLOR_TEXT, "VitaSharp test game");
    gfx_printf(24, 52, 2, COLOR_DIM, "OK %d  FAIL %d  SKIP %d  -> %s", okCount, failCount, skipCount,
               VSTEST_RESULTS_PATH);
    for (int index = 0; index < MENU_COUNT; index++)
    {
        float y = 100.0f + (float)index * 34.0f;
        if (index == selected)
        {
            gfx_draw_rect(16, y - 6, 600, 30, COLOR_HIGHLIGHT);
        }
        gfx_draw_text(32, y, 2, COLOR_TEXT, MENU_LABELS[index]);
        const char *area = MENU_AREAS[index];
        if (area != NULL)
        {
            int areaOk;
            int areaFail;
            int areaSkip;
            results_totals(area, &areaOk, &areaFail, &areaSkip);
            if (areaOk + areaFail + areaSkip > 0)
            {
                gfx_printf(640, y, 2, areaFail > 0 ? COLOR_FAIL : COLOR_OK, "%d/%d OK", areaOk,
                           areaOk + areaFail + areaSkip);
            }
        }
    }
    gfx_draw_text(24, 516, 2, COLOR_DIM, "Up/Down choose, Cross run, Circle back");
    gfx_frame_end();
}

int main(void)
{
    results_init();
    log_line("VSTEST start");
    if (!gfx_init())
    {
        log_line("VSTEST gfx_init failed; exiting");
        result_add("gfx", "init", RESULT_FAIL, "gfx_init failed");
        results_write_file(true);
        sceKernelExitProcess(0);
        return 0;
    }
    init_system();
    run_all();

    int selected = 0;
    bool running = true;
    while (running)
    {
        PadState pad;
        app_read_pad(&pad);
        if (pad.pressed & SCE_CTRL_DOWN)
        {
            selected = (selected + 1) % MENU_COUNT;
        }
        if (pad.pressed & SCE_CTRL_UP)
        {
            selected = (selected + MENU_COUNT - 1) % MENU_COUNT;
        }
        if (pad.pressed & SCE_CTRL_CROSS)
        {
            if (selected == MENU_EXIT)
            {
                running = false;
            }
            else
            {
                run_menu_action((MenuAction)selected);
            }
        }
        draw_menu(selected);
    }
    gfx_shutdown();
    sceKernelExitProcess(0);
    return 0;
}
