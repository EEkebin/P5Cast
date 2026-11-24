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

// Stall the reader after the sender starts a picture larger than the pipe quota.
static DWORD WINAPI drain_stalled_picture(void *input)
{
    DWORD available = 0, read = 0, total = 0;
    for (int attempt = 0; !available && attempt < 1000; attempt++) {
        CHECK(PeekNamedPipe(input, NULL, 0, NULL, &available, NULL));
        if (!available) Sleep(1);
    }
    CHECK(available);
    Sleep(50);
    uint8_t buffer[4096];
    while (total < 65536 + sizeof(uint32_t)) {
        CHECK(ReadFile(input, buffer, sizeof(buffer), &read, NULL) && read);
        total += read;
    }
    return 0;
}

static void verify_video(int codec, unsigned version)
{
    char path[100];
    snprintf(path, sizeof(path), "\\\\.\\pipe\\CadrivectVerify-%lu-%d", GetCurrentProcessId(), codec);
    HANDLE input = CreateNamedPipeA(path, PIPE_ACCESS_INBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, 1, 1024, 1024, 1000, NULL);
    CHECK(input != INVALID_HANDLE_VALUE);
    Video video = {0};
    video.codec = codec;
    video.stats_version = version;
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
    CHECK(video_sample(inter, sizeof(inter), 3, true, &video));
    CHECK(video_sample(config, config_size, 0, false, &video));
    CHECK(!video_sample(inter, sizeof(inter), 0, false, &video));
    CHECK(video_sample(intra, sizeof(intra), 0, false, &video));
    Audio audio = {0};
    audio.underruns = 1; audio.rate = 48000; audio.channels = 2;
    audio.compressed_bytes = 32000; audio.concealments = 1; audio.queue_trims = 4;
    audio.frequency.QuadPart = 1000000;
    audio.max_input_ticks = 27500; audio.max_callback_ticks = 6000;
    AcquireSRWLockExclusive(&video.writer);
    video_stats(&video, &audio, 1000, UINT32_MAX);
    ReleaseSRWLockExclusive(&video.writer);
    CHECK(audio.max_input_ticks == 27500 && audio.max_callback_ticks == 6000 &&
          audio.compressed_bytes == 32000 && audio.window_start_ms == 0);
    video_stats(&video, &audio, 2000, UINT32_MAX);
    CHECK(!audio.max_input_ticks && !audio.max_callback_ticks && !audio.compressed_bytes);
    DWORD count;
    uint8_t packet[200];
    CHECK(ReadFile(input, packet, sizeof(packet), &count, NULL));
    uint8_t expected[200] = {0};
    uint32_t header[] = {0x30564443u + (version << 24), (uint32_t)codec, config_size + sizeof(intra)};
    size_t off = 0;
#define APPEND(data, n) do { memcpy(expected + off, data, n); off += n; } while (0)
    APPEND(header, sizeof(header)); APPEND(config, config_size); APPEND(intra, sizeof(intra));
    uint32_t inter_size = sizeof(inter), sentinel = 0;
    APPEND(&inter_size, 4); APPEND(inter, sizeof(inter));
    APPEND(&header[2], 4); APPEND(config, config_size); APPEND(intra, sizeof(intra));
    uint32_t stats[] = {UINT32_MAX, 1, UINT32_MAX, UINT32_MAX, 128000, 48000, 2, 3, 1, 4, video.slow_handoffs, 27500, 6000};
    APPEND(&sentinel, 4); APPEND(stats, (version == 3 ? 13 : (version == 2 ? 7 : 4)) * sizeof(uint32_t));
    CHECK(count == off && !memcmp(expected, packet, off));
    if (codec == 1 && version == 3) {
        uint8_t *picture = calloc(65536, 1);
        CHECK(picture);
        memcpy(picture, inter, sizeof(inter));
        uint32_t before = video.slow_handoffs;
        HANDLE reader = CreateThread(NULL, 0, drain_stalled_picture, input, 0, NULL);
        CHECK(reader && video_sample(picture, 65536, 0, false, &video));
        CHECK(WaitForSingleObject(reader, 3000) == WAIT_OBJECT_0 && video.slow_handoffs == before + 1);
        CloseHandle(reader);
        free(picture);
    }
    CloseHandle(input);
    CHECK(!video_sample(inter, sizeof(inter), 0, false, &video));
    CHECK(WaitForSingleObject(video.stop, 0) == WAIT_OBJECT_0);
    free(video.config);
    CloseHandle(video.pipe); CloseHandle(video.stop);
}

