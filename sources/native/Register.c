// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#include "Direct.h"
#include <string.h>
#include <stdio.h>
#include <io.h>
#include <fcntl.h>

typedef struct {
    HANDLE done;
    bool success;
    uint8_t reply[74];
} Pairing;

static void registered(ChiakiRegistEvent *event, void *user)
{
    Pairing *pair = user;
    if (event->type == CHIAKI_REGIST_EVENT_TYPE_FINISHED_SUCCESS && event->registered_host) {
        ChiakiRegisteredHost *host = event->registered_host;
        memcpy(pair->reply, "CDR1", 4);
        memcpy(pair->reply + 4, host->server_mac, 6);
        memcpy(pair->reply + 10, host->server_nickname, strnlen(host->server_nickname, 32));
        memcpy(pair->reply + 42, host->rp_regist_key, 16);
        memcpy(pair->reply + 58, host->rp_key, 16);
        pair->success = true;
    }
    SetEvent(pair->done);
}

int register_console(const char *host)
{
    // Returning pairing keys to a terminal or file is never necessary. Our caller
    // starts this helper with anonymous, inherited input and output pipe handles.
    if (GetFileType(GetStdHandle(STD_INPUT_HANDLE)) != FILE_TYPE_PIPE ||
        GetFileType(GetStdHandle(STD_OUTPUT_HANDLE)) != FILE_TYPE_PIPE) return 20;
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
    ChiakiRegistInfo info = {0};
    info.target = CHIAKI_TARGET_PS5_1;
    info.host = host;
    uint8_t digits[8] = {0};
    int result = 20;
    if (fread(info.psn_account_id, 1, 8, stdin) != 8 || fread(digits, 1, 8, stdin) != 8 || fgetc(stdin) != EOF) goto cleanup;
    for (size_t i = 0; i < sizeof(digits); i++) {
        if (digits[i] < '0' || digits[i] > '9') goto cleanup;
        info.pin = info.pin * 10 + digits[i] - '0';
    }
    result = 27;
    if (chiaki_lib_init() != CHIAKI_ERR_SUCCESS) goto cleanup;
    Pairing pair = {0};
    pair.done = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!pair.done) goto cleanup;
    ChiakiLog log;
    chiaki_log_init(&log, 0, NULL, NULL);
    ChiakiRegist registration;
    if (chiaki_regist_start(&registration, &log, &info, registered, &pair) == CHIAKI_ERR_SUCCESS) {
        if (WaitForSingleObject(pair.done, 35000) != WAIT_OBJECT_0) chiaki_regist_stop(&registration);
        chiaki_regist_fini(&registration);
        if (pair.success && fwrite(pair.reply, 1, sizeof(pair.reply), stdout) == sizeof(pair.reply) && fflush(stdout) == 0) result = 0;
        SecureZeroMemory(&registration, sizeof(registration));
    }
    CloseHandle(pair.done);
    SecureZeroMemory(&pair, sizeof(pair));
cleanup:
    SecureZeroMemory(digits, sizeof(digits));
    SecureZeroMemory(&info, sizeof(info));
    return result;
}
