#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <cstring>
#include <new>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/imgutils.h>
}

// Windows loads the pinned runtime and its dependencies only from its own directory or System32.
struct Api {
    HMODULE codec{}, util{};
    decltype(&avcodec_find_decoder) find{};
    decltype(&avcodec_alloc_context3) alloc{};
    decltype(&avcodec_open2) open{};
    decltype(&avcodec_free_context) close{};
    decltype(&avcodec_send_packet) send{};
    decltype(&avcodec_receive_frame) receive{};
    decltype(&av_packet_alloc) packetAlloc{};
    decltype(&av_new_packet) packetNew{};
    decltype(&av_packet_free) packetFree{};
    decltype(&av_frame_alloc) frameAlloc{};
    decltype(&av_frame_free) frameFree{};
    AVCodecContext *context{};
    ~Api() {
        if (context && close) close(&context);
        if (codec) FreeLibrary(codec);
        if (util) FreeLibrary(util);
    }
};

struct Picture {
    void *planes[3];
    int stride[3], width, height, depth, fullRange, primaries, transfer, matrix;
};
// Stable bridge result for a damaged access unit which FFmpeg can recover from.
static constexpr int corruptAccessUnit = -3;

template<class T> bool bind(HMODULE module, const char *name, T &fn) {
    fn=reinterpret_cast<T>(GetProcAddress(module,name));
    return fn!=nullptr;
}
extern "C" __declspec(dllexport) Api* __cdecl CreateDecoder(const wchar_t *codecPath,const wchar_t *utilPath,int codecId) {
    if (codecId<1 || codecId>3) return nullptr;
    Api *a=new(std::nothrow) Api();
    if (!a) return nullptr;
    DWORD flags=LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32;
    a->util=LoadLibraryExW(utilPath,nullptr,flags);
    a->codec=LoadLibraryExW(codecPath,nullptr,flags);
    decltype(&avcodec_version) codecVersion=nullptr;
    decltype(&avutil_version) utilVersion=nullptr;
    if (!a->codec || !a->util || !bind(a->codec,"avcodec_version",codecVersion)
        || !bind(a->util,"avutil_version",utilVersion) || codecVersion()!=LIBAVCODEC_VERSION_INT || utilVersion()!=LIBAVUTIL_VERSION_INT
        || !bind(a->codec,"avcodec_find_decoder",a->find) || !bind(a->codec,"avcodec_alloc_context3",a->alloc)
        || !bind(a->codec,"avcodec_open2",a->open) || !bind(a->codec,"avcodec_free_context",a->close)
        || !bind(a->codec,"avcodec_send_packet",a->send) || !bind(a->codec,"avcodec_receive_frame",a->receive)
        || !bind(a->codec,"av_packet_alloc",a->packetAlloc) || !bind(a->codec,"av_new_packet",a->packetNew)
        || !bind(a->codec,"av_packet_free",a->packetFree) || !bind(a->util,"av_frame_alloc",a->frameAlloc)
        || !bind(a->util,"av_frame_free",a->frameFree)) { delete a; return nullptr; }
    const AVCodec *decoder=a->find(codecId==1?AV_CODEC_ID_H264:AV_CODEC_ID_HEVC);
    if (!decoder || !(a->context=a->alloc(decoder))) { delete a; return nullptr; }
    a->context->thread_count=4;
    a->context->thread_type=FF_THREAD_SLICE;
    a->context->flags|=AV_CODEC_FLAG_LOW_DELAY;
    if (a->open(a->context,decoder,nullptr)<0) { delete a; return nullptr; }
    return a;
}
extern "C" __declspec(dllexport) int __cdecl SubmitPacket(Api *a,const uint8_t *bytes,int n) {
    AVPacket *p=a->packetAlloc();
    if (!p) return AVERROR(ENOMEM);
    int err=a->packetNew(p,n);
    if (err>=0) { memcpy(p->data,bytes,n); err=a->send(a->context,p); }
    a->packetFree(&p);
    return err == AVERROR_INVALIDDATA ? corruptAccessUnit : err;
}
extern "C" __declspec(dllexport) int __cdecl NextPicture(Api *a,AVFrame **frame,Picture *pic) {
    AVFrame *f=a->frameAlloc();
    if (!f) return -1;
    int err=a->receive(a->context,f);
    if (err<0) {
        a->frameFree(&f);
        if (err==AVERROR(EAGAIN) || err==AVERROR_EOF) return 0;
        return err==AVERROR_INVALIDDATA ? corruptAccessUnit : -1;
    }
    int depth=f->format==AV_PIX_FMT_YUV420P10LE ? 10 :
        (f->format==AV_PIX_FMT_YUV420P || f->format==AV_PIX_FMT_YUVJ420P ? 8:0);
    if (!depth || f->width<1 || f->height<1 || f->width>4096 || f->height>2160) { a->frameFree(&f); return -2; }
    for(int i=0;i<3;i++){ pic->planes[i]=f->data[i]; pic->stride[i]=f->linesize[i]; }
    pic->width=f->width; pic->height=f->height; pic->depth=depth;
    pic->fullRange=f->color_range==AVCOL_RANGE_JPEG || f->format==AV_PIX_FMT_YUVJ420P;
    pic->primaries=f->color_primaries; pic->transfer=f->color_trc; pic->matrix=f->colorspace;
    *frame=f; return 1;
}
extern "C" __declspec(dllexport) void __cdecl FreePicture(Api *a,AVFrame *p) { a->frameFree(&p); }
extern "C" __declspec(dllexport) void __cdecl DestroyDecoder(Api *a) { delete a; }