static uint32_t verify_burst_train(bool adaptive)
{
    Audio audio = {0};
    audio_settings(2, 48000, &audio);
    CHECK(audio.device);
    SDL_PauseAudioDevice(audio.device, 1);
    int16_t block[240 * 2], output[240 * 2];
    for (int i = 0; i < 240; i++) { // Continuous 200 Hz triangle, opposite stereo polarity.
        int sample = (i < 60 ? i : i < 180 ? 120 - i : i - 240) * 200;
        block[2*i] = sample;
        block[2*i+1] = -sample;
    }
    // Requests before the first packet are startup silence, not underruns.
    for (int i = 0; i < 20; i++) audio_render(&audio, (Uint8 *)output, sizeof(output));
    CHECK(!audio.underruns && audio.target_us == 15000);
    int last = 0;
    for (int burst = 0; burst < 12; burst++) {
        // Every cycle supplies exactly 65 ms of media, in a single arrival burst.
        for (int i = 0; i < 13; i++) audio_frame(block, 240, &audio);
        for (int i = 0; i < 13; i++) {
            audio_render(&audio, (Uint8 *)output, sizeof(output));
            if (!adaptive) audio.target_us = DIRECT_AUDIO_TARGET_US;
            for (int j = 0; j < 240; j++) {
                CHECK(output[j*2+1] == -output[j*2] && abs(output[j*2] - last) <= 500);
                last = output[j*2];
            }
        }
        if (burst == 0) CHECK(audio.underruns == 1 && audio.target_us == 15000);
    }
    uint32_t underruns = audio.underruns;
    DirectMetrics metrics = {0};
    audio_stats(&audio, GetTickCount64(), &metrics);
    if (adaptive) {
        CHECK(underruns == 2 && metrics.target_us == 25000 && metrics.queue_trims == 2);
        for (int i = 0; i < 6; i++) audio_frame(block, 240, &audio);
        size_t before = audio.queued_frames;
        for (int i = 0; i < 23 * 200; i++) {
            audio_frame(block, 240, &audio);
            audio_render(&audio, (Uint8 *)output, sizeof(output));
            for (int j = 0; j < 240; j++) {
                CHECK(output[j*2+1] == -output[j*2] && abs(output[j*2] - last) <= 500);
                last = output[j*2];
            }
        }
        CHECK(audio.underruns == underruns && audio.target_us == 15000 && audio.queued_frames == before - 480);
        // Repeated genuine starvation can raise the reserve only up to its cap.
        for (int i = 0; i < 16; i++) audio_render(&audio, (Uint8 *)output, sizeof(output));
        for (int episode = 0; episode < 6; episode++) {
            for (int i = 0; i < 11; i++) audio_frame(block, 240, &audio);
            for (int i = 0; i < 12; i++) audio_render(&audio, (Uint8 *)output, sizeof(output));
        }
        CHECK(audio.target_us == DIRECT_AUDIO_MAX_TARGET_US && !audio.playing);
        uint32_t stopped = audio.underruns;
        for (int i = 0; i < 30; i++) audio_render(&audio, (Uint8 *)output, sizeof(output));
        CHECK(audio.underruns == stopped);
        SDL_Delay(25);
        audio_frame(block, 240, &audio); audio_render(&audio, (Uint8 *)output, sizeof(output));
        audio_stats(&audio, GetTickCount64(), &metrics);
        CHECK(metrics.target_us == 55000 && metrics.input_gap_us >= 20000 && metrics.callback_gap_us >= 20000 &&
              metrics.input_gap_us != UINT32_MAX && metrics.callback_gap_us != UINT32_MAX);
        audio_stats(&audio, GetTickCount64(), &metrics);
        CHECK(metrics.input_gap_us == UINT32_MAX && metrics.callback_gap_us == UINT32_MAX);
    } else CHECK(underruns == 12 && metrics.queue_trims == 12);
    SDL_CloseAudioDevice(audio.device);
    return underruns;
}

