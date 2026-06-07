using BepInEx;
using BepInEx.Logging;
using Cinnamon.UI;
using HarmonyLib;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

[assembly: System.Reflection.AssemblyVersion("0.10.8")]
[assembly: Cinnamon.AutoUpdate("Osqat/Cinnamon-")]

namespace Cinnamon
{
    [BepInPlugin("com.osqat.cinnamon", "Cinnamon", "0.10.8")]  // NUMERIC ONLY — BepInEx calls Version.Parse()
    public class Plugin : BaseUnityPlugin
    {
        internal const string PreRelease = "-beta"; // set to "" for stable releases
        internal static ManualLogSource Log;
        internal static string VersionString => Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + PreRelease;

        static string _pendingPatcherPath;

        void Awake()
        {
            Log = Logger;
            string logPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "CinnamonUpdate.log");
            BepInEx.Logging.Logger.Listeners.Add(new CinnamonLogListener(logPath, VersionString));
            Log.LogInfo("[Cinnamon] loaded.");
            new Harmony("com.osqat.cinnamon").PatchAll();
        }

        void Start()
        {
            ExtractPatcher();
            Updater.CheckAsync(BepInEx.Paths.PluginPath, Log);
        }

        void OnApplicationQuit()
        {
            if (_pendingPatcherPath == null) return;
            string pendingPath = _pendingPatcherPath + ".pending";
            if (!File.Exists(pendingPath)) return;
            try
            {
                // Mono holds the patcher DLL memory-mapped for the entire process lifetime —
                // any write to it fails until the process exits. Spawn a cmd that polls
                // until this PID disappears, then moves .pending into place.
                int pid = Process.GetCurrentProcess().Id;
                string batPath = Path.Combine(Path.GetTempPath(), "CinnamonPatcherUpdate.bat");
                var bat = new StringBuilder();
                bat.Append("@echo off\r\n");
                bat.Append(":wait\r\n");
                bat.Append($"tasklist /FI \"PID eq {pid}\" /FO csv 2>nul | findstr /I \"{pid}\" >nul\r\n");
                bat.Append("if %errorlevel% equ 0 (timeout /t 1 /nobreak >nul & goto wait)\r\n");
                bat.Append($"move /y \"{pendingPath}\" \"{_pendingPatcherPath}\"\r\n");
                bat.Append("del \"%~f0\"\r\n");
                File.WriteAllText(batPath, bat.ToString(), Encoding.ASCII);
                Process.Start(new ProcessStartInfo {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{batPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                Log.LogInfo("[Cinnamon] Patcher updater launched — effective next launch.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[Cinnamon] Failed to launch patcher updater: {ex.Message}");
            }
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

                    if (FileMatchesBytes(patcherPath, bytes))
                    {
                        // Clean up any leftover .pending from a previous session
                        try { File.Delete(patcherPath + ".pending"); } catch { }
                        return;
                    }

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

                    // Stage 2: patcher DLL is memory-mapped for this entire process — write
                    // a .pending file now; OnApplicationQuit spawns cmd to apply it after exit.
                    string pendingPath = patcherPath + ".pending";
                    try { File.WriteAllBytes(pendingPath, bytes); }
                    catch (Exception ex) { Log.LogWarning($"[Cinnamon] Could not stage patcher update: {ex.Message}"); return; }
                    Log.LogInfo("[Cinnamon] Patcher update staged — will apply on game exit.");
                    _pendingPatcherPath = patcherPath;
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

        class CinnamonLogListener : ILogListener
        {
            readonly string _path;

            public CinnamonLogListener(string path, string version)
            {
                _path = path;
                try { File.AppendAllText(path, $"\n=== Cinnamon {version} ===\n"); }
                catch { }
            }

            public void LogEvent(object sender, LogEventArgs e)
            {
                if (!e.Source.SourceName.Contains("Cinnamon")) return;
                try { File.AppendAllText(_path, $"[{e.Level}] {e.Data}\n"); }
                catch { }
            }

            public void Dispose() { }
        }
    }
}
