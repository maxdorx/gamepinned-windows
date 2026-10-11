using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GamePinned.Service.Platforms;
using GamePinned.Shared.Models;

namespace GamePinned.Service.Core
{
    /// <summary>
    /// Manages multiple game platforms (Steam, Epic, Riot, etc.)
    /// Coordinates protection, monitoring, and health checks across all platforms
    /// </summary>
    public class PlatformManager
    {
        private readonly List<IGamePlatform> _platforms = new();
        private Config _config;
        private readonly Action<string> _logger;
        private readonly Action<string> _debugLogger;
        private int _healthCheckIndex = 0; // Track which game to check next (staggered health checks)
        private int _healthCheckTickCount = 0;

        public event Action<string, string>? OnLauncherRestartRequested; // platformId, path

        /// <summary>
        /// Fired when a game lock or unlock operation begins during ApplyProtection/RemoveProtection.
        /// Parameters: completedCount, totalCount, gameName, isLocking (true=locking, false=unlocking)
        /// Used to stream progress to the GUI loading dialog.
        /// </summary>
        public event Action<int, int, string, bool>? OnProtectionProgress;

        /// <summary>
        /// All registered platforms
        /// </summary>
        public IReadOnlyList<IGamePlatform> Platforms => _platforms.AsReadOnly();

        public PlatformManager(Config config, string backupPath, Action<string> logger, Action<string>? debugLogger = null)
        {
            _config = config;
            _logger = logger;
            _debugLogger = debugLogger ?? (msg => { }); // no-op if not provided

            // Register all available platforms
            RegisterPlatform(new SteamPlatform(System.IO.Path.Combine(backupPath, "steam"), logger, _debugLogger));
            RegisterPlatform(new EpicPlatform(System.IO.Path.Combine(backupPath, "epic"), logger, _debugLogger));
            RegisterPlatform(new RiotPlatform(System.IO.Path.Combine(backupPath, "riot"), logger, _debugLogger));
        }

        public void UpdateConfig(Config newConfig)
        {
            _config = newConfig;
        }

        /// <summary>
        /// Register a new platform
        /// </summary>
        public void RegisterPlatform(IGamePlatform platform)
        {
            _platforms.Add(platform);
        }

        /// <summary>
        /// Detect and configure all installed platforms
        /// Updates config with detected library paths
        /// </summary>
        public bool DetectPlatforms()
        {
            bool configurationChanged = false;
            foreach (var platform in _platforms)
            {
                try
                {
                    if (!platform.IsInstalled())
                    {
                        _debugLogger($"{platform.PlatformName}: Not installed");
                        continue;
                    }

                    _debugLogger($"{platform.PlatformName}: Detected");

                    // Get or create platform config
                    var platformConfig = _config.Platforms.FirstOrDefault(p => p.PlatformId == platform.PlatformId);
                    if (platformConfig == null)
                    {
                        platformConfig = _config.GetPlatform(platform.PlatformId);
                        configurationChanged = true;
                    }

                    // Auto-detect library paths for all platforms
                    var paths = platform.DetectLibraryPaths()
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (paths.Count > 0)
                    {
                        bool pathsChanged = platformConfig.LibraryPaths.Count != paths.Count ||
                            platformConfig.LibraryPaths.Any(existing =>
                                !paths.Contains(existing, StringComparer.OrdinalIgnoreCase));
                        if (pathsChanged)
                        {
                            platformConfig.LibraryPaths = paths;
                            configurationChanged = true;
                        }
                        _debugLogger($"{platform.PlatformName}: {paths.Count} library path(s)");
                    }
                }
                catch (Exception ex)
                {
                    _logger($"{platform.PlatformName}: Error detecting - {ex.Message}");
                }
            }

            return configurationChanged;
        }

