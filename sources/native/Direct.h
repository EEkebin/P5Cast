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
    bool waiting_for_key, extended_stats;
} Video;
typedef struct {
    SRWLOCK lock;
    SDL_AudioDeviceID device;
    uint32_t rate, channels, underruns;
    uint64_t compressed_bytes, window_start_ms;
    ChiakiAudioSink decoder_sink;
    bool playing;
    volatile LONG failed;
} Audio;
typedef struct {
    SDL_GameController *pad;
    ChiakiControllerState current;
    int8_t touches[2];
    uint64_t next_scan;
} Gamepad;

typedef struct {
    uint32_t queued_us, underruns, rtt_us, target_us;
    uint32_t audio_bps, sample_rate, channels;
} DirectMetrics;

typedef struct {
    HANDLE request, accepted, rejected;
    bool attempted;
} RestControl;

void rest_open(RestControl *control, const char *pipe_name);
void rest_close(RestControl *control);
void rest_tick(RestControl *control, bool connected, ChiakiSession *session, ChiakiErrorCode (*send)(ChiakiSession *));
int register_console(const char *host);
bool video_profile(ChiakiConnectVideoProfile *profile, int codec, const char *fps, const char *kbps);
bool video_header(Video *video);
bool video_sample(uint8_t *data, size_t size, int32_t lost, bool recovered, void *user);
void video_stats(Video *video, const DirectMetrics *stats);
void audio_decoder_sink(Audio *audio, ChiakiOpusDecoder *decoder, ChiakiAudioSink *sink);
void audio_settings(uint32_t channels, uint32_t rate, void *user);
void audio_frame(int16_t *pcm, size_t samples, void *user);
void audio_stats(Audio *audio, uint64_t now_ms, DirectMetrics *metrics);
void gamepad_init(Gamepad *gamepad);
bool gamepad_tick(Gamepad *gamepad);
