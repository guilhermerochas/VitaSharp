/*
 * Check results: kept in memory for the screens, logged, and written to ux0:data/VSTEST/results.txt.
 *
 * File format (one line per check, tab separated):
 *   area<TAB>check<TAB>OK|FAIL|SKIP<TAB>measured value
 * Lines starting with '#' are comments; the last line of a complete run is "# done ok=N fail=N skip=N".
 */
#ifndef VSTEST_RESULTS_H
#define VSTEST_RESULTS_H

#include "common.h"

typedef enum ResultStatus
{
    RESULT_OK = 0,
    RESULT_FAIL = 1,
    RESULT_SKIP = 2
} ResultStatus;

typedef struct ResultEntry
{
    char area[16];
    char check[48];
    ResultStatus status;
    char value[96];
} ResultEntry;

void results_init(void);

/* Records a check (replacing an earlier entry of the same area and check) and logs it. */
void result_add(const char *area, const char *check, ResultStatus status, const char *valueFormat, ...)
    __attribute__((format(printf, 4, 5)));

/* Convenience: OK when the condition holds, FAIL otherwise. */
void result_check(const char *area, const char *check, bool condition, const char *valueFormat, ...)
    __attribute__((format(printf, 4, 5)));

/* Removes the entries of one area (before it runs again). */
void results_clear_area(const char *area);

int results_count(void);
const ResultEntry *results_get(int index);
void results_totals(const char *area, int *okCount, int *failCount, int *skipCount);

/* Rewrites the results file; finished adds the "# done" line. */
void results_write_file(bool finished);

const char *result_status_name(ResultStatus status);

#endif
