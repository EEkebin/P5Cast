// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#pragma once
#include "Direct.h"

typedef struct Haptics Haptics;
Haptics *haptics_create(void);
void haptics_controller(Haptics *h, SDL_GameController *pad);
void haptics_frame(uint8_t *pcm, size_t bytes, void *user);
void haptics_intensity(Haptics *h, ChiakiDualSenseEffectIntensity intensity);
bool haptics_playing(Haptics *h);
void haptics_destroy(Haptics *h);

// Expand one interleaved 3 kHz input block into the rear channels of 48 kHz quad audio.
void haptics_expand(float *quad, const int16_t *stereo, size_t frames,
                    int16_t previous[2], bool *continuous, float gain);
void verify_haptics(void);
