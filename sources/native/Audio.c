// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#include "Direct.h"

#include <stdio.h>
#include <string.h>

bool audio_open_volume(Audio *audio, const char *pipe_name)
{
    char name[200];
    int size = snprintf(name, sizeof(name), "Local\\P5Cast.Rest.%lu.%s.volume", GetCurrentProcessId(), pipe_name);
    if (size < 0 || (size_t)size >= sizeof(name)) return false;
    HANDLE mapping = OpenFileMappingA(FILE_MAP_READ, FALSE, name);
    if (!mapping) return false;
    audio->volume_percent = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, sizeof(LONG));
    CloseHandle(mapping); // The mapped view holds its own reference.
    return audio->volume_percent != NULL;
}

void audio_close_volume(Audio *audio)
{
    if (audio->volume_percent) UnmapViewOfFile((const void *)audio->volume_percent);
    audio->volume_percent = NULL;
}

// Two milliseconds soften a splice without processing steady playback.
static uint32_t ramp_frames(const Audio *audio)
{
    return audio->rate / 500;
}

static void begin_ramp(Audio *audio)
{
    memcpy(audio->ramp_start, audio->last_output, sizeof(audio->last_output));
    audio->ramp_left = ramp_frames(audio);
}

static void apply_volume(Audio *audio, int16_t *pcm, size_t frames)
{
    LONG volume = audio->volume_percent ? __atomic_load_n(audio->volume_percent, __ATOMIC_ACQUIRE) : 100;
    if (volume < 0) volume = 0;
    if (volume > 100) volume = 100;
    LONG previous = audio->gain_initialized ? audio->gain_percent : volume;
    audio->gain_initialized = true;
    audio->gain_percent = volume;
    if (volume == 100 && previous == volume) return;
    size_t fade = previous != volume ? ramp_frames(audio) : 0;
    if (fade > frames) fade = frames;
    for (size_t i = 0; i < frames; ++i) {
        int gain = (int)volume, scale = 100;
        if (i < fade) {
            // A monotonic interpolation between the requested gains cannot amplify peaks.
            gain = (int)previous * (int)(fade - 1 - i) + (int)volume * (int)(i + 1);
            scale *= (int)fade;
        }
        for (uint32_t c = 0; c < audio->channels; c++) {
            size_t index = i * audio->channels + c;
            pcm[index] = (int16_t)((int32_t)pcm[index] * gain / scale);
        }
    }
}

static void opus_header(ChiakiAudioHeader *header, void *user)
{
    Audio *audio = user;
    audio->decoder_sink.header_cb(header, audio->decoder_sink.user);
}

static void opus_frame(uint8_t *packet, size_t bytes, void *user)
{
    Audio *audio = user;
    // libchiaki delivers deduplicated Opus frames and uses zero bytes for concealment.
    AcquireSRWLockExclusive(&audio->lock);
    audio->compressed_bytes += bytes;
    ReleaseSRWLockExclusive(&audio->lock);
    audio->decoder_sink.frame_cb(packet, bytes, audio->decoder_sink.user);
}

void audio_decoder_sink(Audio *audio, ChiakiOpusDecoder *decoder, ChiakiAudioSink *sink)
{
    chiaki_opus_decoder_set_cb(decoder, audio_settings, audio_frame, audio);
    chiaki_opus_decoder_get_sink(decoder, &audio->decoder_sink);
    sink->user = audio;
    sink->header_cb = opus_header;
    sink->frame_cb = opus_frame;
}

void audio_settings(uint32_t channels, uint32_t rate, void *user)
{
    Audio *audio = user;
    AcquireSRWLockExclusive(&audio->lock);
    if (audio->device) SDL_CloseAudioDevice(audio->device);
    audio->device = 0;
    audio->playing = false;
    audio->queued_frames = audio->read_frame = 0;
    audio->gain_initialized = false;
    memset(audio->last_output, 0, sizeof(audio->last_output));
    audio->ramp_left = 0;
    audio->channels = channels;
    audio->rate = rate;
    audio->window_start_ms = GetTickCount64();
    audio->compressed_bytes = 0;
    if ((channels == 1 || channels == 2) && rate >= 8000 && rate <= 48000) {
        SDL_AudioSpec spec = {0};
        spec.freq = (int)rate;
        spec.format = AUDIO_S16SYS;
        spec.channels = (Uint8)channels;
        spec.samples = 256;
        spec.callback = audio_render;
        spec.userdata = audio;
        audio->device = SDL_OpenAudioDevice(NULL, 0, &spec, NULL, 0);
    }
    if (audio->device) SDL_PauseAudioDevice(audio->device, 0);
    else InterlockedExchange(&audio->failed, 1);
    ReleaseSRWLockExclusive(&audio->lock);
}

