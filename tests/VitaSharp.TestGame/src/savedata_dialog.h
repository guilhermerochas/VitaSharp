/*
 * Save data dialog declarations. VitaSDK ships the sceSaveDataDialog* stubs (libSceCommonDialog_stub) but no
 * header, so the parameter layout used here is written from the public interface description of the dialog
 * (field order and sizes as the PS Vita homebrew community documents them, e.g. the Vita3K and VitaSharp
 * implementations): SceSaveDataDialogParam is 0x9C bytes with mode at 0x50 and the user message pointer at 0x60.
 * Only the user message mode is used.
 */
#ifndef VSTEST_SAVEDATA_DIALOG_H
#define VSTEST_SAVEDATA_DIALOG_H

#include <stddef.h>
#include <string.h>

#include <psp2/apputil.h>
#include <psp2/common_dialog.h>
#include <psp2/types.h>

#define VS_SAVEDATA_DIALOG_MODE_USER_MSG 3
#define VS_SAVEDATA_DIALOG_TYPE_SAVE 1
#define VS_SAVEDATA_DIALOG_BUTTON_TYPE_OK 0

typedef struct VsSaveDataDialogUserMessageParam
{
    SceInt32 buttonType;
    const SceChar8 *msg;
    SceAppUtilSaveDataSlot targetSlot;
    SceChar8 reserved[32];
} VsSaveDataDialogUserMessageParam;

typedef struct VsSaveDataDialogParam
{
    SceUInt32 sdkVersion;
    SceCommonDialogParam commonParam;
    SceInt32 mode;
    SceInt32 dispType;
    void *fixedParam;
    void *listParam;
    VsSaveDataDialogUserMessageParam *userMsgParam;
    void *sysMsgParam;
    void *errorCodeParam;
    void *progressBarParam;
    void *slotConfParam;
    SceInt32 flag;
    void *userdata;
    SceChar8 reserved[32];
} VsSaveDataDialogParam;

typedef struct VsSaveDataDialogResult
{
    SceInt32 mode;
    SceInt32 result;
    SceInt32 buttonId;
    SceInt32 slotId;
    void *slotInfo;
    void *userdata;
    SceChar8 reserved[32];
} VsSaveDataDialogResult;

typedef struct VsSaveDataDialogFinishParam
{
    SceInt32 flag;
    SceChar8 reserved[32];
} VsSaveDataDialogFinishParam;

_Static_assert(sizeof(VsSaveDataDialogParam) == 0x9C, "SceSaveDataDialogParam is 0x9C bytes");
_Static_assert(offsetof(VsSaveDataDialogParam, mode) == 0x50, "mode at 0x50");
_Static_assert(offsetof(VsSaveDataDialogParam, userMsgParam) == 0x60, "userMsgParam at 0x60");
_Static_assert(offsetof(VsSaveDataDialogParam, userdata) == 0x78, "userdata at 0x78");

static inline void vs_save_data_dialog_param_init(VsSaveDataDialogParam *param)
{
    memset(param, 0, sizeof(*param));
    _sceCommonDialogSetMagicNumber(&param->commonParam);
    param->sdkVersion = PSP2_SDK_VERSION;
}

int sceSaveDataDialogInit(const VsSaveDataDialogParam *param);
int sceSaveDataDialogGetStatus(void);
int sceSaveDataDialogGetSubStatus(void);
int sceSaveDataDialogGetResult(VsSaveDataDialogResult *result);
int sceSaveDataDialogFinish(const VsSaveDataDialogFinishParam *param);
int sceSaveDataDialogAbort(void);
int sceSaveDataDialogTerm(void);

#endif
