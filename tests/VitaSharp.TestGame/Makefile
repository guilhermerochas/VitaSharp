# VitaSharp test game: builds build/VitaSharpTest.vpk with VitaSDK (no CMake needed).
# Needs VITASDK set (or VitaSDK in ~/vitasdk) and its bin folder on PATH.

TITLE_ID    := VSTEST001
TITLE_NAME  := VitaSharp Test
APP_VERSION := 01.00
TARGET      := VitaSharpTest

VITASDK     ?= $(HOME)/vitasdk
PREFIX      := $(VITASDK)/bin/arm-vita-eabi
CC          := $(PREFIX)-gcc
STRIP       := $(PREFIX)-strip

BUILD_DIR   := build
SRC_DIR     := src
SHADER_DIR  := third_party/vita2d-shaders

SOURCES_C   := $(wildcard $(SRC_DIR)/*.c)
SOURCES_S   := $(wildcard $(SRC_DIR)/*.S)
OBJECTS     := $(patsubst $(SRC_DIR)/%.c,$(BUILD_DIR)/%.o,$(SOURCES_C)) \
               $(patsubst $(SRC_DIR)/%.S,$(BUILD_DIR)/%.o,$(SOURCES_S))

CFLAGS      := -O2 -g -std=gnu11 -Wall -Wextra -Wno-unused-parameter -ffast-math -mfloat-abi=hard
ASFLAGS     := -Wa,-I$(SHADER_DIR)
LDFLAGS     := -Wl,-q

LIBS := \
	-lSceGxm_stub -lSceDisplay_stub -lSceCtrl_stub -lSceTouch_stub -lSceMotion_stub \
	-lSceAudio_stub -lSceCommonDialog_stub -lSceAppUtil_stub -lSceAppMgr_stub -lSceSysmodule_stub \
	-lScePower_stub -lSceRtc_stub -lSceNetCtl_stub -lSceNet_stub -lSceLibKernel_stub \
	-lSceKernelThreadMgr_stub -lSceIofilemgr_stub -lSceSysmem_stub -lSceProcessmgr_stub \
	-lm

.PHONY: all clean

all: $(BUILD_DIR)/$(TARGET).vpk

$(BUILD_DIR):
	mkdir -p $(BUILD_DIR)/sce_sys

$(BUILD_DIR)/%.o: $(SRC_DIR)/%.c $(wildcard $(SRC_DIR)/*.h) | $(BUILD_DIR)
	$(CC) $(CFLAGS) -c $< -o $@

$(BUILD_DIR)/%.o: $(SRC_DIR)/%.S $(wildcard $(SHADER_DIR)/*.gxp) | $(BUILD_DIR)
	$(CC) $(ASFLAGS) -c $< -o $@

$(BUILD_DIR)/$(TARGET).elf: $(OBJECTS)
	$(CC) $(CFLAGS) $(LDFLAGS) $^ $(LIBS) -o $@

$(BUILD_DIR)/$(TARGET).velf: $(BUILD_DIR)/$(TARGET).elf
	vita-elf-create $< $@

$(BUILD_DIR)/eboot.bin: $(BUILD_DIR)/$(TARGET).velf
	vita-make-fself -s $< $@

$(BUILD_DIR)/sce_sys/param.sfo: Makefile | $(BUILD_DIR)
	vita-mksfoex -s APP_VER=$(APP_VERSION) -s TITLE_ID=$(TITLE_ID) "$(TITLE_NAME)" $@

$(BUILD_DIR)/sce_sys/icon0.png: assets/icon0.png | $(BUILD_DIR)
	cp $< $@

$(BUILD_DIR)/$(TARGET).vpk: $(BUILD_DIR)/eboot.bin $(BUILD_DIR)/sce_sys/param.sfo $(BUILD_DIR)/sce_sys/icon0.png
	vita-pack-vpk -s $(BUILD_DIR)/sce_sys/param.sfo -b $(BUILD_DIR)/eboot.bin \
		-a $(BUILD_DIR)/sce_sys/icon0.png=sce_sys/icon0.png $@

clean:
	rm -rf $(BUILD_DIR)
