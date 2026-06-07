using HarmonyLib;
using UnityEngine;

namespace Cinnamon
{
    [HarmonyPatch(typeof(TabletStatsScreen), "UpdateStats")]
    static class TabletStatsVersionPatch
    {
        static void Postfix(TabletStatsScreen __instance)
        {
            if (__instance.VersionNumber != null)
                __instance.VersionNumber.text += $"\nCinnamon v{Plugin.VersionString}";
        }
    }

    [HarmonyPatch(typeof(GameState), "OnGUI")]
    static class GameStateVersionPatch
    {
        static GUIStyle _style;

        static void Postfix()
        {
            if (!ControllerMonitor.Instance.IsMainControllerSet) return;
            if (!PlayerManager.GetInstance().FirstUserLoggedIn) return;
            if (StatTracker.Instance.GetSaveFileDataForMainUser().HideVersion) return;

            if (_style == null)
            {
                _style = new GUIStyle
                {
                    font      = GameSettings.GetInstance().onlineBetaMessageFont,
                    fontSize  = 11,
                    alignment = TextAnchor.LowerLeft
                };
                _style.normal.textColor = new Color(1f, 1f, 1f, 0.3f);
            }

            GUI.Label(new Rect(5f, 20f, 200f, 16f), $"Cin v{Plugin.VersionString}", _style);
        }
    }
}
