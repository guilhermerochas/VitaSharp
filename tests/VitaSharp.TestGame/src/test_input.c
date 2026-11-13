/*
 * Input: buttons, both analog sticks, front and rear touch, motion sensors.
 * test_input_run collects everything it sees during a time window (it ends early once every item was seen, or on
 * START+SELECT held together); test_input_live_screen shows the live values until Circle+START.
 */
#include <math.h>
#include <stdio.h>
#include <string.h>

#include <psp2/ctrl.h>
#include <psp2/motion.h>
#include <psp2/touch.h>

#include "app.h"
#include "gfx.h"

#define AREA "input"
#define STICK_LOW 40
#define STICK_HIGH 215

typedef struct ButtonItem
{
    const char *check;
    const char *label;
    uint32_t mask;
} ButtonItem;

static const ButtonItem BUTTONS[] = {
    {"button_cross", "CROSS", SCE_CTRL_CROSS},
    {"button_circle", "CIRCLE", SCE_CTRL_CIRCLE},
    {"button_square", "SQUARE", SCE_CTRL_SQUARE},
    {"button_triangle", "TRIANGLE", SCE_CTRL_TRIANGLE},
    {"button_up", "UP", SCE_CTRL_UP},
    {"button_down", "DOWN", SCE_CTRL_DOWN},
    {"button_left", "LEFT", SCE_CTRL_LEFT},
    {"button_right", "RIGHT", SCE_CTRL_RIGHT},
    {"button_l", "L", SCE_CTRL_LTRIGGER},
    {"button_r", "R", SCE_CTRL_RTRIGGER},
    {"button_start", "START", SCE_CTRL_START},
    {"button_select", "SELECT", SCE_CTRL_SELECT},
};

typedef struct InputSeen
{
    bool buttons[ARRAY_COUNT(BUTTONS)];
    bool leftStick;
    bool rightStick;
    bool frontTouch;
    bool backTouch;
    bool tilt;
    int frontX;
    int frontY;
    int backX;
    int backY;
    float restAcceleration;
    float maxTiltChange;
    float maxGyro;
} InputSeen;

static float vector_length(SceFVector3 vector)
{
    return sqrtf(vector.x * vector.x + vector.y * vector.y + vector.z * vector.z);
}

static bool stick_moved(int x, int y)
{
    return x < STICK_LOW || x > STICK_HIGH || y < STICK_LOW || y > STICK_HIGH;
}

static bool everything_seen(const InputSeen *seen)
{
    for (size_t index = 0; index < ARRAY_COUNT(BUTTONS); index++)
    {
        if (!seen->buttons[index])
        {
            return false;
        }
    }
    return seen->leftStick && seen->rightStick && seen->frontTouch && seen->backTouch && seen->tilt;
}

static void draw_checklist(const InputSeen *seen, int secondsLeft, const SceCtrlData *pad)
{
    gfx_frame_begin(COLOR_BACKGROUND);
    gfx_draw_text(24, 12, 3, COLOR_TEXT, "Input checks");
    gfx_printf(24, 46, 2, COLOR_DIM, "Press every button, move both sticks, touch front");
    gfx_printf(24, 66, 2, COLOR_DIM, "and back, tilt. %d s left; START+SELECT: stop now", secondsLeft);
    for (size_t index = 0; index < ARRAY_COUNT(BUTTONS); index++)
    {
        float x = 24.0f + (float)(index % 4) * 220.0f;
        float y = 100.0f + (float)(index / 4) * 26.0f;
        gfx_draw_text(x, y, 2, seen->buttons[index] ? COLOR_OK : COLOR_DIM, BUTTONS[index].label);
    }
    gfx_draw_text(24, 190, 2, seen->leftStick ? COLOR_OK : COLOR_DIM, "LEFT STICK");
    gfx_draw_text(244, 190, 2, seen->rightStick ? COLOR_OK : COLOR_DIM, "RIGHT STICK");
    gfx_draw_text(24, 216, 2, seen->frontTouch ? COLOR_OK : COLOR_DIM, "FRONT TOUCH");
    gfx_draw_text(244, 216, 2, seen->backTouch ? COLOR_OK : COLOR_DIM, "BACK TOUCH");
    gfx_draw_text(464, 216, 2, seen->tilt ? COLOR_OK : COLOR_DIM, "TILT");
    gfx_printf(24, 260, 2, COLOR_TEXT, "buttons %08X  L %3d,%3d  R %3d,%3d", (unsigned int)pad->buttons, pad->lx,
               pad->ly, pad->rx, pad->ry);
    gfx_frame_end();
}

