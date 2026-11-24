// WGC float pixels are linear scRGB, D65/BT.709 with 1.0 = 80 cd/m².
// Values outside [0,1], including negative channels, are valid scRGB coordinates.
float3 LinearScRgbTo2020(float3 rgb) {
    const float3x3 conversion = float3x3(
        0.627403896, 0.329283038, 0.043313066,
        0.069097289, 0.919540395, 0.011362316,
        0.016391439, 0.088013308, 0.895595253);
    return mul(conversion, rgb);
}

// SMPTE ST 2084 full-range OETF. Apply the bounds after rotation to BT.2020.
float3 EncodePq80(float3 linear2020) {
    const float m1 = 2610.0 / 16384.0, m2 = 2523.0 / 32.0;
    const float c1 = 3424.0 / 4096.0, c2 = 2413.0 / 128.0, c3 = 2392.0 / 128.0;
    float3 p = pow(saturate(linear2020 * (80.0 / 10000.0)), m1);
    return pow((c1 + c2 * p) / (1.0 + c3 * p), m2);
}
float3 PqAt(float2 uv) {
    return EncodePq80(LinearScRgbTo2020(image.SampleLevel(linearClamp,uv,0).rgb));
}
float4 HdrToPq(Vertex v) : SV_Target {
    return float4(EncodePq80(LinearScRgbTo2020(SamplePicture(v))),1);
}
float4 HdrToLinear2020(Vertex v) : SV_Target {
    return float4(max(0,LinearScRgbTo2020(SamplePicture(v))),1);
}
float3 Linear2020ToScRgb(float3 rgb) {
    const float3x3 inverseGamut=float3x3(
         1.660491,-0.587641,-0.072850,
        -0.124550, 1.132900,-0.008349,
        -0.018151,-0.100579, 1.118730);
    return mul(inverseGamut,rgb);
}
float4 HdrLinear2020ToScRgb(Vertex v) : SV_Target {
    return float4(Linear2020ToScRgb(SamplePicture(v)),1);
}
float4 HdrLinear2020ToPq(Vertex v) : SV_Target {
    return float4(EncodePq80(SamplePicture(v)),1);
}
// The model consumes a bounded, display-referred RGB image. Compress source
// luminance for inference only; the original FP16 picture feeds reconstruction.
static const float NrDiffuseWhite=203.0/80.0;
float3 NrGuideRgb(float3 scRgb) {
    float3 rgb=max(scRgb,0);
    float peak=max(rgb.r,max(rgb.g,rgb.b));
    float3 mapped=rgb/(NrDiffuseWhite+peak);
    return lerp(12.92*mapped,1.055*pow(mapped,1.0/2.4)-0.055,step(0.0031308,mapped));
}
float4 HdrNrGuide(Vertex v) : SV_Target {
    return float4(NrGuideRgb(SamplePicture(v)),1);
}
float NrPeak(float3 rgb) { return max(rgb.r,max(rgb.g,rgb.b)); }
float3 NrLinearGuide(float3 srgb) {
    return lerp(srgb/12.92,pow((srgb+0.055)/1.055,2.4),step(0.04045,srgb));
}
float4 HdrNrReconstruct(Vertex v) : SV_Target {
    float3 original=SamplePicture(v);
    float3 color=max(0,LinearScRgbTo2020(original));
    float3 positive=max(original,0);
    float peak=NrPeak(positive);
    float exposure=NrDiffuseWhite+peak;
    float3 guide=positive/exposure;
    float3 prediction=NrLinearGuide(saturate(previous.SampleLevel(linearClamp,v.uv,0).rgb));
    // Invert the guide's peak compression for the predicted RGB. Limit the
    // inverse exposure near display white, where the inverse is ill-conditioned.
    float exposureRatio=clamp((1-NrPeak(guide))/max(0.0001,1-NrPeak(prediction)),0.5,2.0);
    float3 delta=LinearScRgbTo2020((prediction*exposureRatio-guide)*exposure);
    // Anchor to the uncompressed source, retaining wide-gamut information that
    // was outside the guide. Bound all three residual channels with one scale.
    float limit=0.55*NrPeak(color);
    delta*=saturate(limit/max(0.00001,NrPeak(abs(delta))));
    float2 dx=float2(4.0/sourceSize.x,0),dy=float2(0,4.0/sourceSize.y);
    float3 variation=max(abs(image.SampleLevel(linearClamp,v.uv+dx,0).rgb-original),
                         abs(image.SampleLevel(linearClamp,v.uv-dx,0).rgb-original));
    variation=max(variation,max(abs(image.SampleLevel(linearClamp,v.uv+dy,0).rgb-original),
                                abs(image.SampleLevel(linearClamp,v.uv-dy,0).rgb-original)));
    float scene=smoothstep(0.001,0.015,NrPeak(variation)/max(peak,0.05));
    float strength=scene*(1-StrokeFootprint(v.uv))*(1-smoothstep(12.5,125,NrPeak(color)));
    return float4(clamp(color+strength*delta,0,125),1);
}
float4 FitNrHdrIntermediate(Vertex v) : SV_Target {
    // The SDK midpoint is scRGB; built-in interpolation and the real neural
    // endpoint are linear BT.2020. Blend in one space before encoding HDR10.
    float3 current=SamplePicture(v);
    if(phase>0) current=LinearScRgbTo2020(current);
    float ink=smoothstep(0.35,0.85,StrokeFootprint(v.uv));
    return float4(EncodePq80(lerp(current,SamplePictureTexture(previous,v),ink)),1);
}
float4 FitNgxFrame(Vertex v) : SV_Target {
    // Finishing the enhanced texture at a different size can filter a bright
    // stroke twice. Match Original's source footprint at this display pixel.
    bool hdr=phase>0;
    float3 reference=SamplePictureTexture(previous,v);
    if(hdr) reference=LinearScRgbTo2020(reference);
    float3 color=PreserveSourceWhite(SamplePicture(v),reference,hdr);
    return float4(hdr ? EncodePq80(color) : color,1);
}
float4 HdrSharpToPq(Vertex v) : SV_Target {
    float2 stepSize = 1.0 / sourceSize;
    float3 center = EncodePq80(LinearScRgbTo2020(SharpCenter(v)));
    float3 neighbors = PqAt(v.uv+float2(stepSize.x,0)) + PqAt(v.uv-float2(stepSize.x,0))
                     + PqAt(v.uv+float2(0,stepSize.y)) + PqAt(v.uv-float2(0,stepSize.y));
    return float4(saturate(center + 0.12 * (4.0 * center - neighbors)),1);
}
float4 HdrMotionGuide(Vertex v) : SV_Target {
    // The optical-flow hardware accepts 8-bit BGRA. This monochrome image only guides motion.
    float y = dot(image.SampleLevel(linearClamp,v.uv,0).rgb,float3(0.212639,0.715169,0.072192));
    float pq = EncodePq80(y.xxx).x;
    return float4(pq,pq,pq,1);
}

