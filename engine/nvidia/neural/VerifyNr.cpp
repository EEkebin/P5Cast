#include "NrTransport.h"
#include <cstdio>

int wmain(int argc,wchar_t** argv) {
    if(argc!=3)return 1;
    SetEnvironmentVariableW(L"DXVK_LOG_PATH",L"none");
    try{
        HMODULE dxvk=LoadLibraryExW(argv[1],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        HMODULE bridge=LoadLibraryExW(argv[2],nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!dxvk || !bridge)throw std::runtime_error("Load the built Vulkan and neural rendering bridges");
        auto create=reinterpret_cast<decltype(&D3D11CreateDevice)>(GetProcAddress(dxvk,"D3D11CreateDevice"));
        using OpenFn=int(*)(ID3D11Device*,const wchar_t*,const wchar_t*,int,int,void**,wchar_t*,int);
        auto open=reinterpret_cast<OpenFn>(GetProcAddress(bridge,"P5NrOpen"));
        if(!create || !open)throw std::runtime_error("Missing native bridge entry point");
        ComPtr<ID3D11Device> dx;ComPtr<ID3D11DeviceContext> ctx;
        DxCheck(create(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,
            D3D11_SDK_VERSION,&dx,nullptr,&ctx),"Create stock DXVK device");
        NrTransport transport;
        transport.OpenInstance(dx.Get(),{});transport.OpenDevice({});
        const UINT width=257,height=131;
        transport.AllocateFrame(width,height);
        ComPtr<ID3D11Texture2D> textures[5],readback;
        ID3D11Texture2D* frame[5]{};
        std::vector<unsigned char> input[4];
        for(unsigned i=0;i<5;++i){
            auto& p=transport.planes[i];D3D11_TEXTURE2D_DESC desc{};
            desc.Width=width;desc.Height=height;desc.Format=p.dxFormat;desc.MipLevels=1;desc.ArraySize=1;desc.SampleDesc.Count=1;
            desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            DxCheck(dx->CreateTexture2D(&desc,nullptr,&textures[i]),"Create transport verification texture");
            frame[i]=textures[i].Get();
            if(i<4) input[i].resize(width*height*p.bytes);
            else{
                desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
                DxCheck(dx->CreateTexture2D(&desc,nullptr,&readback),"Create output readback");
            }
        }
        for(unsigned n=0;n<3;++n) {
            for(unsigned i=0;i<4;++i) {
                for(size_t j=0;j<input[i].size();++j)input[i][j]=static_cast<unsigned char>((j/7+n*31+i*19)%251);
                ctx->UpdateSubresource(frame[i],0,nullptr,input[i].data(),width*transport.planes[i].bytes,0);
            }
            transport.Frame(frame,[&] {
                // Prove the physical device receives the color image and returns every byte.
                // This harness invokes a Vulkan copy, independently of the NR provider.
                transport.Layout(0,VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,VK_ACCESS_SHADER_READ_BIT,VK_ACCESS_TRANSFER_READ_BIT);
                VkImageCopy copy{};copy.srcSubresource=copy.dstSubresource={VK_IMAGE_ASPECT_COLOR_BIT,0,0,1};copy.extent={width,height,1};
                vkCmdCopyImage(transport.cmd,transport.planes[0].image,VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,transport.planes[4].image,VK_IMAGE_LAYOUT_GENERAL,1,&copy);
                transport.Layout(0,VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,VK_ACCESS_TRANSFER_READ_BIT,VK_ACCESS_SHADER_READ_BIT);
            });
            ctx->CopyResource(readback.Get(),frame[4]);
            D3D11_MAPPED_SUBRESOURCE map{};DxCheck(ctx->Map(readback.Get(),0,D3D11_MAP_READ,0,&map),"Map returned output");
            bool exact=true;const UINT pitch=width*transport.planes[4].bytes;
            for(UINT y=0;y<height;++y) exact=exact && !memcmp(static_cast<unsigned char*>(map.pData)+size_t(y)*map.RowPitch,
                input[0].data()+size_t(y)*pitch,pitch);
            ctx->Unmap(readback.Get(),0);
            if(!exact)throw std::runtime_error("Changed frame failed the Vulkan GPU round trip");
        }
        VkPhysicalDeviceProperties props{};vkGetPhysicalDeviceProperties(transport.physical,&props);
        printf("Dedicated Vulkan GPU round trip: %s, three independent %ux%u FP16 images, byte-exact.\n",props.deviceName,width,height);
        wchar_t reason[512]{};void* state=nullptr;
        int result=open(dx.Get(),L"Z:\\P5Cast-missing-runtime-fixture",L".",width,height,&state,reason,512);
        if(state || (result!=1 && result!=2))throw std::runtime_error("Missing provider unexpectedly created a session");
        if(strstr(props.deviceName,"GeForce RTX 4090") && (result!=1 || !wcsstr(reason,L"RTX 50")))throw std::runtime_error("4090 availability gate returned an incorrect reason");
        printf("Production DLSS 5 feature gate: result %d, %ls.\n",result,reason);
        return 0;
    }catch(const std::exception& e){fprintf(stderr,"%s\n",e.what());return 1;}
}
