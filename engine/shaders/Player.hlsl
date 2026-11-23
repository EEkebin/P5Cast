cbuffer Frame : register(b0) {
    float2 sourceSize;
    float2 outputSize;
    float phase;
    float2 viewportOrigin;
    float wipeHorizontal;
    float2 targetSize;
    float wipeGripState;
    float reserved;
};
Texture2D<float4> image : register(t0);
Texture2D<float4> previous : register(t1);
Texture2D<float4> flow : register(t2);
SamplerState linearClamp : register(s0);
Texture2D<int2> hardwareMotion : register(t3);

struct Vertex { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
Vertex Fullscreen(uint vertexID : SV_VertexID) {
    Vertex v;
    v.uv = float2((vertexID << 1) & 2, vertexID & 2);
    v.pos = float4(v.uv * float2(2,-2) + float2(-1,1), 0, 1);
    return v;
}
float3 SamplePictureTexture(Texture2D<float4> picture,Vertex v) {
    uint w,h; picture.GetDimensions(w,h);
    if(all(float2(w,h)==targetSize))
        return picture.Load(int3(uint2(v.pos.xy-viewportOrigin),0)).rgb;
    if(any(float2(w,h)>targetSize)) {
        // Integrate four sub-pixel regions when reducing a frame. A single bilinear sample can skip thin strokes.
        float2 d=0.25/targetSize;
        return 0.25*(picture.SampleLevel(linearClamp,v.uv-d,0).rgb + picture.SampleLevel(linearClamp,v.uv+d,0).rgb +
                     picture.SampleLevel(linearClamp,v.uv+float2(d.x,-d.y),0).rgb + picture.SampleLevel(linearClamp,v.uv+float2(-d.x,d.y),0).rgb);
    }
    return picture.SampleLevel(linearClamp,v.uv,0).rgb;
}
float3 SamplePicture(Vertex v) { return SamplePictureTexture(image,v); }
float4 Original(Vertex v) : SV_Target {
    return float4(SamplePicture(v),1);
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
float4 CubicWeights(float t) {
    float t2=t*t, t3=t2*t;
    return float4(-0.5*t+t2-0.5*t3, 1.0-2.5*t2+1.5*t3, 0.5*t+2.0*t2-1.5*t3, -0.5*t2+0.5*t3);
}
float3 SharpCenter(Vertex v) {
    if(any(targetSize<=sourceSize)) return SamplePicture(v);
    float2 p=v.uv*sourceSize-0.5;
    int2 base=int2(floor(p));
    float4 wx=CubicWeights(frac(p.x)), wy=CubicWeights(frac(p.y));
    float3 result=0, lo=1e20, hi=-1e20;
    [unroll] for(int y=0;y<4;y++) [unroll] for(int x=0;x<4;x++) {
        float3 tap=image.Load(int3(clamp(base+int2(x-1,y-1),0,int2(sourceSize)-1),0)).rgb;
        result+=wx[x]*wy[y]*tap;
        if((x==1 || x==2) && (y==1 || y==2)) {lo=min(lo,tap);hi=max(hi,tap);}
    }
    // Bound the reconstruction to local color values to avoid halos around UI text.
    return clamp(result,lo,hi);
}
float4 Sharp(Vertex v) : SV_Target {
    float2 d=1.0/sourceSize;
    float3 c=SharpCenter(v);
    float3 a=image.SampleLevel(linearClamp,v.uv+float2(d.x,0),0).rgb;
    float3 b=image.SampleLevel(linearClamp,v.uv-float2(d.x,0),0).rgb;
    float3 e=image.SampleLevel(linearClamp,v.uv+float2(0,d.y),0).rgb;
    float3 f=image.SampleLevel(linearClamp,v.uv-float2(0,d.y),0).rgb;
    return float4(saturate(c + (4*c-a-b-e-f)*0.12),1);
}
float luma(float3 rgb) { return dot(rgb,float3(.2126,.7152,.0722)); }
float4 OpticalField(Vertex v) : SV_Target {
    // S10.5 input is current-to-previous. Our interpolation field uses previous-to-current.
    float2 backward=float2(hardwareMotion.Load(int3(int2(v.pos.xy),0)))/32.0;
    float currentLuma=luma(image.SampleLevel(linearClamp,v.uv,0).rgb);
    float earlierLuma=luma(previous.SampleLevel(linearClamp,v.uv+backward/sourceSize,0).rgb);
    return float4(-backward,abs(currentLuma-earlierLuma),1);
}
// A block estimate can belong to scenery behind a stationary overlay. Check a
// local edge and its zero-displacement match before accepting that estimate.
bool stationaryEdge(float2 uv,float3 now,float3 before) {
    if(any(abs(now-before)>=0.006)) return false;
    float2 d=1.0/sourceSize;
    float edge=0,delta=0;
    [unroll] for(int i=0;i<4;i++) {
        float2 offset=i==0 ? float2(d.x,0) : i==1 ? float2(0,d.y) : i==2 ? float2(-d.x,0) : float2(0,-d.y);
        float3 nearNow=image.SampleLevel(linearClamp,uv+offset,0).rgb;
        float3 nearBefore=previous.SampleLevel(linearClamp,uv+offset,0).rgb;
        float3 gradient=abs(now-nearNow),change=abs(nearNow-nearBefore);
        edge=max(edge,max(gradient.x,max(gradient.y,gradient.z)));
        delta=max(delta,max(change.x,max(change.y,change.z)));
    }
    return edge>0.08 && delta<0.012;
}
// Opposite flanks distinguish narrow ink from a single long silhouette.
float inkContrast(float center,float2 flank) {
    float2 d=center-flank;
    return max(min(d.x,d.y),min(-d.x,-d.y));
}
float strokeContrast(float2 uv,float center) {
    float2 d=2.0/sourceSize;
    float2 horizontal=float2(luma(image.SampleLevel(linearClamp,uv-float2(d.x,0),0).rgb),
                             luma(image.SampleLevel(linearClamp,uv+float2(d.x,0),0).rgb));
    float2 vertical=float2(luma(image.SampleLevel(linearClamp,uv-float2(0,d.y),0).rgb),
                           luma(image.SampleLevel(linearClamp,uv+float2(0,d.y),0).rgb));
    return max(inkContrast(center,horizontal),inkContrast(center,vertical));
}
struct NgxGuidance { float2 motion : SV_Target0; float ink : SV_Target1; };
NgxGuidance NgxVectors(Vertex v) {
    NgxGuidance guide;
    float3 now=image.Load(int3(int2(v.pos.xy),0)).rgb;
    float3 before=previous.Load(int3(int2(v.pos.xy),0)).rgb;
    guide.motion=phase>0 ? -flow.SampleLevel(linearClamp,v.uv,0).xy : 0;
    if(any(abs(guide.motion)>0.5) && stationaryEdge(v.uv,now,before)) guide.motion=0;

    float hi=max(now.r,max(now.g,now.b)),lo=min(now.r,min(now.g,now.b));
    float3 delta=abs(now-before);
    bool stable=phase==0 || max(delta.r,max(delta.g,delta.b))<0.012*max(1.0,hi);
    // Test the ink's neutrality only; colored backgrounds and antialiased fringes
    // retain their own source RGB. Confidence is independent of output resolution.
    guide.ink=0;
    if(stable && hi-lo<0.04+0.12*hi) {
        float contrast=strokeContrast(v.uv,luma(now))/max(1.0,hi);
        guide.ink=smoothstep(0.16,0.35,contrast);
    }
    return guide;
}
float max4(float4 values) {return max(max(values.x,values.y),max(values.z,values.w));}
// A decoded white HUD element can move independently of the scenery that supplied
// our estimated motion. Bound lost brightness against the current frame, including
// partly covered pixels; no temporal stability or inferred glyph width is required.
float3 PreserveSourceWhite(float3 enhanced,float3 reference,bool hdr) {
    float hi=max(reference.r,max(reference.g,reference.b)),lo=min(reference.r,min(reference.g,reference.b));
    float neutral=1-smoothstep(0.05,0.14,(hi-lo)/max(hi,0.001));
    float3 weights=hdr ? float3(0.2627,0.6780,0.0593) : float3(0.2126,0.7152,0.0722);
    float white=dot(reference,weights);
    float strength=neutral*(hdr ? smoothstep(0.55,1.5,white) : smoothstep(0.55,0.82,white));
    float loss=white-dot(enhanced,weights);
    // Keep small model differences within half a percent of the source white.
    return lerp(enhanced,reference,strength*saturate(1-0.005*white/max(loss,0.000001)));
}
float4 PreserveNgxStrokes(Vertex v) : SV_Target {
    float3 enhanced=image.Load(int3(int2(v.pos.xy),0)).rgb;
    // Include the neighboring source texels that contribute antialiased coverage
    // and the small dark gaps around them, without altering the rest of the scene.
    float2 halfTexel=0.5/sourceSize;
    float ink=max(max4(flow.GatherRed(linearClamp,v.uv-halfTexel)),max4(flow.GatherRed(linearClamp,v.uv+halfTexel)));
    ink=max(ink,max(max4(flow.GatherRed(linearClamp,v.uv+float2(halfTexel.x,-halfTexel.y))),
                    max4(flow.GatherRed(linearClamp,v.uv+float2(-halfTexel.x,halfTexel.y)))));
    float3 reference=previous.SampleLevel(linearClamp,v.uv,0).rgb;
    float floorValue=phase>0 ? 0.00001 : 1.0/255.0;
    float3 error=abs(enhanced-reference)/max(floorValue,0.025*abs(reference));
    float excess=max(error.r,max(error.g,error.b));
    float correction=1.0-rcp(max(1.0,excess));
    return float4(PreserveSourceWhite(lerp(enhanced,reference,ink*correction),reference,phase>0),1);
}
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
    float3 now=image.SampleLevel(linearClamp,v.uv,0).rgb;
    float3 before=previous.SampleLevel(linearClamp,v.uv,0).rgb;
    if(any(abs(motion.xy)>0.5) && stationaryEdge(v.uv,now,before)) return float4(now,1);
    float3 a=previous.SampleLevel(linearClamp,v.uv-phase*shift,0).rgb;
    float3 b=image.SampleLevel(linearClamp,v.uv+(1-phase)*shift,0).rgb;
    // Poor matches and abrupt cuts favor the nearest real frame.
    float3 nearest=phase<0.5 ? before : now;
    return float4(lerp(lerp(a,b,phase),nearest,smoothstep(.06,.17,motion.z)),1);
}
