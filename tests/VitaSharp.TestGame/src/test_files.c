/*
 * Files and saves: sceIo read/write/seek/stat/directories on ux0:, app0: reads, savedata0: (sceAppUtil), and the
 * system save data dialog.
 */
#include <stdio.h>
#include <string.h>

#include <psp2/apputil.h>
#include <psp2/common_dialog.h>
#include <psp2/io/dirent.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>

#include "app.h"
#include "gfx.h"
#include "savedata_dialog.h"

#define AREA "files"
#define WORK_DIR VSTEST_DATA_DIR "/io"
#define FILE_SIZE (64 * 1024)

static uint8_t g_writeBuffer[FILE_SIZE];
static uint8_t g_readBuffer[FILE_SIZE];

static void fill_pattern(uint8_t *buffer, int size)
{
    for (int index = 0; index < size; index++)
    {
        buffer[index] = (uint8_t)((index * 7 + (index >> 8)) & 0xFF);
    }
}

static int write_whole_file(const char *path, const void *data, int size)
{
    SceUID file = sceIoOpen(path, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
    if (file < 0)
    {
        return file;
    }
    int written = sceIoWrite(file, data, size);
    sceIoClose(file);
    return written;
}

static int read_whole_file(const char *path, void *data, int size)
{
    SceUID file = sceIoOpen(path, SCE_O_RDONLY, 0);
    if (file < 0)
    {
        return file;
    }
    int readBytes = sceIoRead(file, data, size);
    sceIoClose(file);
    return readBytes;
}

static void check_basic_io(void)
{
    char path[128];
    snprintf(path, sizeof(path), "%s/data.bin", WORK_DIR);
    sceIoRemove(path);
    sceIoRmdir(WORK_DIR "/sub");
    sceIoRmdir(WORK_DIR);

    int mkdirResult = sceIoMkdir(WORK_DIR, 0777);
    result_check(AREA, "mkdir", mkdirResult >= 0, "0x%08X", (unsigned int)mkdirResult);
    int mkdirAgain = sceIoMkdir(WORK_DIR, 0777);
    result_check(AREA, "mkdir_exists_error", mkdirAgain < 0, "0x%08X", (unsigned int)mkdirAgain);

    fill_pattern(g_writeBuffer, FILE_SIZE);
    int written = write_whole_file(path, g_writeBuffer, FILE_SIZE);
    result_check(AREA, "write_64k", written == FILE_SIZE, "%d bytes", written);

    SceIoStat stat;
    memset(&stat, 0, sizeof(stat));
    int statResult = sceIoGetstat(path, &stat);
    result_check(AREA, "getstat_size", statResult >= 0 && stat.st_size == FILE_SIZE, "res 0x%08X size %lld",
                 (unsigned int)statResult, (long long)stat.st_size);
    result_check(AREA, "getstat_is_file", statResult >= 0 && SCE_S_ISREG(stat.st_mode), "mode 0x%X",
                 (unsigned int)stat.st_mode);
    result_check(AREA, "getstat_mtime", statResult >= 0 && stat.st_mtime.year >= 2020, "year %d",
                 stat.st_mtime.year);

    memset(g_readBuffer, 0, FILE_SIZE);
    int readBytes = read_whole_file(path, g_readBuffer, FILE_SIZE);
    bool same = readBytes == FILE_SIZE && memcmp(g_readBuffer, g_writeBuffer, FILE_SIZE) == 0;
    result_check(AREA, "read_back_64k", same, "%d bytes", readBytes);

    SceUID file = sceIoOpen(path, SCE_O_RDONLY, 0);
    SceOff position = sceIoLseek(file, 1000, SCE_SEEK_SET);
    uint8_t four[4] = {0, 0, 0, 0};
    int seekRead = sceIoRead(file, four, 4);
    bool seekOk = position == 1000 && seekRead == 4 && memcmp(four, g_writeBuffer + 1000, 4) == 0;
    result_check(AREA, "lseek_set_read", seekOk, "pos %lld read %d", (long long)position, seekRead);
    SceOff endPosition = sceIoLseek(file, 0, SCE_SEEK_END);
    result_check(AREA, "lseek_end", endPosition == FILE_SIZE, "%lld", (long long)endPosition);
    SceOff currentPosition = sceIoLseek(file, -16, SCE_SEEK_CUR);
    result_check(AREA, "lseek_cur", currentPosition == FILE_SIZE - 16, "%lld", (long long)currentPosition);
    sceIoClose(file);

    SceUID appendFile = sceIoOpen(path, SCE_O_WRONLY | SCE_O_APPEND, 0777);
    int appended = sceIoWrite(appendFile, "TAIL", 4);
    sceIoClose(appendFile);
    memset(&stat, 0, sizeof(stat));
    sceIoGetstat(path, &stat);
    result_check(AREA, "append", appended == 4 && stat.st_size == FILE_SIZE + 4, "size %lld",
                 (long long)stat.st_size);

    int subResult = sceIoMkdir(WORK_DIR "/sub", 0777);
    SceUID directory = sceIoDopen(WORK_DIR);
    bool sawFile = false;
    bool sawDirectory = false;
    int entries = 0;
    if (directory >= 0)
    {
        SceIoDirent entry;
        memset(&entry, 0, sizeof(entry));
        while (sceIoDread(directory, &entry) > 0)
        {
            entries++;
            if (strcmp(entry.d_name, "data.bin") == 0 && SCE_S_ISREG(entry.d_stat.st_mode))
            {
                sawFile = true;
            }
            if (strcmp(entry.d_name, "sub") == 0 && SCE_S_ISDIR(entry.d_stat.st_mode))
            {
                sawDirectory = true;
            }
            memset(&entry, 0, sizeof(entry));
        }
        sceIoDclose(directory);
    }
    result_check(AREA, "dopen_dread", directory >= 0 && sawFile && sawDirectory, "uid 0x%08X entries %d sub 0x%08X",
                 (unsigned int)directory, entries, (unsigned int)subResult);

    char renamed[128];
    snprintf(renamed, sizeof(renamed), "%s/renamed.bin", WORK_DIR);
    int renameResult = sceIoRename(path, renamed);
    SceIoStat renamedStat;
    bool renamedExists = sceIoGetstat(renamed, &renamedStat) >= 0;
    bool oldGone = sceIoGetstat(path, &renamedStat) < 0;
    result_check(AREA, "rename", renameResult >= 0 && renamedExists && oldGone, "0x%08X",
                 (unsigned int)renameResult);

    int removeResult = sceIoRemove(renamed);
    result_check(AREA, "remove", removeResult >= 0 && sceIoGetstat(renamed, &renamedStat) < 0, "0x%08X",
                 (unsigned int)removeResult);
    int rmdirSub = sceIoRmdir(WORK_DIR "/sub");
    int rmdirWork = sceIoRmdir(WORK_DIR);
    result_check(AREA, "rmdir", rmdirSub >= 0 && rmdirWork >= 0, "0x%08X 0x%08X", (unsigned int)rmdirSub,
                 (unsigned int)rmdirWork);

    SceUID missing = sceIoOpen(VSTEST_DATA_DIR "/does-not-exist.bin", SCE_O_RDONLY, 0);
    result_check(AREA, "open_missing_enoent", missing == (SceUID)0x80010002, "0x%08X", (unsigned int)missing);
    if (missing >= 0)
    {
        sceIoClose(missing);
    }
}

static void check_app0(void)
{
    uint8_t header[8];
    memset(header, 0, sizeof(header));
    int readBytes = read_whole_file("app0:sce_sys/param.sfo", header, sizeof(header));
    bool magic = readBytes == 8 && header[0] == 0 && header[1] == 'P' && header[2] == 'S' && header[3] == 'F';
    result_check(AREA, "app0_param_sfo", magic, "read %d", readBytes);

    SceIoStat stat;
    memset(&stat, 0, sizeof(stat));
    int statResult = sceIoGetstat("app0:eboot.bin", &stat);
    result_check(AREA, "app0_eboot_stat", statResult >= 0 && stat.st_size > 1024, "size %lld",
                 (long long)stat.st_size);

    SceUID writeAttempt = sceIoOpen("app0:vstest_write.bin", SCE_O_WRONLY | SCE_O_CREAT, 0777);
    result_add(AREA, "app0_write_denied", writeAttempt < 0 ? RESULT_OK : RESULT_SKIP, "0x%08X (writable when installed unsafe)",
               (unsigned int)writeAttempt);
    if (writeAttempt >= 0)
    {
        sceIoClose(writeAttempt);
        sceIoRemove("app0:vstest_write.bin");
    }
}

static void check_savedata(void)
{
    const char *path = "savedata0:vstest.bin";
    const char *content = "VitaSharp test game save data";
    int length = (int)strlen(content);
    int written = write_whole_file(path, content, length);
    result_check(AREA, "savedata0_write", written == length, "0x%08X", (unsigned int)written);
    char readBack[64];
    memset(readBack, 0, sizeof(readBack));
    int readBytes = read_whole_file(path, readBack, sizeof(readBack) - 1);
    result_check(AREA, "savedata0_read", readBytes == length && strcmp(readBack, content) == 0, "%d bytes",
                 readBytes);

    SceAppUtilSaveDataSlotParam slotParam;
    memset(&slotParam, 0, sizeof(slotParam));
    const char *title = "VSTEST slot 0";
    for (int index = 0; title[index] != '\0'; index++)
    {
        slotParam.title[index] = (SceWChar16)title[index];
    }
    sceAppUtilSaveDataSlotDelete(0, NULL);
    int createResult = sceAppUtilSaveDataSlotCreate(0, &slotParam, NULL);
    result_check(AREA, "slot_create", createResult >= 0, "0x%08X", (unsigned int)createResult);
    SceAppUtilSaveDataSlotParam readParam;
    memset(&readParam, 0, sizeof(readParam));
    int getResult = sceAppUtilSaveDataSlotGetParam(0, &readParam, NULL);
    bool sameTitle = getResult >= 0 && readParam.title[0] == 'V' && readParam.title[7] == 's';
    result_check(AREA, "slot_get_param", sameTitle, "0x%08X", (unsigned int)getResult);
}

static int save_dialog_status(void)
{
    return sceSaveDataDialogGetStatus();
}

/*
 * The save data dialog stays RUNNING after the user answers: its sub-dialog reports FINISHED and the application
 * then closes it with sceSaveDataDialogFinish. This status ends the wait at that point.
 */
static int save_dialog_answer_status(void)
{
    int status = sceSaveDataDialogGetStatus();
    if (status == SCE_COMMON_DIALOG_STATUS_RUNNING &&
        sceSaveDataDialogGetSubStatus() == SCE_COMMON_DIALOG_STATUS_FINISHED)
    {
        return SCE_COMMON_DIALOG_STATUS_FINISHED;
    }
    return status;
}

static void check_save_dialog(void)
{
    VsSaveDataDialogUserMessageParam userMessage;
    memset(&userMessage, 0, sizeof(userMessage));
    userMessage.buttonType = VS_SAVEDATA_DIALOG_BUTTON_TYPE_OK;
    userMessage.msg = (const SceChar8 *)"VitaSharp test game: save data dialog check. Press OK.";
    userMessage.targetSlot.id = 0;

    VsSaveDataDialogParam param;
    vs_save_data_dialog_param_init(&param);
    param.mode = VS_SAVEDATA_DIALOG_MODE_USER_MSG;
    param.dispType = VS_SAVEDATA_DIALOG_TYPE_SAVE;
    param.userMsgParam = &userMessage;

    int initResult = sceSaveDataDialogInit(&param);
    result_check(AREA, "save_dialog_init", initResult >= 0, "0x%08X", (unsigned int)initResult);
    if (initResult < 0)
    {
        return;
    }
    app_run_dialog("Files and saves", save_dialog_answer_status, 30);
    int status = sceSaveDataDialogGetStatus();
    int subStatus = sceSaveDataDialogGetSubStatus();
    result_check(AREA, "save_dialog_answered", subStatus == SCE_COMMON_DIALOG_STATUS_FINISHED,
                 "status %d substatus %d", status, subStatus);
    if (status == SCE_COMMON_DIALOG_STATUS_RUNNING && subStatus == SCE_COMMON_DIALOG_STATUS_FINISHED)
    {
        VsSaveDataDialogFinishParam finishParam;
        memset(&finishParam, 0, sizeof(finishParam));
        sceSaveDataDialogFinish(&finishParam);
        status = app_run_dialog("Files and saves", save_dialog_status, 5);
    }
    result_check(AREA, "save_dialog_finished", status == SCE_COMMON_DIALOG_STATUS_FINISHED, "status %d", status);
    VsSaveDataDialogResult dialogResult;
    memset(&dialogResult, 0, sizeof(dialogResult));
    int getResult = sceSaveDataDialogGetResult(&dialogResult);
    result_check(AREA, "save_dialog_result", getResult >= 0 && dialogResult.mode == VS_SAVEDATA_DIALOG_MODE_USER_MSG,
                 "res 0x%08X mode %d result %d button %d", (unsigned int)getResult, dialogResult.mode,
                 dialogResult.result, dialogResult.buttonId);
    if (status == SCE_COMMON_DIALOG_STATUS_RUNNING)
    {
        sceSaveDataDialogAbort();
        app_run_dialog("Files and saves", save_dialog_status, 5);
    }
    int termResult = sceSaveDataDialogTerm();
    result_check(AREA, "save_dialog_term", termResult >= 0, "0x%08X", (unsigned int)termResult);
}

void test_files_run(void)
{
    results_clear_area(AREA);
    app_show_progress("Files and saves", "sceIo on ux0:data/VSTEST ...");
    check_basic_io();
    check_app0();
    app_show_progress("Files and saves", "savedata0: and save slots ...");
    check_savedata();
    check_save_dialog();
}
