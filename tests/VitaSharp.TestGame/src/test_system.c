/*
 * System: RTC and calendar helpers, system parameters (language, enter button, time zone, date format), power and
 * battery, display refresh, memory, network presence (no traffic), and the message and IME dialogs.
 */
#include <stdio.h>
#include <string.h>

#include <psp2/apputil.h>
#include <psp2/common_dialog.h>
#include <psp2/display.h>
#include <psp2/ime_dialog.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/message_dialog.h>
#include <psp2/net/net.h>
#include <psp2/net/netctl.h>
#include <psp2/power.h>
#include <psp2/rtc.h>
#include <psp2/sysmodule.h>

#include "app.h"
#include "gfx.h"

#define AREA "system"
#define NET_MEMORY_SIZE (256 * 1024)

static uint8_t g_netMemory[NET_MEMORY_SIZE];
static SceWChar16 g_imeBuffer[SCE_IME_DIALOG_MAX_TEXT_LENGTH + 1];

static void check_rtc(void)
{
    SceDateTime now;
    memset(&now, 0, sizeof(now));
    int clockResult = sceRtcGetCurrentClockLocalTime(&now);
    bool sane = clockResult >= 0 && now.year >= 2020 && now.year < 2100 && now.month >= 1 && now.month <= 12 &&
                now.day >= 1 && now.day <= 31 && now.hour < 24 && now.minute < 60 && now.second < 60;
    result_check(AREA, "rtc_local_time", sane, "%04d-%02d-%02d %02d:%02d:%02d", now.year, now.month, now.day,
                 now.hour, now.minute, now.second);

    SceRtcTick firstTick;
    SceRtcTick secondTick;
    sceRtcGetCurrentTick(&firstTick);
    sceKernelDelayThread(20000);
    sceRtcGetCurrentTick(&secondTick);
    uint64_t tickDelta = secondTick.tick - firstTick.tick;
    result_check(AREA, "rtc_tick_advances", tickDelta >= 15000 && tickDelta < 60000, "%llu us in 20 ms",
                 (unsigned long long)tickDelta);
    unsigned int resolution = sceRtcGetTickResolution();
    result_check(AREA, "rtc_tick_resolution", resolution == 1000000, "%u", resolution);

    result_check(AREA, "rtc_leap_year", sceRtcIsLeapYear(2024) == 1 && sceRtcIsLeapYear(2100) == 0 &&
                                            sceRtcIsLeapYear(2000) == 1,
                 "2024 %d 2100 %d 2000 %d", sceRtcIsLeapYear(2024), sceRtcIsLeapYear(2100), sceRtcIsLeapYear(2000));
    int daysFebruary = sceRtcGetDaysInMonth(2024, 2);
    result_check(AREA, "rtc_days_in_month", daysFebruary == 29 && sceRtcGetDaysInMonth(2023, 2) == 28, "%d",
                 daysFebruary);
    int dayOfWeek = sceRtcGetDayOfWeek(2024, 1, 1);
    result_check(AREA, "rtc_day_of_week", dayOfWeek == 1, "2024-01-01 -> %d (1 = Monday)", dayOfWeek);

    SceDateTime fixed;
    memset(&fixed, 0, sizeof(fixed));
    fixed.year = 2024;
    fixed.month = 2;
    fixed.day = 29;
    fixed.hour = 12;
    SceRtcTick fixedTick;
    int toTick = sceRtcGetTick(&fixed, &fixedTick);
    SceDateTime back;
    memset(&back, 0, sizeof(back));
    int fromTick = sceRtcSetTick(&back, &fixedTick);
    result_check(AREA, "rtc_tick_round_trip", toTick >= 0 && fromTick >= 0 && back.year == 2024 && back.month == 2 &&
                                                  back.day == 29 && back.hour == 12,
                 "%04d-%02d-%02d %02d", back.year, back.month, back.day, back.hour);
}

