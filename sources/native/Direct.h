// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#pragma once
#include <chiaki/session.h>
#include <chiaki/opusdecoder.h>
#include <SDL.h>
#include <windows.h>
#include <stdbool.h>

#define DIRECT_MAX_AU (16u * 1024u * 1024u)
#define DIRECT_AUDIO_TARGET_US 15000u
typedef struct {
    HANDLE pipe, stop;
    SRWLOCK writer;
    int codec;
    uint8_t *config;
    size_t config_size;
    unsigned config_mask;
    bool waiting_for_key;
} Video;
typedef struct {
    SRWLOCK lock;
    SDL_AudioDeviceID device;
    uint32_t rate, channels, underruns;
    bool playing;
    volatile LONG failed;
} Audio;
typedef struct {
    SDL_GameController *pad;
    ChiakiControllerState current;
    int8_t touches[2];
    uint64_t next_scan;
} Gamepad;

int register_console(const char *host);
bool video_header(Video *video);
bool video_sample(uint8_t *data, size_t size, int32_t lost, bool recovered, void *user);
void video_stats(Video *video, const uint32_t stats[4]);
void audio_settings(uint32_t channels, uint32_t rate, void *user);
void audio_frame(int16_t *pcm, size_t samples, void *user);
void audio_stats(Audio *audio, uint32_t *queued_us, uint32_t *underruns);
void gamepad_init(Gamepad *gamepad);
bool gamepad_tick(Gamepad *gamepad);
