// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#include "Direct.h"
#include <stdio.h>

static HANDLE open_control(const char *pipe_name, const char *suffix, DWORD rights)
{
    char name[200];
    int size = snprintf(name, sizeof(name), "Local\\P5Cast.Rest.%lu.%s.%s", GetCurrentProcessId(), pipe_name, suffix);
    if (size < 0 || (size_t)size >= sizeof(name)) return NULL;
    return OpenEventA(rights, FALSE, name);
}

HANDLE session_stop_event(const char *pipe_name)
{
    // The session loop and outstanding video writes both wait on this latch.
    return open_control(pipe_name, "disconnect", SYNCHRONIZE | EVENT_MODIFY_STATE);
}

void rest_open(RestControl *control, const char *pipe_name)
{
    // The dashboard creates all three private events before releasing the credentials on stdin.
    control->request = open_control(pipe_name, "request", SYNCHRONIZE | EVENT_MODIFY_STATE);
    control->accepted = open_control(pipe_name, "accepted", EVENT_MODIFY_STATE);
    control->rejected = open_control(pipe_name, "rejected", EVENT_MODIFY_STATE);
    if (!control->request || !control->accepted || !control->rejected) rest_close(control);
}

void rest_close(RestControl *control)
{
    if (control->request) CloseHandle(control->request);
    if (control->accepted) CloseHandle(control->accepted);
    if (control->rejected) CloseHandle(control->rejected);
    control->request = control->accepted = control->rejected = NULL;
}

void rest_tick(RestControl *control, bool connected, ChiakiSession *session, ChiakiErrorCode (*send)(ChiakiSession *))
{
    if (!control->request || control->queued || WaitForSingleObject(control->request, 0) != WAIT_OBJECT_0) return;
    // Consume each click once. A failed queue operation leaves the session available for another request.
    ResetEvent(control->request);
    control->queued = connected && send(session) == CHIAKI_ERR_SUCCESS;
    SetEvent(control->queued ? control->accepted : control->rejected);
}
