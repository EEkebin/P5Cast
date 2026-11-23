#ifndef P5CAST_HDR_GPU_H
#define P5CAST_HDR_GPU_H
#include <SDL3/SDL.h>

typedef struct HdrGpu HdrGpu;
HdrGpu *hdr_gpu_create(SDL_Window *window, const char *driver);
bool hdr_gpu_output_valid(HdrGpu *gpu);
bool hdr_gpu_frame(HdrGpu *gpu, const void *pixels, int pitch, int width, int height);
void hdr_gpu_destroy(HdrGpu *gpu);
#ifdef P5CAST_VERIFY_HDR
/* GPU regression build mirrors the final blit to a readable target before
   submitting. Swapchain images are write-only on the SDL GPU API. */
bool p5_test_hdr_submit(SDL_GPUDevice *device, SDL_GPUCommandBuffer *cmd, const SDL_GPUBlitInfo *blit,
                        Uint32 surface_width, Uint32 surface_height, const void *pixels,
                        int pitch, int width, int height);
#endif
#endif
