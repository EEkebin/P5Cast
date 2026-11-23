// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#pragma once
#include <chiaki/session.h>
#include <chiaki/opusdecoder.h>
#include <SDL.h>
#include <windows.h>
#include <stdbool.h>

#define DIRECT_MAX_AU (16u * 1024u * 1024u)
#define DIRECT_AUDIO_TARGET_US 15000u
#define DIRECT_AUDIO_MAX_TARGET_US 55000u
#define DIRECT_AUDIO_CAPACITY 8192u /* frames; holds a 100 ms Opus block at 48 kHz */
typedef struct {
    HANDLE pipe, stop;
    SRWLOCK writer;
    int codec;
    uint8_t *config;
    size_t config_size;
    unsigned config_mask;
    bool waiting_for_key;
    unsigned stats_version;
    volatile LONG lost_frames;
    uint32_t slow_handoffs;
    LARGE_INTEGER performance_frequency;
} Video;
typedef struct {
    SRWLOCK lock;
    SDL_AudioDeviceID device;
    const volatile LONG *volume_percent;
    uint32_t rate, channels, underruns, concealments, queue_trims;
    uint32_t target_us, release_frames;
    uint64_t device_frames, last_underrun_frame, stable_frames;
    LARGE_INTEGER frequency;
    int64_t last_input_tick, last_callback_tick, max_input_ticks, max_callback_ticks;
    uint64_t compressed_bytes, window_start_ms;
    ChiakiAudioSink decoder_sink;
    int16_t pcm[DIRECT_AUDIO_CAPACITY * 2], last_output[2], ramp_start[2];
    size_t read_frame, queued_frames;
    uint32_t ramp_left;
    LONG gain_percent;
    bool gain_initialized;
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
    uint32_t audio_bps, sample_rate, channels, concealments, queue_trims;
    uint32_t input_gap_us, callback_gap_us;
} DirectMetrics;

typedef struct {
    HANDLE request, accepted, rejected;
    bool queued;
} RestControl;

void rest_open(RestControl *control, const char *pipe_name);
void rest_close(RestControl *control);
void rest_tick(RestControl *control, bool connected, ChiakiSession *session, ChiakiErrorCode (*send)(ChiakiSession *));
int register_console(const char *host);
bool video_profile(ChiakiConnectVideoProfile *profile, int codec, const char *fps, const char *kbps, const char *height);
bool video_header(Video *video);
bool video_sample(uint8_t *data, size_t size, int32_t lost, bool recovered, void *user);
void video_stats(Video *video, const DirectMetrics *stats);
bool audio_open_volume(Audio *audio, const char *pipe_name);
void audio_close_volume(Audio *audio);
void audio_decoder_sink(Audio *audio, ChiakiOpusDecoder *decoder, ChiakiAudioSink *sink);
void audio_settings(uint32_t channels, uint32_t rate, void *user);
void audio_frame(int16_t *pcm, size_t samples, void *user);
void SDLCALL audio_render(void *user, Uint8 *stream, int length);
void audio_stats(Audio *audio, uint64_t now_ms, DirectMetrics *metrics);
void gamepad_init(Gamepad *gamepad);
bool gamepad_tick(Gamepad *gamepad);
