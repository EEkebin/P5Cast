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
typedef const char *(WINAPI *WglExtensions)(HDC);
#define GL_FUNCTIONS(X) \
 X(CreateShader,PFNGLCREATESHADERPROC) X(ShaderSource,PFNGLSHADERSOURCEPROC) X(CompileShader,PFNGLCOMPILESHADERPROC) \
 X(GetShaderiv,PFNGLGETSHADERIVPROC) X(GetShaderInfoLog,PFNGLGETSHADERINFOLOGPROC) X(DeleteShader,PFNGLDELETESHADERPROC) \
 X(CreateProgram,PFNGLCREATEPROGRAMPROC) X(AttachShader,PFNGLATTACHSHADERPROC) X(LinkProgram,PFNGLLINKPROGRAMPROC) \
 X(GetProgramiv,PFNGLGETPROGRAMIVPROC) X(GetProgramInfoLog,PFNGLGETPROGRAMINFOLOGPROC) X(DeleteProgram,PFNGLDELETEPROGRAMPROC) \
 X(UseProgram,PFNGLUSEPROGRAMPROC) X(GetUniformLocation,PFNGLGETUNIFORMLOCATIONPROC) X(Uniform1f,PFNGLUNIFORM1FPROC) X(Uniform2f,PFNGLUNIFORM2FPROC) \
 X(GenFramebuffers,PFNGLGENFRAMEBUFFERSPROC) X(BindFramebuffer,PFNGLBINDFRAMEBUFFERPROC) X(FramebufferTexture2D,PFNGLFRAMEBUFFERTEXTURE2DPROC) \
 X(BlitFramebuffer,PFNGLBLITFRAMEBUFFERPROC) X(CheckFramebufferStatus,PFNGLCHECKFRAMEBUFFERSTATUSPROC) X(DeleteFramebuffers,PFNGLDELETEFRAMEBUFFERSPROC) X(ActiveTexture,PFNGLACTIVETEXTUREPROC)
typedef struct {
    IUnknown *resource;
    GLuint name;
    HANDLE handle;
    int width,height;
    uint64_t used;
} SharedTexture;

typedef struct {
    SDL_Window *window;
    HWND child;
    SDL_GLContext context;
    SDL_Window *priorWindow;
    SDL_GLContext priorContext;
    HGLRC priorWgl;
    HDC priorDC;
    GLuint fbo,program[6];
    GLint sourceSize[6],areaSize[6],origin[6],phase[6];
    uint64_t clock;
    HANDLE device;
    int windowWidth,windowHeight;
    bool visible;
    IUnknown *presentedResource;
    SharedTexture shared[16];
    DXOPEN dxOpen;DXCLOSE dxClose;DXREGISTER dxRegister;DXUNREGISTER dxUnregister;DXLOCK dxLock,dxUnlock;
#define FIELD(name,type) type name;
    GL_FUNCTIONS(FIELD)
#undef FIELD
} GlEffects;

