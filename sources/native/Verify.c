// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#include "Direct.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Direct verification failed on line %d: %s\n", __LINE__, #x); exit(1); } } while (0)

static void verify_video(int codec)
{
    char path[100];
    snprintf(path, sizeof(path), "\\\\.\\pipe\\CadrivectVerify-%lu-%d", GetCurrentProcessId(), codec);
    HANDLE input = CreateNamedPipeA(path, PIPE_ACCESS_INBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, 1, 1024, 1024, 1000, NULL);
    CHECK(input != INVALID_HANDLE_VALUE);
    Video video = {0};
    video.codec = codec;
    video.stop = CreateEventW(NULL, TRUE, FALSE, NULL);
    video.pipe = CreateFileA(path, GENERIC_WRITE, 0, NULL, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
    CHECK(video.stop && video.pipe != INVALID_HANDLE_VALUE && video_header(&video));
    uint8_t config_avc[] = {0,0,0,1,0x67,8,0,0,1,0x68,7};
    uint8_t config_hevc[] = {0,0,1,64,1,0,0,1,66,1,0,0,1,68,1};
    uint8_t inter[] = {0,0,1,codec == 1 ? 0x41 : 2,1};
    uint8_t intra[] = {0,0,1,codec == 1 ? 0x65 : 38,1};
    uint8_t *config = codec == 1 ? config_avc : config_hevc;
    uint32_t config_size = codec == 1 ? sizeof(config_avc) : sizeof(config_hevc);
    CHECK(!video_sample(inter, sizeof(inter), 0, false, &video));
    CHECK(video_sample(config, config_size, 0, false, &video));
    CHECK(!video_sample(inter, sizeof(inter), 0, false, &video));
    CHECK(video_sample(intra, sizeof(intra), 0, false, &video));
    CHECK(video_sample(inter, sizeof(inter), 0, false, &video));
    CHECK(video_sample(config, config_size, 0, false, &video));
    CHECK(!video_sample(inter, sizeof(inter), 0, false, &video));
    CHECK(video_sample(intra, sizeof(intra), 0, false, &video));
    uint32_t stats[] = {14000, 1, UINT32_MAX, DIRECT_AUDIO_TARGET_US};
    video_stats(&video, stats);
    DWORD count;
    uint8_t packet[200];
    CHECK(ReadFile(input, packet, sizeof(packet), &count, NULL));
    uint8_t expected[200] = {0};
    uint32_t header[] = {0x31564443, (uint32_t)codec, config_size + sizeof(intra)};
    size_t off = 0;
#define APPEND(data, n) do { memcpy(expected + off, data, n); off += n; } while (0)
    APPEND(header, sizeof(header)); APPEND(config, config_size); APPEND(intra, sizeof(intra));
    uint32_t inter_size = sizeof(inter), sentinel = 0;
    APPEND(&inter_size, 4); APPEND(inter, sizeof(inter));
    APPEND(&header[2], 4); APPEND(config, config_size); APPEND(intra, sizeof(intra));
    APPEND(&sentinel, 4); APPEND(stats, sizeof(stats));
    CHECK(count == off && !memcmp(expected, packet, off));
    CloseHandle(input);
    CHECK(!video_sample(inter, sizeof(inter), 0, false, &video));
    CHECK(WaitForSingleObject(video.stop, 0) == WAIT_OBJECT_0);
    free(video.config);
    CloseHandle(video.pipe); CloseHandle(video.stop);
}

static void verify_audio(void)
{
    CHECK(SDL_AudioInit("dummy") == 0);
    CHECK(!strcmp(SDL_GetCurrentAudioDriver(), "dummy"));
    Audio audio = {0};
    audio_settings(2, 48000, &audio);
    CHECK(audio.device && !audio.failed);
    int16_t silence[240 * 2] = {0};
    audio_frame(silence, 240, &audio);
    uint32_t queued, underruns;
    audio_stats(&audio, &queued, &underruns);
    CHECK(queued == 5000 && !audio.playing && !underruns);
    audio_frame(silence, 240, &audio); audio_frame(silence, 240, &audio);
    CHECK(audio.playing);
    for (int i=0; i<100 && SDL_GetQueuedAudioSize(audio.device); i++) SDL_Delay(5);
    audio_frame(silence, 240, &audio);
    audio_stats(&audio, &queued, &underruns);
    CHECK(underruns == 1 && !audio.playing && queued == 5000);
    for (int i=0; i<100; i++) {
        audio_frame(silence, 240, &audio);
        audio_stats(&audio, &queued, &underruns);
        CHECK(queued <= 65000);
    }
    SDL_CloseAudioDevice(audio.device);
    SDL_AudioQuit();
}

static void verify_virtual_pad(void)
{
    SDL_SetHintWithPriority(SDL_HINT_JOYSTICK_HIDAPI, "0", SDL_HINT_OVERRIDE);
    SDL_SetHintWithPriority(SDL_HINT_JOYSTICK_WGI, "0", SDL_HINT_OVERRIDE);
    SDL_SetHintWithPriority(SDL_HINT_JOYSTICK_RAWINPUT, "0", SDL_HINT_OVERRIDE);
    SDL_SetHintWithPriority(SDL_HINT_XINPUT_ENABLED, "0", SDL_HINT_OVERRIDE);
    SDL_SetHintWithPriority(SDL_HINT_DIRECTINPUT_ENABLED, "0", SDL_HINT_OVERRIDE);
    CHECK(SDL_InitSubSystem(SDL_INIT_GAMECONTROLLER) == 0);
    int index = SDL_JoystickAttachVirtual(SDL_JOYSTICK_TYPE_GAMECONTROLLER, SDL_CONTROLLER_AXIS_MAX, SDL_CONTROLLER_BUTTON_MAX, 0);
    CHECK(index >= 0 && SDL_JoystickIsVirtual(index));
    Gamepad pad = {0};
    gamepad_init(&pad);
    pad.pad = SDL_GameControllerOpen(index);
    CHECK(pad.pad);
    SDL_Joystick *joy = SDL_GameControllerGetJoystick(pad.pad);
    CHECK(SDL_JoystickSetVirtualButton(joy, SDL_CONTROLLER_BUTTON_A, SDL_PRESSED) == 0);
    CHECK(SDL_JoystickSetVirtualButton(joy, SDL_CONTROLLER_BUTTON_START, SDL_PRESSED) == 0);
    CHECK(SDL_JoystickSetVirtualAxis(joy, SDL_CONTROLLER_AXIS_LEFTX, 14500) == 0);
    CHECK(SDL_JoystickSetVirtualAxis(joy, SDL_CONTROLLER_AXIS_TRIGGERRIGHT, 32767) == 0);
    CHECK(gamepad_tick(&pad));
    CHECK((pad.current.buttons & (CHIAKI_CONTROLLER_BUTTON_CROSS | CHIAKI_CONTROLLER_BUTTON_OPTIONS)) ==
        (CHIAKI_CONTROLLER_BUTTON_CROSS | CHIAKI_CONTROLLER_BUTTON_OPTIONS));
    CHECK(pad.current.left_x == 14500 && pad.current.r2_state == 255);
    CHECK(SDL_JoystickDetachVirtual(index) == 0);
    CHECK(gamepad_tick(&pad) && !pad.pad && !pad.current.buttons && !pad.current.r2_state);
    SDL_QuitSubSystem(SDL_INIT_GAMECONTROLLER);
}

#undef main
int main(void)
{
    SDL_SetMainReady();
    verify_video(1); verify_video(2); verify_video(3);
    verify_audio();
    verify_virtual_pad();
    puts("Verified CDV1 SPS/PPS/VPS + random access + metrics + disconnection, dummy stereo queue + underrun + backlog cap, and virtual controller input + unplug.");
    return 0;
}
