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
        // While Request is current, Cinnamon hands the game's cursor sprite to the OS via
        // Cursor.SetCursor, which draws above the entire Unity window — IMGUI panels included.
        public static void Request() => CursorOverlayHost.EnsureExists().Request();
    }
}
