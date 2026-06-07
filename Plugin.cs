using BepInEx;
using BepInEx.Logging;
using Cinnamon.UI;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;

[assembly: System.Reflection.AssemblyVersion("0.10.6")]
[assembly: Cinnamon.AutoUpdate("Osqat/Cinnamon-")]

namespace Cinnamon
{
    [BepInPlugin("com.osqat.cinnamon", "Cinnamon", "0.10.6")]  // NUMERIC ONLY — BepInEx calls Version.Parse()
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

                    if (FileMatchesBytes(patcherPath, bytes)) return;

                    // Stage 1: direct write (works on first install — patcher not yet loaded)
                    try
                    {
                        if (File.Exists(patcherPath))
                            try { File.SetAttributes(patcherPath, FileAttributes.Normal); } catch { }
                        File.WriteAllBytes(patcherPath, bytes);
                        Log.LogDebug("[Cinnamon] Patcher refreshed.");
                        return;
                    }
                    catch { }

                    // Stage 2: patcher is locked — write it when the game exits (file handles released)
                    Log.LogInfo("[Cinnamon] Patcher update scheduled for game exit.");
                    AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                    {
                        try
                        {
                            try { File.SetAttributes(patcherPath, FileAttributes.Normal); } catch { }
                            try { File.Delete(patcherPath); } catch { }
                            File.WriteAllBytes(patcherPath, bytes);
                        }
                        catch { }
                    };
                }
            }
            catch (Exception ex) { Log.LogWarning($"[Cinnamon] Failed to extract patcher: {ex.Message}"); }
        }

        static bool FileMatchesBytes(string path, byte[] bytes)
        {
            try
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length != bytes.Length) return false;
                for (int i = 0; i < bytes.Length; i++)
                    if (existing[i] != bytes[i]) return false;
                return true;
            }
            catch { return false; }
        }
    }
}