void test_input_run(int timeoutSeconds)
{
    results_clear_area(AREA);
    InputSeen seen;
    memset(&seen, 0, sizeof(seen));

    SceCtrlData pad;
    memset(&pad, 0, sizeof(pad));
    int peekResult = sceCtrlPeekBufferPositive(0, &pad, 1);
    result_check(AREA, "ctrl_peek", peekResult >= 0, "0x%08X", (unsigned int)peekResult);
    bool centered = !stick_moved(pad.lx, pad.ly) && !stick_moved(pad.rx, pad.ry);
    result_check(AREA, "sticks_centered_at_rest", centered, "L %d,%d R %d,%d", pad.lx, pad.ly, pad.rx, pad.ry);
    int samplingMode = -1;
    sceCtrlGetSamplingMode((SceCtrlPadInputMode *)&samplingMode);
    result_check(AREA, "ctrl_sampling_mode", samplingMode == SCE_CTRL_MODE_ANALOG_WIDE, "%d", samplingMode);

    SceTouchPanelInfo panelInfo;
    memset(&panelInfo, 0, sizeof(panelInfo));
    int panelResult = sceTouchGetPanelInfo(SCE_TOUCH_PORT_FRONT, &panelInfo);
    result_check(AREA, "touch_front_panel_info", panelResult >= 0 && panelInfo.maxAaX > 1000,
                 "0x%08X area %d..%d x %d..%d", (unsigned int)panelResult, panelInfo.minAaX, panelInfo.maxAaX,
                 panelInfo.minAaY, panelInfo.maxAaY);
    memset(&panelInfo, 0, sizeof(panelInfo));
    panelResult = sceTouchGetPanelInfo(SCE_TOUCH_PORT_BACK, &panelInfo);
    result_check(AREA, "touch_back_panel_info", panelResult >= 0 && panelInfo.maxAaX > 1000,
                 "0x%08X area %d..%d x %d..%d", (unsigned int)panelResult, panelInfo.minAaX, panelInfo.maxAaX,
                 panelInfo.minAaY, panelInfo.maxAaY);

    SceMotionState motion;
    memset(&motion, 0, sizeof(motion));
    int motionResult = sceMotionGetState(&motion);
    seen.restAcceleration = vector_length(motion.acceleration);
    result_check(AREA, "motion_get_state", motionResult >= 0, "0x%08X", (unsigned int)motionResult);
    result_check(AREA, "motion_gravity_1g", seen.restAcceleration > 0.8f && seen.restAcceleration < 1.2f,
                 "|a| %.3f g (%.2f %.2f %.2f)", (double)seen.restAcceleration, (double)motion.acceleration.x,
                 (double)motion.acceleration.y, (double)motion.acceleration.z);
    SceFVector3 restVector = motion.acceleration;

    uint64_t start = time_now_us();
    uint64_t deadline = start + (uint64_t)timeoutSeconds * 1000000ull;
    bool stopped = false;
    log_line("VSTEST input window opens at frame %llu for %d s", (unsigned long long)gfx_frame_count(),
             timeoutSeconds);
    while (time_now_us() < deadline && !everything_seen(&seen))
    {
        memset(&pad, 0, sizeof(pad));
        sceCtrlPeekBufferPositive(0, &pad, 1);
        if ((pad.buttons & SCE_CTRL_START) && (pad.buttons & SCE_CTRL_SELECT))
        {
            stopped = true;
            break;
        }
        for (size_t index = 0; index < ARRAY_COUNT(BUTTONS); index++)
        {
            if (pad.buttons & BUTTONS[index].mask)
            {
                seen.buttons[index] = true;
            }
        }
        if (stick_moved(pad.lx, pad.ly))
        {
            seen.leftStick = true;
        }
        if (stick_moved(pad.rx, pad.ry))
        {
            seen.rightStick = true;
        }
        SceTouchData touch;
        memset(&touch, 0, sizeof(touch));
        if (sceTouchPeek(SCE_TOUCH_PORT_FRONT, &touch, 1) >= 0 && touch.reportNum > 0)
        {
            seen.frontTouch = true;
            seen.frontX = touch.report[0].x;
            seen.frontY = touch.report[0].y;
        }
        memset(&touch, 0, sizeof(touch));
        if (sceTouchPeek(SCE_TOUCH_PORT_BACK, &touch, 1) >= 0 && touch.reportNum > 0)
        {
            seen.backTouch = true;
            seen.backX = touch.report[0].x;
            seen.backY = touch.report[0].y;
        }
        memset(&motion, 0, sizeof(motion));
        if (sceMotionGetState(&motion) >= 0)
        {
            float dx = motion.acceleration.x - restVector.x;
            float dy = motion.acceleration.y - restVector.y;
            float dz = motion.acceleration.z - restVector.z;
            float change = sqrtf(dx * dx + dy * dy + dz * dz);
            float gyro = vector_length(motion.angularVelocity);
            if (change > seen.maxTiltChange)
            {
                seen.maxTiltChange = change;
            }
            if (gyro > seen.maxGyro)
            {
                seen.maxGyro = gyro;
            }
            if (change > 0.3f)
            {
                seen.tilt = true;
            }
        }
        int secondsLeft = (int)((deadline - time_now_us()) / 1000000ull);
        draw_checklist(&seen, secondsLeft, &pad);
    }

    ResultStatus missing = stopped ? RESULT_SKIP : RESULT_FAIL;
    for (size_t index = 0; index < ARRAY_COUNT(BUTTONS); index++)
    {
        result_add(AREA, BUTTONS[index].check, seen.buttons[index] ? RESULT_OK : missing,
                   seen.buttons[index] ? "seen" : "not pressed");
    }
    result_add(AREA, "left_stick", seen.leftStick ? RESULT_OK : missing, seen.leftStick ? "moved" : "not moved");
    result_add(AREA, "right_stick", seen.rightStick ? RESULT_OK : missing, seen.rightStick ? "moved" : "not moved");
    result_add(AREA, "touch_front", seen.frontTouch ? RESULT_OK : missing, "last %d,%d", seen.frontX, seen.frontY);
    result_add(AREA, "touch_back", seen.backTouch ? RESULT_OK : missing, "last %d,%d", seen.backX, seen.backY);
    result_add(AREA, "motion_tilt", seen.tilt ? RESULT_OK : missing, "max change %.2f g, gyro %.2f rad/s",
               (double)seen.maxTiltChange, (double)seen.maxGyro);
}

