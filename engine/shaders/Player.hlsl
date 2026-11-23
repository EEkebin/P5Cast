cbuffer Frame : register(b0) {
    float2 sourceSize;
    float2 outputSize;
    float phase;
    float2 viewportOrigin;
    float padding;
};
Texture2D<float4> image : register(t0);
Texture2D<float4> previous : register(t1);
Texture2D<float4> flow : register(t2);
SamplerState linearClamp : register(s0);

struct Vertex { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
Vertex Fullscreen(uint vertexID : SV_VertexID) {
    Vertex v;
    v.uv = float2((vertexID << 1) & 2, vertexID & 2);
    v.pos = float4(v.uv * float2(2,-2) + float2(-1,1), 0, 1);
    return v;
}
float4 Original(Vertex v) : SV_Target {
    return float4(image.SampleLevel(linearClamp,v.uv,0).rgb,1);
}
RWStructuredBuffer<uint> difference : register(u0);
groupshared uint tileChanged;
[numthreads(16,16,1)]
void Changed(uint3 pixel : SV_DispatchThreadID, uint index : SV_GroupIndex) {
    if(index==0) tileChanged=0;
    GroupMemoryBarrierWithGroupSync();
    if(all(pixel.xy<uint2(sourceSize)) && any(image.Load(int3(pixel.xy,0)).rgb!=previous.Load(int3(pixel.xy,0)).rgb))
        InterlockedOr(tileChanged,1);
    GroupMemoryBarrierWithGroupSync();
    if(index==0 && tileChanged) InterlockedOr(difference[0],1);
}
#define A_GPU 1
#define A_HLSL 1
#include "ffx_a.h"
#define FSR_EASU_F 1
#define FSR_RCAS_F 1
AF4 FsrEasuRF(AF2 p) { return image.GatherRed(linearClamp,p); }
AF4 FsrEasuGF(AF2 p) { return image.GatherGreen(linearClamp,p); }
AF4 FsrEasuBF(AF2 p) { return image.GatherBlue(linearClamp,p); }
AF4 FsrRcasLoadF(ASU2 p) { return image.Load(int3(clamp(p,0,int2(outputSize)-1),0)); }
void FsrRcasInputF(inout AF1 r,inout AF1 g,inout AF1 b) {}
#include "ffx_fsr1.h"
float4 Easu(Vertex v) : SV_Target {
    uint4 c0,c1,c2,c3;
    FsrEasuCon(c0,c1,c2,c3,sourceSize.x,sourceSize.y,sourceSize.x,sourceSize.y,outputSize.x,outputSize.y);
    float3 rgb;
    FsrEasuF(rgb,uint2(v.pos.xy),c0,c1,c2,c3);
    return float4(rgb,1);
}
float4 Rcas(Vertex v) : SV_Target {
    uint4 constants;
    FsrRcasCon(constants,0.35);
    float3 rgb;
    FsrRcasF(rgb.r,rgb.g,rgb.b,uint2(v.pos.xy-viewportOrigin),constants);
    return float4(rgb,1);
}
float4 Sharp(Vertex v) : SV_Target {
    float2 d=1.0/sourceSize;
    float3 c=image.SampleLevel(linearClamp,v.uv,0).rgb;
    float3 a=image.SampleLevel(linearClamp,v.uv+float2(d.x,0),0).rgb;
    float3 b=image.SampleLevel(linearClamp,v.uv-float2(d.x,0),0).rgb;
    float3 e=image.SampleLevel(linearClamp,v.uv+float2(0,d.y),0).rgb;
    float3 f=image.SampleLevel(linearClamp,v.uv-float2(0,d.y),0).rgb;
    return float4(saturate(c + (4*c-a-b-e-f)*0.12),1);
}
float luma(float3 rgb) { return dot(rgb,float3(.2126,.7152,.0722)); }
float patchError(float2 uv,float2 offset) {
    float score=0;
    [unroll] for(int y=-1;y<=1;y++) [unroll] for(int x=-1;x<=1;x++) {
        float2 sampleUV=uv+float2(x,y)*3.0/sourceSize;
        float curr=luma(image.SampleLevel(linearClamp,sampleUV,0).rgb);
        float prev=luma(previous.SampleLevel(linearClamp,sampleUV-offset/sourceSize,0).rgb);
        score += abs(curr-prev);
    }
    return score + dot(offset,offset)*0.00008;
}
// Estimate backward correspondence once per 12-pixel tile, then interpolate its field.
float4 Motion(Vertex v) : SV_Target {
    float2 best=0;
    float score=patchError(v.uv,0);
    [unroll] for(int level=0;level<3;level++) {
        int stride=level==0 ? 6 : level==1 ? 3 : 1;
        float2 center=best;
        [loop] for(int y=-1;y<=1;y++) [loop] for(int x=-1;x<=1;x++) {
            if(x==0 && y==0) continue;
            float2 offset=center + float2(x,y)*stride;
            float err=patchError(v.uv,offset);
            if(err<score){ score=err; best=offset; }
        }
    }
    return float4(best,score/9.0,1);
}
float4 Interpolate(Vertex v) : SV_Target {
    float3 motion=flow.SampleLevel(linearClamp,v.uv,0).xyz;
    float2 shift=motion.xy/sourceSize;
    float3 a=previous.SampleLevel(linearClamp,v.uv-phase*shift,0).rgb;
    float3 b=image.SampleLevel(linearClamp,v.uv+(1-phase)*shift,0).rgb;
    // Poor matches and abrupt cuts favor the nearest real frame.
    float3 nearest=phase<0.5 ? previous.SampleLevel(linearClamp,v.uv,0).rgb : image.SampleLevel(linearClamp,v.uv,0).rgb;
    return float4(lerp(lerp(a,b,phase),nearest,smoothstep(.06,.17,motion.z)),1);
}
