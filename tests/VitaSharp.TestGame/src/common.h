/*
 * Shared helpers for the VitaSharp test game.
 */
#ifndef VSTEST_COMMON_H
#define VSTEST_COMMON_H

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#define VSTEST_TITLE_ID "VSTEST001"
#define VSTEST_DATA_DIR "ux0:data/VSTEST"
#define VSTEST_RESULTS_PATH "ux0:data/VSTEST/results.txt"

#define ALIGN_UP(value, alignment) (((value) + ((alignment) - 1)) & ~((alignment) - 1))
#define ARRAY_COUNT(array) (sizeof(array) / sizeof((array)[0]))

/* Microseconds since the process started (sceKernelGetProcessTimeWide). */
uint64_t time_now_us(void);

/* Logs a line to the system log (sceClibPrintf), with a trailing newline. */
void log_line(const char *format, ...) __attribute__((format(printf, 1, 2)));

#endif
