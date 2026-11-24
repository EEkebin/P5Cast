// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#include "Direct.h"
#include <string.h>

static const struct { SDL_GameControllerButton button; uint32_t mask; } buttons[] = {
    {SDL_CONTROLLER_BUTTON_A, CHIAKI_CONTROLLER_BUTTON_CROSS}, {SDL_CONTROLLER_BUTTON_B, CHIAKI_CONTROLLER_BUTTON_MOON},
    {SDL_CONTROLLER_BUTTON_X, CHIAKI_CONTROLLER_BUTTON_BOX}, {SDL_CONTROLLER_BUTTON_Y, CHIAKI_CONTROLLER_BUTTON_PYRAMID},
    {SDL_CONTROLLER_BUTTON_DPAD_UP, CHIAKI_CONTROLLER_BUTTON_DPAD_UP}, {SDL_CONTROLLER_BUTTON_DPAD_DOWN, CHIAKI_CONTROLLER_BUTTON_DPAD_DOWN},
    {SDL_CONTROLLER_BUTTON_DPAD_LEFT, CHIAKI_CONTROLLER_BUTTON_DPAD_LEFT}, {SDL_CONTROLLER_BUTTON_DPAD_RIGHT, CHIAKI_CONTROLLER_BUTTON_DPAD_RIGHT},
    {SDL_CONTROLLER_BUTTON_LEFTSHOULDER, CHIAKI_CONTROLLER_BUTTON_L1}, {SDL_CONTROLLER_BUTTON_RIGHTSHOULDER, CHIAKI_CONTROLLER_BUTTON_R1},
    {SDL_CONTROLLER_BUTTON_LEFTSTICK, CHIAKI_CONTROLLER_BUTTON_L3}, {SDL_CONTROLLER_BUTTON_RIGHTSTICK, CHIAKI_CONTROLLER_BUTTON_R3},
    {SDL_CONTROLLER_BUTTON_START, CHIAKI_CONTROLLER_BUTTON_OPTIONS}, {SDL_CONTROLLER_BUTTON_BACK, CHIAKI_CONTROLLER_BUTTON_SHARE},
    {SDL_CONTROLLER_BUTTON_GUIDE, CHIAKI_CONTROLLER_BUTTON_PS}, {SDL_CONTROLLER_BUTTON_TOUCHPAD, CHIAKI_CONTROLLER_BUTTON_TOUCHPAD}
};

void gamepad_init(Gamepad *pad)
{
    chiaki_controller_state_set_idle(&pad->current);
    pad->touches[0] = pad->touches[1] = -1;
    pad->trigger_strength = Strong;
    pad->triggers.type_left = pad->triggers.type_right = 0x05;
}

bool gamepad_is_dualsense(SDL_GameController *pad)
{
    if (!pad || SDL_GameControllerGetVendor(pad) != 0x054c) return false;
    Uint16 product = SDL_GameControllerGetProduct(pad);
    return product == 0x0ce6 || product == 0x0df2;
}

// libchiaki owns the event contents. SDL output belongs on the polling thread.
void gamepad_feedback_event(Gamepad *pad, const ChiakiEvent *event)
{
    AcquireSRWLockExclusive(&pad->feedback_lock);
    switch (event->type) {
        case CHIAKI_EVENT_TRIGGER_EFFECTS: pad->triggers = event->trigger_effects; break;
        case CHIAKI_EVENT_TRIGGER_INTENSITY: pad->trigger_strength = event->intensity; break;
        default: break;
    }
    ReleaseSRWLockExclusive(&pad->feedback_lock);
}

enum { RIGHT_EFFECT = 10, LEFT_EFFECT = 21, MOTOR_ATTENUATION = 36 };

void gamepad_apply_feedback(Gamepad *pad)
{
    if (!gamepad_is_dualsense(pad->pad) || GetTickCount64() < pad->retry_after) return;
    // DualSense output payload (SDL adds the USB or Bluetooth transport header).
    uint8_t report[47] = {0x0c, 0x40};
    AcquireSRWLockShared(&pad->feedback_lock);
    if (pad->trigger_strength == Off) {
        report[RIGHT_EFFECT] = report[LEFT_EFFECT] = 0x05; // Release both motors.
    } else {
        report[RIGHT_EFFECT] = pad->triggers.type_right;
        report[LEFT_EFFECT] = pad->triggers.type_left;
        memcpy(report + RIGHT_EFFECT + 1, pad->triggers.right, sizeof(pad->triggers.right));
        memcpy(report + LEFT_EFFECT + 1, pad->triggers.left, sizeof(pad->triggers.left));
    }
    // The low nibble stays zero. Waveform vibration strength is applied to PCM.
    report[MOTOR_ATTENUATION] = pad->trigger_strength == Weak ? 0x90 : pad->trigger_strength == Medium ? 0x60 : 0;
    ReleaseSRWLockShared(&pad->feedback_lock);
    if (pad->feedback_sent && !memcmp(report, pad->last_report, sizeof(report))) return;
    if (SDL_GameControllerSendEffect(pad->pad, report, sizeof(report)) < 0) {
        pad->retry_after = GetTickCount64() + 500;
        return;
    }
    memcpy(pad->last_report, report, sizeof(report));
    pad->feedback_sent = true;
}