float4 HdrVsrReconstruct(Vertex v) : SV_Target {
    // NVIDIA processes a monochrome PQ guide. Keep the original floating-point
    // BT.2020 color and add only the change in detail measured on that guide.
    float3 color=max(0,LinearScRgbTo2020(SamplePicture(v)));
    float luminance=dot(color,float3(0.2627,0.6780,0.0593));
    float guide=flow.SampleLevel(linearClamp,v.uv,0).r;
    float enhanced=previous.SampleLevel(linearClamp,v.uv,0).r;
    float4 footprint=flow.GatherRed(linearClamp,v.uv);
    float lo=min(min(footprint.x,footprint.y),min(footprint.z,footprint.w));
    float hi=max(max(footprint.x,footprint.y),max(footprint.z,footprint.w));
    // A quantization boundary on a smooth 10-bit gradient is not an edge.
    float detail=clamp(clamp(enhanced,lo,hi)-guide,-0.025,0.025)*smoothstep(1.0/255.0,4.0/255.0,hi-lo);
    if(abs(detail)<0.00001 || luminance<=0) return float4(color,1);
    float code=saturate(EncodePq80(luminance.xxx).x+detail);
    float p=pow(code,32.0/2523.0);
    float adjusted=125.0*pow(max(p-3424.0/4096.0,0)/(2413.0/128.0-2392.0/128.0*p),16384.0/2610.0);
    float peak=max(color.r,max(color.g,color.b));
    return float4(color*min(adjusted/luminance,125.0/peak),1);
}

// Theme colors are sRGB in SDR. Map UI white to 203 nits for PQ presentation.
float3 WipeColor(float3 rgb) {
    float3 srgb=rgb/255.0;
    float3 linearRgb=lerp(srgb/12.92,pow((srgb+0.055)/1.055,2.4),step(0.04045,srgb));
    return phase<0.5 ? srgb : EncodePq80(LinearScRgbTo2020(linearRgb)*(203.0/80.0));
}
float4 WipeDivider(Vertex v) : SV_Target {
    return float4(WipeColor(float3(38,55,59)),1);
}
float4 WipeHandle(Vertex v) : SV_Target {
    float radius=min(targetSize.x,targetSize.y)*0.5;
    float2 q=abs((v.uv-0.5)*targetSize)-targetSize*0.5+radius;
    float boundary=length(max(q,0))+min(max(q.x,q.y),0)-radius;
    // Same idle, hover and captured colors as the WPF DrawerGrip template.
    float3 color=wipeGripState>1.5 ? float3(143,230,208) :
                 wipeGripState>0.5 ? float3(147,188,179) : float3(81,97,104);
    return float4(WipeColor(color),saturate(0.5-boundary));
}
