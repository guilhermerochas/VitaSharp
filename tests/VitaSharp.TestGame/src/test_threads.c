/*
 * Threads and sync: thread create/start/wait/exit status, semaphores, mutexes (normal, recursive, lightweight),
 * condition variables, event flags, message pipes, and timer accuracy (sceKernelDelayThread against the clock).
 */
#include <stdio.h>
#include <string.h>

#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/rtc.h>

#include "app.h"

#define AREA "threads"
#define DEFAULT_PRIORITY 0x10000100
#define STACK_SIZE (16 * 1024)
#define COUNTER_LOOPS 20000
#define MSGPIPE_MODE_FULL 1
#define MSGPIPE_MESSAGES 32

typedef struct SharedCounter
{
    SceUID mutex;
    SceKernelLwMutexWork *lwMutex;
    volatile int value;
} SharedCounter;

static SharedCounter g_counter;
static SceKernelLwMutexWork g_lwMutexWork;
static SceUID g_semaphore;
static SceUID g_eventFlag;
static SceUID g_msgPipe;
static SceUID g_condMutex;
static SceUID g_cond;
static volatile int g_condReady;
static volatile int g_semaphoreConsumed;

static SceUID start_thread(const char *name, SceKernelThreadEntry entry, int argument)
{
    SceUID thread = sceKernelCreateThread(name, entry, DEFAULT_PRIORITY, STACK_SIZE, 0, 0, NULL);
    if (thread >= 0)
    {
        sceKernelStartThread(thread, sizeof(argument), &argument);
    }
    return thread;
}

static int wait_and_delete(SceUID thread)
{
    int exitStatus = -1;
    SceUInt timeout = 5000000;
    int waitResult = sceKernelWaitThreadEnd(thread, &exitStatus, &timeout);
    sceKernelDeleteThread(thread);
    if (waitResult < 0)
    {
        return waitResult;
    }
    return exitStatus;
}

static int exit_status_thread(SceSize argumentSize, void *argument)
{
    (void)argumentSize;
    int value = *(int *)argument;
    sceKernelExitThread(value * 2);
    return 0;
}

static void check_thread_basics(void)
{
    SceUID thread = sceKernelCreateThread("vstest_exit", exit_status_thread, DEFAULT_PRIORITY, STACK_SIZE, 0, 0, NULL);
    result_check(AREA, "create_thread", thread >= 0, "0x%08X", (unsigned int)thread);
    if (thread < 0)
    {
        return;
    }
    int argument = 21;
    int startResult = sceKernelStartThread(thread, sizeof(argument), &argument);
    int exitStatus = -1;
    SceUInt timeout = 5000000;
    int waitResult = sceKernelWaitThreadEnd(thread, &exitStatus, &timeout);
    result_check(AREA, "start_wait_exit_status", startResult >= 0 && waitResult >= 0 && exitStatus == 42,
                 "start 0x%08X wait 0x%08X status %d", (unsigned int)startResult, (unsigned int)waitResult,
                 exitStatus);
    int threadId = sceKernelGetThreadId();
    result_check(AREA, "get_thread_id", threadId > 0, "0x%08X", (unsigned int)threadId);
    int priority = sceKernelGetThreadCurrentPriority();
    result_check(AREA, "current_priority", priority > 0, "%d", priority);
}

static int mutex_thread(SceSize argumentSize, void *argument)
{
    (void)argumentSize;
    int useLightweight = *(int *)argument;
    for (int loop = 0; loop < COUNTER_LOOPS; loop++)
    {
        if (useLightweight)
        {
            sceKernelLockLwMutex(g_counter.lwMutex, 1, NULL);
        }
        else
        {
            sceKernelLockMutex(g_counter.mutex, 1, NULL);
        }
        int value = g_counter.value;
        if ((loop & 63) == 0)
        {
            sceKernelDelayThread(0);
        }
        g_counter.value = value + 1;
        if (useLightweight)
        {
            sceKernelUnlockLwMutex(g_counter.lwMutex, 1);
        }
        else
        {
            sceKernelUnlockMutex(g_counter.mutex, 1);
        }
    }
    return 0;
}