static void check_system_params(void)
{
    int language = -1;
    int languageResult = sceAppUtilSystemParamGetInt(SCE_SYSTEM_PARAM_ID_LANG, &language);
    result_check(AREA, "param_language", languageResult >= 0 && language >= 0 && language <= 19, "%d", language);
    int enterButton = -1;
    int enterResult = sceAppUtilSystemParamGetInt(SCE_SYSTEM_PARAM_ID_ENTER_BUTTON, &enterButton);
    result_check(AREA, "param_enter_button", enterResult >= 0 && (enterButton == 0 || enterButton == 1),
                 "%d (0 circle, 1 cross)", enterButton);
    int dateFormat = -1;
    int dateResult = sceAppUtilSystemParamGetInt(SCE_SYSTEM_PARAM_ID_DATE_FORMAT, &dateFormat);
    result_check(AREA, "param_date_format", dateResult >= 0 && dateFormat >= 0 && dateFormat <= 2, "%d", dateFormat);
    int timeFormat = -1;
    int timeResult = sceAppUtilSystemParamGetInt(SCE_SYSTEM_PARAM_ID_TIME_FORMAT, &timeFormat);
    result_check(AREA, "param_time_format", timeResult >= 0 && (timeFormat == 0 || timeFormat == 1), "%d",
                 timeFormat);
    int timeZone = 0;
    int zoneResult = sceAppUtilSystemParamGetInt(SCE_SYSTEM_PARAM_ID_TIME_ZONE, &timeZone);
    result_check(AREA, "param_time_zone", zoneResult >= 0 && timeZone >= -720 && timeZone <= 840,
                 "%d minutes", timeZone);
    int daylight = -1;
    int daylightResult = sceAppUtilSystemParamGetInt(SCE_SYSTEM_PARAM_ID_DAYLIGHT_SAVINGS, &daylight);
    result_check(AREA, "param_daylight_savings", daylightResult >= 0 && (daylight == 0 || daylight == 1), "%d",
                 daylight);
    char userName[SCE_SYSTEM_PARAM_USERNAME_MAXSIZE];
    memset(userName, 0, sizeof(userName));
    int nameResult = sceAppUtilSystemParamGetString(SCE_SYSTEM_PARAM_ID_USERNAME, (SceChar8 *)userName,
                                                    sizeof(userName));
    /* The name itself is not written to the results (it is personal data); only its length. */
    result_check(AREA, "param_username", nameResult >= 0 && userName[0] != '\0', "length %d",
                 (int)strlen(userName));
}

static void check_power(void)
{
    int percent = scePowerGetBatteryLifePercent();
    result_check(AREA, "battery_percent", percent >= 0 && percent <= 100, "%d%%", percent);
    int charging = scePowerIsBatteryCharging();
    int online = scePowerIsPowerOnline();
    result_check(AREA, "battery_charging_flags", (charging == 0 || charging == 1) && (online == 0 || online == 1),
                 "charging %d online %d", charging, online);
    int lifeTime = scePowerGetBatteryLifeTime();
    result_add(AREA, "battery_life_time", lifeTime >= 0 ? RESULT_OK : RESULT_SKIP, "%d min", lifeTime);
    int temperature = scePowerGetBatteryTemp();
    int voltage = scePowerGetBatteryVolt();
    result_add(AREA, "battery_temp_volt", (temperature > 0 && voltage > 0) ? RESULT_OK : RESULT_SKIP,
               "temp %d volt %d mV", temperature, voltage);
    int armClock = scePowerGetArmClockFrequency();
    result_check(AREA, "clock_arm", armClock >= 41 && armClock <= 500, "%d MHz", armClock);
    int busClock = scePowerGetBusClockFrequency();
    int gpuClock = scePowerGetGpuClockFrequency();
    result_check(AREA, "clock_bus_gpu", busClock > 0 && gpuClock > 0, "bus %d gpu %d MHz", busClock, gpuClock);
}

