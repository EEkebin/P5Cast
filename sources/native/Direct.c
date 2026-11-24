// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#include "Direct.h"
#include "Haptics.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <io.h>
#include <fcntl.h>
#include <ws2tcpip.h>

typedef struct {
    ChiakiSession session;
    Video video;
    Audio audio;
    Gamepad gamepad;
    Haptics *haptics;
    ChiakiOpusDecoder opus;
    RestControl rest;
    uint8_t pin[4];
    bool has_pin, report_peer;
    volatile LONG status, rumble, rtt_us, connected;
} Direct;

static void finish(Direct *core, LONG status)
{
    if (status) InterlockedCompareExchange(&core->status, status, 0);
    SetEvent(core->video.stop);
}

static void session_event(ChiakiEvent *event, void *user)
{
    Direct *core = user;
    switch (event->type) {
        case CHIAKI_EVENT_CONNECTED: {
            InterlockedExchange(&core->connected, 1);
            // Only the owning dashboard's redirected stream receives this numeric endpoint.
            struct addrinfo *peer = core->session.connect_info.host_addrinfo_selected;
            if (core->report_peer && peer) {
                char address[NI_MAXHOST];
                if (!getnameinfo(peer->ai_addr, (socklen_t)peer->ai_addrlen, address, sizeof(address), NULL, 0, NI_NUMERICHOST)) {
                    fprintf(stdout, "PEER %s\n", address);
                    fflush(stdout);
                }
            }
            uint64_t rtt = core->session.rtt_us;
            if (rtt && rtt != 1000 && rtt < UINT32_MAX) InterlockedExchange(&core->rtt_us, (LONG)rtt);
            break;
        }
        case CHIAKI_EVENT_LOGIN_PIN_REQUEST:
            if (event->login_pin_request.pin_incorrect) finish(core, 25);
            else if (!core->has_pin) finish(core, 24);
            else chiaki_session_set_login_pin(&core->session, core->pin, sizeof(core->pin));
            break;
        case CHIAKI_EVENT_RUMBLE:
            InterlockedExchange(&core->rumble, 0x10000 | (event->rumble.left << 8) | event->rumble.right);
            break;
        case CHIAKI_EVENT_TRIGGER_EFFECTS:
        case CHIAKI_EVENT_TRIGGER_INTENSITY:
            gamepad_feedback_event(&core->gamepad, event);
            break;
        case CHIAKI_EVENT_HAPTIC_INTENSITY:
            haptics_intensity(core->haptics, event->intensity);
            break;
        case CHIAKI_EVENT_QUIT:
            InterlockedExchange(&core->connected, 0);
            finish(core, chiaki_quit_reason_is_error(event->quit.reason) ? 23 : 0);
            break;
        default: break;
    }
}

static void display_state(void *user, bool restricted)
{
    // Respect a console-side restricted screen; libchiaki stops supplying game pictures.
    (void)user; (void)restricted;
}

static HANDLE connect_player(const char *name, HANDLE stop)
{
    size_t length = strlen(name);
    if (length == 0 || length > 120) return INVALID_HANDLE_VALUE;
    for (size_t i = 0; i < length; i++) {
        if (!isalnum((unsigned char)name[i]) && name[i] != '-' && name[i] != '_') return INVALID_HANDLE_VALUE;
    }
    char path[160];
    snprintf(path, sizeof(path), "\\\\.\\pipe\\%s", name);
    uint64_t deadline = GetTickCount64() + 30000;
    do {
        HANDLE pipe = CreateFileA(path, GENERIC_WRITE, 0, NULL, OPEN_EXISTING,
                                  FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, NULL);
        if (pipe != INVALID_HANDLE_VALUE) return pipe;
        DWORD error = GetLastError();
        if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND) break;
        if (error == ERROR_PIPE_BUSY) WaitNamedPipeA(path, 100);
        else Sleep(20);
    } while (GetTickCount64() < deadline && WaitForSingleObject(stop, 0) == WAIT_TIMEOUT);
    return INVALID_HANDLE_VALUE;
}

