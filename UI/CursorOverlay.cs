using UnityEngine;

namespace Cinnamon.UI
{
    public static class CursorOverlay
    {
        public static bool DebugLogging
        {
            get { return CursorOverlayHost.EnsureExists().DebugLogging; }
            set { CursorOverlayHost.EnsureExists().DebugLogging = value; }
        }

        public static bool IsReady
        {
            get { return CursorOverlayHost.EnsureExists().IsCursorReady(); }
        }

        public static void EnsureHost() => CursorOverlayHost.EnsureExists();

        // Call once per frame (from OnGUI, Update, or anywhere) while your panel is active.
        // While Request is current, Cinnamon mirrors the game's cursor renderers above IMGUI
        // and alpha-hides the original SpriteRenderers without disabling the real PickCursor.
        public static void Request() => CursorOverlayHost.EnsureExists().Request();

        // ---- Controller pointer API ----

        // Returns the active game cursor's GUI-space tip position.
        // Returns false when no requested/active game cursor exists.
        public static bool TryGetPointerPosition(out Vector2 guiPos)
        {
            var h = CursorOverlayHost.Instance;
            if (h == null) { guiPos = default; return false; }
            return h.TryGetPointerPosition(out guiPos);
        }

        // True while the active cursor's controller Accept button is held.
        public static bool AcceptHeld     => CursorOverlayHost.Instance?.AcceptHeld     ?? false;

        // True for one frame after the Accept button is released.
        public static bool AcceptReleased => CursorOverlayHost.Instance?.AcceptReleased ?? false;

        // Returns true once per press; clears the flag so subsequent calls return false
        // until the button is pressed again. Safe to call multiple times per OnGUI pass.
        public static bool ConsumeAcceptPressed()
            => CursorOverlayHost.Instance?.ConsumeAcceptPressed() ?? false;
    }
}
