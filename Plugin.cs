using BepInEx;
using BepInEx.Logging;
using Cinnamon.UI;
using System;
using System.IO;

[assembly: System.Reflection.AssemblyVersion("0.9.0")]
[assembly: Cinnamon.AutoUpdate("Osqat/Cinnamon-")]

namespace Cinnamon
{
    [BepInPlugin("com.osqat.cinnamon", "Cinnamon", "0.9.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        void Awake()
        {
            Log = Logger;
            Log.LogInfo("[Cinnamon] loaded.");
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
