/* Native OpenGL picture and motion passes. Windows capture/decoder surfaces are
 * synchronized against the GL driver with WGL_NV_DX_interop2 on the render thread. */
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#define COBJMACROS
#include <d3d11.h>
#include <SDL3/SDL.h>
#include <SDL3/SDL_opengl.h>
#include <SDL3/SDL_opengl_glext.h>
#include <string.h>
#include <stdint.h>
#define P5CAST_PRESENTER_BUILD 1
#include "Presenter.h"

typedef HANDLE (WINAPI *DXOPEN)(void *);
typedef BOOL (WINAPI *DXCLOSE)(HANDLE);
typedef HANDLE (WINAPI *DXREGISTER)(HANDLE,void *,GLuint,GLenum,GLenum);
typedef BOOL (WINAPI *DXUNREGISTER)(HANDLE,HANDLE);
typedef BOOL (WINAPI *DXLOCK)(HANDLE,GLint,HANDLE *);
#define GL_FUNCTIONS(X) \
 X(CreateShader,PFNGLCREATESHADERPROC) X(ShaderSource,PFNGLSHADERSOURCEPROC) X(CompileShader,PFNGLCOMPILESHADERPROC) \
 X(GetShaderiv,PFNGLGETSHADERIVPROC) X(GetShaderInfoLog,PFNGLGETSHADERINFOLOGPROC) X(DeleteShader,PFNGLDELETESHADERPROC) \
 X(CreateProgram,PFNGLCREATEPROGRAMPROC) X(AttachShader,PFNGLATTACHSHADERPROC) X(LinkProgram,PFNGLLINKPROGRAMPROC) \
 X(GetProgramiv,PFNGLGETPROGRAMIVPROC) X(GetProgramInfoLog,PFNGLGETPROGRAMINFOLOGPROC) X(DeleteProgram,PFNGLDELETEPROGRAMPROC) \
 X(UseProgram,PFNGLUSEPROGRAMPROC) X(GetUniformLocation,PFNGLGETUNIFORMLOCATIONPROC) X(Uniform1f,PFNGLUNIFORM1FPROC) X(Uniform2f,PFNGLUNIFORM2FPROC) \
 X(GenFramebuffers,PFNGLGENFRAMEBUFFERSPROC) X(BindFramebuffer,PFNGLBINDFRAMEBUFFERPROC) X(FramebufferTexture2D,PFNGLFRAMEBUFFERTEXTURE2DPROC) \
 X(CheckFramebufferStatus,PFNGLCHECKFRAMEBUFFERSTATUSPROC) X(DeleteFramebuffers,PFNGLDELETEFRAMEBUFFERSPROC) X(ActiveTexture,PFNGLACTIVETEXTUREPROC)
typedef struct {
    IUnknown *resource;
    GLuint name;
    HANDLE handle;
    ID3D11Texture2D *staging;
    GLenum format,type;
    unsigned width,height,bytes;
    uint64_t used;
} SharedTexture;

typedef struct {
    SDL_Window *window;
    HWND child;
    SDL_GLContext context;
    SDL_Window *priorWindow;
    SDL_GLContext priorContext;
    GLuint fbo,program[6];
    GLint sourceSize[6],areaSize[6],origin[6],phase[6];
    uint64_t clock;
    HANDLE device;
    ID3D11Device *d3d;
    ID3D11DeviceContext *immediate;
    SharedTexture shared[16];
    DXOPEN dxOpen;DXCLOSE dxClose;DXREGISTER dxRegister;DXUNREGISTER dxUnregister;DXLOCK dxLock,dxUnlock;
#define FIELD(name,type) type name;
    GL_FUNCTIONS(FIELD)
#undef FIELD
} GlEffects;

