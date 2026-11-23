#include "Presenter.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <string.h>

/* Exercise the same portable entry point used by an SDL playback host. */
int main(int argc, char **argv)
{
    if (argc != 2) {
        fprintf(stderr, "Pass one SDL renderer name.\n");
        return 2;
    }
    const char *driver = argv[1];
    SDL_WindowFlags flags = SDL_WINDOW_HIDDEN;
    if (strcmp(driver, "metal") == 0) flags |= SDL_WINDOW_METAL;
    if (strcmp(driver, "vulkan") == 0) flags |= SDL_WINDOW_VULKAN;
    if (strncmp(driver, "opengl", 6) == 0) flags |= SDL_WINDOW_OPENGL;
    if (!SDL_Init(SDL_INIT_VIDEO)) {
        fprintf(stderr, "%s\n", SDL_GetError());
        return 1;
    }
    SDL_Window *window = SDL_CreateWindow("P5Cast renderer test", 160, 90, flags);
    char error[512] = {0};
    void *presenter = window ? p5_present_create_sdl(window, driver, error, sizeof(error)) : NULL;
    int passed = 0;
    if (!presenter) {
        fprintf(stderr, "%s: %s\n", driver, window ? error : SDL_GetError());
        goto done;
    }
    SDL_Renderer *renderer = SDL_GetRenderer(window);
    if (!renderer || strcmp(SDL_GetRendererName(renderer), driver) != 0) {
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
    presenter = p5_present_create_sdl(window, driver, error, sizeof(error));
    if (!presenter || !p5_present_frame(presenter, pixels, sizeof(pixels[0]), 71, 60, 160, 90, error, sizeof(error))) {
        fprintf(stderr, "%s restart: %s\n", driver, error);
        goto done;
    }
    printf("%s presented 13 frames across two sessions.\n", driver);
    passed = 1;
done:
    p5_present_destroy(presenter);
    SDL_DestroyWindow(window);
    SDL_Quit();
    return passed ? 0 : 1;
}