static void check_display_and_memory(void)
{
    float refreshRate = 0.0f;
    int rateResult = sceDisplayGetRefreshRate(&refreshRate);
    result_check(AREA, "display_refresh_rate", rateResult >= 0 && refreshRate > 59.0f && refreshRate < 61.0f,
                 "%.3f Hz", (double)refreshRate);
    int vcountStart = sceDisplayGetVcount();
    uint64_t start = time_now_us();
    for (int index = 0; index < 30; index++)
    {
        sceDisplayWaitVblankStart();
    }
    uint64_t elapsed = time_now_us() - start;
    int vcountDelta = sceDisplayGetVcount() - vcountStart;
    result_check(AREA, "display_vblank_30", vcountDelta >= 29 && vcountDelta <= 31 && elapsed > 450000 &&
                                                elapsed < 560000,
                 "vcount +%d in %llu ms", vcountDelta, (unsigned long long)(elapsed / 1000));

    SceKernelFreeMemorySizeInfo memoryInfo;
    memset(&memoryInfo, 0, sizeof(memoryInfo));
    memoryInfo.size = sizeof(memoryInfo);
    int memoryResult = sceKernelGetFreeMemorySize(&memoryInfo);
    result_check(AREA, "free_memory", memoryResult >= 0 && memoryInfo.size_user > 0, "user %d KB cdram %d KB",
                 memoryInfo.size_user / 1024, memoryInfo.size_cdram / 1024);
    int model = sceKernelGetModel();
    result_check(AREA, "kernel_model", model == 0x10000 || model == 0x20000, "0x%X", (unsigned int)model);
}

static void check_network(void)
{
    int loadResult = sceSysmoduleLoadModule(SCE_SYSMODULE_NET);
    result_check(AREA, "net_module_load", loadResult >= 0, "0x%08X", (unsigned int)loadResult);
    SceNetInitParam initParam;
    initParam.memory = g_netMemory;
    initParam.size = NET_MEMORY_SIZE;
    initParam.flags = 0;
    int netInit = sceNetInit(&initParam);
    result_check(AREA, "net_init", netInit >= 0 || netInit == (int)0x80410110, "0x%08X", (unsigned int)netInit);
    int ctlInit = sceNetCtlInit();
    result_check(AREA, "netctl_init", ctlInit >= 0 || ctlInit == (int)0x80412102, "0x%08X", (unsigned int)ctlInit);
    int state = -1;
    int stateResult = sceNetCtlInetGetState(&state);
    /* Any state is fine (no network is needed); the call must answer with a known one. */
    result_check(AREA, "netctl_inet_state", stateResult >= 0 && state >= 0 && state <= 3, "state %d", state);
    sceNetCtlTerm();
    sceNetTerm();
}

static int message_dialog_status(void)
{
    return sceMsgDialogGetStatus();
}

static void check_message_dialog(void)
{
    SceMsgDialogUserMessageParam userMessage;
    memset(&userMessage, 0, sizeof(userMessage));
    userMessage.buttonType = SCE_MSG_DIALOG_BUTTON_TYPE_OK;
    userMessage.msg = (const SceChar8 *)"VitaSharp test game: message dialog check. Press OK.";
    SceMsgDialogParam param;
    sceMsgDialogParamInit(&param);
    param.mode = SCE_MSG_DIALOG_MODE_USER_MSG;
    param.userMsgParam = &userMessage;
    int initResult = sceMsgDialogInit(&param);
    result_check(AREA, "msg_dialog_init", initResult >= 0, "0x%08X", (unsigned int)initResult);
    if (initResult < 0)
    {
        return;
    }
    int status = app_run_dialog("System", message_dialog_status, 30);
    result_check(AREA, "msg_dialog_finished", status == SCE_COMMON_DIALOG_STATUS_FINISHED, "status %d", status);
    SceMsgDialogResult dialogResult;
    memset(&dialogResult, 0, sizeof(dialogResult));
    int getResult = sceMsgDialogGetResult(&dialogResult);
    result_check(AREA, "msg_dialog_result", getResult >= 0 && dialogResult.buttonId == SCE_MSG_DIALOG_BUTTON_ID_OK,
                 "res 0x%08X result %d button %d", (unsigned int)getResult, dialogResult.result,
                 dialogResult.buttonId);
    if (status == SCE_COMMON_DIALOG_STATUS_RUNNING)
    {
        sceMsgDialogAbort();
        app_run_dialog("System", message_dialog_status, 5);
    }
    int termResult = sceMsgDialogTerm();
    result_check(AREA, "msg_dialog_term", termResult >= 0, "0x%08X", (unsigned int)termResult);
}

