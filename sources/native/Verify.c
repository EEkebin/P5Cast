// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#include "Direct.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define CHECK(x) do { if (!(x)) { fprintf(stderr, "Direct verification failed on line %d: %s\n", __LINE__, #x); exit(1); } } while (0)

static void verify_profile(void)
{
    ChiakiConnectVideoProfile profile;
    CHECK(video_profile(&profile, 1, NULL, NULL, NULL));
    CHECK(profile.width == 1920 && profile.height == 1080 && profile.max_fps == 60 && profile.bitrate == 15000);
    CHECK(video_profile(&profile, 2, "30", "0", NULL) && profile.max_fps == 30 && profile.bitrate == 15000);
    CHECK(video_profile(&profile, 3, "60", "100000", NULL) && profile.bitrate == 100000 && profile.codec == CHIAKI_CODEC_H265_HDR);
    CHECK(video_profile(&profile, 1, "60", "2000", NULL) && profile.bitrate == 2000);
    CHECK(video_profile(&profile, 2, "60", "37500", NULL) && profile.bitrate == 37500);
    CHECK(!video_profile(&profile, 1, "120", "0", NULL) && !video_profile(&profile, 0, "60", NULL, NULL));
    CHECK(video_profile(&profile, 1, "60", "0", "1080") && profile.width == 1920 && profile.height == 1080 && profile.bitrate == 15000);
    CHECK(video_profile(&profile, 2, "30", "0", "720") && profile.width == 1280 && profile.height == 720 && profile.bitrate == 10000 && profile.max_fps == 30);
    CHECK(video_profile(&profile, 3, "60", "0", "540") && profile.width == 960 && profile.height == 540 && profile.bitrate == 6000 && profile.codec == CHIAKI_CODEC_H265_HDR);
    CHECK(video_profile(&profile, 1, "60", "0", "360") && profile.width == 640 && profile.height == 360 && profile.bitrate == 2000);
    CHECK(video_profile(&profile, 3, "60", "50000", "720") && profile.bitrate == 50000 && profile.codec == CHIAKI_CODEC_H265_HDR);
    const char *invalid_heights[] = {"", "1440", "2160", "0720", "720p", "360 "};
    for (size_t i = 0; i < sizeof(invalid_heights)/sizeof(invalid_heights[0]); i++)
        CHECK(!video_profile(&profile, 1, "60", "0", invalid_heights[i]));
    const char *invalid[] = {"", "1999", "100001", "-2000", "5e4", "35000 ", "4294967296"};
    for (size_t i = 0; i < sizeof(invalid)/sizeof(invalid[0]); i++) CHECK(!video_profile(&profile, 1, "60", invalid[i], NULL));
}