static bool read_auth(ChiakiConnectInfo *info, Direct *core)
{
    _setmode(_fileno(stdin), _O_BINARY);
    if (fread(info->regist_key, 1, sizeof(info->regist_key), stdin) != sizeof(info->regist_key) ||
        fread(info->morning, 1, sizeof(info->morning), stdin) != sizeof(info->morning)) return false;
    size_t n = fread(core->pin, 1, sizeof(core->pin), stdin);
    if (n == 0) return feof(stdin) != 0;
    if (n != sizeof(core->pin) || fgetc(stdin) != EOF) return false;
    for (size_t i = 0; i < n; i++) if (core->pin[i] < '0' || core->pin[i] > '9') return false;
    core->has_pin = true;
    return true;
}

static int run(Direct *core, const char *pipe_name, ChiakiConnectInfo *info)
{
    core->video.pipe = connect_player(pipe_name, core->video.stop);
    if (core->video.pipe == INVALID_HANDLE_VALUE) return 21;
    if (!video_header(&core->video)) return 21;
    SDL_SetMainReady();
    SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
    SDL_SetHint(SDL_HINT_GAMECONTROLLER_USE_BUTTON_LABELS, "0");
    const char *option = getenv("P5CAST_DUALSENSE_FEEDBACK");
    bool feedback = !option || strcmp(option, "0") != 0;
    if (feedback) SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_PS5_RUMBLE, "1");
    if (SDL_Init(SDL_INIT_AUDIO | SDL_INIT_GAMECONTROLLER) < 0) return 22;
    if (chiaki_lib_init() != CHIAKI_ERR_SUCCESS) return 22;
    gamepad_init(&core->gamepad);
    const char *gestures = getenv("P5CAST_TOUCHPAD_GESTURES");
    core->gamepad.forward_contacts = gestures && !strcmp(gestures, "1");
    gamepad_tick(&core->gamepad);
    info->enable_dualsense = feedback && (!core->gamepad.pad || gamepad_is_dualsense(core->gamepad.pad));
    ChiakiLog log;
    chiaki_log_init(&log, 0, NULL, NULL); // Never persist network credentials or protocol traces.
    if (chiaki_session_init(&core->session, info, &log) != CHIAKI_ERR_SUCCESS) return 23;
    SecureZeroMemory(info->regist_key, sizeof(info->regist_key));
    SecureZeroMemory(info->morning, sizeof(info->morning));
    chiaki_opus_decoder_init(&core->opus, &log);
    ChiakiAudioSink sink;
    audio_decoder_sink(&core->audio, &core->opus, &sink);
    chiaki_session_set_audio_sink(&core->session, &sink);
    if (info->enable_dualsense) {
        core->haptics = haptics_create();
        if (core->haptics) {
            ChiakiAudioSink motors = {0};
            motors.user = core->haptics;
            motors.frame_cb = haptics_frame;
            chiaki_session_set_haptics_sink(&core->session, &motors);
        }
    }
    ChiakiCtrlDisplaySink display = {0};
    display.user = core;
    display.cantdisplay_cb = display_state;
    chiaki_session_ctrl_set_display_sink(&core->session, &display);
    chiaki_session_set_event_cb(&core->session, session_event, core);
    chiaki_session_set_video_sample_cb(&core->session, video_sample, &core->video);
    int status = 23;
    if (chiaki_session_start(&core->session) == CHIAKI_ERR_SUCCESS) {
        chiaki_session_set_controller_state(&core->session, &core->gamepad.current);
        uint64_t next_stats = GetTickCount64() + 1000;
        bool rumbling = false;
        while (WaitForSingleObject(core->video.stop, 4) == WAIT_TIMEOUT) {
            rest_tick(&core->rest, InterlockedCompareExchange(&core->connected, 0, 0) != 0,
                      &core->session, chiaki_session_goto_bed);
            if (gamepad_tick(&core->gamepad)) chiaki_session_set_controller_state(&core->session, &core->gamepad.current);
            if (info->enable_dualsense) {
                gamepad_apply_feedback(&core->gamepad);
                haptics_controller(core->haptics, core->gamepad.pad);
            }
            LONG rumble = InterlockedExchange(&core->rumble, 0);
            if (!core->gamepad.pad) rumbling = false;
            else if ((rumble || rumbling) && haptics_playing(core->haptics)) {
                // Return the motors to waveform playback after legacy rumble.
                if (rumbling) SDL_GameControllerRumble(core->gamepad.pad, 0, 0, 0);
                rumbling = false;
            } else if (rumble) {
                int sent = SDL_GameControllerRumble(core->gamepad.pad, ((rumble >> 8) & 255) * 257, (rumble & 255) * 257, 500);
                rumbling = sent == 0 && (rumble & 0xffff);
            }
            LONG audio_error = InterlockedCompareExchange(&core->audio.failed, 0, 0);
            if (audio_error) { finish(core, audio_error); break; }
            if (GetTickCount64() >= next_stats) {
                uint32_t rtt_us = (uint32_t)InterlockedCompareExchange(&core->rtt_us, 0, 0);
                video_stats(&core->video, &core->audio, GetTickCount64(), rtt_us);
                next_stats = GetTickCount64() + 1000;
            }
        }
        gamepad_release_feedback(&core->gamepad);
        if (rumbling && core->gamepad.pad) SDL_GameControllerRumble(core->gamepad.pad, 0, 0, 0);
        chiaki_session_stop(&core->session);
        chiaki_session_join(&core->session);
        status = core->status;
    }
    chiaki_opus_decoder_fini(&core->opus);
    chiaki_session_fini(&core->session);
    haptics_destroy(core->haptics);
    return status;
}

