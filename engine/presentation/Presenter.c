#define P5CAST_PRESENTER_BUILD 1
#include "Presenter.h"
#include "HdrGpu.h"
#include <SDL3/SDL.h>
#include <limits.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

typedef struct Presenter {
    HdrGpu *hdr;
    SDL_Renderer *renderer;
    SDL_Texture *texture;
    int frame_width, frame_height;
#ifdef _WIN32
    SDL_Window *wrapper;
    HWND child;
    int child_width, child_height;
#endif
} Presenter;

static const char *driver_name(const char *driver)
{
    static const char *const names[] = {"direct3d11", "direct3d12", "vulkan", "opengl", "opengles2", "metal"};
    if (!driver) return NULL;
    if (SDL_strcmp(driver, "d3d11") == 0) return "direct3d11";
    if (SDL_strcmp(driver, "d3d12") == 0) return "direct3d12";
    for (size_t i = 0; i < SDL_arraysize(names); ++i) {
        if (SDL_strcmp(driver, names[i]) == 0) return names[i];
    }
    return NULL;
}

int p5_present_available(const char *driver)
{
    const char *name = driver_name(driver);
    if (!name) return 0;
    for (int i = 0; i < SDL_GetNumRenderDrivers(); ++i) {
        if (SDL_strcmp(name, SDL_GetRenderDriver(i)) == 0) return 1;
    }
    return 0;
}

static int fail(char *error, int length, const char *message)
{
    if (error && length > 0) SDL_strlcpy(error, message, (size_t)length);
    return 0;
}

void *p5_present_create_sdl(SDL_Window *window, const char *driver, char *error, int error_length)
{
    return p5_present_create_sdl_color(window, driver, 0, error, error_length);
}

void *p5_present_create_sdl_color(SDL_Window *window, const char *driver, int hdr10, char *error, int error_length)
{
    const char *name = driver_name(driver);
    if (!window || !name) {
        fail(error, error_length, "Expected a playback window and an explicit presentation backend.");
        return NULL;
    }
    if (!hdr10 && !p5_present_available(name)) {
        fail(error, error_length, "This SDL build does not include the requested presentation backend.");
        return NULL;
    }
    Presenter *p = SDL_calloc(1, sizeof(*p));
    if (!p) {
        fail(error, error_length, "Out of memory creating the presenter.");
        return NULL;
    }
    if (hdr10) {
        p->hdr = hdr_gpu_create(window, name);
        if (!p->hdr) {
            fail(error, error_length, SDL_GetError());
            SDL_free(p);
            return NULL;
        }
        return p;
    }
    p->renderer = SDL_CreateRenderer(window, name);
    if (!p->renderer) {
        fail(error, error_length, SDL_GetError());
        SDL_free(p);
        return NULL;
    }
    if (SDL_strcmp(SDL_GetRendererName(p->renderer), name) != 0) {
        fail(error, error_length, "SDL selected a different renderer from the requested backend.");
        p5_present_destroy(p);
        return NULL;
    }
    if (!SDL_SetRenderDrawColor(p->renderer, 0, 0, 0, 255)) {
        fail(error, error_length, SDL_GetError());
        p5_present_destroy(p);
        return NULL;
    }
    return p;
}

#ifdef _WIN32
/* Use a new CS_OWNDC child for each session. A WGL window's pixel format is
   immutable, and the WPF parent also outlives and handles input for this child. */
static const wchar_t child_class[] = L"P5Cast.SDL.Video";

