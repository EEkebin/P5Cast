// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#include "Direct.h"
#include <stdlib.h>
#include <string.h>

// Each write can be canceled when the player exits or stops consuming frames.
static bool write_all(Video *video, const void *data, DWORD size)
{
    OVERLAPPED operation = {0};
    operation.hEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!operation.hEvent) { SetEvent(video->stop); return false; }
    DWORD count = 0;
    BOOL ok = WriteFile(video->pipe, data, size, &count, &operation);
    if (!ok && GetLastError() == ERROR_IO_PENDING) {
        HANDLE waits[] = {operation.hEvent, video->stop};
        if (WaitForMultipleObjects(2, waits, FALSE, 2000) != WAIT_OBJECT_0) {
            CancelIoEx(video->pipe, &operation);
        }
        ok = GetOverlappedResult(video->pipe, &operation, &count, TRUE);
    }
    CloseHandle(operation.hEvent);
    if (!ok || count != size) { SetEvent(video->stop); return false; }
    return true;
}

bool video_header(Video *video)
{
    const uint32_t header[] = {0x31564443u, (uint32_t)video->codec}; // CDV1 on Windows x64.
    video->waiting_for_key = true;
    return write_all(video, header, sizeof(header));
}

void video_stats(Video *video, const uint32_t stats[4])
{
    if (!TryAcquireSRWLockExclusive(&video->writer)) return;
    const uint32_t packet[] = {0, stats[0], stats[1], stats[2], stats[3]};
    write_all(video, packet, sizeof(packet));
    ReleaseSRWLockExclusive(&video->writer);
}

typedef struct { unsigned config; bool picture, key; } NalInfo;
static NalInfo inspect(int codec, const uint8_t *data, size_t size)
{
    NalInfo result = {0};
    for (size_t i = 2; i + 1 < size; i++) {
        if (data[i] != 1 || data[i-1] || data[i-2]) continue;
        unsigned type = data[i+1];
        if (codec == 1) {
            type &= 31;
            if (type == 7 || type == 8) result.config |= 1u << (type-7);
            if (type >= 1 && type <= 5) result.picture = true;
            if (type == 5) result.key = true;
        } else {
            type = (type >> 1) & 63;
            if (type >= 32 && type <= 34) result.config |= 1u << (type-32);
            if (type <= 31) result.picture = true;
            if (type >= 16 && type <= 23) result.key = true;
        }
        i++;
    }
    return result;
}

bool video_sample(uint8_t *data, size_t size, int32_t lost, bool recovered, void *user)
{
    (void)lost; (void)recovered;
    Video *video = user;
    if (!size || size > DIRECT_MAX_AU) return false;
    // libchiaki provides the negotiated profile header before pictures, including
    // each adaptive profile change. Join it to the first independently decodable AU.
    const unsigned required = video->codec == 1 ? 3u : 7u;
    NalInfo nal = inspect(video->codec, data, size);
    if (nal.config && !nal.picture) {
        if (size > 65536) return false;
        size_t prior = video->waiting_for_key && !(video->config_mask & nal.config) ? video->config_size : 0;
        if (prior + size > 65536) return false;
        uint8_t *next = realloc(video->config, prior + size);
        if (!next) return false;
        memcpy(next + prior, data, size);
        video->config = next;
        video->config_size = prior + size;
        video->config_mask = (prior ? video->config_mask : 0u) | nal.config;
        video->waiting_for_key = true;
        return true;
    }
    if (!nal.picture) return true;
    size_t prefix = 0;
    if (video->waiting_for_key) {
        if (!nal.key || ((nal.config | video->config_mask) & required) != required) return false;
        if (nal.config != required) prefix = video->config_size;
    }
    if (prefix + size > DIRECT_MAX_AU) return false;
    uint32_t length = (uint32_t)(prefix + size);
    AcquireSRWLockExclusive(&video->writer);
    bool ok = write_all(video, &length, sizeof(length))
           && (!prefix || write_all(video, video->config, (DWORD)prefix))
           && write_all(video, data, (DWORD)size);
    ReleaseSRWLockExclusive(&video->writer);
    if (ok) video->waiting_for_key = false;
    return ok;
}