void gamepad_release_feedback(Gamepad *pad)
{
    if (pad->feedback_sent && pad->pad && SDL_GameControllerGetAttached(pad->pad)) {
        uint8_t release[47] = {0x0c};
        release[RIGHT_EFFECT] = release[LEFT_EFFECT] = 0x05;
        SDL_GameControllerSendEffect(pad->pad, release, sizeof(release));
    }
    pad->feedback_sent = false;
    pad->retry_after = 0;
}

static uint8_t trigger(SDL_GameController *pad, SDL_GameControllerAxis axis)
{
    int value = SDL_GameControllerGetAxis(pad, axis);
    return value <= 0 ? 0 : (uint8_t)((value * 255 + 16383) / 32767);
}

// Some games interpret a Remote Play contact as a click. In click-only mode
// leave the contact slots idle; the physical button is mapped independently.
void gamepad_contact(Gamepad *pad, ChiakiControllerState *state, int finger, bool down, float x, float y)
{
    if (!pad->forward_contacts) return;
    if (down) {
        uint16_t px = (uint16_t)(x * 1919), py = (uint16_t)(y * 1079);
        if (pad->touches[finger] < 0) pad->touches[finger] = chiaki_controller_state_start_touch(state, px, py);
        else chiaki_controller_state_set_touch_pos(state, (uint8_t)pad->touches[finger], px, py);
    } else if (pad->touches[finger] >= 0) {
        chiaki_controller_state_stop_touch(state, (uint8_t)pad->touches[finger]);
        pad->touches[finger] = -1;
    }
}

bool gamepad_tick(Gamepad *pad)
{
    SDL_Event event;
    while (SDL_PollEvent(&event)) {
        if (event.type == SDL_CONTROLLERDEVICEADDED) pad->next_scan = 0;
    }
    if (pad->pad && !SDL_GameControllerGetAttached(pad->pad)) {
        gamepad_release_feedback(pad);
        SDL_GameControllerClose(pad->pad);
        pad->pad = NULL;
        chiaki_controller_state_set_idle(&pad->current);
        pad->touches[0] = pad->touches[1] = -1;
        pad->next_scan = 0;
        return true;
    }
    uint64_t now = GetTickCount64();
    if (!pad->pad && now >= pad->next_scan) {
        pad->next_scan = now + 1000;
        for (int i = 0; i < SDL_NumJoysticks() && !pad->pad; i++) {
            if (SDL_IsGameController(i)) pad->pad = SDL_GameControllerOpen(i);
        }
    }
    if (!pad->pad) return false;
    ChiakiControllerState state = pad->current;
    state.buttons = 0;
    for (size_t i = 0; i < sizeof(buttons) / sizeof(buttons[0]); i++) {
        if (SDL_GameControllerGetButton(pad->pad, buttons[i].button)) state.buttons |= buttons[i].mask;
    }
    state.left_x = SDL_GameControllerGetAxis(pad->pad, SDL_CONTROLLER_AXIS_LEFTX);
    state.left_y = SDL_GameControllerGetAxis(pad->pad, SDL_CONTROLLER_AXIS_LEFTY);
    state.right_x = SDL_GameControllerGetAxis(pad->pad, SDL_CONTROLLER_AXIS_RIGHTX);
    state.right_y = SDL_GameControllerGetAxis(pad->pad, SDL_CONTROLLER_AXIS_RIGHTY);
    state.l2_state = trigger(pad->pad, SDL_CONTROLLER_AXIS_TRIGGERLEFT);
    state.r2_state = trigger(pad->pad, SDL_CONTROLLER_AXIS_TRIGGERRIGHT);
    for (int finger = 0; finger < 2; finger++) {
        Uint8 down = 0;
        float x = 0, y = 0;
        if (SDL_GameControllerGetTouchpadFinger(pad->pad, 0, finger, &down, &x, &y, NULL) < 0) continue;
        gamepad_contact(pad, &state, finger, down, x, y);
    }
    if (!chiaki_controller_state_equals(&state, &pad->current)) {
        pad->current = state;
        return true;
    }
    return false;
}