static HWND create_child(HWND parent, int width, int height)
{
    WNDCLASSW wc = {0};
    wc.style = CS_OWNDC;
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(NULL);
    wc.lpszClassName = child_class;
    if (!RegisterClassW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return NULL;
    return CreateWindowExW(WS_EX_NOACTIVATE | WS_EX_NOPARENTNOTIFY, child_class, L"",
                           WS_CHILD | WS_DISABLED | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
                           0, 0, width, height, parent, NULL, wc.hInstance, NULL);
}

static SDL_Window *wrap_child(HWND child, const char *name)
{
    SDL_PropertiesID props = SDL_CreateProperties();
    if (!props) return NULL;
    const char *graphics_flag = NULL;
    if (SDL_strncmp(name, "opengl", 6) == 0) graphics_flag = SDL_PROP_WINDOW_CREATE_OPENGL_BOOLEAN;
    else if (SDL_strcmp(name, "vulkan") == 0) graphics_flag = SDL_PROP_WINDOW_CREATE_VULKAN_BOOLEAN;
    SDL_Window *window = NULL;
    if (SDL_SetPointerProperty(props, SDL_PROP_WINDOW_CREATE_WIN32_HWND_POINTER, child) &&
        (!graphics_flag || SDL_SetBooleanProperty(props, graphics_flag, true))) {
        window = SDL_CreateWindowWithProperties(props);
    }
    SDL_DestroyProperties(props);
    return window;
}
#endif

void *p5_present_create(void *native_parent, const char *driver, int width, int height,
                        char *error, int error_length)
{
    return p5_present_create_color(native_parent, driver, width, height, 0, error, error_length);
}

void *p5_present_create_color(void *native_parent, const char *driver, int width, int height,
                              int hdr10, char *error, int error_length)
{
#ifdef _WIN32
    const char *name = driver_name(driver);
    if (!IsWindow((HWND)native_parent) || width <= 0 || height <= 0 || !name) {
        fail(error, error_length, "Expected a valid playback HWND, size, and presentation backend.");
        return NULL;
    }
    if (!SDL_InitSubSystem(SDL_INIT_VIDEO)) {
        fail(error, error_length, SDL_GetError());
        return NULL;
    }
    HWND child = create_child((HWND)native_parent, width, height);
    SDL_Window *wrapper = NULL;
    Presenter *p = NULL;
    if (child) {
        wrapper = wrap_child(child, name);
        if (wrapper) p = p5_present_create_sdl_color(wrapper, name, hdr10, error, error_length);
        else fail(error, error_length, SDL_GetError());
    } else {
        if (error && error_length > 0) SDL_snprintf(error, error_length, "Cannot create playback child (Win32 %lu).", GetLastError());
    }
    if (!p) {
        if (wrapper) SDL_DestroyWindow(wrapper);
        if (child) DestroyWindow(child);
        SDL_QuitSubSystem(SDL_INIT_VIDEO);
        return NULL;
    }
    p->wrapper = wrapper;
    p->child = child;
    p->child_width = width;
    p->child_height = height;
    ShowWindow(child, SW_SHOWNA);
    return p;
#else
    (void)native_parent; (void)driver; (void)width; (void)height; (void)hdr10;
    fail(error, error_length, "Use p5_present_create_sdl_color with the platform playback window.");
    return NULL;
#endif
}

int p5_present_output_hdr10(void *instance)
{
    Presenter *p = instance;
    return p && hdr_gpu_output_valid(p->hdr);
}

int p5_present_resize(void *instance, int output_width, int output_height, char *error, int error_length)
{
    Presenter *p = instance;
    if (!p || output_width <= 0 || output_height <= 0) {
        return fail(error, error_length, "Invalid output size.");
    }
#ifdef _WIN32
    if (p->child && (output_width != p->child_width || output_height != p->child_height)) {
        if (!SetWindowPos(p->child, NULL, 0, 0, output_width, output_height,
                          SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE)) {
            return fail(error, error_length, "Could not resize the embedded playback window.");
        }
        p->child_width = output_width;
        p->child_height = output_height;
    }
#endif
    return 1;
}

int p5_present_frame(void *instance, const void *pixels, int pitch, int width, int height,
                     int output_width, int output_height, char *error, int error_length)
{
    Presenter *p = instance;
    if (!pixels || width <= 0 || height <= 0 || width > INT_MAX / 4 || pitch < width * 4) {
        return fail(error, error_length, "Invalid video frame or stride.");
    }
    if (!p5_present_resize(p, output_width, output_height, error, error_length)) return 0;
    if (p->hdr) {
        if (!hdr_gpu_frame(p->hdr, pixels, pitch, width, height)) return fail(error, error_length, SDL_GetError());
        return 1;
    }
    if (!p->texture || width != p->frame_width || height != p->frame_height) {
        SDL_Texture *texture = SDL_CreateTexture(p->renderer, SDL_PIXELFORMAT_BGRA32,
                                                 SDL_TEXTUREACCESS_STREAMING, width, height);
        if (!texture) return fail(error, error_length, SDL_GetError());
        if (!SDL_SetTextureBlendMode(texture, SDL_BLENDMODE_NONE) ||
            !SDL_SetRenderLogicalPresentation(p->renderer, width, height, SDL_LOGICAL_PRESENTATION_LETTERBOX)) {
            fail(error, error_length, SDL_GetError());
            SDL_DestroyTexture(texture);
            return 0;
        }
        SDL_DestroyTexture(p->texture);
        p->texture = texture;
        p->frame_width = width;
        p->frame_height = height;
    }
    if (!SDL_UpdateTexture(p->texture, NULL, pixels, pitch) ||
        !SDL_RenderClear(p->renderer) || !SDL_RenderTexture(p->renderer, p->texture, NULL, NULL) ||
        !SDL_RenderPresent(p->renderer)) {
        return fail(error, error_length, SDL_GetError());
    }
    return 1;
}

void p5_present_destroy(void *instance)
{
    Presenter *p = instance;
    if (!p) return;
    hdr_gpu_destroy(p->hdr);
    if (p->texture) SDL_DestroyTexture(p->texture);
    if (p->renderer) SDL_DestroyRenderer(p->renderer);
#ifdef _WIN32
    if (p->wrapper) {
        SDL_DestroyWindow(p->wrapper);
        DestroyWindow(p->child);
        SDL_QuitSubSystem(SDL_INIT_VIDEO);
    }
#endif
    SDL_free(p);
}