void audio_stats(Audio *audio, uint64_t now_ms, DirectMetrics *metrics)
{
    AcquireSRWLockExclusive(&audio->lock);
    if (audio->device) SDL_LockAudioDevice(audio->device);
    metrics->queued_us = audio->device ? (uint32_t)(audio->queued_frames * 1000000ull / audio->rate) : UINT32_MAX;
    metrics->underruns = audio->underruns;
    if (audio->device) SDL_UnlockAudioDevice(audio->device);
    metrics->sample_rate = audio->rate ? audio->rate : UINT32_MAX;
    metrics->channels = audio->channels ? audio->channels : UINT32_MAX;
    metrics->audio_bps = UINT32_MAX;
    if (audio->rate && now_ms > audio->window_start_ms) {
        uint64_t elapsed = now_ms - audio->window_start_ms;
        metrics->audio_bps = (uint32_t)(audio->compressed_bytes * 8000 / elapsed);
        audio->compressed_bytes = 0;
        audio->window_start_ms = now_ms;
    }
    ReleaseSRWLockExclusive(&audio->lock);
}

void audio_frame(int16_t *pcm, size_t samples, void *user)
{
    Audio *audio = user;
    AcquireSRWLockExclusive(&audio->lock);
    if (audio->device && samples && samples <= audio->rate / 10) {
        SDL_LockAudioDevice(audio->device);
        size_t target = (size_t)audio->rate * DIRECT_AUDIO_TARGET_US / 1000000;
        if (audio->queued_frames + samples > 4 * target && audio->queued_frames) {
            // A burst accumulated stale audio. Keep the latest target duration,
            // including the new packet, and crossfade from the last emitted sample.
            size_t keep = samples < target ? target - samples : 0;
            audio->read_frame = (audio->read_frame + audio->queued_frames - keep) % DIRECT_AUDIO_CAPACITY;
            audio->queued_frames = keep;
            begin_ramp(audio);
        }
        apply_volume(audio, pcm, samples);
        size_t write = (audio->read_frame + audio->queued_frames) % DIRECT_AUDIO_CAPACITY;
        size_t first = DIRECT_AUDIO_CAPACITY - write;
        if (first > samples) first = samples;
        size_t stride = audio->channels * sizeof(int16_t);
        memcpy(audio->pcm + write * audio->channels, pcm, first * stride);
        memcpy(audio->pcm, pcm + first * audio->channels, (samples - first) * stride);
        audio->queued_frames += samples;
        SDL_UnlockAudioDevice(audio->device);
    }
    ReleaseSRWLockExclusive(&audio->lock);
}

// SDL owns the device lock during this callback. An empty application queue
// is an underrun only when the device actually requests missing samples.
void SDLCALL audio_render(void *user, Uint8 *stream, int length)
{
    Audio *audio = user;
    int16_t *output = (int16_t *)stream;
    size_t frames = (size_t)length / (audio->channels * sizeof(int16_t));
    uint32_t ramp = ramp_frames(audio);
    if (!audio->playing && audio->queued_frames >= (size_t)audio->rate * DIRECT_AUDIO_TARGET_US / 1000000) {
        audio->playing = true;
        begin_ramp(audio);
    }
    size_t ready = audio->playing ? audio->queued_frames : 0;
    if (ready > frames) ready = frames;
    for (size_t i = 0; i < frames; ++i) {
        if (i == ready && audio->playing) {
            audio->underruns++;
            audio->playing = false;
            begin_ramp(audio);
        }
        for (uint32_t c = 0; c < audio->channels; c++) {
            int sample = i < ready ? audio->pcm[audio->read_frame * audio->channels + c] : 0;
            if (audio->ramp_left) {
                uint32_t left = audio->ramp_left - 1;
                sample = (audio->ramp_start[c] * (int)left + sample * (int)(ramp - left)) / (int)ramp;
            }
            *output++ = audio->last_output[c] = (int16_t)sample;
        }
        if (audio->ramp_left) audio->ramp_left--;
        if (i < ready) audio->read_frame = (audio->read_frame + 1) % DIRECT_AUDIO_CAPACITY;
    }
    audio->queued_frames -= ready;
}