static void restore_wgl(HDC dc,HGLRC context) {
    if(wglGetCurrentContext()!=context || wglGetCurrentDC()!=dc) wglMakeCurrent(dc,context);
}
static bool enter(GlEffects *g) {
    g->priorWgl=wglGetCurrentContext();g->priorDC=wglGetCurrentDC();
    g->priorWindow=SDL_GL_GetCurrentWindow();g->priorContext=SDL_GL_GetCurrentContext();
    if(SDL_GL_MakeCurrent(g->window,g->context)) return true;
    restore_wgl(g->priorDC,g->priorWgl);
    return false;
}
static void leave(GlEffects *g) {
    SDL_GL_MakeCurrent(g->priorWindow,g->priorContext);
    /* SDL tracks only contexts bound through SDL. Windows hosts and overlays
     * may have left their own native WGL context current on this thread. */
    restore_wgl(g->priorDC,g->priorWgl);
}
static void release_texture(GlEffects *g,SharedTexture *t) {
    if(t->handle) g->dxUnregister(g->device,t->handle);
    if(t->name) glDeleteTextures(1,&t->name);
    if(t->resource) IUnknown_Release(t->resource);
    memset(t,0,sizeof(*t));
}
static void destroy(GlEffects *g,bool current) {
    if(!g) return;
    if(g->context) {
        for(int i=0;i<16;i++) {
            /* Destroying a lost context also destroys its GL names. Never
             * delete a name on an unrelated current context. */
            if(!current) g->shared[i].name=0;
            release_texture(g,&g->shared[i]);
        }
        if(g->device) g->dxClose(g->device);
        if(current) {
            for(int i=0;i<6;i++) if(g->program[i]) g->DeleteProgram(g->program[i]);
            if(g->fbo) g->DeleteFramebuffers(1,&g->fbo);
            leave(g);
        }
        SDL_GL_DestroyContext(g->context);
    }
    if(g->window) SDL_DestroyWindow(g->window);
    if(g->child) DestroyWindow(g->child);
    SDL_QuitSubSystem(SDL_INIT_VIDEO);
    restore_wgl(g->priorDC,g->priorWgl);
    SDL_free(g);
}
P5_API int p5_gl_shared_textures(const GlEffects *g) {
    int count=0;
    for(int i=0;i<16;i++) if(g->shared[i].handle) ++count;
    return count;
}
P5_API void p5_gl_destroy(GlEffects *g) {if(g) destroy(g,enter(g));}
static GLuint compile(GlEffects *g,GLenum kind,const char *prefix,const char *body,char *error,int length) {
    GLuint shader=g->CreateShader(kind);
    const char *pieces[]={prefix,body};
    g->ShaderSource(shader,2,pieces,NULL);g->CompileShader(shader);
    GLint ok=0;g->GetShaderiv(shader,GL_COMPILE_STATUS,&ok);
    if(!ok) {g->GetShaderInfoLog(shader,length,NULL,error);g->DeleteShader(shader);return 0;}
    return shader;
}
SDL_Window *p5_gl_hidden_child(HWND parent,HWND *child);
static bool supports_d3d11_interop(void) {
    WglExtensions query=(WglExtensions)SDL_GL_GetProcAddress("wglGetExtensionsStringARB");
    const char *extensions=query ? query(wglGetCurrentDC()) : NULL;
    const char name[]="WGL_NV_DX_interop2";
    if(!extensions) return false;
    const char *next=extensions;
    while((next=strstr(next,name))!=NULL) {
        const char *end=next+sizeof(name)-1;
        if((next==extensions || next[-1]==' ') && (!*end || *end==' ')) return true;
        next=end;
    }
    return false;
}
P5_API GlEffects *p5_gl_create(HWND parent,ID3D11Device *device,const char *body,char *error,int length) {
    HGLRC priorWgl=wglGetCurrentContext();
    HDC priorDC=wglGetCurrentDC();
    if(!SDL_InitSubSystem(SDL_INIT_VIDEO)) {SDL_strlcpy(error,SDL_GetError(),length);restore_wgl(priorDC,priorWgl);return NULL;}
    GlEffects *g=SDL_calloc(1,sizeof(*g));
    if(!g) {SDL_QuitSubSystem(SDL_INIT_VIDEO);restore_wgl(priorDC,priorWgl);return NULL;}
    g->priorWgl=priorWgl;g->priorDC=priorDC;
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
    if(supports_d3d11_interop() && g->dxOpen && g->dxClose && g->dxRegister && g->dxUnregister && g->dxLock && g->dxUnlock)
        g->device=g->dxOpen(device);
    if(!g->device) {
        SDL_strlcpy(error,"The OpenGL driver cannot share this playback adapter's GPU textures (WGL_NV_DX_interop2).",length);
        goto fail;
    }
    SDL_GL_SetSwapInterval(0); /* Frame pacing is controlled by the player. */
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
    destroy(g,g->context!=NULL);return NULL;
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
    slot->handle=g->dxRegister(g->device,resource,slot->name,GL_TEXTURE_2D,1); /* WGL_ACCESS_READ_WRITE_NV */
    if(!slot->handle){release_texture(g,slot);return NULL;}
    D3D11_TEXTURE2D_DESC description;
    ID3D11Texture2D_GetDesc((ID3D11Texture2D *)resource,&description);
    slot->width=(int)description.Width;
    slot->height=(int)description.Height;
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
    else ok=1;
unlock:
    g->FramebufferTexture2D(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,0,0);
    if(count && !g->dxUnlock(g->device,count,handles)) {SDL_strlcpy(error,"OpenGL could not release shared frame textures.",length);ok=0;}
done:
    leave(g);return ok;
}

