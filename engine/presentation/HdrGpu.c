#include "HdrGpu.h"
#include <limits.h>

/* The decoder pipeline supplies display-referred ST.2084/BT.2020. Preserve
   the packed channels through the upload, blit and presentation surface. */
static const SDL_GPUTextureFormat format = SDL_GPU_TEXTUREFORMAT_R10G10B10A2_UNORM;
static const SDL_GPUSwapchainComposition composition = SDL_GPU_SWAPCHAINCOMPOSITION_HDR10_ST2084;

struct HdrGpu {
    SDL_GPUDevice *device;
    SDL_Window *claimed_window;
    SDL_GPUTransferBuffer *upload;
    SDL_GPUTexture *image;
    Uint32 width, height, aligned_pitch;
};

static bool display_has_hdr(SDL_Window *window)
{
#if defined(SDL_PLATFORM_WINDOWS) || defined(SDL_PLATFORM_MACOS)
    /* DXGI's output mode / Cocoa's EDR headroom. On macOS the graphics
       driver's swapchain-composition capability alone only tests OS support. */
    SDL_DisplayID display = SDL_GetDisplayForWindow(window);
    return display && SDL_GetBooleanProperty(SDL_GetDisplayProperties(display),
                                             SDL_PROP_DISPLAY_HDR_ENABLED_BOOLEAN, false);
#else
    /* Some compositors do not expose output headroom to SDL; their explicit
       surface format / colorspace negotiation is authoritative. */
    (void)window;
    return true;
#endif
}

bool hdr_gpu_output_valid(HdrGpu *gpu)
{
    return gpu && gpu->claimed_window && display_has_hdr(gpu->claimed_window) &&
           SDL_GetGPUSwapchainTextureFormat(gpu->device, gpu->claimed_window) == format;
}

HdrGpu *hdr_gpu_create(SDL_Window *window, const char *driver)
{
    SDL_GPUShaderFormat shaders;
    if (SDL_strcmp(driver, "direct3d12") == 0) shaders = SDL_GPU_SHADERFORMAT_DXBC;
    else if (SDL_strcmp(driver, "vulkan") == 0) shaders = SDL_GPU_SHADERFORMAT_SPIRV;
    else if (SDL_strcmp(driver, "metal") == 0) shaders = SDL_GPU_SHADERFORMAT_METALLIB;
    else {
        SDL_SetError("HDR10 presentation requires Direct3D 12, Vulkan or Metal.");
        return NULL;
    }
    if (!display_has_hdr(window)) {
        SDL_SetError("HDR10 requires a playback display with HDR enabled.");
        return NULL;
    }
    HdrGpu *gpu = SDL_calloc(1, sizeof(*gpu));
    if (!gpu) { SDL_OutOfMemory(); return NULL; }
    gpu->device = SDL_CreateGPUDevice(shaders, false, driver);
    if (!gpu->device) goto failed;
    if (SDL_strcmp(SDL_GetGPUDeviceDriver(gpu->device), driver) != 0) {
        SDL_SetError("Requested HDR10 graphics driver was not selected.");
        goto failed;
    }
    if (!SDL_ClaimWindowForGPUDevice(gpu->device, window)) goto failed;
    gpu->claimed_window = window;
    if (!SDL_WindowSupportsGPUSwapchainComposition(gpu->device, window, composition)) {
        SDL_SetError("The display surface does not support a BT.2020 / ST.2084 HDR10 swap chain.");
        goto failed;
    }
    if (!SDL_SetGPUSwapchainParameters(gpu->device, window, composition, SDL_GPU_PRESENTMODE_VSYNC)) goto failed;
    if (!hdr_gpu_output_valid(gpu)) {
        SDL_SetError("The HDR10 surface did not negotiate the required R10G10B10A2 pixel layout.");
        goto failed;
    }
    return gpu;
failed: {
        char error[512];
        SDL_strlcpy(error, SDL_GetError(), sizeof(error));
        hdr_gpu_destroy(gpu);
        SDL_SetError("%s", error);
        return NULL;
    }
}

