#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <endpointvolume.h>
#include <wrl/client.h>
#include <string>
#include <iostream>
#include <iomanip>
#include <sstream>
#include <vector>
#include <stdexcept>
using Microsoft::WRL::ComPtr;
static void Check(HRESULT hr) {
    if(FAILED(hr)) {char error[96];sprintf_s(error,"WASAPI fixture failed (HRESULT 0x%08X)",unsigned(hr));throw std::runtime_error(error);}
}
static GUID sharedSession{};
struct Stream {
    ComPtr<IAudioClient> client;
    ComPtr<ISimpleAudioVolume> volume;
    void Open(IMMDevice* endpoint,float level,bool muted,bool shared=false) {
        GUID session{};Check(CoCreateGuid(&session));
        // Isolated from normal applications and shared only by the two fixture processes.
        if(shared) session=sharedSession;
        Check(endpoint->Activate(__uuidof(IAudioClient),CLSCTX_INPROC_SERVER,nullptr,reinterpret_cast<void**>(client.GetAddressOf())));
        WAVEFORMATEX* format=nullptr;
        Check(client->GetMixFormat(&format));
        auto hr=client->Initialize(AUDCLNT_SHAREMODE_SHARED,AUDCLNT_STREAMFLAGS_NOPERSIST | (shared?AUDCLNT_STREAMFLAGS_CROSSPROCESS:0),1000000,0,format,&session);
        CoTaskMemFree(format);Check(hr);
        Check(client->GetService(IID_PPV_ARGS(&volume)));
        Check(volume->SetMasterVolume(level,nullptr));Check(volume->SetMute(muted,nullptr));
        ComPtr<IAudioRenderClient> render; Check(client->GetService(IID_PPV_ARGS(&render)));
        UINT32 frames=0;Check(client->GetBufferSize(&frames));
        BYTE* data=nullptr;Check(render->GetBuffer(frames,&data));
        // This fixture only submits the explicit WASAPI silence flag. No samples or tones.
        Check(render->ReleaseBuffer(frames,AUDCLNT_BUFFERFLAGS_SILENT));Check(client->Start());
    }
};
int wmain(int argc,wchar_t** argv){
    try{
        if(argc!=2)return 2;
        Check(CLSIDFromString(argv[1],&sharedSession));
        Check(CoInitializeEx(nullptr,COINIT_MULTITHREADED));
        ComPtr<IMMDeviceEnumerator> endpoints;
        Check(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&endpoints)));
        ComPtr<IMMDevice> output;Check(endpoints->GetDefaultAudioEndpoint(eRender,eMultimedia,&output));
        ComPtr<IAudioEndpointVolume> master;
        Check(output->Activate(__uuidof(IAudioEndpointVolume),CLSCTX_INPROC_SERVER,nullptr,reinterpret_cast<void**>(master.GetAddressOf())));
        std::vector<Stream> streams;
        std::string line;
        while(std::getline(std::cin,line)){
            std::istringstream command(line);std::string action;command>>action;
            if(action=="exit")break;
            if(action=="open" || action=="shared"){
                float value;int muted;command>>value>>muted;streams.emplace_back();streams.back().Open(output.Get(),value,muted!=0,action=="shared");std::cout<<"ok\n";
            }else{
                unsigned i;command>>i;auto& stream=streams.at(i);
                if(action=="set") {float value;command>>value;Check(stream.volume->SetMasterVolume(value,nullptr));std::cout<<"ok\n";}
                else if(action=="get") {
                    float volume,device; BOOL muted,muteDevice;
                    Check(stream.volume->GetMasterVolume(&volume));Check(stream.volume->GetMute(&muted));
                    Check(master->GetMasterVolumeLevelScalar(&device));Check(master->GetMute(&muteDevice));
                    std::cout<<std::fixed<<std::setprecision(4)<<volume<<" "<<int(muted)<<" "<<device<<" "<<int(muteDevice)<<"\n";
                }else throw std::runtime_error("Unexpected fixture command");
            }
            std::cout.flush();
        }
    } catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}
    return 0;
}