P5_API int p5_gl_resize(GlEffects *g,int width,int height,char *error,int length) {
    if(width<=0 || height<=0) { SDL_strlcpy(error,"Invalid OpenGL window size.",length);return 0; }
    if(width==g->windowWidth && height==g->windowHeight) return 1;
    if(!SetWindowPos(g->child,NULL,0,0,width,height,SWP_NOMOVE|SWP_NOZORDER|SWP_NOACTIVATE)) {
        SDL_strlcpy(error,"Could not resize the OpenGL playback window.",length);return 0;
    }
    g->windowWidth=width;
    g->windowHeight=height;
    return 1;
}

/* The D3D texture includes the player's overlays. Bind it directly as the GL read
 * framebuffer and blit into the WGL back buffer. WGL texture row zero holds the
 * top video scanline; the window framebuffer numbers rows from the bottom. */
P5_API int p5_gl_present(GlEffects *g,IUnknown *resource,const int *sampleXY,unsigned char *sample,
                         char *error,int length) {
    if(!g->windowWidth || !g->windowHeight || !resource) {
        SDL_strlcpy(error,"Missing OpenGL video frame or window size.",length);return 0;
    }
    if(!enter(g)) {SDL_strlcpy(error,SDL_GetError(),length);return 0;}
    int ok=0;
    if(g->presentedResource && g->presentedResource!=resource) {
        for(int i=0;i<16;i++) if(g->shared[i].resource==g->presentedResource) {
            release_texture(g,&g->shared[i]);break;
        }
        g->presentedResource=NULL;
    }
    SharedTexture *texture=share(g,resource);
    if(!texture || !g->dxLock(g->device,1,&texture->handle)) {
        SDL_strlcpy(error,"Could not share the finished frame with the OpenGL window.",length);goto done;
    }
    g->BindFramebuffer(GL_DRAW_FRAMEBUFFER,0);
    g->BindFramebuffer(GL_READ_FRAMEBUFFER,g->fbo);
    g->FramebufferTexture2D(GL_READ_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,texture->name,0);
    if(g->CheckFramebufferStatus(GL_READ_FRAMEBUFFER)!=GL_FRAMEBUFFER_COMPLETE) {
        SDL_strlcpy(error,"OpenGL could not read the shared presentation texture.",length);goto unlock;
    }
    int width=g->windowWidth,height=g->windowHeight;
    if((int64_t)width*texture->height>(int64_t)height*texture->width)
        width=SDL_max(1,(int)((int64_t)height*texture->width/texture->height));
    else
        height=SDL_max(1,(int)((int64_t)width*texture->height/texture->width));
    int left=(g->windowWidth-width)/2, bottom=(g->windowHeight-height)/2;
    if(width!=g->windowWidth || height!=g->windowHeight) {
        glClearColor(0,0,0,1);
        glClear(GL_COLOR_BUFFER_BIT);
    }
    g->BlitFramebuffer(0,0,texture->width,texture->height,left,bottom+height,left+width,bottom,
                       GL_COLOR_BUFFER_BIT,GL_LINEAR);
    if(glGetError()!=GL_NO_ERROR) {
        SDL_strlcpy(error,"OpenGL could not blit into the window back buffer.",length);goto unlock;
    }
    g->presentedResource=resource;
    ok=1;
unlock:
    g->FramebufferTexture2D(GL_READ_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,0,0);
    g->BindFramebuffer(GL_FRAMEBUFFER,0);
    if(!g->dxUnlock(g->device,1,&texture->handle)) {
        SDL_strlcpy(error,"OpenGL could not release the presentation texture.",length);ok=0;
    }
    if(ok) {
        /* Only the pixel-verification harness supplies sample coordinates. */
        if(sampleXY && sample) {
            glReadBuffer(GL_BACK);
            glReadPixels(sampleXY[0],g->windowHeight-1-sampleXY[1],1,1,GL_RGBA,GL_UNSIGNED_BYTE,sample);
            if(glGetError()!=GL_NO_ERROR) {
                SDL_strlcpy(error,"OpenGL back-buffer pixel verification failed.",length);ok=0;
            }
        }
        if(ok && !SDL_GL_SwapWindow(g->window)) {SDL_strlcpy(error,SDL_GetError(),length);ok=0;}
        if(ok && !g->visible) {ShowWindow(g->child,SW_SHOWNA);g->visible=true;}
    }
done:
    leave(g);return ok;
}
#endif
