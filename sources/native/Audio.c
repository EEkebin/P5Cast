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

static void apply_volume(const Audio *audio, int16_t *pcm, size_t count)
{
    if (!audio->volume_percent) return; // Legacy CLI calls use unmodified PCM.
    LONG volume = __atomic_load_n(audio->volume_percent, __ATOMIC_ACQUIRE);
    if (volume >= 100) return;
    if (volume <= 0) {
        memset(pcm, 0, count * sizeof(*pcm));
        return;
    }
    for (size_t i = 0; i < count; ++i) pcm[i] = (int16_t)((int32_t)pcm[i] * volume / 100);
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
    audio->channels = channels;
    audio->rate = rate;
    audio->window_start_ms = GetTickCount64();
    audio->compressed_bytes = 0;
    if (channels == 1 || channels == 2) {
        SDL_AudioSpec spec = {0};
        spec.freq = (int)rate;
        spec.format = AUDIO_S16SYS;
        spec.channels = (Uint8)channels;
        spec.samples = 256;
        audio->device = SDL_OpenAudioDevice(NULL, 0, &spec, NULL, 0);
    }
    if (!audio->device) InterlockedExchange(&audio->failed, 1);
    ReleaseSRWLockExclusive(&audio->lock);
}

static uint32_t queue_us(Audio *audio)
{
    if (!audio->device || !audio->rate || !audio->channels) return UINT32_MAX;
    uint64_t bytes = SDL_GetQueuedAudioSize(audio->device);
    return (uint32_t)((bytes * 1000000) / (audio->rate * audio->channels * sizeof(int16_t)));
}

void audio_stats(Audio *audio, uint64_t now_ms, DirectMetrics *metrics)
{
    AcquireSRWLockExclusive(&audio->lock);
    metrics->queued_us = queue_us(audio);
    metrics->underruns = audio->underruns;
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
        uint32_t queued = queue_us(audio);
        if (audio->playing && !queued) {
            audio->underruns++;
            SDL_PauseAudioDevice(audio->device, 1);
            audio->playing = false;
        }
        if (queued > DIRECT_AUDIO_TARGET_US * 4) {
            SDL_PauseAudioDevice(audio->device, 1);
            SDL_ClearQueuedAudio(audio->device);
            audio->playing = false;
        }
        size_t sample_count = samples * audio->channels;
        apply_volume(audio, pcm, sample_count);
        if (SDL_QueueAudio(audio->device, pcm, (Uint32)(sample_count * sizeof(int16_t))) != 0) {
            InterlockedExchange(&audio->failed, 1);
        } else if (!audio->playing && queue_us(audio) >= DIRECT_AUDIO_TARGET_US) {
            SDL_PauseAudioDevice(audio->device, 0);
            audio->playing = true;
        }
    }
    ReleaseSRWLockExclusive(&audio->lock);
}
