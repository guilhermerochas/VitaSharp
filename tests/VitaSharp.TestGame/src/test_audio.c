/*
 * Audio: sceAudioOut ports (MAIN 48 kHz, BGM 44.1 kHz, VOICE mono), volume and config calls, and two tones played
 * at the same time from two threads; the blocking period of sceAudioOutOutput is measured against the clock.
 */
#include <math.h>
#include <stdio.h>
#include <string.h>

#include <psp2/audioout.h>
#include <psp2/kernel/threadmgr.h>

#include "app.h"

#define AREA "audio"
#define GRAIN 512
#define BLOCKS 96
#define WARMUP_BLOCKS 8
#define TWO_PI 6.28318530718f

typedef struct ToneJob
{
    int port;
    int sampleRate;
    float frequency;
    float amplitude;
    uint64_t steadyMicroseconds;
    int outputErrors;
    int16_t samples[GRAIN * 2];
} ToneJob;

static ToneJob g_jobs[2];

static int tone_thread(SceSize argumentSize, void *argument)
{
    (void)argumentSize;
    ToneJob *job = *(ToneJob **)argument;
    float phase = 0.0f;
    float step = TWO_PI * job->frequency / (float)job->sampleRate;
    uint64_t steadyStart = 0;
    for (int block = 0; block < BLOCKS; block++)
    {
        for (int sample = 0; sample < GRAIN; sample++)
        {
            int16_t value = (int16_t)(sinf(phase) * job->amplitude * 32767.0f);
            job->samples[sample * 2] = value;
            job->samples[sample * 2 + 1] = value;
            phase += step;
            if (phase > TWO_PI)
            {
                phase -= TWO_PI;
            }
        }
        if (block == WARMUP_BLOCKS)
        {
            steadyStart = time_now_us();
        }
        if (sceAudioOutOutput(job->port, job->samples) < 0)
        {
            job->outputErrors++;
        }
    }
    job->steadyMicroseconds = time_now_us() - steadyStart;
    sceAudioOutOutput(job->port, NULL);
    return 0;
}

/* Samples between rising zero crossings of the generated tone (checks the generator, not the hardware). */
static float generated_period(float frequency, int sampleRate)
{
    float step = TWO_PI * frequency / (float)sampleRate;
    int firstCrossing = -1;
    int crossings = 0;
    int lastCrossing = -1;
    float previous = 0.0f;
    for (int sample = 1; sample < sampleRate / 10; sample++)
    {
        float value = sinf(step * (float)sample);
        if (previous < 0.0f && value >= 0.0f)
        {
            if (firstCrossing < 0)
            {
                firstCrossing = sample;
            }
            lastCrossing = sample;
            crossings++;
        }
        previous = value;
    }
    if (crossings < 2)
    {
        return 0.0f;
    }
    return (float)(lastCrossing - firstCrossing) / (float)(crossings - 1);
}

static void check_port_calls(int port, const char *prefix, int wantedRate)
{
    char name[48];
    int volumes[2] = {SCE_AUDIO_VOLUME_0DB / 2, SCE_AUDIO_VOLUME_0DB / 2};
    int volumeResult = sceAudioOutSetVolume(port, SCE_AUDIO_VOLUME_FLAG_L_CH | SCE_AUDIO_VOLUME_FLAG_R_CH, volumes);
    snprintf(name, sizeof(name), "%s_set_volume", prefix);
    result_check(AREA, name, volumeResult >= 0, "0x%08X", (unsigned int)volumeResult);
    int length = sceAudioOutGetConfig(port, SCE_AUDIO_OUT_CONFIG_TYPE_LEN);
    int frequency = sceAudioOutGetConfig(port, SCE_AUDIO_OUT_CONFIG_TYPE_FREQ);
    snprintf(name, sizeof(name), "%s_get_config", prefix);
    result_check(AREA, name, length == GRAIN && frequency == wantedRate, "len %d freq %d", length, frequency);
}