static void check_mutexes(void)
{
    g_counter.mutex = sceKernelCreateMutex("vstest_mutex", 0, 0, NULL);
    result_check(AREA, "mutex_create", g_counter.mutex >= 0, "0x%08X", (unsigned int)g_counter.mutex);
    g_counter.value = 0;
    SceUID first = start_thread("vstest_mutex_a", mutex_thread, 0);
    SceUID second = start_thread("vstest_mutex_b", mutex_thread, 0);
    wait_and_delete(first);
    wait_and_delete(second);
    result_check(AREA, "mutex_two_threads", g_counter.value == 2 * COUNTER_LOOPS, "count %d (want %d)",
                 g_counter.value, 2 * COUNTER_LOOPS);

    int tryOwned = sceKernelTryLockMutex(g_counter.mutex, 1);
    int tryAgain = sceKernelTryLockMutex(g_counter.mutex, 1);
    sceKernelUnlockMutex(g_counter.mutex, 1);
    result_check(AREA, "mutex_trylock_not_recursive", tryOwned >= 0 && tryAgain < 0, "0x%08X 0x%08X",
                 (unsigned int)tryOwned, (unsigned int)tryAgain);
    sceKernelDeleteMutex(g_counter.mutex);

    SceUID recursive = sceKernelCreateMutex("vstest_recursive", SCE_KERNEL_MUTEX_ATTR_RECURSIVE, 0, NULL);
    int lockOne = sceKernelLockMutex(recursive, 1, NULL);
    int lockTwo = sceKernelLockMutex(recursive, 1, NULL);
    int unlockOne = sceKernelUnlockMutex(recursive, 1);
    int unlockTwo = sceKernelUnlockMutex(recursive, 1);
    int unlockExtra = sceKernelUnlockMutex(recursive, 1);
    result_check(AREA, "mutex_recursive", lockOne >= 0 && lockTwo >= 0 && unlockOne >= 0 && unlockTwo >= 0 &&
                                              unlockExtra < 0,
                 "%X %X %X %X %X", (unsigned int)lockOne, (unsigned int)lockTwo, (unsigned int)unlockOne,
                 (unsigned int)unlockTwo, (unsigned int)unlockExtra);
    sceKernelDeleteMutex(recursive);

    int lwCreate = sceKernelCreateLwMutex(&g_lwMutexWork, "vstest_lw", 0, 0, NULL);
    g_counter.lwMutex = &g_lwMutexWork;
    g_counter.value = 0;
    first = start_thread("vstest_lw_a", mutex_thread, 1);
    second = start_thread("vstest_lw_b", mutex_thread, 1);
    wait_and_delete(first);
    wait_and_delete(second);
    result_check(AREA, "lwmutex_two_threads", lwCreate >= 0 && g_counter.value == 2 * COUNTER_LOOPS,
                 "create 0x%08X count %d", (unsigned int)lwCreate, g_counter.value);
    sceKernelDeleteLwMutex(&g_lwMutexWork);
}

static int semaphore_consumer(SceSize argumentSize, void *argument)
{
    (void)argumentSize;
    int count = *(int *)argument;
    for (int index = 0; index < count; index++)
    {
        SceUInt timeout = 2000000;
        if (sceKernelWaitSema(g_semaphore, 1, &timeout) < 0)
        {
            return index;
        }
        g_semaphoreConsumed++;
    }
    return count;
}

static void check_semaphores(void)
{
    g_semaphore = sceKernelCreateSema("vstest_sema", 0, 0, 100, NULL);
    result_check(AREA, "sema_create", g_semaphore >= 0, "0x%08X", (unsigned int)g_semaphore);
    g_semaphoreConsumed = 0;
    SceUID consumer = start_thread("vstest_consumer", semaphore_consumer, 50);
    for (int index = 0; index < 50; index++)
    {
        sceKernelSignalSema(g_semaphore, 1);
        if ((index % 10) == 0)
        {
            sceKernelDelayThread(1000);
        }
    }
    int consumed = wait_and_delete(consumer);
    result_check(AREA, "sema_producer_consumer", consumed == 50 && g_semaphoreConsumed == 50, "%d consumed",
                 consumed);

    int pollEmpty = sceKernelPollSema(g_semaphore, 1);
    sceKernelSignalSema(g_semaphore, 3);
    int pollTwo = sceKernelPollSema(g_semaphore, 2);
    SceKernelSemaInfo info;
    memset(&info, 0, sizeof(info));
    info.size = sizeof(info);
    int infoResult = sceKernelGetSemaInfo(g_semaphore, &info);
    result_check(AREA, "sema_poll_and_info", pollEmpty < 0 && pollTwo >= 0 && infoResult >= 0 && info.currentCount == 1,
                 "poll0 0x%08X poll2 0x%08X count %d", (unsigned int)pollEmpty, (unsigned int)pollTwo,
                 info.currentCount);

    uint64_t start = time_now_us();
    SceUInt timeout = 20000;
    int timedOut = sceKernelWaitSema(g_semaphore, 5, &timeout);
    uint64_t waited = time_now_us() - start;
    result_check(AREA, "sema_wait_timeout", timedOut == (int)0x80028005 && waited >= 18000 && waited < 60000,
                 "0x%08X after %llu us", (unsigned int)timedOut, (unsigned long long)waited);
    sceKernelDeleteSema(g_semaphore);
}