#undef main
int main(int argc, char **argv)
{
    if (argc == 2 && !strcmp(argv[1], "--version")) { puts("CadrivectDirect / libchiaki 1.10.1 / CDV1 + CDV2 + CDV3"); return 0; }
    if (argc == 3 && !strcmp(argv[1], "--register")) return register_console(argv[2]);
    bool volume_control = argc >= 7 && !strcmp(argv[argc - 1], "--session-control");
    bool rest_control = volume_control || (argc >= 7 && !strcmp(argv[argc - 1], "--rest-control"));
    if (rest_control) argc--;
    if (argc < 4 || argc > 7 || strlen(argv[3]) != 1) return 20;
    ChiakiConnectInfo info = {0};
    if (!video_profile(&info.video_profile, argv[3][0] - '0', argc >= 5 ? argv[4] : NULL,
                       argc >= 6 ? argv[5] : NULL, argc >= 7 ? argv[6] : NULL)) return 20;
    Direct core = {0};
    core.rtt_us = -1;
    core.video.pipe = INVALID_HANDLE_VALUE;
    core.video.codec = argv[3][0] - '0';
    const char *version = getenv("P5CAST_DIRECT_STATS_VERSION");
    core.video.stats_version = version && !strcmp(version, "3") ? 3 : (argc >= 6 ? 2 : 1);
    core.video.stop = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!core.video.stop) return 22;
    info.host = argv[2];
    info.ps5 = true;
    info.packet_loss_max = 0.05;
    info.enable_idr_on_fec_failure = true;
    int status = 20;
    if (read_auth(&info, &core)) {
        if (rest_control) {
            rest_open(&core.rest, argv[1]);
            HANDLE stop = session_stop_event(argv[1]);
            if (stop) { CloseHandle(core.video.stop); core.video.stop = stop; }
            core.report_peer = GetFileType(GetStdHandle(STD_OUTPUT_HANDLE)) == FILE_TYPE_PIPE;
        }
        // The dashboard initializes this scalar before releasing credentials on stdin.
        // Fail before opening SDL if the required mapping is unavailable.
        if (volume_control && !audio_open_volume(&core.audio, argv[1])) status = 28;
        else status = run(&core, argv[1], &info);
    }
    rest_close(&core.rest);
    if (core.audio.device) SDL_CloseAudioDevice(core.audio.device);
    audio_close_volume(&core.audio);
    if (core.gamepad.pad) SDL_GameControllerClose(core.gamepad.pad);
    SDL_Quit();
    if (core.video.pipe != INVALID_HANDLE_VALUE) CloseHandle(core.video.pipe);
    CloseHandle(core.video.stop);
    free(core.video.config);
    SecureZeroMemory(&info, sizeof(info));
    SecureZeroMemory(&core, sizeof(core));
    return status;
}
