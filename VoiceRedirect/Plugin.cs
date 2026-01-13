using System;
using System.Collections;
using BepInEx;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;
using Photon.Voice.Unity;
using UnityEngine;

namespace VoiceRedirect;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    
    private static ConfigEntry<string> _redirectNamesCsv;
    
    public static HashSet<string> RedirectSet = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Process> Helpers = new();

    private void Awake()
    {
        Log = Logger;
        
        _redirectNamesCsv = Config.Bind(
            "VoiceRedirect",
            "RedirectNames",
            "Nobody",
            "Comma-separated characterName values to redirect (mute in-game, play via helper). Example: FihWarrior5,el negrito ojo claro,Jorge0301,"
        );
        
        RebuildRedirectSet();
        _redirectNamesCsv.SettingChanged += (_, _) => RebuildRedirectSet();

        
        Log.LogInfo($"Voice Redirect {MyPluginInfo.PLUGIN_GUID} is loaded!");
        StartCoroutine(AttachLoop());
    }
    
    private static void RebuildRedirectSet()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string csv = _redirectNamesCsv.Value ?? "";
        foreach (var part in csv.Split([','], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim();
            if (name.Length > 0) set.Add(name);
        }

        RedirectSet = set;
        Log?.LogInfo("RedirectNames = " + string.Join(", ", RedirectSet));
    }
    
    private IEnumerator AttachLoop()
    {
        while (true)
        {
            Speaker[] speakers = FindObjectsByType<Speaker>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var sp in speakers)
            {
                var go = sp.gameObject;

                if (go.GetComponent<VoiceTap>() == null && sp.IsLinked)
                {
                    go.AddComponent<VoiceTap>();

                    Log.LogInfo($"Attached VoiceTap to {go.name}");
                }
            }
            yield return new WaitForSeconds(1.0f);
        }
    }

    internal static void EnsureHelperRunning(string pipeName, int sampleRate, int channels)
    {
        lock (Helpers)
        {
            if (Helpers.TryGetValue(pipeName, out var p))
            {
                if (p != null && !p.HasExited) return;
                Helpers.Remove(pipeName);
            }
            
            string pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            string helperPath = Path.Combine(pluginDir, "VoicePipeHelper.exe");

            if (!File.Exists(helperPath))
            {
                Log.LogError("Helper not found: " + helperPath);
                return;
            }
            
            int ppid = Process.GetCurrentProcess().Id;
            string helperArgs = $"--pipe \"{pipeName}\" --rate {sampleRate} --ch {channels} --ppid {ppid}";
            SpawnHelperDetached(helperPath, helperArgs, pipeName);
        }
    }
    private static void SpawnHelperDetached(string helperPath, string helperArgs, string pipeName)
    {
        string cmdArgs = "/c start \"\" /b " +
                         "\"" + helperPath + "\" " +
                         helperArgs;

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmdArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            WorkingDirectory = Path.GetDirectoryName(helperPath),
        };

        try
        {
            var proc = Process.Start(psi);
            Helpers[pipeName] = proc;
            Log.LogInfo($"Spawned helper for pipe '{pipeName}' (pid={proc.Id})");
        }
        catch (Exception ex)
        {
            Log.LogError("Failed to spawn helper: " + ex);
        }
    }

}
