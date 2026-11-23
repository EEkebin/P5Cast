cbuffer Loupe : register(b1) {
    float2 lensCenter, copyOrigin;
    float lensRadius, magnification, lensScale, labelRow;
    float2 labelCenter, lensPadding;
};

float4 InspectionLoupe(Vertex v) : SV_Target {
    float2 delta=v.pos.xy-lensCenter;
    float inside=lensRadius-length(delta);
    clip(inside+0.5);
    // Sample the already-presentable texture: preserve PQ codes and replicate individual pixels.
    int2 src=int2(floor(lensCenter+delta/magnification))-int2(copyOrigin);
    float3 rgb=image.Load(int3(src,0)).rgb;
    float2 local=(v.pos.xy-labelCenter)/lensScale;
    if(lensRadius>=38*lensScale && all(abs(local)<float2(25,10))) {
        float2 q=abs(local)-float2(19,4);
        float pill=6-length(max(q,0))-min(max(q.x,q.y),0);
        float2 uv=local/float2(50,20)+0.5;
        uv.y=(labelRow+uv.y)/5;
        float ink=previous.SampleLevel(linearClamp,uv,0).r;
        float3 label=lerp(WipeColor(float3(22,34,39)),WipeColor(float3(212,242,234)),ink);
        rgb=lerp(rgb,label,saturate(pill*lensScale));
    }
    // A dark keyline keeps the mint outer rim legible over bright and dark content.
    rgb=lerp(WipeColor(float3(30,47,52)),rgb,saturate(inside-2*lensScale));
    rgb=lerp(WipeColor(float3(143,230,208)),rgb,saturate(inside-lensScale));
    return float4(rgb,saturate(inside+0.5));
}
