using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

[assembly: System.Reflection.AssemblyVersion("0.10.9")]

namespace CinnamonPatcher
{
    public static class Patcher
    {
        const string AutoUpdateAttrName = "Cinnamon.AutoUpdateAttribute";
        static ManualLogSource Log;

        [DllImport("urlmon.dll", CharSet = CharSet.Unicode)]
        static extern int URLDownloadToFile(IntPtr pCaller, string szURL, string szFileName, uint dwReserved, IntPtr lpfnCB);

        [DllImport("wininet.dll", SetLastError = true)]
        static extern bool InternetSetOption(IntPtr hInternet, int dwOption, ref int lpBuffer, int dwBufferLength);

        public static IEnumerable<string> TargetDLLs => Array.Empty<string>();
        public static void Patch(AssemblyDefinition _) { }

        public static void Initialize()
        {
            Log = Logger.CreateLogSource("CinnamonPatcher");
            Log.LogInfo("[CinnamonPatcher] Checking for updates...");

            int timeout = 10_000;
            InternetSetOption(IntPtr.Zero, 2, ref timeout, sizeof(int));  // INTERNET_OPTION_CONNECT_TIMEOUT
            InternetSetOption(IntPtr.Zero, 5, ref timeout, sizeof(int));  // INTERNET_OPTION_SEND_TIMEOUT
            InternetSetOption(IntPtr.Zero, 6, ref timeout, sizeof(int));  // INTERNET_OPTION_RECEIVE_TIMEOUT

            var results = new List<string>();
            try
            {
                foreach (var dllPath in Directory.GetFiles(Paths.PluginPath, "*.dll", SearchOption.AllDirectories))
                    TryUpdate(dllPath, results);
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[CinnamonPatcher] {ex.Message}");
            }

            // If Cinnamon.dll is gone the user uninstalled — schedule patcher self-removal
            if (Directory.GetFiles(Paths.PluginPath, "Cinnamon.dll", SearchOption.AllDirectories).Length == 0)
            {
                Log.LogInfo("[CinnamonPatcher] Cinnamon.dll not found — scheduling patcher removal on game exit.");
                results.Add("[REMOVED] CinnamonPatcher scheduled for removal");
                ScheduleSelfDelete();
            }

            WriteUpdateLog(results);
        }

        static void TryUpdate(string dllPath, List<string> results)
        {
            string repo = null, asmName = null;
            Version current = null;

            try
            {
                var rp = new ReaderParameters { ReadSymbols = false };
                using (var asm = AssemblyDefinition.ReadAssembly(dllPath, rp))
                {
                    asmName = asm.Name.Name;
                    var v = asm.Name.Version;
                    current = new Version(v.Major, v.Minor, v.Build);
                    foreach (var attr in asm.CustomAttributes)
                    {
                        if (attr.AttributeType.FullName != AutoUpdateAttrName) continue;
                        repo = (string)attr.ConstructorArguments[0].Value;
                        break;
                    }
                }
            }
            catch { return; }

            if (repo == null) return;

            string dllName = asmName + ".dll";
            string tempJson = Path.Combine(Path.GetTempPath(), $"CinnamonPatcher_{asmName}.json");

            try
            {
                int hr = URLDownloadToFile(IntPtr.Zero,
                    $"https://api.github.com/repos/{repo}/releases?per_page=1",
                    tempJson, 0, IntPtr.Zero);

                if (hr != 0)
                {
                    Log.LogWarning($"[CinnamonPatcher] {dllName} fetch failed (0x{hr:X8}).");
                    results.Add($"[FAILED] {dllName}: fetch error 0x{hr:X8}");
                    return;
                }

                string json = File.ReadAllText(tempJson);
                File.Delete(tempJson);

                var tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (!tagMatch.Success || !Version.TryParse(tagMatch.Groups[1].Value.TrimStart('v', 'V'), out var latest))
                    return;

                if (latest <= current)
                {
                    Log.LogInfo($"[CinnamonPatcher] {dllName} up to date (v{current}).");
                    results.Add($"[OK] {dllName} v{current}");
                    return;
                }

                Log.LogInfo($"[CinnamonPatcher] {dllName}: v{current} -> v{latest}. Downloading...");

                var dlMatch = Regex.Match(json,
                    "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]+/" + Regex.Escape(dllName) + ")\"");
                if (!dlMatch.Success)
                {
                    Log.LogWarning($"[CinnamonPatcher] No asset '{dllName}' in release.");
                    results.Add($"[FAILED] {dllName}: no asset in release");
                    return;
                }

                hr = URLDownloadToFile(IntPtr.Zero, dlMatch.Groups[1].Value, dllPath, 0, IntPtr.Zero);
                if (hr == 0)
                {
                    Log.LogInfo($"[CinnamonPatcher] {dllName} updated to v{latest}! Loading new version now.");
                    results.Add($"[UPDATED] {dllName} v{current} -> v{latest}");
                }
                else
                {
                    Log.LogWarning($"[CinnamonPatcher] Download failed (0x{hr:X8}).");
                    results.Add($"[FAILED] {dllName}: download error 0x{hr:X8}");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[CinnamonPatcher] {dllName}: {ex.Message}");
                results.Add($"[FAILED] {dllName}: {ex.Message}");
                try { if (File.Exists(tempJson)) File.Delete(tempJson); } catch { }
            }
        }

        // Spawn a hidden cmd that waits for this PID to exit, then deletes CinnamonPatcher.dll.
        // Mono holds the DLL memory-mapped for the entire process — deletion only works after exit.
        static void ScheduleSelfDelete()
        {
            try
            {
                string selfPath = Assembly.GetExecutingAssembly().Location;
                int pid = Process.GetCurrentProcess().Id;
                string batPath = Path.Combine(Path.GetTempPath(), "CinnamonPatcherRemove.bat");
                var bat = new StringBuilder();
                bat.Append("@echo off\r\n");
                bat.Append(":wait\r\n");
                bat.Append($"tasklist /FI \"PID eq {pid}\" /FO csv 2>nul | findstr /I \"{pid}\" >nul\r\n");
                bat.Append("if %errorlevel% equ 0 (timeout /t 1 /nobreak >nul & goto wait)\r\n");
                bat.Append($"del /f /q \"{selfPath}\"\r\n");
                bat.Append("del \"%~f0\"\r\n");
                File.WriteAllText(batPath, bat.ToString(), Encoding.ASCII);
                Process.Start(new ProcessStartInfo {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{batPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                Log.LogInfo("[CinnamonPatcher] Removal scheduled for game exit.");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[CinnamonPatcher] Failed to schedule removal: {ex.Message}");
            }
        }

        static void WriteUpdateLog(List<string> results)
        {
            try
            {
                string logPath = Path.Combine(Paths.BepInExRootPath, "CinnamonUpdate.log");
                string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                var lines = new List<string> { $"=== CinnamonPatcher v{version} — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===" };
                lines.AddRange(results);
                File.WriteAllLines(logPath, lines);
            }
            catch { }
        }
    }
}