static void verify_video(int codec, bool extended)
{
    char path[100];
    snprintf(path, sizeof(path), "\\\\.\\pipe\\CadrivectVerify-%lu-%d", GetCurrentProcessId(), codec);
    HANDLE input = CreateNamedPipeA(path, PIPE_ACCESS_INBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, 1, 1024, 1024, 1000, NULL);
    CHECK(input != INVALID_HANDLE_VALUE);
    Video video = {0};
    video.codec = codec;
    video.extended_stats = extended;
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
    DirectMetrics metrics = {14000, 1, UINT32_MAX, DIRECT_AUDIO_TARGET_US, 128000, 48000, 2};
    video_stats(&video, &metrics);
    DWORD count;
    uint8_t packet[200];
    CHECK(ReadFile(input, packet, sizeof(packet), &count, NULL));
    uint8_t expected[200] = {0};
    uint32_t header[] = {extended ? 0x32564443u : 0x31564443u, (uint32_t)codec, config_size + sizeof(intra)};
    size_t off = 0;
#define APPEND(data, n) do { memcpy(expected + off, data, n); off += n; } while (0)
    APPEND(header, sizeof(header)); APPEND(config, config_size); APPEND(intra, sizeof(intra));
    uint32_t inter_size = sizeof(inter), sentinel = 0;
    APPEND(&inter_size, 4); APPEND(inter, sizeof(inter));
    APPEND(&header[2], 4); APPEND(config, config_size); APPEND(intra, sizeof(intra));
    uint32_t stats[] = {14000, 1, UINT32_MAX, DIRECT_AUDIO_TARGET_US, 128000, 48000, 2};
    APPEND(&sentinel, 4); APPEND(stats, extended ? sizeof(stats) : 4 * sizeof(uint32_t));
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
    DirectMetrics metrics = {0};
    audio_stats(&audio, GetTickCount64(), &metrics);
    CHECK(metrics.queued_us == 5000 && !audio.playing && !metrics.underruns);
    audio_frame(silence, 240, &audio); audio_frame(silence, 240, &audio);
    CHECK(audio.playing);
    for (int i=0; i<100 && SDL_GetQueuedAudioSize(audio.device); i++) SDL_Delay(5);
    audio_frame(silence, 240, &audio);
    audio_stats(&audio, GetTickCount64(), &metrics);
    CHECK(metrics.underruns == 1 && !audio.playing && metrics.queued_us == 5000);
    for (int i=0; i<100; i++) {
        audio_frame(silence, 240, &audio);
        audio_stats(&audio, GetTickCount64(), &metrics);
        CHECK(metrics.queued_us <= 65000);
    }
    SDL_CloseAudioDevice(audio.device);
    Audio received = {0};
    ChiakiOpusDecoder decoder;
    ChiakiAudioSink sink;
    ChiakiLog log;
    chiaki_log_init(&log, 0, NULL, NULL);
    chiaki_opus_decoder_init(&decoder, &log);
    audio_decoder_sink(&received, &decoder, &sink);
    audio_stats(&received, GetTickCount64(), &metrics);
    CHECK(metrics.audio_bps == UINT32_MAX && metrics.sample_rate == UINT32_MAX && metrics.channels == UINT32_MAX);
    ChiakiAudioHeader header;
    chiaki_audio_header_set(&header, 2, 16, 48000, 960);
    sink.header_cb(&header, sink.user);
    CHECK(received.device && !received.failed);
    uint8_t opus_silence[] = {0xf8, 0xff, 0xfe};
    sink.frame_cb(opus_silence, sizeof(opus_silence), sink.user);
    sink.frame_cb(NULL, 0, sink.user);
    CHECK(received.playing); // The actual Opus decoder queued PCM on the dummy device.
    uint64_t start = received.window_start_ms;
    audio_stats(&received, start+1000, &metrics);
    CHECK(metrics.sample_rate == 48000 && metrics.channels == 2 && metrics.audio_bps == 24);
    audio_stats(&received, start+2000, &metrics);
    CHECK(metrics.audio_bps == 0);
    sink.frame_cb(opus_silence, sizeof(opus_silence), sink.user);
    sink.frame_cb(opus_silence, sizeof(opus_silence), sink.user);
    audio_stats(&received, start+2500, &metrics);
    CHECK(metrics.audio_bps == 96);
    chiaki_opus_decoder_fini(&decoder);
    SDL_CloseAudioDevice(received.device);
    SDL_AudioQuit();
}

