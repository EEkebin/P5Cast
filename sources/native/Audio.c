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

static void measure_gap(int64_t *last, int64_t *maximum)
{
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    if (*last && now.QuadPart - *last > *maximum) *maximum = now.QuadPart - *last;
    *last = now.QuadPart;
}

static uint32_t take_gap(int64_t *maximum, int64_t frequency)
{
    if (!*maximum || !frequency) return UINT32_MAX;
    double us = *maximum * (1000000.0 / (double)frequency);
    *maximum = 0;
    return us < UINT32_MAX - 1 ? (uint32_t)(us + 0.5) : UINT32_MAX - 1;
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
    // libopus accepts five output rates and up to 120 ms of PCM per packet.
    uint32_t rate = header->rate;
    if ((header->channels != 1 && header->channels != 2) ||
        (rate != 8000 && rate != 12000 && rate != 16000 && rate != 24000 && rate != 48000) ||
        !header->frame_size || header->frame_size > rate * DIRECT_OPUS_MAX_MS / 1000) {
        InterlockedExchange(&audio->failed, DIRECT_AUDIO_FORMAT_ERROR);
        return;
    }
    audio->decoder_sink.header_cb(header, audio->decoder_sink.user);
    ChiakiOpusDecoder *decoder = audio->decoder_sink.user;
    if (!decoder->opus_decoder) InterlockedExchange(&audio->failed, DIRECT_AUDIO_FORMAT_ERROR);
}

static void opus_frame(uint8_t *packet, size_t bytes, void *user)
{
    Audio *audio = user;
    if (InterlockedCompareExchange(&audio->failed, 0, 0)) return;
    // libchiaki delivers deduplicated Opus frames and uses zero bytes for concealment.
    AcquireSRWLockExclusive(&audio->lock);
    audio->compressed_bytes += bytes;
    if (!bytes) audio->concealments++;
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
    audio->target_us = DIRECT_AUDIO_TARGET_US;
    audio->stable_frames = audio->device_frames = audio->last_underrun_frame = 0;
    audio->release_frames = 0;
    audio->last_input_tick = audio->last_callback_tick = audio->max_input_ticks = audio->max_callback_ticks = 0;
    QueryPerformanceFrequency(&audio->frequency);
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
        // Keep the device callback below the initial 15 ms reserve at every Opus rate.
        spec.samples = rate >= 32000 ? 256 : rate >= 16000 ? 128 : 64;
        spec.callback = audio_render;
        spec.userdata = audio;
        audio->device = SDL_OpenAudioDevice(NULL, 0, &spec, NULL, 0);
    }
    if (audio->device) SDL_PauseAudioDevice(audio->device, 0);
    else InterlockedExchange(&audio->failed, DIRECT_AUDIO_DEVICE_ERROR);
    ReleaseSRWLockExclusive(&audio->lock);
}

void audio_stats(Audio *audio, uint64_t now_ms, DirectMetrics *metrics)
{
    AcquireSRWLockExclusive(&audio->lock);
    if (audio->device) SDL_LockAudioDevice(audio->device);
    metrics->queued_us = audio->device ? (uint32_t)(audio->queued_frames * 1000000ull / audio->rate) : UINT32_MAX;
    metrics->target_us = audio->device ? audio->target_us : UINT32_MAX;
    metrics->input_gap_us = take_gap(&audio->max_input_ticks, audio->frequency.QuadPart);
    metrics->callback_gap_us = take_gap(&audio->max_callback_ticks, audio->frequency.QuadPart);
    metrics->underruns = audio->underruns;
    metrics->concealments = audio->concealments;
    metrics->queue_trims = audio->queue_trims;
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
    if (audio->device && samples && samples <= audio->rate * DIRECT_OPUS_MAX_MS / 1000) {
        SDL_LockAudioDevice(audio->device);
        measure_gap(&audio->last_input_tick, &audio->max_input_ticks);
        size_t limit = (size_t)audio->rate * (uint64_t)(audio->target_us + 45000u) / 1000000;
        if (audio->queued_frames + samples > limit && audio->queued_frames) {
            // Preserve an extra 15 ms after a late burst so its following gap
            // does not immediately exhaust a queue just cut down to its target.
            size_t reserve = (size_t)audio->rate * (uint64_t)(audio->target_us + 15000u) / 1000000;
            size_t keep = samples < reserve ? reserve - samples : 0;
            audio->read_frame = (audio->read_frame + audio->queued_frames - keep) % DIRECT_AUDIO_CAPACITY;
            audio->queued_frames = keep;
            audio->queue_trims++;
            audio->release_frames = 0;
            audio->stable_frames = 0;
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
    measure_gap(&audio->last_callback_tick, &audio->max_callback_ticks);
    audio->device_frames += frames;
    size_t target = (size_t)audio->rate * (uint64_t)audio->target_us / 1000000;
    if (!audio->playing && audio->queued_frames >= target && audio->queued_frames >= frames) {
        audio->playing = true;
        begin_ramp(audio);
    }
    size_t ready = audio->playing ? audio->queued_frames : 0;
    if (ready > frames) ready = frames;
    // After stable playback, release any surplus by blending one extra input
    // frame across a whole output callback. Leave the new reserve intact.
    bool shorten = audio->playing && frames > 1 && audio->release_frames && audio->queued_frames > frames + target;
    for (size_t i = 0; i < frames; ++i) {
        if (i == ready && audio->playing) {
            audio->underruns++;
            if (audio->last_underrun_frame && audio->device_frames - audio->last_underrun_frame <= 3ull * audio->rate) {
                audio->target_us += 10000;
                if (audio->target_us > DIRECT_AUDIO_MAX_TARGET_US) audio->target_us = DIRECT_AUDIO_MAX_TARGET_US;
            }
            audio->last_underrun_frame = audio->device_frames;
            audio->stable_frames = audio->release_frames = 0;
            audio->playing = false;
            begin_ramp(audio);
        }
        for (uint32_t c = 0; c < audio->channels; c++) {
            int sample = i < ready ? audio->pcm[audio->read_frame * audio->channels + c] : 0;
            if (shorten) {
                size_t next = (audio->read_frame + 1) % DIRECT_AUDIO_CAPACITY;
                int other = audio->pcm[next * audio->channels + c];
                sample += (other - sample) * (int)(i + 1) / (int)frames;
            }
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
    if (shorten) {
        audio->read_frame = (audio->read_frame + 1) % DIRECT_AUDIO_CAPACITY;
        audio->queued_frames--;
        audio->release_frames--;
    }
    if (audio->playing && audio->target_us > DIRECT_AUDIO_TARGET_US) {
        audio->stable_frames += frames;
        if (audio->stable_frames >= 10ull * audio->rate) {
            audio->stable_frames = 0;
            audio->target_us -= 5000;
            audio->release_frames = audio->rate / 200;
        }
    }
}
