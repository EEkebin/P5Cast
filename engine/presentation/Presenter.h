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

/* Portable embedding. The caller initializes SDL video, supplies a window appropriate
   for the driver, and services SDL events. The window remains owned by the caller.
   Call create, frame, and destroy on the SDL video thread. */
P5_API void *p5_present_create_sdl(struct SDL_Window *window, const char *driver,
                                   char *error, int error_length);
/* BGRA8 SDR pixels in top-to-bottom row order. Output dimensions specify the size
   of a Windows embedded child. Portable hosts resize their own window.
   Returns 1 after a successful present, or 0 with a UTF-8 error. */
P5_API int p5_present_frame(void *instance, const void *bgra, int pitch, int width, int height,
                            int output_width, int output_height, char *error, int error_length);
P5_API void p5_present_destroy(void *instance);

#ifdef __cplusplus
}
#endif
#endif
