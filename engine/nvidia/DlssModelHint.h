#pragma once
#include <d3d11.h>
#include "nvsdk_ngx_params.h"

// The hint is scoped to a single playback device and read at feature creation.
// NVIDIA's driver and application overrides retain the final model choice.
static const GUID P5DlssModelId={0x477a020b,0xd534,0x4f2a,{0x99,0x33,0x66,0xef,0x40,0x71,0x97,0x35}};
inline void ApplyDlssModelHint(ID3D11Device* device,NVSDK_NGX_Parameter* parameters) {
    unsigned preset=0;
    UINT bytes=sizeof(preset);
    device->GetPrivateData(P5DlssModelId,&bytes,&preset);
    parameters->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA,preset);
    parameters->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality,preset);
    parameters->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced,preset);
    parameters->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Performance,preset);
    parameters->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraPerformance,preset);
}
