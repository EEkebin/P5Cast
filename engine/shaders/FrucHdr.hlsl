// NVIDIA FRUC returns an RGBA8 sample. The values in the bridge are full-range
// BT.2020 PQ, so the midpoint covers the same luminance range as both real frames.
float4 FrucHdrMidpoint(Vertex v) : SV_Target {
    int3 pixel=int3(int2(v.pos.xy),0);
    float3 midpoint=image.Load(pixel).rgb;
    float3 newer=previous.Load(pixel).rgb, older=flow.Load(pixel).rgb;
    // Vendor upscalers produce linear BT.2020. Real captured frames use scRGB.
    float3 a=EncodePq80(phase>0 ? older : LinearScRgbTo2020(older));
    float3 b=EncodePq80(phase>0 ? newer : LinearScRgbTo2020(newer));
    float3 qa=round(a*255)/255, qb=round(b*255)/255;
    const float3x3 toScRgb=float3x3(1.660491,-0.587641,-0.072850,
        -0.124550,1.132900,-0.008349,-0.018151,-0.100579,1.118730);
    // Stationary gradients can move a code or two in the SDK's 8-bit warp.
    // Keep the original FP16 sample when the guide confirms the same surface.
    if(all(older==newer) && all(abs(midpoint-qa)<2.1/255))
        return float4(phase>0 ? mul(toScRgb,newer) : newer,1);
    float3 span=qb-qa;
    float lengthSquared=dot(span,span);
    float weight=lengthSquared>0 ? saturate(dot(midpoint-qa,span)/max(lengthSquared,1e-8)) : 0.5;
    float3 distance=abs(midpoint-lerp(qa,qb,weight))*255;
    float agreement=saturate(1-max(distance.r,max(distance.g,distance.b)));
    // Recover endpoint precision where the interpolator's RGB result agrees.
    // Across moving boundaries a disagreeing midpoint stays owned by FRUC.
    float3 pq=midpoint+agreement*lerp(a-qa,b-qb,weight);
    return float4(mul(toScRgb,DirectPqToNits(pq)/80),1);
}
