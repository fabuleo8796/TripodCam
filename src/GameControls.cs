using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace TripodCam;

/// Switches the game's controls off while the tripod is being placed or the panel is open, so WASD flies the camera
/// instead of flipping the phone, and Esc/Enter don't reach the game's menus. (From StickerSwap's GameInputPause.)
/// Controls come back once Enter, Esc, the left mouse button and the tripod and menu keys are let go, so the closing
/// press never leaks into the game.
internal static class GameControls
{
    static readonly List<InputAction> paused = new();
    static bool waitingForRelease;

    /// True from pausing until the closing key is released.
    public static bool Active { get; private set; }

    public static void Pause()
    {
        if (Active && !waitingForRelease) return;
        waitingForRelease = false;
        Active = true;
        try
        {
            var enabled = new Il2CppSystem.Collections.Generic.List<InputAction>();
            InputSystem.ListEnabledActions(enabled);
            for (var i = 0; i < enabled.Count; i++)
            {
                var action = enabled[i];
                if (action == null || IsPointer(action)) continue;
                action.Disable();
                paused.Add(action);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't pause the game's controls: {e.Message}");
        }
    }

    /// The controls return in Tick() once the keys are up.
    public static void Resume()
    {
        if (Active) waitingForRelease = true;
    }

    public static void Tick(Keyboard keyboard, Mouse mouse)
    {
        if (!waitingForRelease) return;
        if (keyboard != null && (keyboard.enterKey.isPressed || keyboard.numpadEnterKey.isPressed || keyboard.escapeKey.isPressed
                                 || keyboard[Plugin.TripodKey.Value].isPressed || keyboard[Plugin.MenuKey.Value].isPressed)) return;
        if (mouse != null && mouse.leftButton.isPressed) return;
        foreach (var action in paused)
        {
            try { action?.Enable(); } catch { /* the action's owner may be gone */ }
        }
        paused.Clear();
        waitingForRelease = false;
        Active = false;
    }

    /// The UI's pointing and clicking stay on; everything else pauses (the game's own scroll-to-zoom too).
    static bool IsPointer(InputAction action)
    {
        var map = action.actionMap;
        if (map != null && map.name != "UI") return false;
        var name = (action.name ?? "").ToLowerInvariant();
        return name.Contains("point") || name.Contains("click") || name.Contains("scroll") || name.Contains("tracked");
    }
}
