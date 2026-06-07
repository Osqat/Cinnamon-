using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

[assembly: System.Reflection.AssemblyVersion("1.0.0")]

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

        static void WriteUpdateLog(List<string> results)
        {
            try
            {
                string logPath = Path.Combine(Paths.BepInExRootPath, "CinnamonUpdate.log");
                var lines = new List<string> { $"Cinnamon update check — {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
                lines.AddRange(results);
                File.WriteAllLines(logPath, lines);
            }
            catch { }
        }
    }
}
