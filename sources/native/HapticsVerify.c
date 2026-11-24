// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#include "Haptics.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void check(bool condition, const char *message)
{
    if (!condition) { fprintf(stderr, "Haptics verification failed: %s\n", message); exit(1); }
}

void verify_haptics(void)
{
    const int16_t stereo[] = {-32768, 32767, 32767, -32768, 8192, -8192};
    float quad[3 * 16 * 4], split[3 * 16 * 4];
    int16_t prev[2] = {0};
    bool continuous = false;
    haptics_expand(quad, stereo, 3, prev, &continuous, 1);
    for (int i = 0; i < 48; i++) {
        check(quad[4*i] == 0 && quad[4*i+1] == 0, "front/headphone channels must be silent");
        if (i < 16) check(quad[4*i+2] == -1 && quad[4*i+3] == 32767 / 32768.0f, "first stereo sample");
    }
    check(quad[4*23+2] == -.5f/32768 && quad[4*23+3] == -.5f/32768, "linear midpoint");
    check(quad[4*47+2] == .25f && quad[4*47+3] == -.25f, "left/right actuator routing");
    continuous = false;
    haptics_expand(split, stereo, 1, prev, &continuous, 1);
    haptics_expand(split + 64, stereo + 2, 2, prev, &continuous, 1);
    check(memcmp(quad, split, sizeof(quad)) == 0, "packet boundaries");
    continuous = false;
    haptics_expand(split, stereo, 3, prev, &continuous, .5f);
    for (int i = 0; i < 3*16*4; ++i) check(split[i] == quad[i]*.5f, "console intensity");
    Haptics *h = haptics_create();
    check(h != NULL && !haptics_playing(h), "initial state");
    haptics_controller(h, NULL);
    haptics_intensity(h, Off);
    haptics_frame((uint8_t *)stereo, sizeof(stereo), h);
    haptics_frame((uint8_t *)stereo, 3, h);
    check(!haptics_playing(h), "discard feedback without a matched controller");
    haptics_destroy(h);
    puts("Verified USB haptic channel routing, 3-to-48 kHz conversion, packet boundaries, intensity and idle teardown.");
}