static bool enter(GlEffects *g) {
    g->priorWindow=SDL_GL_GetCurrentWindow();g->priorContext=SDL_GL_GetCurrentContext();
    return SDL_GL_MakeCurrent(g->window,g->context);
}
static void leave(GlEffects *g) { SDL_GL_MakeCurrent(g->priorWindow,g->priorContext); }
static void release_texture(GlEffects *g,SharedTexture *t) {
    if(t->handle) g->dxUnregister(g->device,t->handle);
    if(t->name) glDeleteTextures(1,&t->name);
    if(t->staging) ID3D11Texture2D_Release(t->staging);
    if(t->resource) IUnknown_Release(t->resource);
    memset(t,0,sizeof(*t));
}
static void destroy(GlEffects *g) {
    if(!g) return;
    if(g->context) {
        for(int i=0;i<16;i++) release_texture(g,&g->shared[i]);
        if(g->device) g->dxClose(g->device);
        for(int i=0;i<6;i++) if(g->program[i]) g->DeleteProgram(g->program[i]);
        if(g->fbo) g->DeleteFramebuffers(1,&g->fbo);
        leave(g);
        SDL_GL_DestroyContext(g->context);
    }
    if(g->window) SDL_DestroyWindow(g->window);
    if(g->child) DestroyWindow(g->child);
    if(g->immediate) ID3D11DeviceContext_Release(g->immediate);
    SDL_free(g);
    SDL_QuitSubSystem(SDL_INIT_VIDEO);
}
P5_API int p5_gl_shared_textures(const GlEffects *g) {
    int count=0;
    for(int i=0;i<16;i++) if(g->shared[i].handle) ++count;
    return count;
}
P5_API void p5_gl_destroy(GlEffects *g) {if(g && enter(g)) destroy(g);}
static GLuint compile(GlEffects *g,GLenum kind,const char *prefix,const char *body,char *error,int length) {
    GLuint shader=g->CreateShader(kind);
    const char *pieces[]={prefix,body};
    g->ShaderSource(shader,2,pieces,NULL);g->CompileShader(shader);
    GLint ok=0;g->GetShaderiv(shader,GL_COMPILE_STATUS,&ok);
    if(!ok) {g->GetShaderInfoLog(shader,length,NULL,error);g->DeleteShader(shader);return 0;}
    return shader;
}
SDL_Window *p5_gl_hidden_child(HWND parent,HWND *child);
P5_API GlEffects *p5_gl_create(HWND parent,ID3D11Device *device,const char *body,int allowSharing,char *error,int length) {
    if(!SDL_InitSubSystem(SDL_INIT_VIDEO)) {SDL_strlcpy(error,SDL_GetError(),length);return NULL;}
    GlEffects *g=SDL_calloc(1,sizeof(*g));
    if(!g) {SDL_QuitSubSystem(SDL_INIT_VIDEO);return NULL;}
    g->d3d=device;
    ID3D11Device_GetImmediateContext(device,&g->immediate);
    g->priorWindow=SDL_GL_GetCurrentWindow();g->priorContext=SDL_GL_GetCurrentContext();
    g->window=p5_gl_hidden_child(parent,&g->child);
    if(!g->window || !(g->context=SDL_GL_CreateContext(g->window))) {SDL_strlcpy(error,SDL_GetError(),length);goto fail;}
#define LOAD(name,type) g->name=(type)SDL_GL_GetProcAddress("gl"#name);if(!g->name){SDL_strlcpy(error,"OpenGL is missing gl"#name,length);goto fail;}
    GL_FUNCTIONS(LOAD)
#undef LOAD
    g->dxOpen=(DXOPEN)SDL_GL_GetProcAddress("wglDXOpenDeviceNV");
    g->dxClose=(DXCLOSE)SDL_GL_GetProcAddress("wglDXCloseDeviceNV");
    g->dxRegister=(DXREGISTER)SDL_GL_GetProcAddress("wglDXRegisterObjectNV");
    g->dxUnregister=(DXUNREGISTER)SDL_GL_GetProcAddress("wglDXUnregisterObjectNV");
    g->dxLock=(DXLOCK)SDL_GL_GetProcAddress("wglDXLockObjectsNV");
    g->dxUnlock=(DXLOCK)SDL_GL_GetProcAddress("wglDXUnlockObjectsNV");
    if(allowSharing && g->dxOpen && g->dxClose && g->dxRegister && g->dxUnregister && g->dxLock && g->dxUnlock)
        g->device=g->dxOpen(device);
    GLuint vertex=compile(g,GL_VERTEX_SHADER,"#version 430\n","void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.-1.,0,1);}",error,length);
    if(!vertex) goto fail;
    for(int i=0;i<6;i++) {
        char prefix[64];SDL_snprintf(prefix,sizeof(prefix),"#version 430\n#define EFFECT %d\n",i);
        GLuint fragment=compile(g,GL_FRAGMENT_SHADER,prefix,body,error,length);
        if(!fragment) {g->DeleteShader(vertex);goto fail;}
        GLuint p=g->program[i]=g->CreateProgram();
        g->AttachShader(p,vertex);g->AttachShader(p,fragment);g->LinkProgram(p);
        g->DeleteShader(fragment);
        GLint ok=0;g->GetProgramiv(p,GL_LINK_STATUS,&ok);
        if(!ok){g->GetProgramInfoLog(p,length,NULL,error);g->DeleteShader(vertex);goto fail;}
        g->sourceSize[i]=g->GetUniformLocation(p,"sourceSize");g->origin[i]=g->GetUniformLocation(p,"areaOrigin");
        g->areaSize[i]=g->GetUniformLocation(p,"areaSize");g->phase[i]=g->GetUniformLocation(p,"phase");
    }
    g->DeleteShader(vertex);
    g->GenFramebuffers(1,&g->fbo);
    leave(g);return g;
fail:
    destroy(g);return NULL;
}
/* When WGL interop is unavailable (or the driver rejects one texture),
 * transfer only that resource through host-visible D3D staging. Effects
 * still execute on the selected GL device. */
