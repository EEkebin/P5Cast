// Shared bindings for display-referred SDR input from the player. Color and history
// use two packed FP16 pairs; motion is current-to-previous displacement in pixels.
#extension GL_GOOGLE_include_directive : require
#include "nr_common.glsl"
layout(local_size_x=8,local_size_y=8) in;
layout(std430,binding=0) readonly buffer Color { uvec2 color[]; };
layout(std430,binding=1) readonly buffer Motion { uint motion[]; };
layout(std430,binding=2) buffer History { uvec2 history[]; };
layout(std430,binding=4) writeonly buffer Output { uvec2 outputPixels[]; };
layout(std430,binding=5) readonly buffer Params {
    uint width,height,fullWidth,fullHeight;
    uint validHistory,seed,parity;
    float blendScale;
};
vec3 rgb(uvec2 packedColor) {
    return vec3(unpackHalf2x16(packedColor.x),unpackHalf2x16(packedColor.y).x);
}
uvec2 packRgb(vec3 value) {
    return uvec2(packHalf2x16(value.rg),packHalf2x16(vec2(value.b,1)));
}
vec3 priorAt(ivec2 p) {
    p=clamp(p,ivec2(0),ivec2(width,height)-1);
    return rgb(history[(1u-parity)*width*height+uint(p.y)*width+uint(p.x)]);
}
bool reproject(ivec2 p,out vec3 prior) {
    if(validHistory==0) return false;
    vec2 prev=vec2(p)+unpackHalf2x16(motion[uint(p.y)*width+uint(p.x)]);
    if(any(isnan(prev)) || any(lessThan(prev,vec2(-0.5))) || any(greaterThanEqual(prev,vec2(width,height)-0.5))) return false;
    ivec2 base=ivec2(floor(prev));
    vec2 f=fract(prev);
    prior=mix(mix(priorAt(base),priorAt(base+ivec2(1,0)),f.x),
              mix(priorAt(base+ivec2(0,1)),priorAt(base+1),f.x),f.y);
    return true;
}