static void verify_audio(void)
{
    CHECK(SDL_AudioInit("dummy") == 0);
    CHECK(!strcmp(SDL_GetCurrentAudioDriver(), "dummy"));
    uint32_t fixed = verify_burst_train(false);
    uint32_t adaptive = verify_burst_train(true);
    printf("Synthetic 48 kHz burst train: %u underruns with fixed 15 ms, %u adaptive; stable playback returned to 15 ms.\n",
           (unsigned)fixed, (unsigned)adaptive);
    Audio audio = {0};
    audio_settings(2, 48000, &audio);
    CHECK(audio.device && !audio.failed);
    SDL_PauseAudioDevice(audio.device, 1); // Issue exact device requests below.
    int16_t tone[240 * 2], out[720 * 2];
    for (size_t i = 0; i < 240; i++) {tone[i*2] = 24000; tone[i*2+1] = -24000;}
    audio_frame(tone, 240, &audio);
    DirectMetrics metrics = {0};
    audio_stats(&audio, GetTickCount64(), &metrics);
    CHECK(metrics.queued_us == 5000 && !audio.playing && !metrics.underruns);
    audio_frame(tone, 240, &audio); audio_frame(tone, 240, &audio);
    audio_render(&audio, (Uint8 *)out, sizeof(out));
    CHECK(audio.playing && !audio.queued_frames && out[1438] == 24000);
    // The device has enough audio through the end of this buffer, even though
    // the producer sees zero queued. An on-time packet must play immediately.
    audio_frame(tone, 240, &audio);
    audio_render(&audio, (Uint8 *)out, sizeof(tone));
    CHECK(out[0] == 24000 && out[1] == -24000 && audio.underruns == 0);
    audio_render(&audio, (Uint8 *)out, sizeof(tone));
    for (int i=0; i<240; i++) {
        CHECK(out[i*2] == (i < 96 ? 24000 * (95-i) / 96 : 0));
        CHECK(out[i*2+1] == -out[i*2]);
    }
    CHECK(audio.underruns == 1 && !audio.playing);
    audio_frame(tone, 240, &audio); audio_frame(tone, 240, &audio); audio_frame(tone, 240, &audio);
    audio_render(&audio, (Uint8 *)out, sizeof(tone));
    CHECK(out[0] == 250 && out[190] == 24000 && out[478] == 24000);
    for (size_t i = 0; i < 240; i++) {tone[i*2] = -24000; tone[i*2+1] = 24000;}
    for (int i=0; i<100; i++) {
        audio_frame(tone, 240, &audio);
        audio_stats(&audio, GetTickCount64(), &metrics);
        CHECK(metrics.queued_us <= DIRECT_AUDIO_TARGET_US * 4);
    }
    CHECK(metrics.queue_trims > 0 && metrics.underruns == 1);
    audio_render(&audio, (Uint8 *)out, sizeof(tone));
    for (int i=0; i<240; ++i) {
        int expected = i < 96 ? 24000 - 500*(i+1) : -24000;
        CHECK(out[i*2] == expected && out[i*2+1] == -expected);
    }
    puts("Measured 48 kHz stereo: on-time boundary had zero underruns; starvation faded to silence; burst splice limited to 500 counts/sample for a 48000-count step.");
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
    SDL_PauseAudioDevice(received.device, 1);
    uint8_t opus_silence[] = {0xf8, 0xff, 0xfe};
    sink.frame_cb(opus_silence, sizeof(opus_silence), sink.user);
    sink.frame_cb(NULL, 0, sink.user);
    audio_render(&received, (Uint8 *)out, sizeof(tone));
    CHECK(received.playing); // The actual Opus decoder supplied PCM to the playback callback.
    uint64_t start = received.window_start_ms;
    audio_stats(&received, start+1000, &metrics);
    CHECK(metrics.sample_rate == 48000 && metrics.channels == 2 && metrics.audio_bps == 24 && metrics.concealments == 1);
    audio_stats(&received, start+2000, &metrics);
    CHECK(metrics.audio_bps == 0);
    sink.frame_cb(opus_silence, sizeof(opus_silence), sink.user);
    sink.frame_cb(opus_silence, sizeof(opus_silence), sink.user);
    audio_stats(&received, start+2500, &metrics);
    CHECK(metrics.audio_bps == 96);

    // Two 60 ms Opus silence frames in one valid constant-size packet (TOC config 3).
    chiaki_audio_header_set(&header, 2, 16, 48000, 5760);
    sink.header_cb(&header, sink.user);
    CHECK(received.device && !received.failed);
    SDL_PauseAudioDevice(received.device, 1);
    uint8_t long_silence[] = {0x19, 0xff, 0xfe, 0xff, 0xfe};
    sink.frame_cb(long_silence, sizeof(long_silence), sink.user);
    CHECK(received.queued_frames == 5760);
    header.rate = 44100;
    sink.header_cb(&header, sink.user);
    CHECK(received.failed == DIRECT_AUDIO_FORMAT_ERROR && decoder.audio_header.rate == 48000);
    sink.frame_cb(opus_silence, sizeof(opus_silence), sink.user);
    CHECK(received.queued_frames == 5760);
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
        if (!block) {
            DirectMetrics metrics = {0};
            audio_stats(&audio, GetTickCount64(), &metrics);
            CHECK(metrics.queued_us == 5000);
        }
    }
    DirectMetrics stats = {0};
    for (int i = 0; i < 200; i++) {
        audio_stats(&audio, GetTickCount64(), &stats);
        if (stats.underruns > 0) break;
        SDL_Delay(5);
    }
    CHECK(stats.underruns && !stats.queued_us);
    SDL_Delay(25);
    SDL_CloseAudioDevice(audio.device);
    audio_close_volume(&audio); CHECK(!audio.volume_percent);
    UnmapViewOfFile((const void *)percent); CloseHandle(map);
    SDL_AudioQuit();

    FILE *capture = fopen(filename, "rb");
    CHECK(capture);
    int16_t stereo[2]; int leading_silence = 0, emitted = 0;
    while (fread(stereo, sizeof(stereo), 1, capture) == 1) {
        if (emitted == 0 && stereo[0] == 0 && stereo[1] == 0) { leading_silence++; continue; }
        int l = 0, r = 0;
        if (emitted < 480) {
            int index = emitted % 240;
            int gain = emitted < 240 ? 25 : 100, previous = emitted < 240 ? 0 : 25;
            int numerator = index < 96 ? previous * (95-index) + gain * (index+1) : gain * 96;
            l = 12000 * numerator / 9600;
            r = -10000 * numerator / 9600;
        } else if (emitted < 576) {
            l = 12000 * (575-emitted) / 96;
            r = -10000 * (575-emitted) / 96;
        }
        CHECK(stereo[0] == l && stereo[1] == r);
        emitted++;
    }
    CHECK(!ferror(capture)); fclose(capture); CHECK(DeleteFileA(filename));
    CHECK(leading_silence >= 240 && emitted >= 576);
    puts("Measured SDL disk output: first 240 stereo frames muted, 2 ms gain ramps, exact 25%/100% plateaus, smooth return to silence.");
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
        for (unsigned version = 1; version <= 3; version++) verify_video(codec, version);
    }
    verify_audio();
    verify_first_audio_volume();
    verify_rest();
    verify_virtual_pad();
    puts("Verified video bitrate profiles, CDV1/CDV2/CDV3 framing and loss counters, actual decoded stereo Opus and bitrate, bounded dummy queue, virtual controller, and session-scoped explicit rest command exactly once.");
    return 0;
}
