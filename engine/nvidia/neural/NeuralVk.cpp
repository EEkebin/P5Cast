#include "NrTransport.h"
#include <direct.h>
#include <memory>
#include "nvsdk_ngx_helpers_dlssnr_vk.h"
#include <bcrypt.h>
#include <array>

static const char* projectId="15b28241-0e5d-45d2-8a98-f9676d9723ea";
static std::vector<const char*> ExtensionNames(uint32_t count,VkExtensionProperties* properties) {
    std::vector<const char*> names;
    for(uint32_t i=0;i<count;++i) names.push_back(properties[i].extensionName);
    return names;
}
static void NgxCheck(NVSDK_NGX_Result r,const char* op) {
    if(NVSDK_NGX_FAILED(r)) {
        char message[240];snprintf(message,sizeof(message),"%s (NGX 0x%08X)",op,static_cast<unsigned>(r));
        throw std::runtime_error(message);
    }
}
static void Explain(wchar_t* buffer,int count,const char* text) {
    if(!buffer || count<=0)return;
    _snwprintf_s(buffer,count,_TRUNCATE,L"%hs",text);
}
struct MissingProvider : std::runtime_error { using std::runtime_error::runtime_error; };
struct RuntimeFile {
    HANDLE handle=INVALID_HANDLE_VALUE;
    ~RuntimeFile() { if(handle!=INVALID_HANDLE_VALUE) CloseHandle(handle); }
    void Open(const std::wstring& dir) {
        auto path=dir+L"\\nvngx_dlssnr.dll";
        handle=CreateFileW(path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(handle==INVALID_HANDLE_VALUE){
            auto err=GetLastError();
            if(err==ERROR_FILE_NOT_FOUND || err==ERROR_PATH_NOT_FOUND) throw MissingProvider("Install the optional NVIDIA Neural Rendering runtime");
            throw std::runtime_error("Could not open the optional NVIDIA runtime for verification");
        }
        // Match the runtime accepted by the installer even if the local cache changes.
        BCRYPT_ALG_HANDLE alg{}; BCRYPT_HASH_HANDLE hash{};
        NTSTATUS status=BCryptOpenAlgorithmProvider(&alg,BCRYPT_SHA256_ALGORITHM,nullptr,0);
        if(status>=0) status=BCryptCreateHash(alg,&hash,nullptr,0,nullptr,0,0);
        std::array<unsigned char,32> digest{},expected={0x2b,0xc1,0x9b,0xc0,0x9a,0x7b,0x89,0xdd,0x44,0xf7,0x3e,0x2a,0xc0,0x10,0x8f,0xcc,
            0x58,0x6e,0x21,0x54,0xbd,0xd0,0x83,0x0f,0x24,0x2f,0x4f,0x04,0x0e,0x28,0x78,0x9f};
        unsigned char chunk[64*1024];DWORD read=0;
        while(status>=0) {
            if(!ReadFile(handle,chunk,sizeof(chunk),&read,nullptr)){status=-1;break;}
            if(!read)break;status=BCryptHashData(hash,chunk,read,0);
        }
        if(status>=0)status=BCryptFinishHash(hash,digest.data(),static_cast<ULONG>(digest.size()),0);
        if(hash)BCryptDestroyHash(hash);if(alg)BCryptCloseAlgorithmProvider(alg,0);
        if(status<0 || digest!=expected) throw std::runtime_error("The optional NVIDIA runtime failed SHA-256 verification");
    }
};
struct NrSession {
    NrTransport gpu;
    RuntimeFile runtime;
    bool initialized{};
    NVSDK_NGX_Handle* feature{};
    NVSDK_NGX_Parameter* params{};
    NVSDK_NGX_Resource_VK resources[5]{};
    ~NrSession() {
        if(gpu.device)vkDeviceWaitIdle(gpu.device);
        if(feature)NVSDK_NGX_VULKAN_ReleaseFeature(feature);
        if(params)NVSDK_NGX_VULKAN_DestroyParameters(params);
        if(initialized)NVSDK_NGX_VULKAN_Shutdown1(gpu.device);
    }
    void Open(ID3D11Device* source,const wchar_t* dir,const wchar_t* data,UINT w,UINT h) {
        VkPhysicalDeviceProperties props{};
        vkGetPhysicalDeviceProperties(NrTransport::Selected(source),&props);
        if(props.vendorID!=0x10de || !strstr(props.deviceName,"GeForce RTX 50"))
            throw std::runtime_error("DLSS 5 Neural Rendering requires a GeForce RTX 50 Series GPU");
        if(props.apiVersion<VK_API_VERSION_1_2) throw std::runtime_error("DLSS 5 Neural Rendering requires Vulkan 1.2");
        // September 2026 GeForce release enabling NR. The provider can require a newer driver.
        if(props.driverVersion<((616u<<22)|(64u<<14))) throw std::runtime_error("Update the NVIDIA driver for DLSS 5 Neural Rendering (616.64 or newer)");
        runtime.Open(dir);
        if(_wmkdir(data)!=0 && GetFileAttributesW(data)==INVALID_FILE_ATTRIBUTES) throw std::runtime_error("Could not create the P5Cast NGX data directory");
        NVSDK_NGX_FeatureCommonInfo common{};
        const wchar_t* paths[]={dir};
        common.PathListInfo.Path=paths;common.PathListInfo.Length=1;
        NVSDK_NGX_FeatureDiscoveryInfo disc{};
        disc.SDKVersion=NVSDK_NGX_Version_API;disc.FeatureID=NVSDK_NGX_Feature_DLSSNR;
        disc.Identifier.IdentifierType=NVSDK_NGX_Application_Identifier_Type_Project_Id;
        disc.Identifier.v.ProjectDesc={projectId,NVSDK_NGX_ENGINE_TYPE_CUSTOM,"P5Cast 1"};
        disc.ApplicationDataPath=data; disc.FeatureInfo=&common;
        uint32_t extensionCount=0;VkExtensionProperties* extensions=nullptr;
        NgxCheck(NVSDK_NGX_VULKAN_GetFeatureInstanceExtensionRequirements(&disc,&extensionCount,&extensions),"Find DLSS 5 Vulkan instance requirements");
        gpu.OpenInstance(source,ExtensionNames(extensionCount,extensions));
        NVSDK_NGX_FeatureRequirement supported{};
        NgxCheck(NVSDK_NGX_VULKAN_GetFeatureRequirements(gpu.instance,gpu.physical,&disc,&supported),"Check DLSS 5 hardware and OS requirements");
        if(supported.FeatureSupported) throw std::runtime_error("NVIDIA reports that the OS, GPU or driver does not support DLSS 5 Neural Rendering");
        NgxCheck(NVSDK_NGX_VULKAN_GetFeatureDeviceExtensionRequirements(gpu.instance,gpu.physical,&disc,&extensionCount,&extensions),"Find DLSS 5 Vulkan device requirements");
        gpu.OpenDevice(ExtensionNames(extensionCount,extensions));
        NgxCheck(NVSDK_NGX_VULKAN_Init_with_ProjectID(projectId,NVSDK_NGX_ENGINE_TYPE_CUSTOM,"P5Cast 1",data,
            gpu.instance,gpu.physical,gpu.device,vkGetInstanceProcAddr,vkGetDeviceProcAddr,&common,NVSDK_NGX_Version_API),"Initialize NVIDIA NGX for neural rendering");
        initialized=true;
        NgxCheck(NVSDK_NGX_VULKAN_GetCapabilityParameters(&params),"Read NVIDIA neural rendering capabilities");
        int needsDriver=0,available=0;
        params->Get(NVSDK_NGX_Parameter_DLSSNR_NeedsUpdatedDriver,&needsDriver);
        if(needsDriver) throw std::runtime_error("The neural rendering provider requires a newer NVIDIA driver");
        NgxCheck(params->Get(NVSDK_NGX_Parameter_DLSSNR_Available,&available),"Query NVIDIA neural rendering availability");
        if(!available) throw std::runtime_error("NVIDIA NGX reports neural rendering unavailable on this system");
        gpu.AllocateFrame(w,h);
        for(unsigned i=0;i<5;++i) {auto& p=gpu.planes[i];
            resources[i]=NVSDK_NGX_Create_ImageView_Resource_VK(p.view,p.image,NrTransport::Range(),p.vkFormat,w,h,i==4);}
        NVSDK_NGX_DLSSNR_Create_Params create{};create.Width=w;create.Height=h;
        params->Set(NVSDK_NGX_Parameter_DLSSNR_Hint_Render_Preset,static_cast<unsigned>(NVSDK_NGX_DLSSNR_Hint_Render_Preset_B));
        gpu.Begin();
        NgxCheck(NGX_VULKAN_CREATE_DLSSNR_EXT1(gpu.device,gpu.cmd,1,1,&feature,params,&create),"Create DLSS 5 Neural Rendering");
        gpu.Finish();
    }
    void Evaluate(ID3D11Texture2D* (&textures)[5],bool reset) {
        gpu.Frame(textures,[&]{
            NVSDK_NGX_VK_DLSSNR_Eval_Params e{};
            e.pInColor=&resources[0];e.pInMVec=&resources[1];e.pInDepth=&resources[2];e.pInControlMask=&resources[3];e.pInOutput=&resources[4];
            e.InColorSubrectSize=e.InMVecSubrectSize=e.InDepthSubrectSize=e.InControlMaskSubrectSize=e.InOutputSubrectSize={gpu.width,gpu.height};
            e.InEnabled=1;e.InReset=reset;e.InIntensity=1.f;e.InLocalStructureStrength=.7f;e.InLocalToneStrength=.3f;
            e.InStyle=0;e.InUseAutoMask=1;e.InMaskBlendMode=NVSDK_NGX_DLSSNR_Mask_Blend_Mode_Min;e.InSkinStructureStrength=.5f;
            e.InMVecScaleX=e.InMVecScaleY=1.f;
            params->Set(NVSDK_NGX_Parameter_Jitter_Offset_X,0.f);params->Set(NVSDK_NGX_Parameter_Jitter_Offset_Y,0.f);
            NgxCheck(NGX_VULKAN_EVALUATE_DLSSNR_EXT(gpu.cmd,feature,params,&e),"Evaluate DLSS 5 Neural Rendering");
        });
    }
};
#define P5_EXPORT extern "C" __declspec(dllexport)
P5_EXPORT int P5NrOpen(ID3D11Device* device,const wchar_t* runtime,const wchar_t* data,int width,int height,NrSession** out,wchar_t* reason,int chars) {
    if(out)*out=nullptr;
    try{
        if(!device || !runtime || !*runtime || !data || !*data || !out || width<1 || height<1 || width>16384 || height>16384) throw std::runtime_error("Invalid neural rendering configuration");
        std::unique_ptr<NrSession> session(new NrSession);
        session->Open(device,runtime,data,width,height);
        *out=session.release();Explain(reason,chars,"");return 0;
    } catch(const MissingProvider& e){Explain(reason,chars,e.what());return 2;}
      catch(const std::exception& e){Explain(reason,chars,e.what());return 1;}
}
P5_EXPORT int P5NrEvaluate(NrSession* session,ID3D11Texture2D* color,ID3D11Texture2D* motion,ID3D11Texture2D* depth,
                           ID3D11Texture2D* mask,ID3D11Texture2D* output,int reset,wchar_t* reason,int chars) {
    try{
        if(!session)throw std::runtime_error("Missing neural rendering session");
        ID3D11Texture2D* planes[]={color,motion,depth,mask,output};session->Evaluate(planes,reset!=0);Explain(reason,chars,"");return 0;
    } catch(const std::exception& e){Explain(reason,chars,e.what());return 1;}
}
P5_EXPORT void P5NrClose(NrSession* session) {delete session;}
