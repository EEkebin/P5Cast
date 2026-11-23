#include "../engine/presentation/Presenter.h"
#include "../engine/presentation/HdrGpu.h"
#include <stdio.h>
#include <string.h>
#ifdef _WIN32
#include <windows.h>
#endif

static unsigned verified_frames;
static unsigned long long verified_pixels;

/* SDL swapchain textures are write-only. Record the production blit again into
   a readable RGB10 target, in the same command buffer as the real present. */
bool p5_test_hdr_submit(SDL_GPUDevice *device, SDL_GPUCommandBuffer *cmd,
                        const SDL_GPUBlitInfo *production, Uint32 sw, Uint32 sh,
                        const void *pixels, int pitch, int width, int height)
{
    SDL_GPUTexture *mirror = NULL;
    SDL_GPUTransferBuffer *download = NULL;
    SDL_GPUFence *fence = NULL;
    bool ok = false;
    if (sw != (Uint32)width || sh != (Uint32)height) {
        SDL_SetError("Expected a one-to-one source and swapchain on this frame.");
        goto done;
    }
    SDL_GPUTextureCreateInfo target = {0};
    target.type = SDL_GPU_TEXTURETYPE_2D;
    target.format = SDL_GPU_TEXTUREFORMAT_R10G10B10A2_UNORM;
    target.usage = SDL_GPU_TEXTUREUSAGE_COLOR_TARGET;
    target.width = sw;
    target.height = sh;
    target.layer_count_or_depth = target.num_levels = 1;
    mirror = SDL_CreateGPUTexture(device, &target);
    if (!mirror) goto done;
    Uint32 stride = (sw * 4u + 255u) & ~255u;
    SDL_GPUTransferBufferCreateInfo buffer = {0};
    buffer.usage = SDL_GPU_TRANSFERBUFFERUSAGE_DOWNLOAD;
    buffer.size = stride * sh;
    download = SDL_CreateGPUTransferBuffer(device, &buffer);
    if (!download) goto done;

    SDL_GPUBlitInfo blit = *production;
    blit.destination.texture = mirror;
    SDL_BlitGPUTexture(cmd, &blit);
    SDL_GPUCopyPass *copy = SDL_BeginGPUCopyPass(cmd);
    if (!copy) goto done;
    SDL_GPUTextureRegion image = {0};
    image.texture = mirror;
    image.w = sw;
    image.h = sh;
    image.d = 1;
    SDL_GPUTextureTransferInfo destination = {0};
    destination.transfer_buffer = download;
    destination.pixels_per_row = stride / 4;
    destination.rows_per_layer = sh;
    SDL_DownloadFromGPUTexture(copy, &image, &destination);
    SDL_EndGPUCopyPass(copy);
    fence = SDL_SubmitGPUCommandBufferAndAcquireFence(cmd);
    cmd = NULL;
    if (!fence || !SDL_WaitForGPUFences(device, true, &fence, 1)) goto done;
    const Uint8 *actual = SDL_MapGPUTransferBuffer(device, download, false);
    if (!actual) goto done;
    ok = true;
    for (Uint32 y = 0; y < sh; ++y) {
        const Uint32 *expected = (const Uint32 *)((const Uint8 *)pixels + (size_t)y * pitch);
        const Uint32 *row = (const Uint32 *)(actual + (size_t)y * stride);
        for (Uint32 x = 0; x < sw; ++x) {
            if (expected[x] != row[x]) {
                ok = SDL_SetError("RGB10 mismatch at (%u,%u): expected %08x, got %08x",
                                  x, y, expected[x], row[x]);
                break;
            }
        }
        if (!ok) break;
    }
    SDL_UnmapGPUTransferBuffer(device, download);
    if (ok) {
        ++verified_frames;
        verified_pixels += (unsigned long long)sw * sh;
    }
done:
    if (cmd) SDL_SubmitGPUCommandBuffer(cmd);
    if (fence) SDL_ReleaseGPUFence(device, fence);
    if (download) SDL_ReleaseGPUTransferBuffer(device, download);
    if (mirror) SDL_ReleaseGPUTexture(device, mirror);
    return ok;
}

static SDL_DisplayID hdr_display(void)
{
    int count = 0;
    SDL_DisplayID *ids = SDL_GetDisplays(&count), selected = 0;
    for (int i = 0; i < count; ++i) {
        if (SDL_GetBooleanProperty(SDL_GetDisplayProperties(ids[i]), SDL_PROP_DISPLAY_HDR_ENABLED_BOOLEAN, false)) {
            selected = ids[i];
            break;
        }
    }
    SDL_free(ids);
    return selected;
}

