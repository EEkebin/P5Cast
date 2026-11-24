// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#define COBJMACROS
#include "Haptics.h"
#include <initguid.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <cfgmgr32.h>
#include <devpkey.h>
#include <stdlib.h>
#include <string.h>

enum { INPUT_CAPACITY = 180, UPSAMPLE = 16, MAX_PATH_CHARS = 1024 };
struct Haptics {
    HANDLE thread, wake;
    SRWLOCK lock;
    SDL_JoystickID pad;
    WCHAR path[MAX_PATH_CHARS];
    unsigned generation;
    bool stop, ready, continuous;
    float gain;
    ULONGLONG last_packet, last_waveform;
    size_t queued;
    int16_t samples[INPUT_CAPACITY * 2], previous[2];
};

typedef struct {
    IAudioClient *client;
    IAudioRenderClient *render;
    UINT32 capacity;
    bool floating, started;
} Output;

static bool controller_container(const WCHAR *path, GUID *id)
{
    WCHAR instance[MAX_DEVICE_ID_LEN];
    ULONG bytes = sizeof(instance);
    DEVPROPTYPE type;
    DEVINST dev;
    if (CM_Get_Device_Interface_PropertyW(path, &DEVPKEY_Device_InstanceId, &type,
            (PBYTE)instance, &bytes, 0) != CR_SUCCESS || type != DEVPROP_TYPE_STRING ||
        CM_Locate_DevNodeW(&dev, instance, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return false;
    bytes = sizeof(*id);
    return CM_Get_DevNode_PropertyW(dev, &DEVPKEY_Device_ContainerId, &type, (PBYTE)id, &bytes, 0) == CR_SUCCESS &&
        type == DEVPROP_TYPE_GUID && bytes == sizeof(*id) && !IsEqualGUID(id, &GUID_NULL);
}

static bool same_container(IMMDevice *device, const GUID *id)
{
    IPropertyStore *props = NULL;
    if (FAILED(IMMDevice_OpenPropertyStore(device, STGM_READ, &props))) return false;
    PROPVARIANT value;
    PropVariantInit(&value);
    const PROPERTYKEY key = {DEVPKEY_Device_ContainerId.fmtid, DEVPKEY_Device_ContainerId.pid};
    HRESULT hr = IPropertyStore_GetValue(props, &key, &value);
    GUID parsed;
    bool match = SUCCEEDED(hr) &&
        ((value.vt == VT_CLSID && value.puuid && IsEqualGUID(value.puuid, id)) ||
         (value.vt == VT_LPWSTR && value.pwszVal && SUCCEEDED(CLSIDFromString(value.pwszVal, &parsed)) && IsEqualGUID(&parsed, id)));
    PropVariantClear(&value);
    IPropertyStore_Release(props);
    return match;
}

static void close_output(Output *out)
{
    if (out->client && out->started) IAudioClient_Stop(out->client);
    if (out->render) IAudioRenderClient_Release(out->render);
    if (out->client) IAudioClient_Release(out->client);
    memset(out, 0, sizeof(*out));
}

static bool open_quad(IMMDevice *device, Output *out)
{
    if (FAILED(IMMDevice_Activate(device, &IID_IAudioClient, CLSCTX_ALL, NULL, (void **)&out->client))) return false;
    WAVEFORMATEX *format = NULL;
    bool ok = false;
    if (SUCCEEDED(IAudioClient_GetMixFormat(out->client, &format)) &&
        format->wFormatTag == WAVE_FORMAT_EXTENSIBLE && format->cbSize >= 22 &&
        format->nChannels == 4 && format->nSamplesPerSec == 48000) {
        const WAVEFORMATEXTENSIBLE *ext = (const WAVEFORMATEXTENSIBLE *)format;
        DWORD mask = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT | SPEAKER_BACK_LEFT | SPEAKER_BACK_RIGHT;
        out->floating = IsEqualGUID(&ext->SubFormat, &KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) &&
            format->wBitsPerSample == 32 && format->nBlockAlign == 16;
        bool pcm16 = IsEqualGUID(&ext->SubFormat, &KSDATAFORMAT_SUBTYPE_PCM) &&
            format->wBitsPerSample == 16 && format->nBlockAlign == 8;
        // Use the actual endpoint mix format. Windows must never downmix
        // actuator signals to the front speakers of a stereo endpoint.
        ok = ext->dwChannelMask == mask && (out->floating || pcm16) &&
            SUCCEEDED(IAudioClient_Initialize(out->client, AUDCLNT_SHAREMODE_SHARED, 0, 200000, 0, format, NULL)) &&
            SUCCEEDED(IAudioClient_GetBufferSize(out->client, &out->capacity)) && out->capacity >= UPSAMPLE &&
            SUCCEEDED(IAudioClient_GetService(out->client, &IID_IAudioRenderClient, (void **)&out->render));
    }
    CoTaskMemFree(format);
    if (!ok) close_output(out);
    return ok;
}

static void find_output(const WCHAR *path, Output *out)
{
    GUID container;
    if (!controller_container(path, &container)) return;
    IMMDeviceEnumerator *enumerator = NULL;
    IMMDeviceCollection *devices = NULL;
    if (FAILED(CoCreateInstance(&CLSID_MMDeviceEnumerator, NULL, CLSCTX_INPROC_SERVER,
            &IID_IMMDeviceEnumerator, (void **)&enumerator))) return;
    if (SUCCEEDED(IMMDeviceEnumerator_EnumAudioEndpoints(enumerator, eRender, DEVICE_STATE_ACTIVE, &devices))) {
        UINT count = 0;
        IMMDeviceCollection_GetCount(devices, &count);
        for (UINT i = 0; i < count && !out->client; ++i) {
            IMMDevice *device = NULL;
            if (SUCCEEDED(IMMDeviceCollection_Item(devices, i, &device))) {
                if (same_container(device, &container)) open_quad(device, out);
                IMMDevice_Release(device);
            }
        }
        IMMDeviceCollection_Release(devices);
    }
    IMMDeviceEnumerator_Release(enumerator);
}

void haptics_expand(float *quad, const int16_t *stereo, size_t frames,
                    int16_t previous[2], bool *continuous, float gain)
{
    if (frames == 0) return;
    if (!*continuous) memcpy(previous, stereo, 2 * sizeof(*stereo));
    *continuous = true;
    for (size_t i = 0; i < frames; ++i, stereo += 2) {
        for (int k = 1; k <= UPSAMPLE; ++k, quad += 4) {
            quad[0] = quad[1] = 0;
            for (int c = 0; c < 2; ++c)
                quad[2+c] = (previous[c] + (stereo[c] - previous[c]) * (k / (float)UPSAMPLE)) * (gain / 32768.0f);
        }
        memcpy(previous, stereo, 2 * sizeof(*stereo));
    }
}

static bool pump(Haptics *h, Output *out, unsigned generation)
{
    UINT32 padding;
    if (FAILED(IAudioClient_GetCurrentPadding(out->client, &padding)) || padding > out->capacity) return false;
    // Never put more than 20 ms in the actual endpoint, regardless of its buffer size.
    UINT32 capacity = out->capacity < 960 ? out->capacity : 960;
    if (padding + UPSAMPLE > capacity) return true;
    size_t frames = (capacity - padding) / UPSAMPLE;
    float quad[960 * 4];
    AcquireSRWLockExclusive(&h->lock);
    if (GetTickCount64() - h->last_packet > 60) {
        h->continuous = false;
        h->queued = 0;
    }
    if (frames > h->queued) frames = h->queued;
    if (generation != h->generation || h->stop) frames = 0;
    haptics_expand(quad, h->samples, frames, h->previous, &h->continuous, h->gain);
    h->queued -= frames;
    if (frames) memmove(h->samples, h->samples + 2*frames, h->queued * 2 * sizeof(int16_t));
    ReleaseSRWLockExclusive(&h->lock);
    if (!frames) return true;
    BYTE *data = NULL;
    UINT32 count = (UINT32)frames * UPSAMPLE;
    if (FAILED(IAudioRenderClient_GetBuffer(out->render, count, &data))) return false;
    if (out->floating) memcpy(data, quad, count * 4 * sizeof(float));
    else for (size_t i = 0; i < count * 4; ++i) ((int16_t *)data)[i] = (int16_t)(quad[i]*32768.0f);
    if (FAILED(IAudioRenderClient_ReleaseBuffer(out->render, count, 0))) return false;
    if (!out->started) out->started = SUCCEEDED(IAudioClient_Start(out->client));
    return out->started;
}

static DWORD WINAPI worker(void *arg)
{
    Haptics *h = arg;
    if (FAILED(CoInitializeEx(NULL, COINIT_MULTITHREADED))) return 0;
    Output out = {0};
    unsigned current = 0;
    WCHAR path[MAX_PATH_CHARS] = {0};
    ULONGLONG retry = 0;
    for (;;) {
        AcquireSRWLockShared(&h->lock);
        bool stop = h->stop, changed = current != h->generation;
        if (changed) {
            current = h->generation;
            memcpy(path, h->path, sizeof(path));
        }
        ReleaseSRWLockShared(&h->lock);
        if (stop) break;
        if (changed) { close_output(&out); retry = 0; }
        if (!out.client && path[0] && GetTickCount64() >= retry) {
            find_output(path, &out);
            retry = GetTickCount64() + 2000;
        }
        if (out.client && !pump(h, &out, current)) {
            close_output(&out);
            retry = GetTickCount64() + 2000;
        }
        AcquireSRWLockExclusive(&h->lock);
        h->ready = out.client && current == h->generation;
        bool drain = h->queued && h->ready;
        ReleaseSRWLockExclusive(&h->lock);
        WaitForSingleObject(h->wake, drain ? 4 : path[0] ? 2000 : INFINITE);
    }
    close_output(&out);
    CoUninitialize();
    return 0;
}

Haptics *haptics_create(void)
{
    Haptics *h = calloc(1, sizeof(*h));
    if (!h) return NULL;
    h->pad = -1;
    h->gain = 1;
    h->wake = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (h->wake) h->thread = CreateThread(NULL, 0, worker, h, 0, NULL);
    if (!h->thread) { if (h->wake) CloseHandle(h->wake); free(h); return NULL; }
    return h;
}

void haptics_controller(Haptics *h, SDL_GameController *pad)
{
    if (!h) return;
    SDL_Joystick *joy = pad ? SDL_GameControllerGetJoystick(pad) : NULL;
    SDL_JoystickID id = joy ? SDL_JoystickInstanceID(joy) : -1;
    if (id == h->pad) return;
    h->pad = id;
    WCHAR path[MAX_PATH_CHARS] = {0};
    if (pad && SDL_GameControllerGetVendor(pad) == 0x054c &&
        (SDL_GameControllerGetProduct(pad) == 0x0ce6 || SDL_GameControllerGetProduct(pad) == 0x0df2)) {
        const char *hid = SDL_JoystickPath(joy);
        if (hid && !MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, hid, -1, path, MAX_PATH_CHARS)) path[0] = 0;
    }
    AcquireSRWLockExclusive(&h->lock);
    memcpy(h->path, path, sizeof(path));
    h->generation++;
    h->ready = h->continuous = false;
    h->queued = 0;
    h->last_packet = h->last_waveform = 0;
    ReleaseSRWLockExclusive(&h->lock);
    SetEvent(h->wake);
}

void haptics_frame(uint8_t *pcm, size_t bytes, void *user)
{
    Haptics *h = user;
    if (!h || !pcm || !bytes || bytes % 4 || bytes > INPUT_CAPACITY * 4) return;
    AcquireSRWLockExclusive(&h->lock);
    if (h->ready && h->gain > 0) {
        ULONGLONG now = GetTickCount64();
        size_t frames = bytes / 4;
        if (now - h->last_packet > 60 || frames + h->queued > INPUT_CAPACITY) {
            h->queued = 0;
            h->continuous = false;
        }
        h->last_packet = now;
        // Some titles send silent PCM while using conventional rumble.
        for (size_t i = 0; i < bytes; ++i) {
            if (pcm[i]) { h->last_waveform = now; break; }
        }
        memcpy(h->samples + 2*h->queued, pcm, bytes);
        h->queued += frames;
        SetEvent(h->wake);
    }
    ReleaseSRWLockExclusive(&h->lock);
}

void haptics_intensity(Haptics *h, ChiakiDualSenseEffectIntensity intensity)
{
    if (!h) return;
    AcquireSRWLockExclusive(&h->lock);
    switch (intensity) {
        case Off: h->gain = 0; h->queued = 0; h->continuous = false; h->last_waveform = 0; break;
        case Weak: h->gain = 1.0f/3; break;
        case Medium: h->gain = .5f; break;
        case Strong: h->gain = 1; break;
    }
    ReleaseSRWLockExclusive(&h->lock);
}

bool haptics_playing(Haptics *h)
{
    if (!h) return false;
    AcquireSRWLockShared(&h->lock);
    bool playing = h->ready && h->gain > 0 && h->last_waveform && GetTickCount64()-h->last_waveform <= 60;
    ReleaseSRWLockShared(&h->lock);
    return playing;
}

void haptics_destroy(Haptics *h)
{
    if (!h) return;
    AcquireSRWLockExclusive(&h->lock);
    h->stop = true;
    ReleaseSRWLockExclusive(&h->lock);
    SetEvent(h->wake);
    WaitForSingleObject(h->thread, INFINITE);
    CloseHandle(h->thread);
    CloseHandle(h->wake);
    free(h);
}
