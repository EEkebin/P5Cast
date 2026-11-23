#ifndef P5CAST_PRESENTER_H
#define P5CAST_PRESENTER_H

#if defined(_WIN32)
#if defined(P5CAST_PRESENTER_BUILD)
#define P5_API __declspec(dllexport)
#else
#define P5_API __declspec(dllimport)
#endif
#else
#define P5_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

struct SDL_Window;

/* Tests the compiled SDL driver list. Device availability is checked on creation. */
P5_API int p5_present_available(const char *driver);

/* Windows embedding. The caller owns the parent HWND and pumps its messages. */
P5_API void *p5_present_create(void *native_parent, const char *driver, int width, int height,
                               char *error, int error_length);
P5_API void *p5_present_create_color(void *native_parent, const char *driver, int width, int height,
                                     int hdr10, char *error, int error_length);

/* Portable embedding. The caller initializes SDL video, supplies a window appropriate
   for the driver, and services SDL events. The window remains owned by the caller.
   Call create, frame, and destroy on the SDL video thread. */
P5_API void *p5_present_create_sdl(struct SDL_Window *window, const char *driver,
                                   char *error, int error_length);
P5_API void *p5_present_create_sdl_color(struct SDL_Window *window, const char *driver,
                                          int hdr10, char *error, int error_length);
/* Returns 1 for an HDR10_ST2084 swapchain with a negotiated R10G10B10A2 format. */
P5_API int p5_present_output_hdr10(void *instance);
/* Update an embedded Windows child's size between frames on the SDL video thread.
   Portable hosts continue to size their own window. */
P5_API int p5_present_resize(void *instance, int output_width, int output_height, char *error, int error_length);
/* SDR uses BGRA8; HDR10 uses packed R10G10B10A2 UNORM (R in the low 10 bits)
   containing PQ-encoded BT.2020 RGB. Rows are top-to-bottom. Output dimensions specify the size
   of a Windows embedded child. Portable hosts resize their own window.
   Returns 1 after a successful present, or 0 with a UTF-8 error. */
P5_API int p5_present_frame(void *instance, const void *pixels, int pitch, int width, int height,
                            int output_width, int output_height, char *error, int error_length);
P5_API void p5_present_destroy(void *instance);

#ifdef __cplusplus
}
#endif
#endif