static bool check_frame(SDL_Window *window, void *presenter, int frame)
{
    int width, height;
    if (!SDL_GetWindowSizeInPixels(window, &width, &height)) return false;
    if (width <= 0 || width > 4096 || height <= 0 || height > 4096) return SDL_SetError("Unexpected probe surface size.");
    int pitch = (width + 5) * 4;
    Uint8 *pixels = SDL_malloc((size_t)pitch * height);
    if (!pixels) return SDL_OutOfMemory();
    memset(pixels, 0xA5, (size_t)pitch * height);
    for (int y = 0; y < height; ++y) {
        Uint32 *row = (Uint32 *)(pixels + (size_t)y * pitch);
        for (int x = 0; x < width; ++x) {
            Uint32 r = (x * 13 + y + frame) & 1023;
            Uint32 g = (x + y * 7 + frame * 29) & 1023;
            Uint32 b = (x * 3 + y * 17 + frame * 47) & 1023;
            row[x] = r | (g << 10) | (b << 20) | (3u << 30);
        }
    }
    char error[512] = {0};
    bool ok = p5_present_frame(presenter, pixels, pitch, width, height, width, height, error, sizeof(error)) != 0;
    SDL_free(pixels);
    if (!ok) return SDL_SetError("%s", error);
    if (!p5_present_output_hdr10(presenter)) return SDL_SetError("Lost the HDR10 swapchain.");
    return true;
}

int main(int argc, char **argv)
{
    if (argc != 2) { fprintf(stderr, "Pass one SDL GPU driver name.\n"); return 2; }
#ifdef _WIN32
    char name[64];
    snprintf(name, sizeof(name), "P5Cast-HDR-probe-%lu", GetCurrentProcessId());
    HDESK desktop = CreateDesktopA(name, NULL, NULL, 0, GENERIC_ALL, NULL);
    if (!desktop || !SetThreadDesktop(desktop)) { fprintf(stderr, "Could not create the private test desktop.\n"); return 1; }
#endif
    if (!SDL_Init(SDL_INIT_VIDEO)) { fprintf(stderr, "%s\n", SDL_GetError()); return 1; }
    int result = 1;
    SDL_Window *window = NULL;
    void *presenter = NULL;
    char error[512] = {0};
    SDL_DisplayID display = hdr_display();
    if (!display) { fprintf(stderr, "Enable HDR on a connected display before running this test.\n"); goto done; }
    SDL_Rect rect;
    if (!SDL_GetDisplayBounds(display, &rect)) goto done;
    SDL_WindowFlags flags = SDL_WINDOW_RESIZABLE;
    if (!strcmp(argv[1], "vulkan")) flags |= SDL_WINDOW_VULKAN;
    if (!strcmp(argv[1], "metal")) flags |= SDL_WINDOW_METAL;
    window = SDL_CreateWindow("P5Cast HDR transport check", 257, 83, flags);
    if (!window || !SDL_SetWindowPosition(window, rect.x + 120, rect.y + 120) || !SDL_SyncWindow(window)) goto done;
    if (SDL_GetDisplayForWindow(window) != display) { SDL_SetError("Window is outside the active HDR monitor."); goto done; }
    presenter = p5_present_create_sdl_color(window, argv[1], 1, error, sizeof(error));
    if (!presenter) { SDL_SetError("%s", error); goto done; }
    for (int i = 0; i < 4; ++i) {
        if (i == 2 && (!SDL_SetWindowSize(window, 383, 129) || !SDL_SyncWindow(window))) goto done;
        SDL_PumpEvents();
        if (!check_frame(window, presenter, i)) goto done;
    }
    if (verified_frames != 4) { SDL_SetError("Expected four submitted, non-occluded HDR frames."); goto done; }
    printf("%s on %s: ST2084, R10G10B10A2, %llu exact pixels across %u frames and a resize.\n",
           argv[1], SDL_GetDisplayName(display), verified_pixels, verified_frames);
    result = 0;
done:
    if (result) fprintf(stderr, "%s\n", SDL_GetError());
    p5_present_destroy(presenter);
    SDL_DestroyWindow(window);
    SDL_Quit();
    return result;
}