        /// <summary>
        /// Get all installed games across all enabled platforms
        /// </summary>
        public List<GameInfo> GetAllGames()
        {
            var allGames = new List<GameInfo>();
            var seenGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var platform in _platforms)
            {
                var platformConfig = _config.Platforms.FirstOrDefault(p => p.PlatformId == platform.PlatformId);
                if (platformConfig == null || !platformConfig.Enabled)
                    continue;

                // Deduplicate library paths (case-insensitive)
                var uniquePaths = platformConfig.LibraryPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var libraryPath in uniquePaths)
                {
                    try
                    {
                        var games = platform.GetInstalledGames(libraryPath);
                        foreach (var game in games)
                        {
                            // Deduplicate games by unique key (platform|name|path)
                            var key = $"{game.PlatformId}|{game.GameName}|{game.InstallPath}";
                            if (!seenGames.Add(key))
                                continue; // Skip duplicate

                            // Apply exclusion filter — OrdinalIgnoreCase because Steam folder names
                            // can differ in casing from what the GUI stored (e.g. "DOTA 2" vs "Dota 2")
                            if (platformConfig.ExcludedGames.Contains(game.GameName, StringComparer.OrdinalIgnoreCase))
                            {
                                game.IsExcluded = true;
                            }
                            allGames.Add(game);
                        }
                    }
                    catch (Exception ex)
                    {
                        _debugLogger($"Error getting games from {platform.PlatformName} at {libraryPath}: {ex.Message}");
                    }
                }
            }

