using System;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx.Logging;
using Mono.Cecil;

namespace Cinnamon
{
    internal static class Updater
    {
        const string AutoUpdateAttrName = "Cinnamon.AutoUpdateAttribute";

        internal static void CheckAsync(string pluginsPath, ManualLogSource log)
        {
            var t = new Thread(() => Run(pluginsPath, log));
            t.IsBackground = true;
            t.Name = "CinnamonUpdater";
            t.Start();
        }

        static void Run(string pluginsPath, ManualLogSource log)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) => true;
                foreach (var dllPath in Directory.GetFiles(pluginsPath, "*.dll", SearchOption.AllDirectories))
                    TryUpdate(dllPath, log);
            }
            catch (Exception ex)
            {
                log.LogWarning($"[Cinnamon] Updater error: {ex.Message}");
            }
        }

        static void TryUpdate(string dllPath, ManualLogSource log)
        {
            string repo = null;
            string asmName = null;
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

            CheckAndUpdate(repo, asmName, dllPath, current, log);
        }

        static void CheckAndUpdate(string repo, string asmName, string dllPath, Version current, ManualLogSource log)
        {
            string dllName = asmName + ".dll";
            string json;
            try
            {
                using (var wc = new TimedWebClient())
                {
                    wc.Proxy = null;
                    wc.Headers.Add(HttpRequestHeader.UserAgent, "UCH-CinnamonUpdater/1.0");
                    json = wc.DownloadString($"https://api.github.com/repos/{repo}/releases/latest");
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"[Cinnamon] {dllName} update check failed: {ex.Message}");
                return;
            }

            var tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
            if (!tagMatch.Success || !Version.TryParse(tagMatch.Groups[1].Value.TrimStart('v', 'V'), out var latest))
                return;

            if (latest <= current)
            {
                log.LogInfo($"[Cinnamon] {dllName} up to date (v{current}).");
                return;
            }

            log.LogInfo($"[Cinnamon] {dllName}: v{current} -> v{latest}. Downloading...");

            var dlMatch = Regex.Match(json,
                "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]+/" + Regex.Escape(dllName) + ")\"");
            if (!dlMatch.Success)
            {
                log.LogWarning($"[Cinnamon] No asset '{dllName}' found in release.");
                return;
            }

            string pendingPath = dllPath + ".pending";
            try
            {
                using (var wc = new TimedWebClient())
                {
                    wc.Proxy = null;
                    wc.Headers.Add(HttpRequestHeader.UserAgent, "UCH-CinnamonUpdater/1.0");
                    wc.DownloadFile(dlMatch.Groups[1].Value, pendingPath);
                }
                log.LogInfo($"[Cinnamon] {dllName} updated to v{latest} — restart to apply.");
            }
            catch (Exception ex)
            {
                log.LogWarning($"[Cinnamon] Failed to download {dllName}: {ex.Message}");
            }
        }

        class TimedWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri uri)
            {
                var r = base.GetWebRequest(uri);
                r.Timeout = 10_000;
                return r;
            }
        }
    }
}