static void verify_first_audio_volume(void)
{
    char directory[MAX_PATH], filename[MAX_PATH], name[200];
    CHECK(GetTempPathA(MAX_PATH, directory) > 0);
    CHECK(GetTempFileNameA(directory, "p5v", 0, filename));
    CHECK(SDL_setenv("SDL_DISKAUDIOFILE", filename, 1) == 0);
    CHECK(SDL_AudioInit("disk") == 0 && !strcmp(SDL_GetCurrentAudioDriver(), "disk"));
    snprintf(name, sizeof(name), "Local\\P5Cast.Rest.%lu.volume-test.volume", GetCurrentProcessId());
    HANDLE map = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(LONG), name);
    CHECK(map && GetLastError() != ERROR_ALREADY_EXISTS);
    volatile LONG *percent = MapViewOfFile(map, FILE_MAP_WRITE, 0, 0, sizeof(LONG));
    CHECK(percent);
    InterlockedExchange(percent, 0);
    Audio audio = {0};
    CHECK(!audio_open_volume(&audio, "wrong-session"));
    CHECK(audio_open_volume(&audio, "volume-test"));
    audio_settings(2, 48000, &audio);
    CHECK(audio.device && !audio.failed);
    int16_t pcm[240 * 2];
    const LONG levels[] = {0, 25, 100};
    for (size_t block = 0; block < 3; block++) {
        InterlockedExchange(percent, levels[block]);
        for (size_t i = 0; i < 240; i++) {
            pcm[i*2] = block == 0 ? 29000 : 12000;
            pcm[i*2+1] = block == 0 ? -27000 : -10000;
        }
        audio_frame(pcm, 240, &audio);
        if (!block) CHECK(SDL_GetQueuedAudioSize(audio.device) == sizeof(pcm) && !audio.playing);
    }
    CHECK(audio.playing);
    for (int i = 0; i < 200 && SDL_GetQueuedAudioSize(audio.device); i++) SDL_Delay(5);
    CHECK(!SDL_GetQueuedAudioSize(audio.device));
    SDL_Delay(25);
    SDL_CloseAudioDevice(audio.device);
    audio_close_volume(&audio); CHECK(!audio.volume_percent);
    UnmapViewOfFile((const void *)percent); CloseHandle(map);
    SDL_AudioQuit();

    FILE *capture = fopen(filename, "rb");
    CHECK(capture);
    int16_t stereo[2]; int leading_silence = 0, scaled = 0, full = 0;
    while (fread(stereo, sizeof(stereo), 1, capture) == 1) {
        if (stereo[0] == 0 && stereo[1] == 0) {
            if (!scaled && !full) leading_silence++;
        } else if (stereo[0] == 3000 && stereo[1] == -2500) {
            CHECK(!full); scaled++;
        } else {
            CHECK(stereo[0] == 12000 && stereo[1] == -10000); full++;
        }
    }
    CHECK(!ferror(capture)); fclose(capture); CHECK(DeleteFileA(filename));
    CHECK(leading_silence >= 240 && scaled == 240 && full == 240);
    puts("Measured SDL disk output: first 240 stereo frames silent, next 240 at 25%, last 240 bit-exact at 100%.");
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

static int rest_calls;
static ChiakiErrorCode record_rest(ChiakiSession *session)
{
    (void)session;
    rest_calls++;
    return CHIAKI_ERR_SUCCESS;
}

static void verify_rest(void)
{
    char name[100];
    const char *suffix[] = {"request","accepted","rejected"};
    HANDLE events[3];
    for (int i = 0; i < 3; i++) {
        snprintf(name, sizeof(name), "Local\\P5Cast.Rest.%lu.fixture.%s", GetCurrentProcessId(), suffix[i]);
        events[i] = CreateEventA(NULL, TRUE, FALSE, name);
        CHECK(events[i] && GetLastError() != ERROR_ALREADY_EXISTS);
    }
    RestControl control = {0};
    rest_open(&control, "another-session");
    rest_tick(&control, true, NULL, record_rest);
    CHECK(!rest_calls && !control.request);
    rest_open(&control, "fixture");
    CHECK(control.request && control.accepted && control.rejected);
    rest_tick(&control, true, NULL, record_rest);
    rest_close(&control);
    CHECK(!rest_calls); // Ordinary stop or disconnect never issues a rest request.
    rest_open(&control, "fixture");
    SetEvent(events[0]);
    rest_tick(&control, false, NULL, record_rest);
    CHECK(!rest_calls && WaitForSingleObject(events[2], 0) == WAIT_OBJECT_0);
    ResetEvent(events[2]);
    rest_tick(&control, true, NULL, record_rest);
    CHECK(!rest_calls);
    SetEvent(events[0]);
    rest_tick(&control, true, NULL, record_rest);
    SetEvent(events[0]);
    rest_tick(&control, true, NULL, record_rest);
    CHECK(rest_calls == 1 && WaitForSingleObject(events[1], 0) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(events[2], 0) == WAIT_TIMEOUT);
    rest_close(&control);
    for (int i = 0; i < 3; i++) CloseHandle(events[i]);
}

#undef main
int main(void)
{
    SDL_SetMainReady();
    verify_profile();
    for (int codec = 1; codec <= 3; codec++) {
        verify_video(codec, false);
        verify_video(codec, true);
    }
    verify_audio();
    verify_first_audio_volume();
    verify_rest();
    verify_virtual_pad();
    puts("Verified video bitrate profiles, CDV1/CDV2 framing, actual decoded stereo Opus and bitrate, bounded dummy queue, virtual controller, and session-scoped explicit rest command exactly once.");
    return 0;
}
