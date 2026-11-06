using Frosty.ModSupport;
using FrostySdk;
using FrostySdk.Interfaces;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LaunchPlatformPlugin.Actions
{
    /// <summary>
    /// Launch for Steam copies of EA games that also need the EA app (tested with Mirror's Edge
    /// Catalyst on Steam): the game is started by Steam -> EA app, so the -dataPath argument Frosty
    /// passes to the executable is lost. Instead:
    ///  1. shut down Steam and the EA app,
    ///  2. start Steam with GAME_DATA_DIR pointing at the mod data (the EA app and the game inherit it),
    ///  3. start the game through Steam (steam.exe -applaunch &lt;app id&gt;),
    ///  4. when the game exits, shut both down again and restart Steam without the variable, so other
    ///     Frostbite games launched later are not redirected to this mod data.
    /// The game install is not modified beyond what Frosty's mod applying already writes.
    /// </summary>
    internal static class SteamWithEAApp
    {
        // Frosty profile name -> Steam app id
        private static readonly Dictionary<string, string> SteamAppIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "MirrorsEdgeCatalyst", "1233570" },
        };

        private static string steamExe;
        private static bool active;

        public static void PreLaunch(ILogger logger, string modDataPath)
        {
            active = false;
            FrostyModExecutor.LaunchGameOverride = null;

            if (!SteamAppIds.TryGetValue(ProfilesLibrary.ProfileName, out string appId))
            {
                logger.Log($"SteamWithEAApp: no Steam app id known for {ProfilesLibrary.ProfileName}; launching normally");
                return;
            }

            steamExe = FindSteamExe();
            if (steamExe == null)
            {
                logger.Log("SteamWithEAApp: Steam installation not found in the registry; launching normally");
                return;
            }

            logger.Log("Closing Steam and the EA app");
            ShutDownLaunchers();

            logger.Log("Starting Steam with the mod data path");
            Dictionary<string, string> env = new Dictionary<string, string> { { "GAME_DATA_DIR", modDataPath.TrimEnd('\\') } };
            FrostyModExecutor.ExecuteProcess(steamExe, "", false, false, env);
            active = true;

            // Frosty applies the mods after this; then, instead of starting the .exe directly,
            // ask the running Steam to start the game.
            FrostyModExecutor.LaunchGameOverride = (exe, args) =>
            {
                FrostyModExecutor.LaunchGameOverride = null;
                FrostyModExecutor.ExecuteProcess(steamExe, $"-applaunch {appId}");
                return true;
            };
        }

        public static void PostLaunch(ILogger logger, CancellationToken cancelToken)
        {
            FrostyModExecutor.LaunchGameOverride = null;
            if (!active)
                return;

            logger.Log("Waiting for the game to start (Steam, then the EA app, start it)");
            Process game = WaitForProcess(ProfilesLibrary.ProfileName, TimeSpan.FromMinutes(3), cancelToken);
            if (game == null)
            {
                logger.Log("The game did not start within 3 minutes. Steam is still running with the mod data path; restart Steam before playing other EA games.");
                return;
            }

            game.EnableRaisingEvents = true;
            game.Exited += (s, e) =>
            {
                // restore the launchers without GAME_DATA_DIR
                try
                {
                    ShutDownLaunchers();
                    FrostyModExecutor.ExecuteProcess(steamExe, "");
                }
                catch (Exception)
                {
                }
                active = false;
            };
        }

        private static void ShutDownLaunchers()
        {
            // ask Steam to exit cleanly first, then make sure it and the EA app are gone
            if (Process.GetProcessesByName("steam").Length > 0)
            {
                try
                {
                    FrostyModExecutor.ExecuteProcess(steamExe, "-shutdown");
                }
                catch (Exception)
                {
                }
                WaitForExit("steam", TimeSpan.FromSeconds(30));
            }

            foreach (string name in new[] { "steam", "EADesktop" })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); } catch (Exception) { }
                }
                WaitForExit(name, TimeSpan.FromSeconds(10));
            }
        }

        private static void WaitForExit(string name, TimeSpan timeout)
        {
            DateTime end = DateTime.UtcNow + timeout;
            while (Process.GetProcessesByName(name).Length > 0 && DateTime.UtcNow < end)
                Thread.Sleep(250);
        }

        private static Process WaitForProcess(string name, TimeSpan timeout, CancellationToken cancelToken)
        {
            DateTime end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end)
            {
                cancelToken.ThrowIfCancellationRequested();
                Process[] found = Process.GetProcessesByName(name);
                if (found.Length > 0)
                    return found[0];
                Thread.Sleep(1000);
            }
            return null;
        }

        private static string FindSteamExe()
        {
            string[] candidates =
            {
                ReadRegistry(Registry.CurrentUser, "Software\\Valve\\Steam", "SteamExe"),
                Combine(ReadRegistry(Registry.LocalMachine, "SOFTWARE\\WOW6432Node\\Valve\\Steam", "InstallPath"), "steam.exe"),
                Combine(ReadRegistry(Registry.LocalMachine, "SOFTWARE\\Valve\\Steam", "InstallPath"), "steam.exe"),
            };
            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            return null;
        }

        private static string Combine(string dir, string file) => string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, file);

        private static string ReadRegistry(RegistryKey root, string key, string value)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(key))
                    return k?.GetValue(value) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