static int event_setter(SceSize argumentSize, void *argument)
{
    (void)argumentSize;
    (void)argument;
    sceKernelDelayThread(5000);
    sceKernelSetEventFlag(g_eventFlag, 0x1);
    sceKernelDelayThread(5000);
    sceKernelSetEventFlag(g_eventFlag, 0x4);
    return 0;
}

static void check_event_flags(void)
{
    g_eventFlag = sceKernelCreateEventFlag("vstest_evf", 0, 0, NULL);
    result_check(AREA, "evf_create", g_eventFlag >= 0, "0x%08X", (unsigned int)g_eventFlag);
    SceUID setter = start_thread("vstest_evf_set", event_setter, 0);
    unsigned int outBits = 0;
    SceUInt timeout = 2000000;
    int waitAnd = sceKernelWaitEventFlag(g_eventFlag, 0x5, SCE_EVENT_WAITAND, &outBits, &timeout);
    wait_and_delete(setter);
    result_check(AREA, "evf_wait_and", waitAnd >= 0 && (outBits & 0x5) == 0x5, "0x%08X bits 0x%X",
                 (unsigned int)waitAnd, outBits);

    unsigned int pollBits = 0;
    int pollOr = sceKernelPollEventFlag(g_eventFlag, 0x6, SCE_EVENT_WAITOR | SCE_EVENT_WAITCLEAR, &pollBits);
    unsigned int afterClear = 0;
    int pollAfter = sceKernelPollEventFlag(g_eventFlag, 0x5, SCE_EVENT_WAITOR, &afterClear);
    result_check(AREA, "evf_poll_or_clear", pollOr >= 0 && pollBits == 0x5 && pollAfter < 0,
                 "poll 0x%08X bits 0x%X after 0x%08X", (unsigned int)pollOr, pollBits, (unsigned int)pollAfter);

    /* sceKernelClearEventFlag ANDs the flag with its argument: the bits given are the ones kept. */
    sceKernelSetEventFlag(g_eventFlag, 0xF0);
    sceKernelClearEventFlag(g_eventFlag, ~0x30u);
    unsigned int remaining = 0;
    sceKernelPollEventFlag(g_eventFlag, 0xFF, SCE_EVENT_WAITOR, &remaining);
    result_check(AREA, "evf_set_clear", remaining == 0xC0, "bits 0x%X (want 0xC0)", remaining);
    sceKernelDeleteEventFlag(g_eventFlag);
}

static int pipe_sender(SceSize argumentSize, void *argument)
{
    (void)argumentSize;
    (void)argument;
    for (int index = 0; index < MSGPIPE_MESSAGES; index++)
    {
        uint32_t message[4] = {0xC0DE0000u + (uint32_t)index, (uint32_t)index, (uint32_t)(index * index), 0xFEEDu};
        unsigned int sent = 0;
        sceKernelSendMsgPipe(g_msgPipe, message, sizeof(message), MSGPIPE_MODE_FULL, &sent, NULL);
    }
    return 0;
}

static void check_message_pipe(void)
{
    g_msgPipe = sceKernelCreateMsgPipe("vstest_pipe", 0x40, 12, 0x1000, NULL);
    result_check(AREA, "msgpipe_create", g_msgPipe >= 0, "0x%08X", (unsigned int)g_msgPipe);
    if (g_msgPipe < 0)
    {
        return;
    }
    SceUID sender = start_thread("vstest_pipe_tx", pipe_sender, 0);
    int good = 0;
    for (int index = 0; index < MSGPIPE_MESSAGES; index++)
    {
        uint32_t message[4] = {0, 0, 0, 0};
        unsigned int received = 0;
        SceUInt timeout = 2000000;
        int result = sceKernelReceiveMsgPipe(g_msgPipe, message, sizeof(message), MSGPIPE_MODE_FULL, &received,
                                             &timeout);
        bool expected = result >= 0 && message[0] == 0xC0DE0000u + (uint32_t)index &&
                        message[2] == (uint32_t)(index * index) && message[3] == 0xFEEDu;
        if (expected)
        {
            good++;
        }
    }
    wait_and_delete(sender);
    result_check(AREA, "msgpipe_send_receive", good == MSGPIPE_MESSAGES, "%d/%d messages in order", good,
                 MSGPIPE_MESSAGES);
    uint32_t nothing[4];
    unsigned int received = 0;
    int tryEmpty = sceKernelTryReceiveMsgPipe(g_msgPipe, nothing, sizeof(nothing), MSGPIPE_MODE_FULL, &received);
    result_check(AREA, "msgpipe_try_empty", tryEmpty < 0, "0x%08X", (unsigned int)tryEmpty);
    sceKernelDeleteMsgPipe(g_msgPipe);
}

