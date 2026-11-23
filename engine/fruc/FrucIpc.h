#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11_4.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <cstdint>
using Microsoft::WRL::ComPtr;

// One outstanding request at a time, published via a pair of kernel events.
// Images stay in GPU memory. The process boundary contains the vendor SDK's
// CUDA registrations, including cleanup when a session ends or a driver fails.
struct Packet {
    unsigned version,width,height;
    LUID adapter;
    HANDLE parent,request,response,input,output,fence;
    UINT64 sequence;
    bool stop;
    HRESULT result;
    int valid;
    wchar_t step[128],runtime[32768];
};
struct Handle {
    HANDLE value{};
    ~Handle() {if(value && value!=INVALID_HANDLE_VALUE) CloseHandle(value);}
};
struct View {
    Packet* data{};
    ~View() {if(data) UnmapViewOfFile(data);}
};

HRESULT OpenFrucCore(ID3D11Device*,UINT,UINT,const wchar_t*,void**,const wchar_t**);
HRESULT ProcessFrucCore(void*,ID3D11Texture2D*,ID3D11Texture2D*,int*);
void CloseFrucCore(void*);
