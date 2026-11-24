// FFmpeg supplies three 4:2:0 planes. Keep luma at its original sample positions.
cbuffer VideoColor : register(b1) {
    float sampleMax, blackCode, whiteCode, neutralCode;
    float chromaRange, kr, kb, pqInput;
    float hdrOutput, whiteNits, hlgInput, wideGamut;
};
float3 DirectPqToNits(float3 v) {
    float3 p=pow(saturate(v),1.0/(2523.0/32.0));
    return 10000.0*pow(max(p-3424.0/4096.0,0)/max(2413.0/128.0-2392.0/128.0*p,0.000001),1.0/(2610.0/16384.0));
}
float3 Direct709ToLinear(float3 v) {
    v=saturate(v);
    return lerp(v/12.92,pow((v+0.055)/1.055,2.4),step(0.04045,v));
}
float3 DirectToSrgb(float3 v) {
    v=saturate(v);
    return lerp(12.92*v,1.055*pow(v,1.0/2.4)-0.055,step(0.0031308,v));
}
float3 MapHdrToSdr(float3 relative) {
    float peak=max(relative.r,max(relative.g,relative.b));
    if(peak>0.75) relative*=(0.75+0.25*(1-exp(-4*(peak-0.75))))/peak;
    return relative;
}
float4 SdrPreview(Vertex v) : SV_Target {
    float3 pq=image.Load(int3(int2(v.pos.xy-viewportOrigin),0)).rgb;
    float3 rec709=max(0,Linear2020ToScRgb(DirectPqToNits(pq)));
    float3 sdr=MapHdrToSdr(rec709/phase);
    float3 codes=round(255*DirectToSrgb(sdr))/255;
    return float4(EncodePq80(LinearScRgbTo2020(Direct709ToLinear(codes))*(phase/80)),1);
}
float4 DirectPlanes(Vertex v) : SV_Target {
    float y=(image.Load(int3((int2)v.pos.xy,0)).r*sampleMax-blackCode)/(whiteCode-blackCode);
    float cb=(previous.SampleLevel(linearClamp,v.uv,0).r*sampleMax-neutralCode)/chromaRange;
    float cr=(flow.SampleLevel(linearClamp,v.uv,0).r*sampleMax-neutralCode)/chromaRange;
    float r=y+2*(1-kr)*cr, b=y+2*(1-kb)*cb;
    float3 coded=float3(r,(y-kr*r-kb*b)/(1-kr-kb),b);
    if (pqInput<0.5 && hlgInput<0.5 && hdrOutput<0.5 && wideGamut<0.5) return float4(saturate(coded),1);
    float3 nits;
    if(pqInput>0.5) nits=DirectPqToNits(coded);
    else if(hlgInput>0.5) {
        float3 e=saturate(coded);
        float3 scene=lerp(e*e/3,(exp((e-0.55991073)/0.17883277)+0.28466892)/12,step(0.5,e));
        nits=1000*scene*pow(max(dot(scene,float3(0.2627,0.678,0.0593)),0.000001),0.2);
    } else nits=Direct709ToLinear(coded)*whiteNits;
    if(wideGamut>0.5) nits=Linear2020ToScRgb(nits);
    if(hdrOutput>0.5) return float4(nits/80,1);
    // Display-referred shoulder for HDR video played on an SDR monitor.
    float3 relative=max(nits,0)/max(whiteNits,1);
    if(pqInput>0.5 || hlgInput>0.5) relative=MapHdrToSdr(relative);
    return float4(DirectToSrgb(relative),1);
}