/*
 * threadmgr 3.74 refuses a message pipe whose size rounds to no 4 KB page with ILLEGAL_SIZE (0x8002000B) before it
 * allocates anything (0x81015DB4); size 1 already makes a one-page pipe.
 */
static void check_message_pipe_size_zero(void)
{
    SceUID zero = sceKernelCreateMsgPipe("vstest_pipe0", 0x40, 0, 0, NULL);
    result_check(AREA, "msgpipe0_create_refused", zero == (SceUID)0x8002000B, "0x%08X", (unsigned int)zero);
    if (zero >= 0)
    {
        sceKernelDeleteMsgPipe(zero);
    }
    SceUID one = sceKernelCreateMsgPipe("vstest_pipe1", 0x40, 0, 1, NULL);
    result_check(AREA, "msgpipe1_create", one >= 0, "0x%08X", (unsigned int)one);
    if (one >= 0)
    {
        sceKernelDeleteMsgPipe(one);
    }
}

static int cond_signaller(SceSize argumentSize, void *argument)
{
    (void)argumentSize;
    (void)argument;
    sceKernelDelayThread(5000);
    sceKernelLockMutex(g_condMutex, 1, NULL);
    g_condReady = 1;
    sceKernelSignalCond(g_cond);
    sceKernelUnlockMutex(g_condMutex, 1);
    return 0;
}

static void check_condition_variable(void)
{
    g_condMutex = sceKernelCreateMutex("vstest_cond_mutex", 0, 0, NULL);
    g_cond = sceKernelCreateCond("vstest_cond", 0, g_condMutex, NULL);
    result_check(AREA, "cond_create", g_cond >= 0, "0x%08X", (unsigned int)g_cond);
    g_condReady = 0;
    sceKernelLockMutex(g_condMutex, 1, NULL);
    SceUID signaller = start_thread("vstest_cond_sig", cond_signaller, 0);
    int waitResult = 0;
    while (!g_condReady && waitResult >= 0)
    {
        unsigned int timeout = 2000000;
        waitResult = sceKernelWaitCond(g_cond, &timeout);
    }
    sceKernelUnlockMutex(g_condMutex, 1);
    wait_and_delete(signaller);
    result_check(AREA, "cond_wait_signal", waitResult >= 0 && g_condReady, "0x%08X", (unsigned int)waitResult);
    sceKernelDeleteCond(g_cond);
    sceKernelDeleteMutex(g_condMutex);
}

static void check_timing(void)
{
    /* A delay never returns before the time asked (the threadmgr wakes the thread from its timer list at the deadline
     * or later); how much later depends on how busy the machine is (on the console, higher-priority threads; on an
     * emulator, the host), so the lateness is reported and does not decide. */
    const unsigned int delays[3] = {1000, 10000, 100000};
    const char *names[3] = {"delay_1ms", "delay_10ms", "delay_100ms"};
    for (int index = 0; index < 3; index++)
    {
        uint64_t start = time_now_us();
        sceKernelDelayThread(delays[index]);
        uint64_t elapsed = time_now_us() - start;
        bool notEarly = elapsed >= delays[index];
        result_check(AREA, names[index], notEarly, "%llu us (%+lld us late)", (unsigned long long)elapsed,
                     (long long)elapsed - (long long)delays[index]);
    }

    SceRtcTick rtcStart;
    SceRtcTick rtcEnd;
    sceRtcGetCurrentTick(&rtcStart);
    uint64_t processStart = time_now_us();
    sceKernelDelayThread(500000);
    sceRtcGetCurrentTick(&rtcEnd);
    uint64_t processElapsed = time_now_us() - processStart;
    int64_t rtcElapsed = (int64_t)(rtcEnd.tick - rtcStart.tick);
    int64_t drift = rtcElapsed - (int64_t)processElapsed;
    result_check(AREA, "rtc_vs_process_clock", drift > -5000 && drift < 5000, "rtc %lld us process %llu us",
                 (long long)rtcElapsed, (unsigned long long)processElapsed);

    uint64_t previous = time_now_us();
    bool monotonic = true;
    for (int index = 0; index < 10000; index++)
    {
        uint64_t now = time_now_us();
        if (now < previous)
        {
            monotonic = false;
        }
        previous = now;
    }
    result_check(AREA, "process_time_monotonic", monotonic, "10000 reads");

    SceInt64 systemTime = sceKernelGetSystemTimeWide();
    result_check(AREA, "system_time_wide", systemTime > 0, "%lld", (long long)systemTime);
}

