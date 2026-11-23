// OpenGL fragment stages for Windows SDR playback. The build embeds AMD's
// licensed GLSL implementation of EASU and RCAS from engine/third_party.
layout(binding=0) uniform sampler2D frame;
layout(binding=1) uniform sampler2D prior;
layout(binding=2) uniform sampler2D vectors;
uniform vec2 sourceSize;
uniform vec2 areaOrigin;
uniform vec2 areaSize;
uniform float phase;
out vec4 pixel;

#if EFFECT==2 || EFFECT==3
#define A_GPU 1
#define A_GLSL 1
#include "ffx_a.h"
#define FSR_EASU_F 1
#define FSR_RCAS_F 1
AF4 FsrEasuRF(AF2 p) {return textureGather(frame,p,0);}
AF4 FsrEasuGF(AF2 p) {return textureGather(frame,p,1);}
AF4 FsrEasuBF(AF2 p) {return textureGather(frame,p,2);}
AF4 FsrRcasLoadF(ASU2 p) {return texelFetch(frame,clamp(p,ivec2(0),textureSize(frame,0)-1),0);}
void FsrRcasInputF(inout AF1 r,inout AF1 g,inout AF1 b) {}
#include "ffx_fsr1.h"
#endif

vec3 fitFrame(vec2 uv,ivec2 pos) {
    vec2 size=vec2(textureSize(frame,0));
    if(all(equal(size,areaSize))) return texelFetch(frame,pos,0).rgb;
    if(any(greaterThan(size,areaSize))) {
        vec2 d=.25/areaSize;
        return .25*(textureLod(frame,uv-d,0).rgb+textureLod(frame,uv+d,0).rgb+
                    textureLod(frame,uv+vec2(d.x,-d.y),0).rgb+textureLod(frame,uv+vec2(-d.x,d.y),0).rgb);
    }
    return textureLod(frame,uv,0).rgb;
}
float luma(vec3 color) {return dot(color,vec3(.2126,.7152,.0722));}
float patchError(vec2 uv,vec2 offset) {
    float error=0;
    for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++) {
        vec2 p=uv+vec2(x,y)*3./sourceSize;
        error+=abs(luma(textureLod(frame,p,0).rgb)-luma(textureLod(prior,p-offset/sourceSize,0).rgb));
    }
    return error+dot(offset,offset)*.00008;
}
bool stationaryEdge(vec2 uv,vec3 current,vec3 previous) {
    if(any(greaterThanEqual(abs(current-previous),vec3(.006)))) return false;
    vec3 gradient=vec3(0),change=vec3(0);
    const vec2 offsets[4]=vec2[4](vec2(1,0),vec2(0,1),vec2(-1,0),vec2(0,-1));
    for(int i=0;i<4;i++) {
        vec2 p=uv+offsets[i]/sourceSize;
        vec3 near=textureLod(frame,p,0).rgb;
        gradient=max(gradient,abs(current-near));change=max(change,abs(near-textureLod(prior,p,0).rgb));
    }
    return any(greaterThan(gradient,vec3(.08))) && all(lessThan(change,vec3(.012)));
}
void main() {
    vec2 position=gl_FragCoord.xy-areaOrigin,uv=position/areaSize;
#if EFFECT==0
    pixel=vec4(fitFrame(uv,ivec2(position)),1);
#elif EFFECT==1
    vec3 center=fitFrame(uv,ivec2(position));
    vec2 stepSize=1./sourceSize;
    vec3 neighbors=textureLod(frame,uv+vec2(stepSize.x,0),0).rgb+textureLod(frame,uv-vec2(stepSize.x,0),0).rgb+
                   textureLod(frame,uv+vec2(0,stepSize.y),0).rgb+textureLod(frame,uv-vec2(0,stepSize.y),0).rgb;
    pixel=vec4(clamp(center+.12*(4.*center-neighbors),0.,1.),1.);
#elif EFFECT==2
    uvec4 c0,c1,c2,c3;
    FsrEasuCon(c0,c1,c2,c3,sourceSize.x,sourceSize.y,sourceSize.x,sourceSize.y,areaSize.x,areaSize.y);
    vec3 color;FsrEasuF(color,uvec2(position),c0,c1,c2,c3);pixel=vec4(color,1);
#elif EFFECT==3
    uvec4 c;FsrRcasCon(c,.35);
    vec3 color;FsrRcasF(color.r,color.g,color.b,uvec2(position),c);pixel=vec4(color,1);
#elif EFFECT==4
    vec2 best=vec2(0);float score=patchError(uv,best);
    for(int level=0;level<3;level++) {
        float stride=level==0?6.:level==1?3.:1.;vec2 center=best;
        for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++) {
            if(x==0 && y==0) continue;
            vec2 offset=center+vec2(x,y)*stride;
            float error=patchError(uv,offset);if(error<score) {score=error;best=offset;}
        }
    }
    pixel=vec4(best,score/9.,1);
#elif EFFECT==5
    vec3 motion=textureLod(vectors,uv,0).xyz,now=textureLod(frame,uv,0).rgb,before=textureLod(prior,uv,0).rgb;
    if(any(greaterThan(abs(motion.xy),vec2(.5))) && stationaryEdge(uv,now,before)) {pixel=vec4(now,1);return;}
    vec2 shift=motion.xy/sourceSize;
    vec3 a=textureLod(prior,uv-phase*shift,0).rgb,b=textureLod(frame,uv+(1.-phase)*shift,0).rgb;
    pixel=vec4(mix(mix(a,b,phase),phase<.5?before:now,smoothstep(.06,.17,motion.z)),1);
#endif
}