static int ime_dialog_status(void)
{
    return sceImeDialogGetStatus();
}

static void check_ime_dialog(void)
{
    static const char *initialText = "VitaSharp";
    static SceWChar16 initialWide[16];
    static SceWChar16 title[32];
    const char *titleText = "VitaSharp test: IME dialog";
    for (int index = 0; index < 15 && initialText[index] != '\0'; index++)
    {
        initialWide[index] = (SceWChar16)initialText[index];
    }
    for (int index = 0; index < 31 && titleText[index] != '\0'; index++)
    {
        title[index] = (SceWChar16)titleText[index];
    }
    memset(g_imeBuffer, 0, sizeof(g_imeBuffer));

    SceImeDialogParam param;
    sceImeDialogParamInit(&param);
    param.supportedLanguages = 0;
    param.languagesForced = SCE_FALSE;
    param.type = SCE_IME_TYPE_DEFAULT;
    param.option = 0;
    param.textBoxMode = SCE_IME_DIALOG_TEXTBOX_MODE_DEFAULT;
    param.title = title;
    param.maxTextLength = 32;
    param.initialText = initialWide;
    param.inputTextBuffer = g_imeBuffer;
    int initResult = sceImeDialogInit(&param);
    result_check(AREA, "ime_dialog_init", initResult >= 0, "0x%08X", (unsigned int)initResult);
    if (initResult < 0)
    {
        return;
    }
    int status = app_run_dialog("System", ime_dialog_status, 60);
    result_check(AREA, "ime_dialog_finished", status == SCE_COMMON_DIALOG_STATUS_FINISHED, "status %d", status);
    SceImeDialogResult dialogResult;
    memset(&dialogResult, 0, sizeof(dialogResult));
    int getResult = sceImeDialogGetResult(&dialogResult);
    char narrow[40];
    int length = 0;
    while (length < 39 && g_imeBuffer[length] != 0)
    {
        narrow[length] = g_imeBuffer[length] < 128 ? (char)g_imeBuffer[length] : '?';
        length++;
    }
    narrow[length] = '\0';
    result_check(AREA, "ime_dialog_result", getResult >= 0 && length > 0, "res 0x%08X button %d text \"%s\"",
                 (unsigned int)getResult, dialogResult.button, narrow);
    if (status == SCE_COMMON_DIALOG_STATUS_RUNNING)
    {
        sceImeDialogAbort();
        app_run_dialog("System", ime_dialog_status, 5);
    }
    int termResult = sceImeDialogTerm();
    result_check(AREA, "ime_dialog_term", termResult >= 0, "0x%08X", (unsigned int)termResult);
}

void test_system_run(bool withDialogs)
{
    if (!withDialogs)
    {
        results_clear_area(AREA);
        app_show_progress("System", "clock, parameters, power, display, network ...");
        check_rtc();
        check_system_params();
        check_power();
        check_display_and_memory();
        check_network();
        return;
    }
    app_show_progress("System", "message dialog ...");
    check_message_dialog();
    app_show_progress("System", "IME dialog ...");
    check_ime_dialog();
}
