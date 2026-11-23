// SPDX-License-Identifier: LicenseRef-AGPL-3.0-only-OpenSSL
#include "Direct.h"

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
}

static uint8_t trigger(SDL_GameController *pad, SDL_GameControllerAxis axis)
{
    int value = SDL_GameControllerGetAxis(pad, axis);
    return value <= 0 ? 0 : (uint8_t)((value * 255 + 16383) / 32767);
}

bool gamepad_tick(Gamepad *pad)
{
    SDL_Event event;
    while (SDL_PollEvent(&event)) {
        if (event.type == SDL_CONTROLLERDEVICEADDED) pad->next_scan = 0;
    }
    if (pad->pad && !SDL_GameControllerGetAttached(pad->pad)) {
        SDL_GameControllerClose(pad->pad);
        pad->pad = NULL;
        gamepad_init(pad);
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
        if (down) {
            uint16_t px = (uint16_t)(x * 1919), py = (uint16_t)(y * 1079);
            if (pad->touches[finger] < 0) pad->touches[finger] = chiaki_controller_state_start_touch(&state, px, py);
            else chiaki_controller_state_set_touch_pos(&state, (uint8_t)pad->touches[finger], px, py);
        } else if (pad->touches[finger] >= 0) {
            chiaki_controller_state_stop_touch(&state, (uint8_t)pad->touches[finger]);
            pad->touches[finger] = -1;
        }
    }
    if (!chiaki_controller_state_equals(&state, &pad->current)) {
        pad->current = state;
        return true;
    }
    return false;
}
