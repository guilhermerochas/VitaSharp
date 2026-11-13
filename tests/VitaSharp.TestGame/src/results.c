#include "results.h"

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/kernel/clib.h>
#include <psp2/kernel/processmgr.h>

#define MAX_RESULTS 512

static ResultEntry g_results[MAX_RESULTS];
static int g_resultCount = 0;

uint64_t time_now_us(void)
{
    return sceKernelGetProcessTimeWide();
}

void log_line(const char *format, ...)
{
    char buffer[256];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    sceClibPrintf("%s\n", buffer);
}

void results_init(void)
{
    g_resultCount = 0;
    sceIoMkdir("ux0:data", 0777);
    sceIoMkdir(VSTEST_DATA_DIR, 0777);
}

const char *result_status_name(ResultStatus status)
{
    if (status == RESULT_OK)
    {
        return "OK";
    }
    if (status == RESULT_FAIL)
    {
        return "FAIL";
    }
    return "SKIP";
}

static ResultEntry *find_or_add(const char *area, const char *check)
{
    for (int index = 0; index < g_resultCount; index++)
    {
        if (strcmp(g_results[index].area, area) == 0 && strcmp(g_results[index].check, check) == 0)
        {
            return &g_results[index];
        }
    }
    if (g_resultCount >= MAX_RESULTS)
    {
        return NULL;
    }
    ResultEntry *entry = &g_results[g_resultCount];
    g_resultCount++;
    return entry;
}

static void add_entry(const char *area, const char *check, ResultStatus status, const char *valueFormat, va_list args)
{
    ResultEntry *entry = find_or_add(area, check);
    if (entry == NULL)
    {
        log_line("VSTEST results table full, dropping %s %s", area, check);
        return;
    }
    snprintf(entry->area, sizeof(entry->area), "%s", area);
    snprintf(entry->check, sizeof(entry->check), "%s", check);
    entry->status = status;
    vsnprintf(entry->value, sizeof(entry->value), valueFormat, args);
    log_line("VSTEST\t%s\t%s\t%s\t%s", entry->area, entry->check, result_status_name(status), entry->value);
}

void result_add(const char *area, const char *check, ResultStatus status, const char *valueFormat, ...)
{
    va_list args;
    va_start(args, valueFormat);
    add_entry(area, check, status, valueFormat, args);
    va_end(args);
}

void result_check(const char *area, const char *check, bool condition, const char *valueFormat, ...)
{
    va_list args;
    va_start(args, valueFormat);
    add_entry(area, check, condition ? RESULT_OK : RESULT_FAIL, valueFormat, args);
    va_end(args);
}

void results_clear_area(const char *area)
{
    int kept = 0;
    for (int index = 0; index < g_resultCount; index++)
    {
        if (strcmp(g_results[index].area, area) != 0)
        {
            g_results[kept] = g_results[index];
            kept++;
        }
    }
    g_resultCount = kept;
}

int results_count(void)
{
    return g_resultCount;
}

const ResultEntry *results_get(int index)
{
    if (index < 0 || index >= g_resultCount)
    {
        return NULL;
    }
    return &g_results[index];
}

void results_totals(const char *area, int *okCount, int *failCount, int *skipCount)
{
    *okCount = 0;
    *failCount = 0;
    *skipCount = 0;
    for (int index = 0; index < g_resultCount; index++)
    {
        if (area != NULL && strcmp(g_results[index].area, area) != 0)
        {
            continue;
        }
        if (g_results[index].status == RESULT_OK)
        {
            (*okCount)++;
        }
        else if (g_results[index].status == RESULT_FAIL)
        {
            (*failCount)++;
        }
        else
        {
            (*skipCount)++;
        }
    }
}

static void write_text(SceUID file, const char *text)
{
    sceIoWrite(file, text, strlen(text));
}

void results_write_file(bool finished)
{
    SceUID file = sceIoOpen(VSTEST_RESULTS_PATH, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
    if (file < 0)
    {
        log_line("VSTEST cannot write %s: 0x%08X", VSTEST_RESULTS_PATH, (unsigned int)file);
        return;
    }
    char line[256];
    write_text(file, "# VitaSharp test game results: area\tcheck\tOK|FAIL|SKIP\tvalue\n");
    for (int index = 0; index < g_resultCount; index++)
    {
        const ResultEntry *entry = &g_results[index];
        snprintf(line, sizeof(line), "%.15s\t%.47s\t%s\t%.95s\n", entry->area, entry->check, result_status_name(entry->status),
                 entry->value);
        write_text(file, line);
    }
    if (finished)
    {
        int okCount;
        int failCount;
        int skipCount;
        results_totals(NULL, &okCount, &failCount, &skipCount);
        snprintf(line, sizeof(line), "# done ok=%d fail=%d skip=%d\n", okCount, failCount, skipCount);
        write_text(file, line);
        log_line("VSTEST done ok=%d fail=%d skip=%d", okCount, failCount, skipCount);
    }
    sceIoClose(file);
}