/*
 * Producer / consumer with a deadline, the shape of an audio port's mixer (Dead Space's Android port): an output
 * thread sleeps one audio period (11 ms) and signals a mixer through a semaphore polled the way pthread-embedded's
 * sem_wait does (500 us timed waits); each mixer round hands four jobs to worker threads that run VFP short-vector
 * code (FPSCR LEN = 4) and waits for them. Every round, the first one included (cold code), must end within the
 * period in guest time: on the console a round takes about a millisecond, and a round over the period leaves the
 * output thread signalling a busy mixer (the port's audio stopped for good).
 */
#define MIXER_PERIOD_US 11000
#define MIXER_ROUNDS 12
#define MIXER_WORKERS 4
#define MIXER_JOB_ITERATIONS 8000

static SceUID g_mixSema;
static SceUID g_jobSema[MIXER_WORKERS];
static SceUID g_jobDoneSema;
static volatile int g_mixerStop;
static volatile unsigned int g_jobResult[MIXER_WORKERS];
static SceUInt64 g_roundUs[MIXER_ROUNDS];
static volatile int g_roundsDone;

/* s8..s11 += s16..s19 (and s16..s19 *= s24..s27 = 1.0) in short-vector mode: s11 ends at 1 + iterations only when
 * the vector length applied. Returns the bits of s11. */
static unsigned int vector_job(int iterations)
{
    unsigned int bits;
    __asm__ volatile(
        "vmov.f32 s8, #1.0\n\t"
        "vmov.f32 s9, #1.0\n\t"
        "vmov.f32 s10, #1.0\n\t"
        "vmov.f32 s11, #1.0\n\t"
        "vmov.f32 s16, #1.0\n\t"
        "vmov.f32 s17, #1.0\n\t"
        "vmov.f32 s18, #1.0\n\t"
        "vmov.f32 s19, #1.0\n\t"
        "vmov.f32 s24, #1.0\n\t"
        "vmov.f32 s25, #1.0\n\t"
        "vmov.f32 s26, #1.0\n\t"
        "vmov.f32 s27, #1.0\n\t"
        "vmrs r1, fpscr\n\t"
        "orr r2, r1, #0x30000\n\t"
        "vmsr fpscr, r2\n\t"
        "1:\n\t"
        "vadd.f32 s8, s8, s16\n\t"
        "vmul.f32 s16, s16, s24\n\t"
        "subs %[n], %[n], #1\n\t"
        "bne 1b\n\t"
        "vmsr fpscr, r1\n\t"
        "vmov %[bits], s11\n\t"
        : [n] "+r"(iterations), [bits] "=r"(bits)
        :
        : "r1", "r2", "s8", "s9", "s10", "s11", "s16", "s17", "s18", "s19", "s24", "s25", "s26", "s27", "cc",
          "memory");
    return bits;
}

/* pthread-embedded's sem_wait: 500 us timed waits until a unit comes. */
static void embedded_sem_wait(SceUID sema)
{
    while (true)
    {
        SceUInt timeout = 500;
        if (sceKernelWaitSema(sema, 1, &timeout) >= 0)
        {
            return;
        }
    }
}

static int mixer_worker(SceSize argumentSize, void *argument)
{
    int index = *(int *)argument;
    while (true)
    {
        embedded_sem_wait(g_jobSema[index]);
        if (g_mixerStop)
        {
            return 0;
        }
        g_jobResult[index] = vector_job(MIXER_JOB_ITERATIONS);
        sceKernelSignalSema(g_jobDoneSema, 1);
    }
}

