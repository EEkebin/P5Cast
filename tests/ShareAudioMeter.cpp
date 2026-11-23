// Test instrumentation only. Scope is fixed to this fixture's own process tree.
// Summarize frequency amplitudes in memory; never open an endpoint or save audio.
#include <windows.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <wrl.h>
#include <cmath>
#include <algorithm>

using namespace Microsoft::WRL;

struct Event {
    HANDLE value = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    ~Event() { if (value) CloseHandle(value); }
};

class Activated final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, FtmBase,
                                             IActivateAudioInterfaceCompletionHandler> {
public:
    Event done;
    HRESULT result = E_PENDING;
    ComPtr<IAudioClient> client;
    STDMETHODIMP ActivateCompleted(IActivateAudioInterfaceAsyncOperation* op) override {
        HRESULT activation = E_FAIL;
        ComPtr<IUnknown> object;
        result = op->GetActivateResult(&activation, &object);
        if (SUCCEEDED(result)) result = activation;
        if (SUCCEEDED(result)) result = object.As(&client);
        SetEvent(done.value);
        return S_OK;
    }
};

struct Reading { double left700, right1100, offTone; UINT32 detectedWindows; };
struct Spectrum {
    // A 100 ms block contains an exact number of cycles at each fixture frequency.
    static constexpr int block = 4800;
    double cosine[3][480]{}, sine[3][480]{}, re[3]{}, im[3]{};
    int cursor = 0;
    Reading reading{};
    Spectrum() {
        const int frequencies[] = {700, 1100, 1700};
        for (int k = 0; k < 3; ++k)
            for (int i = 0; i < 480; ++i) {
                double angle = 6.283185307179586 * frequencies[k] * i / 48000;
                cosine[k][i] = std::cos(angle);
                sine[k][i] = std::sin(angle);
            }
    }
    void sample(short left, short right) {
        const int levels[] = {left, right, left};
        for (int k = 0; k < 3; ++k) {
            re[k] += levels[k] * cosine[k][cursor % 480];
            im[k] += levels[k] * sine[k][cursor % 480];
        }
        if (++cursor != block) return;
        double amplitude[3];
        for (int k = 0; k < 3; ++k) {
            amplitude[k] = 2 * std::hypot(re[k], im[k]) / block;
            re[k] = im[k] = 0;
        }
        reading.left700 = (std::max)(reading.left700, amplitude[0]);
        reading.right1100 = (std::max)(reading.right1100, amplitude[1]);
        reading.offTone = (std::max)(reading.offTone, amplitude[2]);
        if (amplitude[0] > 1 && amplitude[1] > 1 &&
            (std::min)(amplitude[0], amplitude[1]) > 8 * amplitude[2])
            ++reading.detectedWindows;
        cursor = 0;
    }
};

static HRESULT meter(void (__stdcall *launch)(), Reading* output) {
    auto activated = Make<Activated>();
    if (!activated || !activated->done.value) return E_OUTOFMEMORY;
    AUDIOCLIENT_ACTIVATION_PARAMS params{};
    params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
    params.ProcessLoopbackParams.TargetProcessId = GetCurrentProcessId();
    params.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
    PROPVARIANT variant{};
    variant.vt = VT_BLOB;
    variant.blob = { sizeof(params), reinterpret_cast<BYTE*>(&params) };
    ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
    HRESULT hr = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
        __uuidof(IAudioClient), &variant, activated.Get(), &operation);
    if (FAILED(hr)) return hr;
    if (WaitForSingleObject(activated->done.value, 4000) != WAIT_OBJECT_0)
        return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
    if (FAILED(activated->result)) return activated->result;
    Event packet;
    if (!packet.value) return E_OUTOFMEMORY;
    auto client = activated->client;
    WAVEFORMATEX format{WAVE_FORMAT_PCM, 2, 48000, 192000, 4, 16, 0};
    hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK |
        AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM, 0, 0, &format, nullptr);
    if (FAILED(hr)) return hr;
    ComPtr<IAudioCaptureClient> capture;
    hr = client->GetService(IID_PPV_ARGS(&capture));
    if (FAILED(hr)) return hr;
    if (FAILED(hr = client->SetEventHandle(packet.value))) return hr;
    if (FAILED(hr = client->Start())) return hr;
    launch();
    Spectrum spectrum;
    ULONGLONG deadline = GetTickCount64() + 5000;
    while (GetTickCount64() < deadline && spectrum.reading.detectedWindows < 4) {
        WaitForSingleObject(packet.value, 50);
        UINT32 available = 0;
        while (SUCCEEDED(hr = capture->GetNextPacketSize(&available)) && available) {
            BYTE* bytes = nullptr;
            UINT32 frames = 0;
            DWORD flags = 0;
            hr = capture->GetBuffer(&bytes, &frames, &flags, nullptr, nullptr);
            if (FAILED(hr)) break;
            const short* samples = (flags & AUDCLNT_BUFFERFLAGS_SILENT) ? nullptr : reinterpret_cast<short*>(bytes);
            for (UINT32 i = 0; i < frames; ++i)
                spectrum.sample(samples ? samples[2*i] : 0, samples ? samples[2*i+1] : 0);
            hr = capture->ReleaseBuffer(frames);
            if (FAILED(hr)) break;
        }
        if (FAILED(hr)) break;
    }
    client->Stop();
    *output = spectrum.reading;
    return hr;
}

extern "C" __declspec(dllexport) HRESULT __stdcall VerifyOwnChildAudio(
    void (__stdcall *launch)(), Reading* output) {
    if (!launch || !output) return E_POINTER;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return hr;
    hr = meter(launch, output);
    CoUninitialize();
    return hr;
}
