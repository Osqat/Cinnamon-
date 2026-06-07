using BepInEx;
using BepInEx.Logging;
using Cinnamon.UI;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;

[assembly: System.Reflection.AssemblyVersion("0.10.0")]
[assembly: Cinnamon.AutoUpdate("Osqat/Cinnamon-")]

namespace Cinnamon
{
    [BepInPlugin("com.osqat.cinnamon", "Cinnamon", "0.10.0" + PreRelease)]
    public class Plugin : BaseUnityPlugin
    {
        internal const string PreRelease = "-beta"; // set to "" for stable releases
        internal static ManualLogSource Log;
        internal static string VersionString => Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + PreRelease;

        void Awake()
        {
            Log = Logger;
            Log.LogInfo("[Cinnamon] loaded.");
            new Harmony("com.osqat.cinnamon").PatchAll();
        }

        void Start()
        {
            ExtractPatcher();
            Updater.CheckAsync(BepInEx.Paths.PluginPath, Log);
        }

        static void ExtractPatcher()
        {
            try
            {
                string patcherPath = Path.Combine(BepInEx.Paths.PatcherPluginPath, "CinnamonPatcher.dll");
                using (var src = typeof(Plugin).Assembly.GetManifestResourceStream("CinnamonPatcher.dll"))
                {
                    if (src == null) { Log.LogWarning("[Cinnamon] Embedded patcher not found."); return; }
                    var bytes = new byte[src.Length];
                    src.Read(bytes, 0, bytes.Length);
                    try
                    {
                        File.WriteAllBytes(patcherPath, bytes);
                    }
                    catch
                    {
                        File.Delete(patcherPath);
                        File.WriteAllBytes(patcherPath, bytes);
                    }
                }
                Log.LogDebug("[Cinnamon] Patcher refreshed.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Cinnamon] Failed to extract patcher: {ex.Message}");
            }
        }
    }
}