void test_input_live_screen(void)
{
    while (true)
    {
        SceCtrlData pad;
        memset(&pad, 0, sizeof(pad));
        sceCtrlPeekBufferPositive(0, &pad, 1);
        if ((pad.buttons & SCE_CTRL_CIRCLE) && (pad.buttons & SCE_CTRL_START))
        {
            return;
        }
        SceTouchData front;
        SceTouchData back;
        memset(&front, 0, sizeof(front));
        memset(&back, 0, sizeof(back));
        sceTouchPeek(SCE_TOUCH_PORT_FRONT, &front, 1);
        sceTouchPeek(SCE_TOUCH_PORT_BACK, &back, 1);
        SceMotionState motion;
        memset(&motion, 0, sizeof(motion));
        sceMotionGetState(&motion);

        gfx_frame_begin(COLOR_BACKGROUND);
        gfx_draw_text(24, 12, 3, COLOR_TEXT, "Input: live values");
        gfx_draw_text(24, 46, 2, COLOR_DIM, "Circle+START: back");
        gfx_printf(24, 80, 2, COLOR_TEXT, "buttons %08X", (unsigned int)pad.buttons);
        for (size_t index = 0; index < ARRAY_COUNT(BUTTONS); index++)
        {
            bool down = (pad.buttons & BUTTONS[index].mask) != 0;
            gfx_draw_text(24.0f + (float)(index % 6) * 150.0f, 104.0f + (float)(index / 6) * 22.0f, 2,
                          down ? COLOR_OK : COLOR_DIM, BUTTONS[index].label);
        }
        gfx_printf(24, 160, 2, COLOR_TEXT, "left stick  %3d %3d", pad.lx, pad.ly);
        gfx_printf(24, 182, 2, COLOR_TEXT, "right stick %3d %3d", pad.rx, pad.ry);
        gfx_draw_rect(600, 150, 128, 128, 0xFF404040u);
        gfx_draw_rect(600.0f + (float)pad.lx / 2.0f - 4.0f, 150.0f + (float)pad.ly / 2.0f - 4.0f, 8, 8, COLOR_OK);
        gfx_draw_rect(760, 150, 128, 128, 0xFF404040u);
        gfx_draw_rect(760.0f + (float)pad.rx / 2.0f - 4.0f, 150.0f + (float)pad.ry / 2.0f - 4.0f, 8, 8, COLOR_SKIP);
        gfx_printf(24, 220, 2, COLOR_TEXT, "front touch %u: %d,%d", (unsigned int)front.reportNum,
                   front.reportNum > 0 ? front.report[0].x : -1, front.reportNum > 0 ? front.report[0].y : -1);
        gfx_printf(24, 242, 2, COLOR_TEXT, "back touch  %u: %d,%d", (unsigned int)back.reportNum,
                   back.reportNum > 0 ? back.report[0].x : -1, back.reportNum > 0 ? back.report[0].y : -1);
        for (unsigned int report = 0; report < front.reportNum && report < SCE_TOUCH_MAX_REPORT; report++)
        {
            float x = (float)front.report[report].x * DISPLAY_WIDTH / 1920.0f;
            float y = (float)front.report[report].y * DISPLAY_HEIGHT / 1088.0f;
            gfx_draw_rect(x - 10.0f, y - 10.0f, 20, 20, COLOR_OK);
        }
        for (unsigned int report = 0; report < back.reportNum && report < SCE_TOUCH_MAX_REPORT; report++)
        {
            float x = (float)back.report[report].x * DISPLAY_WIDTH / 1920.0f;
            float y = (float)back.report[report].y * DISPLAY_HEIGHT / 890.0f;
            gfx_draw_rect(x - 10.0f, y - 10.0f, 20, 20, COLOR_SKIP);
        }
        gfx_printf(24, 290, 2, COLOR_TEXT, "accel %+.3f %+.3f %+.3f g", (double)motion.acceleration.x,
                   (double)motion.acceleration.y, (double)motion.acceleration.z);
        gfx_printf(24, 312, 2, COLOR_TEXT, "gyro  %+.3f %+.3f %+.3f rad/s", (double)motion.angularVelocity.x,
                   (double)motion.angularVelocity.y, (double)motion.angularVelocity.z);
        gfx_printf(24, 334, 2, COLOR_TEXT, "quat  %+.3f %+.3f %+.3f %+.3f", (double)motion.deviceQuat.x,
                   (double)motion.deviceQuat.y, (double)motion.deviceQuat.z, (double)motion.deviceQuat.w);
        gfx_frame_end();
    }
}
