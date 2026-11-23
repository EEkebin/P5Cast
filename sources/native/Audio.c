// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#include "Direct.h"

void audio_settings(uint32_t channels, uint32_t rate, void *user)
{
    Audio *audio = user;
    AcquireSRWLockExclusive(&audio->lock);
    if (audio->device) SDL_CloseAudioDevice(audio->device);
    audio->device = 0;
    audio->playing = false;
    audio->channels = channels;
    audio->rate = rate;
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

void audio_stats(Audio *audio, uint32_t *queued_us, uint32_t *underruns)
{
    AcquireSRWLockShared(&audio->lock);
    *queued_us = queue_us(audio);
    *underruns = audio->underruns;
    ReleaseSRWLockShared(&audio->lock);
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
        if (SDL_QueueAudio(audio->device, pcm, (Uint32)(samples * audio->channels * sizeof(int16_t))) != 0) {
            InterlockedExchange(&audio->failed, 1);
        } else if (!audio->playing && queue_us(audio) >= DIRECT_AUDIO_TARGET_US) {
            SDL_PauseAudioDevice(audio->device, 0);
            audio->playing = true;
        }
    }
    ReleaseSRWLockExclusive(&audio->lock);
}
