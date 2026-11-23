#include "Presenter.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <string.h>

/* Exercise the same portable entry point used by an SDL playback host. */
int main(int argc, char **argv)
{
    bool hdr = argc == 3 && strcmp(argv[2], "--hdr10") == 0;
    if (argc != 2 && !hdr) {
        fprintf(stderr, "Pass an SDL driver name and optionally --hdr10.\n");
        return 2;
    }
    const char *driver = argv[1];
    SDL_WindowFlags flags = hdr ? 0 : SDL_WINDOW_HIDDEN;
    if (strcmp(driver, "metal") == 0) flags |= SDL_WINDOW_METAL;
    if (strcmp(driver, "vulkan") == 0) flags |= SDL_WINDOW_VULKAN;
    if (strncmp(driver, "opengl", 6) == 0) flags |= SDL_WINDOW_OPENGL;
    if (!SDL_Init(SDL_INIT_VIDEO)) {
        fprintf(stderr, "%s\n", SDL_GetError());
        return 1;
    }
    SDL_Window *window = SDL_CreateWindow("P5Cast renderer test", 160, 90, flags);
    char error[512] = {0};
    if (hdr && window) {
        int count = 0;
        SDL_DisplayID *displays = SDL_GetDisplays(&count);
        for (int i = 0; displays && i < count; ++i) {
            if (SDL_GetBooleanProperty(SDL_GetDisplayProperties(displays[i]), SDL_PROP_DISPLAY_HDR_ENABLED_BOOLEAN, false)) {
                int pos = SDL_WINDOWPOS_CENTERED_DISPLAY(displays[i]);
                SDL_SetWindowPosition(window, pos, pos);
                SDL_SyncWindow(window);
                break;
            }
        }
        SDL_free(displays);
    }
    void *presenter = window ? p5_present_create_sdl_color(window, driver, hdr, error, sizeof(error)) : NULL;
    int passed = 0;
    if (!presenter) {
        fprintf(stderr, "%s: %s\n", driver, window ? error : SDL_GetError());
        goto done;
    }
    SDL_Renderer *renderer = SDL_GetRenderer(window);
    if (p5_present_output_hdr10(presenter) != hdr || (!hdr && (!renderer || strcmp(SDL_GetRendererName(renderer), driver) != 0))) {
        fprintf(stderr, "Requested driver %s did not own the window.\n", driver);
        goto done;
    }
    /* Odd row padding and a size change exercise pitch handling and texture recreation. */
    unsigned char pixels[60][384];
    for (int frame = 0; frame < 12; ++frame) {
        int width = frame < 6 ? 89 : 71;
        for (int y = 0; y < 60; ++y) {
            for (int x = 0; x < width; ++x) {
                unsigned char *pixel = &pixels[y][x * 4];
                if (hdr) {
                    Uint32 rgb10 = 3u << 30 | (Uint32)(x * 11) | (Uint32)(y * 17) << 10 | (Uint32)(frame * 93) << 20;
                    memcpy(pixel, &rgb10, sizeof(rgb10));
                    continue;
                }
                pixel[0] = (unsigned char)(y * 4);
                pixel[1] = (unsigned char)(x * 2);
                pixel[2] = (unsigned char)(frame * 20);
                pixel[3] = 255;
            }
        }
        SDL_PumpEvents();
        if (!p5_present_frame(presenter, pixels, sizeof(pixels[0]), width, 60, 160, 90, error, sizeof(error))) {
            fprintf(stderr, "%s frame %d: %s\n", driver, frame, error);
            goto done;
        }
    }
    if (p5_present_frame(presenter, pixels, 1, 71, 60, 160, 90, error, sizeof(error))) {
        fprintf(stderr, "Presenter accepted an invalid frame stride.\n");
        goto done;
    }
    p5_present_destroy(presenter);
    presenter = NULL;
    /* Reuse the host window after tearing down a session. */
    presenter = p5_present_create_sdl_color(window, driver, hdr, error, sizeof(error));
    if (!presenter || !p5_present_frame(presenter, pixels, sizeof(pixels[0]), 71, 60, 160, 90, error, sizeof(error))) {
        fprintf(stderr, "%s restart: %s\n", driver, error);
        goto done;
    }
    printf("%s presented 13 %s frames across two sessions.\n", driver, hdr ? "HDR10" : "SDR");
    passed = 1;
done:
    p5_present_destroy(presenter);
    SDL_DestroyWindow(window);
    SDL_Quit();
    return passed ? 0 : 1;
}
