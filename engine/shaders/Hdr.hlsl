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
float4 HdrLinear2020ToPq(Vertex v) : SV_Target {
    return float4(EncodePq80(SamplePicture(v)),1);
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

// Theme colors are sRGB in SDR. Map UI white to 203 nits for PQ presentation.
float3 WipeColor(float3 rgb) {
    float3 srgb=rgb/255.0;
    float3 linearRgb=lerp(srgb/12.92,pow((srgb+0.055)/1.055,2.4),step(0.04045,srgb));
    return phase<0.5 ? srgb : EncodePq80(LinearScRgbTo2020(linearRgb)*(203.0/80.0));
}
float4 WipeDivider(Vertex v) : SV_Target {
    float2 local=v.pos.xy-viewportOrigin;
    float thickness=wipeHorizontal>0 ? targetSize.y : targetSize.x;
    float axis=wipeHorizontal>0 ? local.y : local.x;
    float edge=min(axis,thickness-axis);
    float3 outline=WipeColor(float3(35,61,59)), mint=WipeColor(float3(143,230,208));
    return float4(lerp(outline,mint,saturate(edge-thickness*0.18+0.5)),1);
}
float WipeSegment(float2 p,float2 start,float2 end) {
    float2 span=end-start;
    float along=saturate(dot(p-start,span)/max(dot(span,span),0.0001));
    return length(p-start-along*span);
}
float4 WipeHandle(Vertex v) : SV_Target {
    // Work along the drag axis so rotating the handle also rotates its arrows.
    float2 size=wipeHorizontal>0 ? targetSize.yx : targetSize;
    float2 p=abs((v.uv-0.5)*(targetSize));
    p=wipeHorizontal>0 ? p.yx : p;
    float radius=min(size.x,size.y)*0.5;
    float2 q=p-size*0.5+radius;
    float boundary=length(max(q,0))+min(max(q.x,q.y),0)-radius;
    float border=clamp(size.y/20,1.0,2.5);
    float3 face=WipeColor(float3(23,42,45)), rim=WipeColor(float3(111,181,165));
    float3 color=lerp(face,rim,saturate(boundary+border+0.5));
    float arrow=WipeSegment(p,float2(size.x*0.28,0),size*float2(0.17,0.18));
    float bar=length(float2(p.x,max(0,p.y-size.y*0.22)));
    float ink=saturate(0.5+max(0.9,size.y/22)-min(arrow,bar));
    return float4(lerp(color,WipeColor(float3(170,246,226)),ink),saturate(0.5-boundary));
}