            return allGames;
        }

        /// <summary>
        /// Apply protection to all games (enforcing license limits)
        /// Only protects games explicitly selected by user in ProtectedGames list
        /// </summary>
        public void ApplyProtection(bool licenseValid, bool silent = false)
        {
            if (!silent) _logger("Applying locks...");

            var allGames = GetAllGames();
            var blockedUsers = _config.BlockedUsers;

            // Determine which games user wants protected (from ProtectedGames list)
            var gamesToProtect = new List<GameInfo>();
            foreach (var game in allGames)
            {
                var platformConfig = _config.GetPlatform(game.PlatformId);

                // User explicitly selected this game for protection
                bool userWantsProtection = platformConfig.ProtectedGames.Contains(game.GameName, StringComparer.OrdinalIgnoreCase);

                // Game is not explicitly exempted
                bool notExempted = !game.IsExcluded;

                if (userWantsProtection && notExempted)
                {
                    gamesToProtect.Add(game);
                }
            }

            var usersDesc = blockedUsers.Count == 0 ? "all (default)" : string.Join(", ", blockedUsers);
            _debugLogger($"[PlatformManager] ApplyProtection: licenseValid={licenseValid} total={allGames.Count} toProtect={gamesToProtect.Count} blockedUsers={blockedUsers.Count} [{usersDesc}]");

            // Free tier: Only protect first game from user's selection
            if (!licenseValid)
            {
                if (!silent) _debugLogger("  FREE TIER: Limited to 1 game");

                // Enforce 1-game limit
                var gameToProtect = gamesToProtect.FirstOrDefault();

                // TWO-PASS: Lock first, then unlock.
                // Critical for Riot: Valorant/TFT protect C:\Riot Games with /T, cascading deny rules
                // to all subfolders. If lock and unlock run in parallel, Valorant's cascade can overwrite
                // a completed LoR unlock. Lock-then-unlock ensures the final state is always correct.

                // Pass 1: Lock the one selected game
                if (gameToProtect != null)
                {
                    OnProtectionProgress?.Invoke(1, 1, gameToProtect.GameName, true);
                    ApplyGameProtection(gameToProtect, blockedUsers, _config.BlockAllNonAdminByDefault, silent);
                }

                // Pass 2: Unlock all other games AFTER locking is complete
                int unlockCompleted0 = 0;
                var otherGames = allGames.Where(g => !g.Equals(gameToProtect)).ToList();
                System.Threading.Tasks.Parallel.ForEach(otherGames, game =>
                {
                    var n = Interlocked.Increment(ref unlockCompleted0);
                    OnProtectionProgress?.Invoke(n, otherGames.Count, game.GameName, false);
                    var platform = _platforms.FirstOrDefault(p => p.PlatformId == game.PlatformId);
                    if (platform == null) return;
                    try { platform.RemoveProtection(game, blockedUsers); } catch (Exception ex) { _debugLogger($"[PlatformManager] RemoveProtection({game.GameName}) failed: {ex.Message}"); }
                });

                if (gamesToProtect.Count > 1)
                    _logger($"\n  ⚠️  FREE TIER: Only locking '{gameToProtect?.GameName}'. Remove others from lock list or upgrade.");
                else if (gamesToProtect.Count == 0)
                    _logger("  ℹ️  No games selected. Click 'Lock' on a game to lock it.");

                return;
            }

            // Paid tier: Protect all games in user's selection
            if (!silent) _debugLogger("  PAID TIER: Unlimited locks");

            // TWO-PASS: Lock first, then unlock.
            // This prevents a race condition where Valorant's /T cascade (locking C:\Riot Games
            // and all subfolders) runs AFTER LoR's unlock — re-locking LoR in the same cycle.

            // Pass 1: Lock all protected games in parallel
            int lockCompleted = 0;
            int lockTotal = gamesToProtect.Count;
            System.Threading.Tasks.Parallel.ForEach(gamesToProtect, game =>
            {
                var n = Interlocked.Increment(ref lockCompleted);
                OnProtectionProgress?.Invoke(n, lockTotal, game.GameName, true);
                ApplyGameProtection(game, blockedUsers, _config.BlockAllNonAdminByDefault, silent);
            });

            // Pass 2: Unlock all unprotected games AFTER all locks are applied
            var gamesToUnprotect = allGames.Except(gamesToProtect).ToList();
            int unlockCompleted = 0;
            int unlockTotal = gamesToUnprotect.Count;
            System.Threading.Tasks.Parallel.ForEach(gamesToUnprotect, game =>
            {
                var n = Interlocked.Increment(ref unlockCompleted);
                OnProtectionProgress?.Invoke(n, unlockTotal, game.GameName, false);
                var platform = _platforms.FirstOrDefault(p => p.PlatformId == game.PlatformId);
                if (platform == null) return;
                try { platform.RemoveProtection(game, blockedUsers); } catch (Exception ex) { _debugLogger($"[PlatformManager] RemoveProtection({game.GameName}) failed: {ex.Message}"); }
            });

            if (gamesToProtect.Count == 0)
                _logger("  ℹ️  No games selected. Click 'Lock' on games to lock them.");
        }

        /// <summary>
        /// Apply protection to a single game (respecting license tier)
        /// Paid tier: auto-locks all new games (caller already added to ProtectedGames)
        /// Free tier: only locks if game is in ProtectedGames and under the 1-game limit
        /// </summary>
        public void ApplySingleGameProtection(GameInfo game, bool licenseValid)
        {
            var platformConfig = _config.GetPlatform(game.PlatformId);

            // Free tier: must be explicitly in ProtectedGames (user locks manually via GUI)
            if (!licenseValid && !platformConfig.ProtectedGames.Contains(game.GameName, StringComparer.OrdinalIgnoreCase))
                return;

            // Free tier: Check if already at limit
            if (!licenseValid)
            {
                var allGames = GetAllGames();
                var gamesToProtect = allGames.Where(g =>
                {
                    var pc = _config.GetPlatform(g.PlatformId);
                    return pc.ProtectedGames.Contains(g.GameName, StringComparer.OrdinalIgnoreCase) && !g.IsExcluded;
                }).ToList();

                if (gamesToProtect.Count >= 1 && !gamesToProtect.Contains(game))
                {
                    _logger($"  {game.GameName} - Lock skipped (Free Tier limit: 1 game)");
                    return;
                }
            }

            var blockedUsers = _config.BlockedUsers;
            ApplyGameProtection(game, blockedUsers, _config.BlockAllNonAdminByDefault, false);
        }

        /// <summary>
        /// Apply protection to a single game
        /// </summary>
        private void ApplyGameProtection(GameInfo game, List<string> blockedUsers, bool blockAllNonAdminByDefault, bool silent)
        {
            try
            {
                var platform = _platforms.FirstOrDefault(p => p.PlatformId == game.PlatformId);
                if (platform == null)
                {
                    _logger($"  {game.GameName} - ERROR: Platform {game.PlatformId} not found");
                    return;
                }

                // Skip re-applying protection if the platform is currently patching.
                // This prevents PushConfig (Steam game changes), maintenance-mode toggle,
                // or any other ApplyProtection trigger from re-locking Riot games mid-update.
                if (platform.IsUpdateInProgress())
                {
                    _debugLogger($"[ApplyGameProtection] Skipping {game.GameName} ({game.PlatformId}) — platform update in progress");
                    return;
                }

                platform.ApplyProtection(game, blockedUsers, blockAllNonAdminByDefault);
                if (!silent)
                {
                    if (platform.CheckProtectionActive(game.InstallPath))
                        _logger($"  {game.GameName} ({game.PlatformId}) - Locked");
                    else
                        _logger($"[WARNING] Lock FAILED: {game.GameName} ({game.PlatformId})");
                }
            }
            catch (Exception ex)
            {
                _logger($"  {game.GameName} ({game.PlatformId}) - ERROR: {ex.Message}");
                _debugLogger($"[ApplyGameProtection] {game.GameName} ({game.PlatformId}) threw: {ex}");
            }
        }

        /// <summary>
        /// Remove protection from all games
        /// </summary>
        public void RemoveProtection(string reason = "unspecified")
        {
            _logger($"Removing locks... (reason: {reason})");

            var allGames = GetAllGames();
            var blockedUsers = _config.BlockedUsers;
            int removeCompleted = 0;
            int removeTotal = allGames.Count;

            System.Threading.Tasks.Parallel.ForEach(allGames, game =>
            {
                var n = Interlocked.Increment(ref removeCompleted);
                OnProtectionProgress?.Invoke(n, removeTotal, game.GameName, false);
                try
                {
                    var platform = _platforms.FirstOrDefault(p => p.PlatformId == game.PlatformId);
                    platform?.RemoveProtection(game, blockedUsers);
                    _logger($"  {game.GameName} ({game.PlatformId}) - Unlocked");
                }
                catch (Exception ex)
                {
                    _logger($"  {game.GameName} ({game.PlatformId}) - ERROR: {ex.Message}");
                    _debugLogger($"[RemoveProtection] {game.GameName} ({game.PlatformId}) threw: {ex}");
                }
            });
        }

        /// <summary>
        /// Remove explicit GamePinned-style delete-deny residue across all platforms while
        /// preserving launcher/vendor allow ACEs, inheritance, and ownership. Heavier than
        /// RemoveProtection because it discovers residue recursively; call only at lifecycle
        /// transition points (uninstall, detected version change on startup), never from routine
        /// paths like maintenance-mode toggle. See Critical #59.
        /// </summary>
        public void ResetAllToDefault(string reason = "unspecified")
        {
            _logger($"Cleaning GamePinned delete-deny residue from all game folders... (reason: {reason})");

            var allGames = GetAllGames();
            var byPlatform = allGames
                .GroupBy(g => g.PlatformId)
                .ToDictionary(g => g.Key, g => g.ToList());

            _debugLogger($"[ResetAllToDefault] {allGames.Count} game(s) found: " +
                string.Join(", ", _platforms.Select(p => $"{p.PlatformId}={(byPlatform.TryGetValue(p.PlatformId, out var l) ? l.Count : 0)}")));

            foreach (var platform in _platforms)
            {
                var gamesForPlatform = byPlatform.TryGetValue(platform.PlatformId, out var list) ? list : new List<GameInfo>();
                try
                {
                    platform.ResetToDefault(gamesForPlatform);
                }
                catch (Exception ex)
                {
                    _logger($"  {platform.PlatformName} - ResetToDefault ERROR: {ex.Message}");
                    _debugLogger($"[ResetAllToDefault] {platform.PlatformName} threw: {ex}");
                }
            }

            _logger("  GamePinned delete-deny residue cleanup completed.");
        }

        /// <summary>
        /// Start all platform watchers for uninstall/new game detection
        /// </summary>
        public void StartWatchers(Action<GameInfo> onUninstallDetected, Action<GameInfo> onNewGameDetected)
        {
            foreach (var platform in _platforms)
            {
                var platformConfig = _config.Platforms.FirstOrDefault(p => p.PlatformId == platform.PlatformId);
                if (platformConfig == null || !platformConfig.Enabled)
                    continue;

                try
                {
                    platform.StartUninstallWatcher(onUninstallDetected);
                    platform.StartNewGameWatcher(onNewGameDetected);
                    _debugLogger($"Started watchers for {platform.PlatformName}");
                }
                catch (Exception ex)
                {
                    _debugLogger($"Error starting watchers for {platform.PlatformName}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Stop all platform watchers.
        /// </summary>
        /// <param name="releaseHandles">
        /// True only on full service shutdown. Forwarded to each platform so Riot can release
        /// its no-delete-share file handles. Default false keeps Riot Layer 2 alive across the
        /// transient stop+start cycles in UpdateConfig / ToggleMaintenance (Critical #53).
        /// </param>
        public void StopWatchers(bool releaseHandles = false)
        {
            foreach (var platform in _platforms)
            {
                try
                {
                    platform.StopWatchers(releaseHandles);
                }
                catch (Exception ex)
                {
                    _debugLogger($"Error stopping watchers for {platform.PlatformName}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Perform health check across all platforms
        /// </summary>
        public void HealthCheck(bool licenseValid)
        {
            // Staggered health checks: Check a small batch of games per call
            // This spreads the load over time instead of checking all games at once
            // With 50 games and batch size 5, full rotation takes ~100 seconds (10s timer * 10 ticks)

            var allGames = GetAllGames();
            var blockedUsers = _config.BlockedUsers;

            // Determine which games should be protected based on ProtectedGames list + license
            var gamesToProtect = new List<GameInfo>();
            foreach (var game in allGames)
            {
                var platformConfig = _config.GetPlatform(game.PlatformId);
                if (platformConfig.ProtectedGames.Contains(game.GameName, StringComparer.OrdinalIgnoreCase) && !game.IsExcluded)
                    gamesToProtect.Add(game);
            }

            // Free tier: Only first game
            if (!licenseValid && gamesToProtect.Count > 1)
                gamesToProtect = gamesToProtect.Take(1).ToList();

            if (gamesToProtect.Count == 0) return;

            // Check a batch of games (default: 5 games per health check call)
            // Cap batch at gamesToProtect.Count to avoid checking the same game multiple times
            const int batchSize = 5;
            int actualBatch = Math.Min(batchSize, gamesToProtect.Count);
            var gamesToCheck = new List<GameInfo>();

            for (int i = 0; i < actualBatch; i++)
            {
                // Rotate through games using modulo to wrap around
                int index = _healthCheckIndex % gamesToProtect.Count;
                gamesToCheck.Add(gamesToProtect[index]);
                _healthCheckIndex++;
            }

            // Reset index if it gets too large (prevent overflow)
            if (_healthCheckIndex > 10000)
            {
                _healthCheckIndex = 0;
            }

            // Perform health check on this batch
            foreach (var game in gamesToCheck)
            {
                try
                {
                    var platform = _platforms.FirstOrDefault(p => p.PlatformId == game.PlatformId);
                    if (platform == null) continue;

                    platform.HealthCheck(new List<GameInfo> { game }, blockedUsers, _config.BlockAllNonAdminByDefault);
                }
                catch (Exception ex)
                {
                    _logger($"Health check error for {game.GameName}: {ex.Message}");
                    _debugLogger($"[HealthCheck] {game.GameName} threw: {ex}");
                }
            }

            _healthCheckTickCount++;
            // Log a summary every ~1 hour (360 ticks × 10s = 3600s)
            if (_healthCheckTickCount % 360 == 0)
            {
                _debugLogger($"[HealthCheck] Hourly summary: {gamesToProtect.Count} game(s) monitored, {gamesToCheck.Count} in last batch");
            }
        }

        /// <summary>
        /// Backup all platform manifests
        /// </summary>
        public void BackupManifests()
        {
            foreach (var platform in _platforms)
            {
                var platformConfig = _config.Platforms.FirstOrDefault(p => p.PlatformId == platform.PlatformId);
                if (platformConfig == null || !platformConfig.Enabled)
                    continue;

                foreach (var libraryPath in platformConfig.LibraryPaths)
                {
                    try
                    {
                        platform.BackupManifests(libraryPath, platformConfig.ExcludedGames);
                    }
                    catch (Exception ex)
                    {
                        _logger($"Backup error for {platform.PlatformName}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Restore manifests for a specific game
        /// </summary>
        public void RestoreGameManifests(GameInfo game)
        {
            try
            {
                var platform = _platforms.FirstOrDefault(p => p.PlatformId == game.PlatformId);
                if (platform == null)
                {
                    _logger($"Cannot restore {game.GameName}: Platform {game.PlatformId} not found");
                    return;
                }

                // Get library path from game's install path
                var libraryPath = GetLibraryPathFromGame(game);
                if (string.IsNullOrEmpty(libraryPath))
                {
                    _logger($"Cannot restore {game.GameName}: Library path not found");
                    return;
                }

                var platformConfig = _config.Platforms.FirstOrDefault(p => p.PlatformId == game.PlatformId);
                var excludedGames = platformConfig?.ExcludedGames ?? new List<string>();

                platform.RestoreGameManifest(game, libraryPath, excludedGames);
                _debugLogger($"Restored manifest for {game.GameName}");
            }
            catch (Exception ex)
            {
                _logger($"Error restoring manifests for {game.GameName}: {ex.Message}");
            }
        }

        /// <summary>
        /// Kill launcher for a specific platform
        /// </summary>
        public void KillPlatformLauncher(string platformId)
        {
            _debugLogger($"[PlatformManager] KillLauncher: {platformId}");
            var platform = _platforms.FirstOrDefault(p => p.PlatformId == platformId);
            platform?.KillLauncher();
        }

        /// <summary>
        /// Locate the Epic Games Launcher executable.
        /// Checks the registry InstallLocation first (handles custom-drive installs),
        /// then falls back to the two standard Program Files paths.
        /// </summary>
        private static string? FindEpicLauncherExe()
        {
            // 1. Registry: HKLM\SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher -> InstallLocation
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher");
                var installLocation = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(installLocation))
                {
                    var candidate = System.IO.Path.Combine(
                        installLocation, "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe");
                    if (System.IO.File.Exists(candidate)) return candidate;
                }
            }
            catch { }

            // 2. Common fixed paths
            var fallbackPaths = new[]
            {
                @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
                @"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"
            };
            return fallbackPaths.FirstOrDefault(System.IO.File.Exists);
        }

        /// <summary>
        /// Locate the Riot Client Services executable.
        /// Reads C:\ProgramData\Riot Games\RiotClientInstalls.json first — this file is
        /// written by the Riot installer and reflects the actual install location even when
        /// Riot is installed on a non-C: drive. Falls back to the default path if missing.
        /// </summary>
        private static string? FindRiotLauncherExe()
        {
            // 1. Parse RiotClientInstalls.json for the true install path
            const string installsJson = @"C:\ProgramData\Riot Games\RiotClientInstalls.json";
            try
            {
                if (System.IO.File.Exists(installsJson))
                {
                    var content = System.IO.File.ReadAllText(installsJson);
                    using var doc = System.Text.Json.JsonDocument.Parse(content);
                    // Prefer rc_live (production), then rc_default
                    foreach (var key in new[] { "rc_live", "rc_default" })
                    {
                        if (doc.RootElement.TryGetProperty(key, out var prop))
                        {
                            var path = prop.GetString();
                            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                                return path;
                        }
                    }
                }
            }
            catch { }

            // 2. Fallback to the default path
            const string defaultPath = @"C:\Riot Games\Riot Client\RiotClientServices.exe";
            return System.IO.File.Exists(defaultPath) ? defaultPath : null;
        }

        /// <summary>
        /// Start launcher for a specific platform
        /// </summary>
        public void StartPlatformLauncher(string platformId)
        {
            var platform = _platforms.FirstOrDefault(p => p.PlatformId == platformId);
            if (platform == null) return;

            // Session 0 Isolation Fix:
            // Service cannot launch GUI apps directly. Request GUI to do it via Pipe.

            if (platform.PlatformId == "steam")
            {
                var steamPaths = platform.DetectLibraryPaths();
                var steamExe = steamPaths
                    .Select(p => System.IO.Path.Combine(p, "steam.exe"))
                    .FirstOrDefault(System.IO.File.Exists);

                if (!string.IsNullOrEmpty(steamExe))
                {
                    OnLauncherRestartRequested?.Invoke(platformId, steamExe);
                    _debugLogger($"Requested GUI to restart {platform.PlatformName}");
                    return;
                }
            }
            else if (platform.PlatformId == "epic")
            {
                var epicExe = FindEpicLauncherExe();
                if (!string.IsNullOrEmpty(epicExe))
                {
                    OnLauncherRestartRequested?.Invoke(platformId, epicExe);
                    _debugLogger($"Requested GUI to restart {platform.PlatformName}");
                    return;
                }
                else
                {
                    _debugLogger($"Epic launcher exe not found (checked registry + common paths).");
                }
            }
            else if (platform.PlatformId == "riot")
            {
                var riotExe = FindRiotLauncherExe();
                if (!string.IsNullOrEmpty(riotExe))
                {
                    OnLauncherRestartRequested?.Invoke(platformId, riotExe);
                    _debugLogger($"Requested GUI to restart {platform.PlatformName}");
                    return;
                }
                else
                {
                    _debugLogger($"Riot launcher exe not found (checked RiotClientInstalls.json + common paths).");
                }
            }

            // Fallback: Try platform's StartLauncher() method
            _debugLogger($"Using fallback StartLauncher for {platform.PlatformName}");
            platform.StartLauncher();
        }

        /// <summary>
        /// Get library path from game's install path
        /// </summary>
        private string? GetLibraryPathFromGame(GameInfo game)
        {
            // Steam: Extract steamapps path from install path
            if (game.PlatformId == "steam" && game.InstallPath.Contains("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                var idx = game.InstallPath.IndexOf("steamapps", StringComparison.OrdinalIgnoreCase);
                var path = game.InstallPath.Substring(0, idx).TrimEnd('\\', '/');
                return string.IsNullOrEmpty(path) ? game.InstallPath : path;
            }

            // Epic: Fixed manifest location
            if (game.PlatformId == "epic")
            {
                return @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
            }

            // Riot: Fixed manifest location
            if (game.PlatformId == "riot")
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Riot Games", "Metadata");
            }

            return null;
        }

        /// <summary>
        /// Clear login/session data for the given platform so next launch shows a fresh login screen.
        /// Detects the currently logged-in Windows user's LocalAppData and SID automatically.
        /// </summary>
        public void ClearPlatformSession(string platformId)
        {
            var platform = _platforms.FirstOrDefault(p => p.PlatformId == platformId);
            if (platform == null)
            {
                _debugLogger($"[ClearSession] Platform '{platformId}' not found");
                return;
            }

            var (localAppData, userSid) = GetCurrentUserInfo();
            if (string.IsNullOrEmpty(localAppData))
            {
                _debugLogger("[ClearSession] Could not determine current user LocalAppData — aborting");
                return;
            }

            _debugLogger($"[ClearSession] Clearing {platform.PlatformName} session — user localAppData={localAppData} sid={userSid ?? "unknown"}");
            platform.ClearSession(localAppData, userSid);
        }

        /// <summary>
        /// Get the currently logged-in Windows user's LocalAppData path and SID.
        /// Uses Winlogon LastLoggedOnUser registry value; falls back to scanning C:\Users\.
        /// Required because the service runs as SYSTEM and cannot use %LOCALAPPDATA% directly.
        /// </summary>
        private (string? localAppData, string? userSid) GetCurrentUserInfo()
        {
            // Primary: read LastLoggedOnUser from Winlogon (most reliable for single-user sessions)
            try
            {
                using var winlogon = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
                var lastUser = winlogon?.GetValue("LastLoggedOnUser")?.ToString();
                if (!string.IsNullOrEmpty(lastUser))
                {
                    // Format is "COMPUTERNAME\username" or just "username"
                    var userName = lastUser.Contains('\\') ? lastUser.Split('\\')[1] : lastUser;
                    var localAppData = System.IO.Path.Combine(@"C:\Users", userName, "AppData", "Local");
                    if (System.IO.Directory.Exists(localAppData))
                    {
                        var sid = GetUserSidFromProfileList(userName);
                        _debugLogger($"[ClearSession] Current user: {userName} sid={sid ?? "not found"}");
                        return (localAppData, sid);
                    }
                }
            }
            catch { }

            // Fallback: scan C:\Users for non-system profile folders, pick most recently used
            try
            {
                string[] skip = ["Public", "Default", "Default User", "All Users"];
                var userDir = System.IO.Directory.GetDirectories(@"C:\Users")
                    .Where(d => !skip.Contains(System.IO.Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                    .OrderByDescending(d => System.IO.Directory.GetLastWriteTime(d))
                    .FirstOrDefault();
                if (userDir != null)
                {
                    var localAppData = System.IO.Path.Combine(userDir, "AppData", "Local");
                    if (System.IO.Directory.Exists(localAppData))
                    {
                        var userName = System.IO.Path.GetFileName(userDir);
                        var sid = GetUserSidFromProfileList(userName);
                        _debugLogger($"[ClearSession] Current user (fallback scan): {userName} sid={sid ?? "not found"}");
                        return (localAppData, sid);
                    }
                }
            }
            catch { }

            return (null, null);
        }

        /// <summary>
        /// Look up a Windows username's SID from the ProfileList registry key.
        /// </summary>
        private static string? GetUserSidFromProfileList(string userName)
        {
            try
            {
                using var profileList = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
                if (profileList == null) return null;
                foreach (var sidKey in profileList.GetSubKeyNames())
                {
                    using var profileKey = profileList.OpenSubKey(sidKey);
                    var profilePath = profileKey?.GetValue("ProfileImagePath")?.ToString();
                    if (profilePath != null &&
                        System.IO.Path.GetFileName(profilePath).Equals(userName, StringComparison.OrdinalIgnoreCase))
                        return sidKey;
                }
            }
            catch { }
            return null;
        }
    }
}