static bool allocate_transfer(GlEffects *g,SharedTexture *t,IUnknown *resource) {
    D3D11_TEXTURE2D_DESC desc;
    ID3D11Texture2D_GetDesc((ID3D11Texture2D *)resource,&desc);
    GLenum internal;
    switch(desc.Format) {
        case DXGI_FORMAT_B8G8R8A8_UNORM:internal=GL_RGBA8;t->format=GL_BGRA;t->type=GL_UNSIGNED_BYTE;t->bytes=4;break;
        case DXGI_FORMAT_R8G8B8A8_UNORM:internal=GL_RGBA8;t->format=GL_RGBA;t->type=GL_UNSIGNED_BYTE;t->bytes=4;break;
        case DXGI_FORMAT_R16G16B16A16_FLOAT:internal=GL_RGBA16F;t->format=GL_RGBA;t->type=GL_HALF_FLOAT;t->bytes=8;break;
        default:return false;
    }
    t->width=desc.Width;t->height=desc.Height;
    desc.Usage=D3D11_USAGE_STAGING;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ|D3D11_CPU_ACCESS_WRITE;
    desc.BindFlags=0;desc.MiscFlags=0;
    if(FAILED(ID3D11Device_CreateTexture2D(g->d3d,&desc,NULL,&t->staging))) return false;
    glBindTexture(GL_TEXTURE_2D,t->name);
    glTexImage2D(GL_TEXTURE_2D,0,internal,t->width,t->height,0,t->format,t->type,NULL);
    return glGetError()==GL_NO_ERROR;
}
static bool upload(GlEffects *g,SharedTexture *t) {
    D3D11_MAPPED_SUBRESOURCE map;
    ID3D11DeviceContext_CopyResource(g->immediate,(ID3D11Resource *)t->staging,(ID3D11Resource *)t->resource);
    if(FAILED(ID3D11DeviceContext_Map(g->immediate,(ID3D11Resource *)t->staging,0,D3D11_MAP_READ,0,&map))) return false;
    glBindTexture(GL_TEXTURE_2D,t->name);
    glPixelStorei(GL_UNPACK_ROW_LENGTH,map.RowPitch/t->bytes);
    glTexSubImage2D(GL_TEXTURE_2D,0,0,0,t->width,t->height,t->format,t->type,map.pData);
    glPixelStorei(GL_UNPACK_ROW_LENGTH,0);
    ID3D11DeviceContext_Unmap(g->immediate,(ID3D11Resource *)t->staging,0);
    return glGetError()==GL_NO_ERROR;
}
static bool download(GlEffects *g,SharedTexture *t,int x,int y,int w,int h,const int *clip) {
    int r=x+w,b=y+h;
    if(clip) {x=SDL_max(x,clip[0]);y=SDL_max(y,clip[1]);r=SDL_min(r,clip[0]+clip[2]);b=SDL_min(b,clip[1]+clip[3]);}
    if(r<=x || b<=y) return true;
    D3D11_MAPPED_SUBRESOURCE map;
    if(FAILED(ID3D11DeviceContext_Map(g->immediate,(ID3D11Resource *)t->staging,0,D3D11_MAP_WRITE,0,&map))) return false;
    glPixelStorei(GL_PACK_ROW_LENGTH,map.RowPitch/t->bytes);
    glReadPixels(x,y,r-x,b-y,t->format,t->type,(uint8_t *)map.pData+y*map.RowPitch+x*t->bytes);
    glPixelStorei(GL_PACK_ROW_LENGTH,0);
    ID3D11DeviceContext_Unmap(g->immediate,(ID3D11Resource *)t->staging,0);
    if(glGetError()!=GL_NO_ERROR) return false;
    D3D11_BOX region={x,y,0,r,b,1};
    ID3D11DeviceContext_CopySubresourceRegion(g->immediate,(ID3D11Resource *)t->resource,0,x,y,0,(ID3D11Resource *)t->staging,0,&region);
    return true;
}
static SharedTexture *share(GlEffects *g,IUnknown *resource) {
    SharedTexture *slot=&g->shared[0];
    for(int i=0;i<16;i++) {
        SharedTexture *t=&g->shared[i];
        if(t->resource==resource){t->used=++g->clock;return t;}
        if(t->used<slot->used) slot=t;
    }
    release_texture(g,slot);
    glGenTextures(1,&slot->name);
    if(g->device) slot->handle=g->dxRegister(g->device,resource,slot->name,GL_TEXTURE_2D,1); /* WGL_ACCESS_READ_WRITE_NV */
    if(!slot->handle && !allocate_transfer(g,slot,resource)){release_texture(g,slot);return NULL;}
    IUnknown_AddRef(resource);slot->resource=resource;slot->used=++g->clock;
    return slot;
}
P5_API int p5_gl_draw(GlEffects *g,int effect,IUnknown *output,IUnknown *input,IUnknown *prior,IUnknown *flow,
                      int sx,int sy,int x,int y,int w,int h,const int *clip,float phase,char *error,int length) {
    if(!enter(g)){SDL_strlcpy(error,SDL_GetError(),length);return 0;}
    IUnknown *resources[]={output,input,prior,flow};
    SharedTexture *textures[4]={0};
    HANDLE handles[4];
    int count=0,ok=0;
    for(int i=0;i<4;i++) if(resources[i]) {
        textures[i]=share(g,resources[i]);
        if(!textures[i]) {SDL_snprintf(error,length,"Cannot register playback texture %d with OpenGL (%lu).",i,GetLastError());goto done;}
        bool duplicate=false;
        for(int j=0;j<count;j++) if(handles[j]==textures[i]->handle) duplicate=true;
        if(textures[i]->handle && !duplicate) handles[count++]=textures[i]->handle;
        if(i>0 && textures[i]->staging && !upload(g,textures[i])) {SDL_strlcpy(error,"OpenGL texture upload failed.",length);goto done;}
    }
    if(count && !g->dxLock(g->device,count,handles)) {SDL_strlcpy(error,"OpenGL could not lock shared frame textures.",length);goto done;}
    g->BindFramebuffer(GL_FRAMEBUFFER,g->fbo);
    g->FramebufferTexture2D(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,textures[0]->name,0);
    if(g->CheckFramebufferStatus(GL_FRAMEBUFFER)!=GL_FRAMEBUFFER_COMPLETE) {
        SDL_strlcpy(error,"OpenGL shared effect framebuffer is incomplete.",length);goto unlock;
    }
    for(int i=1;i<4;i++) if(textures[i]) {
        g->ActiveTexture(GL_TEXTURE0+i-1);glBindTexture(GL_TEXTURE_2D,textures[i]->name);
        glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_LINEAR);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_S,GL_CLAMP_TO_EDGE);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_T,GL_CLAMP_TO_EDGE);
    }
    g->UseProgram(g->program[effect]);
    g->Uniform2f(g->sourceSize[effect],(float)sx,(float)sy);g->Uniform2f(g->areaSize[effect],(float)w,(float)h);
    g->Uniform2f(g->origin[effect],(float)x,(float)y);g->Uniform1f(g->phase[effect],phase);
    glViewport(x,y,w,h);
    if(clip) {glEnable(GL_SCISSOR_TEST);glScissor(clip[0],clip[1],clip[2],clip[3]);}
    glDrawArrays(GL_TRIANGLES,0,3);
    if(clip) glDisable(GL_SCISSOR_TEST);
    GLenum code=glGetError();
    if(code!=GL_NO_ERROR) SDL_snprintf(error,length,"OpenGL effect %d failed (0x%x).",effect,code);
    else if(textures[0]->staging && !download(g,textures[0],x,y,w,h,clip)) SDL_strlcpy(error,"OpenGL texture readback failed.",length);
    else ok=1;
unlock:
    g->FramebufferTexture2D(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,0,0);
    if(count && !g->dxUnlock(g->device,count,handles)) {SDL_strlcpy(error,"OpenGL could not release shared frame textures.",length);ok=0;}
done:
    leave(g);return ok;
}
#endif