static int mixer_thread(SceSize argumentSize, void *argument)
{
    while (g_roundsDone < MIXER_ROUNDS)
    {
        embedded_sem_wait(g_mixSema);
        if (g_mixerStop)
        {
            return 0;
        }
        SceUInt64 start = sceKernelGetProcessTimeWide();
        for (int worker = 0; worker < MIXER_WORKERS; worker++)
        {
            sceKernelSignalSema(g_jobSema[worker], 1);
        }
        for (int worker = 0; worker < MIXER_WORKERS; worker++)
        {
            embedded_sem_wait(g_jobDoneSema);
        }
        g_roundUs[g_roundsDone] = sceKernelGetProcessTimeWide() - start;
        g_roundsDone++;
    }
    return 0;
}

static void check_mixer_deadline(void)
{
    g_mixSema = sceKernelCreateSema("vstest_mix", 0, 0, 1000, NULL);
    g_jobDoneSema = sceKernelCreateSema("vstest_jobdone", 0, 0, 1000, NULL);
    SceUID workers[MIXER_WORKERS];
    g_mixerStop = 0;
    g_roundsDone = 0;
    for (int worker = 0; worker < MIXER_WORKERS; worker++)
    {
        g_jobSema[worker] = sceKernelCreateSema("vstest_job", 0, 0, 1000, NULL);
        g_jobResult[worker] = 0;
        workers[worker] = start_thread("vstest_mixworker", mixer_worker, worker);
    }
    SceUID mixer = start_thread("vstest_mixer", mixer_thread, 0);
    int busySignals = 0;
    int signals = 0;
    int periods = 0;
    /* the output thread's loop (here: this thread): one period, then a signal to the mixer */
    while (g_roundsDone < MIXER_ROUNDS && periods < MIXER_ROUNDS * 4)
    {
        sceKernelDelayThread(MIXER_PERIOD_US);
        periods++;
        if (signals > g_roundsDone)
        {
            busySignals++; /* the previous round is still running: the period finds the mixer busy */
            continue;
        }
        sceKernelSignalSema(g_mixSema, 1);
        signals++;
    }
    g_mixerStop = 1;
    sceKernelSignalSema(g_mixSema, 1);
    for (int worker = 0; worker < MIXER_WORKERS; worker++)
    {
        sceKernelSignalSema(g_jobSema[worker], 1);
    }
    wait_and_delete(mixer);
    for (int worker = 0; worker < MIXER_WORKERS; worker++)
    {
        wait_and_delete(workers[worker]);
        sceKernelDeleteSema(g_jobSema[worker]);
    }
    sceKernelDeleteSema(g_mixSema);
    sceKernelDeleteSema(g_jobDoneSema);

    SceUInt64 worst = 0;
    for (int round = 0; round < g_roundsDone; round++)
    {
        if (g_roundUs[round] > worst)
        {
            worst = g_roundUs[round];
        }
    }
    float expected = 1.0f + MIXER_JOB_ITERATIONS;
    unsigned int expectedBits;
    memcpy(&expectedBits, &expected, sizeof(expectedBits));
    bool vectorOk = true;
    for (int worker = 0; worker < MIXER_WORKERS; worker++)
    {
        vectorOk = vectorOk && g_jobResult[worker] == expectedBits;
    }
    result_check(AREA, "mixer_vector_jobs", vectorOk, "s11 0x%08X (want 0x%08X)", g_jobResult[0], expectedBits);
    result_check(AREA, "mixer_round_deadline",
                 g_roundsDone == MIXER_ROUNDS && worst <= MIXER_PERIOD_US && busySignals == 0,
                 "%d rounds, first %llu us, worst %llu us (period %d us), %d signals to a busy mixer", g_roundsDone,
                 (unsigned long long)(g_roundsDone > 0 ? g_roundUs[0] : 0), (unsigned long long)worst,
                 MIXER_PERIOD_US, busySignals);
}

void test_threads_run(void)
{
    results_clear_area(AREA);
    app_show_progress("Threads and sync", "threads, mutexes, semaphores ...");
    check_thread_basics();
    check_mutexes();
    check_semaphores();
    app_show_progress("Threads and sync", "event flags, message pipes, condition variables, timers ...");
    check_event_flags();
    check_message_pipe();
    check_message_pipe_size_zero();
    check_condition_variable();
    check_timing();
    app_show_progress("Threads and sync", "mixer round deadline (VFP vector jobs) ...");
    check_mixer_deadline();
}