static bool resize_inputs(HdrGpu *gpu, Uint32 w, Uint32 h)
{
    /* Native D3D12 and Metal transfers can require 256-byte row alignment. */
    Uint64 pitch = ((Uint64)w * 4 + 255) & ~(Uint64)255;
    if (pitch * h > UINT_MAX) return SDL_SetError("HDR frame exceeds the transfer buffer limit.");
    SDL_GPUTransferBufferCreateInfo buffer = {0};
    buffer.usage = SDL_GPU_TRANSFERBUFFERUSAGE_UPLOAD;
    buffer.size = (Uint32)(pitch * h);
    SDL_GPUTransferBuffer *upload = SDL_CreateGPUTransferBuffer(gpu->device, &buffer);
    if (!upload) return false;
    SDL_GPUTextureCreateInfo texture = {0};
    texture.type = SDL_GPU_TEXTURETYPE_2D;
    texture.format = format;
    texture.usage = SDL_GPU_TEXTUREUSAGE_SAMPLER;
    texture.width = w; texture.height = h;
    texture.layer_count_or_depth = 1;
    texture.num_levels = 1;
    texture.sample_count = SDL_GPU_SAMPLECOUNT_1;
    SDL_GPUTexture *image = SDL_CreateGPUTexture(gpu->device, &texture);
    if (!image) { SDL_ReleaseGPUTransferBuffer(gpu->device, upload); return false; }
    if (gpu->image) SDL_ReleaseGPUTexture(gpu->device, gpu->image);
    if (gpu->upload) SDL_ReleaseGPUTransferBuffer(gpu->device, gpu->upload);
    gpu->image = image; gpu->upload = upload;
    gpu->width = w; gpu->height = h; gpu->aligned_pitch = (Uint32)pitch;
    return true;
}

bool hdr_gpu_frame(HdrGpu *gpu, const void *pixels, int pitch, int width, int height)
{
    Uint32 w = (Uint32)width, h = (Uint32)height;
    if (gpu->width != w || gpu->height != h) {
        if (!resize_inputs(gpu, w, h)) return false;
    }
    Uint8 *mapped = SDL_MapGPUTransferBuffer(gpu->device, gpu->upload, true);
    if (!mapped) return false;
    for (Uint32 y = 0; y < h; ++y)
        SDL_memcpy(mapped + (size_t)y * gpu->aligned_pitch, (const Uint8 *)pixels + (size_t)y * pitch, (size_t)w * 4);
    SDL_UnmapGPUTransferBuffer(gpu->device, gpu->upload);
    SDL_GPUCommandBuffer *cmd = SDL_AcquireGPUCommandBuffer(gpu->device);
    if (!cmd) return false;
    SDL_GPUTexture *surface = NULL;
    Uint32 sw, sh;
    if (!SDL_WaitAndAcquireGPUSwapchainTexture(cmd, gpu->claimed_window, &surface, &sw, &sh)) {
        SDL_CancelGPUCommandBuffer(cmd);
        return false;
    }
    if (!surface) return SDL_SubmitGPUCommandBuffer(cmd); /* Minimized or temporarily occluded. */
    if (!hdr_gpu_output_valid(gpu)) {
        SDL_SubmitGPUCommandBuffer(cmd);
        return SDL_SetError("The playback display or surface lost its 10-bit HDR output.");
    }
    SDL_GPUCopyPass *copy = SDL_BeginGPUCopyPass(cmd);
    if (!copy) { SDL_SubmitGPUCommandBuffer(cmd); return false; }
    SDL_GPUTextureTransferInfo source = {0};
    source.transfer_buffer = gpu->upload;
    source.pixels_per_row = gpu->aligned_pitch / 4;
    source.rows_per_layer = h;
    SDL_GPUTextureRegion image = {0};
    image.texture = gpu->image; image.w = w; image.h = h; image.d = 1;
    SDL_UploadToGPUTexture(copy, &source, &image, true);
    SDL_EndGPUCopyPass(copy);

    SDL_GPUBlitInfo blit = {0};
    blit.source.texture = gpu->image; blit.source.w = w; blit.source.h = h;
    blit.destination.texture = surface;
    double scale = SDL_min((double)sw / w, (double)sh / h);
    blit.destination.w = SDL_clamp((Uint32)(w * scale + 0.5), 1u, sw);
    blit.destination.h = SDL_clamp((Uint32)(h * scale + 0.5), 1u, sh);
    blit.destination.x = (sw - blit.destination.w) / 2;
    blit.destination.y = (sh - blit.destination.h) / 2;
    blit.load_op = SDL_GPU_LOADOP_CLEAR;
    blit.clear_color.a = 1;
    blit.filter = (blit.destination.w == w && blit.destination.h == h) ? SDL_GPU_FILTER_NEAREST : SDL_GPU_FILTER_LINEAR;
    SDL_BlitGPUTexture(cmd, &blit);
#ifdef P5CAST_VERIFY_HDR
    return p5_test_hdr_submit(gpu->device, cmd, &blit, sw, sh, pixels, pitch, width, height);
#else
    return SDL_SubmitGPUCommandBuffer(cmd);
#endif
}

void hdr_gpu_destroy(HdrGpu *gpu)
{
    if (!gpu) return;
    if (gpu->upload) SDL_ReleaseGPUTransferBuffer(gpu->device, gpu->upload);
    if (gpu->image) SDL_ReleaseGPUTexture(gpu->device, gpu->image);
    if (gpu->claimed_window) SDL_ReleaseWindowFromGPUDevice(gpu->device, gpu->claimed_window);
    if (gpu->device) SDL_DestroyGPUDevice(gpu->device);
    SDL_free(gpu);
}