void test_audio_run(void)
{
    results_clear_area(AREA);
    app_show_progress("Audio", "two tones on two ports for about one second ...");

    int mainPort = sceAudioOutOpenPort(SCE_AUDIO_OUT_PORT_TYPE_MAIN, GRAIN, 48000, SCE_AUDIO_OUT_MODE_STEREO);
    result_check(AREA, "open_main_48k", mainPort >= 0, "0x%08X", (unsigned int)mainPort);
    int bgmPort = sceAudioOutOpenPort(SCE_AUDIO_OUT_PORT_TYPE_BGM, GRAIN, 44100, SCE_AUDIO_OUT_MODE_STEREO);
    result_check(AREA, "open_bgm_44k", bgmPort >= 0, "0x%08X", (unsigned int)bgmPort);
    int badPort = sceAudioOutOpenPort(SCE_AUDIO_OUT_PORT_TYPE_MAIN, GRAIN, 12345, SCE_AUDIO_OUT_MODE_STEREO);
    result_check(AREA, "open_bad_rate_error", badPort < 0, "0x%08X", (unsigned int)badPort);
    if (badPort >= 0)
    {
        sceAudioOutReleasePort(badPort);
    }
    if (mainPort < 0 || bgmPort < 0)
    {
        return;
    }
    check_port_calls(mainPort, "main", 48000);
    check_port_calls(bgmPort, "bgm", 44100);

    g_jobs[0].port = mainPort;
    g_jobs[0].sampleRate = 48000;
    g_jobs[0].frequency = 1000.0f;
    g_jobs[0].amplitude = 0.25f;
    g_jobs[1].port = bgmPort;
    g_jobs[1].sampleRate = 44100;
    g_jobs[1].frequency = 1500.0f;
    g_jobs[1].amplitude = 0.25f;
    SceUID threads[2];
    for (int index = 0; index < 2; index++)
    {
        g_jobs[index].outputErrors = 0;
        g_jobs[index].steadyMicroseconds = 0;
        threads[index] = sceKernelCreateThread("vstest_tone", tone_thread, 0x10000100 - 10, 16 * 1024, 0, 0, NULL);
        ToneJob *job = &g_jobs[index];
        sceKernelStartThread(threads[index], sizeof(job), &job);
    }
    for (int index = 0; index < 2; index++)
    {
        SceUInt timeout = 10000000;
        sceKernelWaitThreadEnd(threads[index], NULL, &timeout);
        sceKernelDeleteThread(threads[index]);
    }

    const char *periodNames[2] = {"main_block_period", "bgm_block_period"};
    for (int index = 0; index < 2; index++)
    {
        int steadyBlocks = BLOCKS - WARMUP_BLOCKS;
        double measured = (double)g_jobs[index].steadyMicroseconds / 1000.0 / (double)steadyBlocks;
        double wanted = (double)GRAIN * 1000.0 / (double)g_jobs[index].sampleRate;
        bool accurate = g_jobs[index].outputErrors == 0 && measured > wanted * 0.9 && measured < wanted * 1.1;
        result_check(AREA, periodNames[index], accurate, "%.3f ms per %d samples (want %.3f), errors %d", measured,
                     GRAIN, wanted, g_jobs[index].outputErrors);
    }
    float period = generated_period(1000.0f, 48000);
    result_check(AREA, "tone_1khz_period", fabsf(period - 48.0f) < 0.5f, "%.2f samples at 48 kHz", (double)period);

    int restSample = sceAudioOutGetRestSample(mainPort);
    result_check(AREA, "get_rest_sample", restSample >= 0, "%d", restSample);
    int releaseMain = sceAudioOutReleasePort(mainPort);
    int releaseBgm = sceAudioOutReleasePort(bgmPort);
    result_check(AREA, "release_ports", releaseMain >= 0 && releaseBgm >= 0, "0x%08X 0x%08X",
                 (unsigned int)releaseMain, (unsigned int)releaseBgm);
    int voicePort = sceAudioOutOpenPort(SCE_AUDIO_OUT_PORT_TYPE_VOICE, 256, 48000, SCE_AUDIO_OUT_MODE_MONO);
    result_check(AREA, "open_voice_mono", voicePort >= 0, "0x%08X", (unsigned int)voicePort);
    if (voicePort >= 0)
    {
        sceAudioOutReleasePort(voicePort);
    }
}
