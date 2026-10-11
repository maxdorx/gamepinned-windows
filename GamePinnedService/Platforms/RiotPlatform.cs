using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Principal;
using GamePinned.Service.Core;
using GamePinned.Shared.Models;

namespace GamePinned.Service.Platforms
{
    /// <summary>
    /// Riot Client platform implementation for GamePinned
    /// Handles Riot games (Valorant, League of Legends, etc.) detection, protection, and manifest backup/restore
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class RiotPlatform : IGamePlatform
    {
        private List<FileSystemWatcher> _watchers = new();
        private volatile bool _stoppingWatchers = false;
        private string _backupPath;
        private Action<string> _logger;
        private Action<string> _debugLogger;
        private Action<GameInfo>? _onUninstallDetected;
        private Action<GameInfo>? _onNewGameDetected;
        private readonly HashSet<string> _notInstalledLogged = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _aclMutationLock = new();

        // Hybrid patcher detection — both signals must agree (Critical #30).
        //
        // Signal A (disk I/O rate on RiotClientServices.exe): sampled per HealthCheck tick
        // via GetProcessIoCounters. Idle client writes ~0 KB/s; active patcher sustains MB/s.
        // Threshold is 512 KB/s sustained for 2 consecutive ticks (~20s) — well above any
        // background telemetry noise.
        private long _lastIoWriteBytes = 0;
        private DateTime _lastIoSampleTime = DateTime.MinValue;
        private int _sustainedHighIoTicks = 0;

        // Signal B (narrow FSW): rolling window of file Create/Delete events confined to
        // known game install folders. Filters out Riot Client's own folder and any path
        // outside detected game install paths. Requires sustained activity (≥5 events in
        // 10s) to filter Vanguard/anti-cheat single-event noise.
        private readonly Queue<long> _recentFswEventTicks = new Queue<long>();
        private readonly object _fswEventLock = new object();

        // Cached game install paths — repopulated on every GetInstalledGames() call.
        // Read lock-free via volatile reference swap; written by GetInstalledGames replacing
        // the entire HashSet atomically.
        private volatile HashSet<string> _knownGameInstallPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Subset of known paths that GamePinned currently intends to protect. Activity
        // discovery remains broad for patch detection, but the mass-deletion fallback must
        // never kill Riot for a game the administrator explicitly unlocked. Updated by the
        // same serialized ApplyProtection/RemoveProtection lifecycle as ACLs and handles.
        private volatile HashSet<string> _protectedGameInstallPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Uninstall detection via FSW deletion ratio (secondary layer, belt-and-suspenders).
        // Primary layer: open file handles with no-delete-sharing (see _gameHandles below).
        private readonly Queue<long> _uninstallDeleteTicks = new Queue<long>();
        private readonly Queue<long> _uninstallCreateTicks = new Queue<long>();
        private readonly object _uninstallDetectLock = new object();
        private int _uninstallKillInProgress = 0; // 0 = idle, 1 = kill in progress (Interlocked)
        private const int UninstallWindowSeconds = 20;
        private const int UninstallMinDeletions = 15;
        private const double UninstallMinDeleteRatio = 3.0; // deletes must be 3× creates

        // Primary uninstall prevention: open game executables with FileShare.ReadWrite but NOT
        // FileShare.Delete. Any process trying to delete a held file gets ERROR_SHARING_VIOLATION
        // synchronously — before any deletion happens, with zero latency.
        // Released during patching (IsRiotPlatformPatching) so the patcher can replace files.
        private readonly Dictionary<string, List<FileStream>> _gameHandles =
            new Dictionary<string, List<FileStream>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gameHandlesLock = new object();

        private const long HighIoThresholdBytesPerSec = 512 * 1024; // 512 KB/s
        private const int RequiredSustainedTicks = 2;               // ~20s of sustained high I/O
        private const int FswActivityWindowSeconds = 10;
        private const int FswActivityMinEvents = 5;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        public string PlatformName => "Riot Games";
        public string PlatformId => "riot";

        public RiotPlatform(string backupPath, Action<string> logger, Action<string>? debugLogger = null)
        {
            _backupPath = backupPath;
            _logger = logger;
            _debugLogger = debugLogger ?? (_ => { });
            Directory.CreateDirectory(_backupPath);
        }

        public bool IsInstalled()
        {
            if (Directory.Exists(@"C:\Riot Games\Riot Client"))
                return true;

            var metadataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Riot Games", "Metadata");
            if (Directory.Exists(metadataPath))
                return true;

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Riot Client");
                if (key != null)
                    return true;
            }
            catch { }

            try
            {
                var proc = Process.GetProcessesByName("RiotClientServices").FirstOrDefault();
                if (proc != null)
                    return true;
            }
            catch { }

            return false;
        }

        public List<string> DetectLibraryPaths()
        {
            var libraryPaths = new List<string>();
            var metadataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Riot Games", "Metadata");

            if (Directory.Exists(metadataPath))
            {
                libraryPaths.Add(metadataPath);
            }

            return libraryPaths;
        }

        public List<GameInfo> GetInstalledGames(string libraryPath)
        {
            var games = new List<GameInfo>();

            if (!Directory.Exists(libraryPath))
                return games;

            try
            {
                // IMPORTANT: YAML files are in subfolders (bacon.live\bacon.live.product_settings.yaml)
                var yamlFiles = Directory.GetFiles(libraryPath, "*.product_settings.yaml", SearchOption.AllDirectories);

                foreach (var yamlFile in yamlFiles)
                {
                    try
                    {
                        var game = ParseYamlManifest(yamlFile);
                        if (game == null)
                            continue;

                        if (!Directory.Exists(game.InstallPath))
                            continue;

                        games.Add(game);
                    }
                    catch (Exception ex)
                    {
                        _logger($"[Riot] Failed to parse {Path.GetFileName(yamlFile)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger($"[Riot] Error scanning library: {ex.Message}");
            }

            // Refresh narrow FSW filter set — atomic swap, lock-free read in OnNarrowActivityEvent.
            // Riot has exactly one library path so this call sees the full game set.
            var newPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games)
                if (!string.IsNullOrEmpty(g.InstallPath))
                    newPaths.Add(g.InstallPath.TrimEnd('\\'));
            _knownGameInstallPaths = newPaths;

            return games;
        }

        private GameInfo? ParseYamlManifest(string yamlFile)
        {
            try
            {
                // Only process .product_settings.yaml files (actual game manifests)
                var fileName = Path.GetFileName(yamlFile).ToLowerInvariant();
                if (!fileName.EndsWith(".product_settings.yaml"))
                    return null;

                var yamlText = File.ReadAllText(yamlFile);

                // Simple text-based parsing to avoid YamlDotNet deserialization issues with nested structures
                // Just extract the fields we need using string operations
                string? installPath = null;

                // Look for product_install_full_path: "C:/Riot Games/..."
                var fullPathMatch = System.Text.RegularExpressions.Regex.Match(
                    yamlText,
                    @"product_install_full_path:\s*[""']?(.+?)[""']?\s*$",
                    System.Text.RegularExpressions.RegexOptions.Multiline
                );

                if (fullPathMatch.Success)
                {
                    installPath = fullPathMatch.Groups[1].Value.Trim().Trim('"', '\'');
                }
                else
                {
                    // Fallback: Look for product_install_root
                    var rootMatch = System.Text.RegularExpressions.Regex.Match(
                        yamlText,
                        @"product_install_root:\s*[""']?(.+?)[""']?\s*$",
                        System.Text.RegularExpressions.RegexOptions.Multiline
                    );

                    if (rootMatch.Success)
                    {
                        installPath = rootMatch.Groups[1].Value.Trim().Trim('"', '\'');
                    }
                }

                if (string.IsNullOrEmpty(installPath))
                    return null;

                // Normalize and canonicalize before any refinement or Directory.Exists check.
                // Never trim "C:\\" into drive-relative "C:": a malformed/unknown product
                // manifest could otherwise make the shared-root fallback accept an entire drive
                // as a game and later apply protection at that scope.
                installPath = installPath.Replace('/', '\\').Trim();
                if (!Path.IsPathFullyQualified(installPath))
                {
                    _debugLogger($"[Riot] {Path.GetFileName(yamlFile)}: install path is not fully qualified, skipping");
                    return null;
                }

                installPath = Path.GetFullPath(installPath);
                var installRoot = Path.GetPathRoot(installPath);
                if (string.IsNullOrEmpty(installRoot) ||
                    string.Equals(installPath.TrimEnd('\\'), installRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    _debugLogger($"[Riot] {Path.GetFileName(yamlFile)}: install path resolves to a volume/share root, skipping");
                    return null;
                }
                installPath = installPath.TrimEnd('\\');

                // Filter out Riot Client itself (only want actual games)
                if (installPath.Contains("Riot Client", StringComparison.OrdinalIgnoreCase))
                    return null;

                var gameName = GetGameNameFromYamlFile(yamlFile);

                // Refine path: finds game-specific subfolder, or returns null if game not installed.
                // This filters out phantom games whose YAML metadata exists but game folder is gone.
                var refinedPath = RefineRiotInstallPath(gameName, installPath);
                if (refinedPath == null)
                    return null; // Game not installed — skip

                installPath = refinedPath;

                return new GameInfo
                {
                    PlatformId = "riot",
                    GameName = gameName,
                    InstallPath = installPath,
                    ManifestPath = yamlFile,
                    IsProtected = CheckProtectionActiveCore(installPath, yamlFile)
                };
            }
            catch (Exception ex)
            {
                _logger($"[Riot] YAML parse error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Refines a generic install root (e.g. C:\Riot Games) to a game-specific subfolder,
        /// or returns null if the game is not actually installed.
        ///
        /// Problem: Riot YAML files report C:\Riot Games as product_install_full_path for games
        /// like Valorant and TFT even after those games are uninstalled (the metadata YAML stays).
        /// C:\Riot Games exists (it's the shared root), so the Directory.Exists check in
        /// GetInstalledGames passes, causing phantom games to appear in the GUI.
        ///
        /// Fix: If a game has known subfolder candidates (e.g. "VALORANT") but none exist under
        /// the reported path, the game is not installed → return null → skip the game.
        /// If the path already contains a game-specific component (e.g. "LoR" in
        /// C:\Riot Games\LoR\live\Game), the path is already specific → return as-is.
        /// </summary>
        private string? RefineRiotInstallPath(string gameName, string basePath)
        {
            // Maps game name → candidate subfolder names (checked in order, first existing wins)
            var subfolderCandidates = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Valorant", new[] { "VALORANT", "Valorant" } },
                { "League of Legends", new[] { "League of Legends" } },
                { "Teamfight Tactics", new[] { "Teamfight Tactics", "TeamFightTactics", "TFT" } },
                { "Legends of Runeterra", new[] { "Legends of Runeterra", "LoR" } }
            };

            if (!subfolderCandidates.TryGetValue(gameName, out var candidates))
            {
                // Unknown game name (e.g. Teamfighttactics.pbe, valorant.pbe, etc.)
                // If basePath looks like the shared Riot root (has a "Riot Client" sibling),
                // returning it would apply deny rules on the ENTIRE C:\Riot Games tree.
                // Return null to skip instead — the game has no known subfolder mapping.
                if (Directory.Exists(Path.Combine(basePath, "Riot Client")))
                {
                    if (_notInstalledLogged.Add(gameName))
                        _debugLogger($"[Riot] {gameName}: basePath is Riot root ({basePath}) — unknown game, no subfolder mapping, skipping to avoid root-level deny rules");
                    return null;
                }
                return basePath; // basePath is already a specific path (not the shared root)
            }

            // Check if basePath already contains a game-specific component.
            // e.g. C:\Riot Games\LoR\live\Game contains "LoR" → already refined, return as-is.
            // This prevents false-negative on LoR which already has a deep specific path from YAML.
            var pathParts = basePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Any(part => candidates.Any(c => c.Equals(part, StringComparison.OrdinalIgnoreCase))))
                return basePath; // Already game-specific

            // basePath is a root/shared path (e.g. C:\Riot Games). Look for the game subfolder.
            foreach (var subfolder in candidates)
            {
                var specificPath = Path.Combine(basePath, subfolder);
                if (Directory.Exists(specificPath))
                {
                    _notInstalledLogged.Remove(gameName); // Reset so re-uninstall logs again
                    _debugLogger($"[Riot] Path refined: {gameName} → {specificPath} (was {basePath})");
                    return specificPath;
                }
            }

            // Game has known candidates but NONE exist → game is not installed.
            // The YAML metadata remains after uninstall — skip this game entirely.
            if (_notInstalledLogged.Add(gameName))
                _debugLogger($"[Riot] {gameName}: no install folder found under {basePath} — not installed, skipping");
            return null;
        }

        private string GetGameNameFromYamlFile(string yamlFile)
        {
            var fileName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(yamlFile));

            var gameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "valorant.live", "Valorant" },
                { "valorant", "Valorant" },
                { "league_of_legends.live", "League of Legends" },
                { "league_of_legends", "League of Legends" },
                { "bacon.live", "Legends of Runeterra" },
                { "bacon", "Legends of Runeterra" },
                { "teamfighttactics.live", "Teamfight Tactics" },
                { "teamfighttactics", "Teamfight Tactics" }
            };

            if (gameMap.TryGetValue(fileName, out var gameName))
                return gameName;

            return char.ToUpper(fileName[0]) + fileName.Substring(1);
        }

        /// <summary>
        /// Opens all .exe files in the game folder with FileShare.ReadWrite but NOT FileShare.Delete.
        /// Windows blocks any attempt to delete a file while a no-delete-share handle is open,
        /// returning ERROR_SHARING_VIOLATION synchronously before the deletion happens.
        /// The running game process is unaffected — the OS loader only needs read access.
        /// </summary>
        private void AcquireGameHandles(string installPath)
        {
            lock (_gameHandlesLock)
            {
                if (_gameHandles.ContainsKey(installPath)) return; // already held

                var handles = new List<FileStream>();
                try
                {
                    foreach (var pattern in new[] { "*.exe", "*.dll" })
                    {
                        foreach (var file in Directory.EnumerateFiles(installPath, pattern, SearchOption.AllDirectories))
                        {
                            try
                            {
                                // FileShare.ReadWrite allows game launch and normal operations.
                                // Omitting FileShare.Delete means no process can delete this file.
                                handles.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                            }
                            catch { /* file may be locked by a running game — skip, still protected by ACL */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _debugLogger($"[Riot] AcquireGameHandles enumerate failed for {Path.GetFileName(installPath)}: {ex.Message}");
                }

                if (handles.Count > 0)
                    _gameHandles[installPath] = handles;

                _debugLogger($"[Riot] AcquireGameHandles: {handles.Count} exe+dll handles held for {Path.GetFileName(installPath)}");
            }
        }

        private void ReleaseGameHandles(string installPath)
        {
            lock (_gameHandlesLock)
            {
                if (!_gameHandles.TryGetValue(installPath, out var handles)) return;
                foreach (var fs in handles)
                    try { fs.Dispose(); } catch { }
                _gameHandles.Remove(installPath);
                _debugLogger($"[Riot] ReleaseGameHandles: released for {Path.GetFileName(installPath)}");
            }
        }

        private void ReleaseAllHandles()
        {
            lock (_gameHandlesLock)
            {
                foreach (var kvp in _gameHandles)
                    foreach (var fs in kvp.Value)
                        try { fs.Dispose(); } catch { }
                _gameHandles.Clear();
                _debugLogger("[Riot] ReleaseAllHandles: all exe handles released");
            }
        }

        public void ApplyProtection(GameInfo game, List<string> blockedUsers, bool blockAllNonAdminByDefault)
        {
            lock (_aclMutationLock)
            {
                ApplyProtectionCore(game, blockedUsers, blockAllNonAdminByDefault);
            }
        }

        private void SetGameProtectionIntent(string installPath, bool isProtected)
        {
            if (string.IsNullOrWhiteSpace(installPath))
                return;

            var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
            var updatedPaths = new HashSet<string>(_protectedGameInstallPaths,
                StringComparer.OrdinalIgnoreCase);
            bool changed = isProtected
                ? updatedPaths.Add(normalizedPath)
                : updatedPaths.Remove(normalizedPath);
            if (!changed)
                return;

            _protectedGameInstallPaths = updatedPaths;

            // Never carry a partial uninstall signature across a lock-state transition.
            // These queues aggregate events, so retaining old entries could make activity
            // from a newly locked or unlocked game complete a stale threshold.
            lock (_uninstallDetectLock)
            {
                _uninstallDeleteTicks.Clear();
                _uninstallCreateTicks.Clear();
            }
        }

        private void ApplyProtectionCore(GameInfo game, List<string> blockedUsers, bool blockAllNonAdminByDefault)
        {
            if (!Directory.Exists(game.InstallPath))
            {
                SetGameProtectionIntent(game.InstallPath, isProtected: false);
                _logger($"[LOCK] Skipping {game.GameName} - install path does not exist");
                return;
            }

            SetGameProtectionIntent(game.InstallPath, isProtected: true);

            _debugLogger($"[Riot] ApplyProtection: {game.GameName} at {game.InstallPath} | blockedUsers={blockedUsers.Count} blockAll={blockAllNonAdminByDefault}");

            var localUsers = GetLocalUsersForSync();
            // Pre-resolve all local user identities once — avoids repeated SID lookups inside loops.
            var localUserToIdentity = localUsers.ToDictionary(u => u, u => ResolveToIdentity(u), StringComparer.OrdinalIgnoreCase);
            var usersToBlockSet = new HashSet<string>(blockedUsers, StringComparer.OrdinalIgnoreCase);

            if (blockedUsers.Count == 0)
            {
                var localIdentities = localUserToIdentity.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (blockAllNonAdminByDefault)
                {
                    var groupIdentity = new List<string> { "*S-1-5-32-545" };
                    if (IsDesiredProtectionContractActive(game, groupIdentity))
                    {
                        _debugLogger($"[Riot] ApplyProtection: exact ACL contract already active for {game.GameName}; preserving ACLs and reacquiring handles");
                        AcquireGameHandles(game.InstallPath);
                        return;
                    }

                    // 0. CRITICAL: Clean up stale explicit deny ACEs on parent directories.
                    // Old code applied SetDenyRule /T on the shared Riot root (C:\Riot Games), which
                    // stamped explicit ACEs on every subfolder including Riot Client. These persist
                    // even after removing the parent rule. CleanupParentDenyRules walks up the parent
                    // chain and removes them one folder at a time (no /T). It also cleans the Riot
                    // Client launcher folder to prevent "COULDN'T UPDATE" errors.
                    CleanupParentDenyRules(game.InstallPath, blockedUsers, localUsers);

                    // Establish the desired game and manifest boundaries before removing
                    // obsolete identities. Discover the old identities from the ACL itself;
                    // profile folder names are not a reliable account inventory.
                    SetDenyRuleFolderOnlyBatch(game.InstallPath, groupIdentity);
                    if (!string.IsNullOrEmpty(game.ManifestPath))
                    {
                        SetSharedParentDeleteChildDenyBatch(game.ManifestPath, groupIdentity);
                        SetDenyRuleFileBatch(game.ManifestPath, groupIdentity);
                    }

                    var obsoleteIdentities = DiscoverObsoleteProtectionIdentities(game, groupIdentity);
                    if (obsoleteIdentities.Count > 0)
                    {
                        RemoveDenyRuleBatch(game.InstallPath, obsoleteIdentities);
                        if (!string.IsNullOrEmpty(game.ManifestPath))
                        {
                            RemoveDenyRuleFileBatch(game.ManifestPath, obsoleteIdentities);
                            foreach (var identity in obsoleteIdentities)
                                RemoveSharedParentDeleteChildDenyIfUnused(game.ManifestPath, identity);
                        }
                    }
                }
                else
                {
                    CleanupParentDenyRules(game.InstallPath, blockedUsers, localUsers);

                    // Explicitly unblock everyone: batch remove group + all local users in one walk.
                    var allToRemove = new List<string> { "*S-1-5-32-545" };
                    allToRemove.AddRange(localIdentities);
                    allToRemove.AddRange(DiscoverProtectionIdentities(game));
                    allToRemove = allToRemove.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    RemoveDenyRuleBatch(game.InstallPath, allToRemove);
                    if (!string.IsNullOrEmpty(game.ManifestPath))
                    {
                        RemoveDenyRuleFileBatch(game.ManifestPath, allToRemove);
                        foreach (var identity in allToRemove)
                            RemoveSharedParentDeleteChildDenyIfUnused(game.ManifestPath, identity);
                    }
                }
            }
            else
            {
                // Specific users: determine which identities to apply and which to remove.
                var toApplyIdentities = new List<string>();
                var toRemoveIdentities = new List<string> { "*S-1-5-32-545" }; // always remove group rule when using specific users

                foreach (var user in localUsers)
                {
                    bool shouldBlock = usersToBlockSet.Contains(user) ||
                                       usersToBlockSet.Any(u => u.EndsWith("\\" + user, StringComparison.OrdinalIgnoreCase));
                    var identity = localUserToIdentity[user];
                    if (shouldBlock)
                        toApplyIdentities.Add(identity);
                    else
                        toRemoveIdentities.Add(identity);
                }

                // Non-local blocked users (e.g. domain accounts not in C:\Users)
                foreach (var blockName in blockedUsers)
                {
                    if (!localUsers.Any(u => string.Equals(u, blockName, StringComparison.OrdinalIgnoreCase) ||
                                             blockName.EndsWith("\\" + u, StringComparison.OrdinalIgnoreCase)))
                    {
                        toApplyIdentities.Add(ResolveToIdentity(blockName));
                    }
                }

                // Deduplicate — a resolved SID might appear from multiple name forms
                toApplyIdentities = toApplyIdentities.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                toRemoveIdentities = toRemoveIdentities
                    .Where(id => !toApplyIdentities.Contains(id, StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (toApplyIdentities.Count > 0 && IsDesiredProtectionContractActive(game, toApplyIdentities))
                {
                    _debugLogger($"[Riot] ApplyProtection: exact ACL contract already active for {game.GameName}; preserving ACLs and reacquiring handles");
                    AcquireGameHandles(game.InstallPath);
                    return;
                }

                CleanupParentDenyRules(game.InstallPath, blockedUsers, localUsers);

                // Make before break: install the complete desired contract while the old
                // identity remains active, then remove only identities not in the desired set.
                if (toApplyIdentities.Count > 0)
                {
                    SetDenyRuleFolderOnlyBatch(game.InstallPath, toApplyIdentities);
                    if (!string.IsNullOrEmpty(game.ManifestPath))
                    {
                        SetSharedParentDeleteChildDenyBatch(game.ManifestPath, toApplyIdentities);
                        SetDenyRuleFileBatch(game.ManifestPath, toApplyIdentities);
                    }
                }

                var obsoleteSpecificIdentities = DiscoverObsoleteProtectionIdentities(
                    game, toApplyIdentities);
                if (obsoleteSpecificIdentities.Count > 0)
                {
                    RemoveDenyRuleBatch(game.InstallPath, obsoleteSpecificIdentities);
                    if (!string.IsNullOrEmpty(game.ManifestPath))
                    {
                        RemoveDenyRuleFileBatch(game.ManifestPath, obsoleteSpecificIdentities);
                        foreach (var identity in obsoleteSpecificIdentities)
                            RemoveSharedParentDeleteChildDenyIfUnused(game.ManifestPath, identity);
                    }
                }
            }

            // Lock status logged by PlatformManager.ApplyGameProtection (avoids duplicate/contradicting messages)

            // Acquire no-delete-share handles on all game exes. Any process trying to delete
            // one of these files gets ERROR_SHARING_VIOLATION before deletion starts.
            AcquireGameHandles(game.InstallPath);
        }

        public void RemoveProtection(GameInfo game, List<string> blockedUsers)
        {
            lock (_aclMutationLock)
            {
                RemoveProtectionCore(game, blockedUsers);
            }
        }

        private void RemoveProtectionCore(GameInfo game, List<string> blockedUsers)
        {
            SetGameProtectionIntent(game.InstallPath, isProtected: false);

            // Release file handles first so the patcher/uninstaller can delete files if needed
            // (maintenance mode, explicit unlock, update detected).
            ReleaseGameHandles(game.InstallPath);

            if (!Directory.Exists(game.InstallPath))
                return;

            var normalizedPath = game.InstallPath.Replace('/', '\\').TrimEnd('\\');

            // CRITICAL: Break ACL inheritance on this path before removing deny rules.
            //
            // Problem: Another Riot game (e.g. Valorant/TFT) may protect a PARENT path
            // (C:\Riot Games) with /T + (OI)(CI) flags. This cascades an inherited
            // (I)(OI)(CI)(DENY)(DE,DC) down to every subfolder including this game's path.
            // RemoveDenyRuleBatch /T only removes *explicit* deny ACEs — inherited ones survive.
            //
            // Fix: /inheritance:d disables inheritance and COPIES inherited ACEs as explicit.
            // Now RemoveDenyRuleBatch /T can delete those copied rules, leaving this folder clean.
            //
            // Side effect: this folder no longer inherits future ACE changes from its parent.
            // That is intentional — we want this game to be fully independent of what other
            // Riot games' protection does to the shared C:\Riot Games root.
            RunCmd("cmd.exe", $"/c icacls \"{normalizedPath}\" /inheritance:d /C /Q");
            _debugLogger($"[Riot] RemoveProtection: broke ACL inheritance on {normalizedPath}");

            // Collect ALL identities: group + all local users + all explicitly blocked users.
            // This ensures old user-specific deny rules from previous configs are cleaned up.
            var localUsers = GetLocalUsersForSync();
            var allIdentities = new List<string> { "*S-1-5-32-545" };
            foreach (var user in localUsers)
                allIdentities.Add(ResolveToIdentity(user));
            foreach (var username in blockedUsers)
                allIdentities.Add(ResolveToIdentity(username));
            allIdentities.AddRange(DiscoverProtectionIdentities(game));
            allIdentities = allIdentities.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // One batch remove covers all identities in a single tree walk (was N+1 walks)
            RemoveDenyRuleBatch(normalizedPath, allIdentities);
            if (!string.IsNullOrEmpty(game.ManifestPath))
            {
                RemoveDenyRuleFileBatch(game.ManifestPath, allIdentities);
                foreach (var identity in allIdentities)
                    RemoveSharedParentDeleteChildDenyIfUnused(game.ManifestPath, identity);
            }

            // Clean up stale explicit deny ACEs on parent directories + Riot Client folder
            CleanupParentDenyRules(normalizedPath, blockedUsers, localUsers);
        }

        /// <summary>
        /// Removes every explicit delete-deny identity from each game, manifest, and the shared
        /// Riot root while preserving launcher/vendor allow ACEs. See IGamePlatform.ResetToDefault
        /// and Critical #59.
        /// </summary>
        public void ResetToDefault(List<GameInfo> games)
        {
            lock (_aclMutationLock)
            {
                ResetToDefaultCore(games);
            }
        }

        private void ResetToDefaultCore(List<GameInfo> games)
        {
            _protectedGameInstallPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_uninstallDetectLock)
            {
                _uninstallDeleteTicks.Clear();
                _uninstallCreateTicks.Clear();
            }

            foreach (var game in games)
            {
                try
                {
                    // Defensive: ResetToDefault runs before AcquireGameHandles in the Start()
                    // sequence, and the standalone uninstall-path PlatformManager never acquires
                    // handles at all, but release is a cheap no-op if nothing is held (see the
                    // TryGetValue guard in ReleaseGameHandles), so call it anyway for defense in
                    // depth against future Start() reordering.
                    ReleaseGameHandles(game.InstallPath);

                    if (Directory.Exists(game.InstallPath))
                    {
                        var normalizedPath = game.InstallPath.Replace('/', '\\').TrimEnd('\\');
                        var identities = AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                            normalizedPath, recursive: true, _debugLogger);
                        RemoveDenyRuleBatch(normalizedPath, identities);
                        _debugLogger($"[Riot] ResetToDefault removed {identities.Count} deny identity/identities from {game.GameName} while preserving allow ACEs");
                    }
                    if (!string.IsNullOrEmpty(game.ManifestPath) && File.Exists(game.ManifestPath))
                    {
                        var identities = AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                            game.ManifestPath, recursive: false, _debugLogger);
                        RemoveDenyRuleFileBatch(game.ManifestPath, identities);
                        foreach (var identity in identities)
                            RemoveSharedParentDeleteChildDenyIfUnused(game.ManifestPath, identity);
                    }
                }
                catch (Exception ex)
                {
                    _debugLogger($"[Riot] ResetToDefault({game.GameName}) threw: {ex}");
                }
            }

            // Also clean the shared Riot root independently of whether any game is currently
            // installed. Historical /T bugs left explicit denies in Riot Client descendants.
            try
            {
                var riotRoot = FindRiotRoot();
                if (!string.IsNullOrEmpty(riotRoot))
                {
                    var identities = AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                        riotRoot, recursive: true, _debugLogger);
                    RemoveDenyRuleBatch(riotRoot, identities);
                    _debugLogger($"[Riot] ResetToDefault removed {identities.Count} deny identity/identities from Riot root while preserving allow ACEs");
                }
                else
                {
                    _debugLogger("[Riot] ResetToDefault: no Riot root found, skipping root reset");
                }
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] ResetToDefault(Riot root) threw: {ex}");
            }
        }

        /// <summary>
        /// Resolves the shared Riot install root (parent of "Riot Client\RiotClientServices.exe"),
        /// independent of any specific game. Mirrors PlatformManager.FindRiotLauncherExe's
        /// resolution order (RiotClientInstalls.json first, then the common default path) per
        /// Critical #40: never hardcode only C:\... for Riot paths, always check the
        /// authoritative source first. Returns null if Riot isn't installed anywhere findable.
        /// </summary>
        private string? FindRiotRoot()
        {
            const string installsJson = @"C:\ProgramData\Riot Games\RiotClientInstalls.json";
            try
            {
                if (File.Exists(installsJson))
                {
                    var content = File.ReadAllText(installsJson);
                    using var doc = System.Text.Json.JsonDocument.Parse(content);
                    foreach (var key in new[] { "rc_live", "rc_default" })
                    {
                        if (doc.RootElement.TryGetProperty(key, out var prop))
                        {
                            var exePath = prop.GetString();
                            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                            {
                                // exePath = "<root>\Riot Client\RiotClientServices.exe"
                                var riotClientDir = Path.GetDirectoryName(exePath);
                                var root = riotClientDir != null ? Path.GetDirectoryName(riotClientDir) : null;
                                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                                    return root;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] FindRiotRoot: RiotClientInstalls.json read/parse failed: {ex.Message}");
            }

            const string defaultRoot = @"C:\Riot Games";
            return Directory.Exists(Path.Combine(defaultRoot, "Riot Client")) ? defaultRoot : null;
        }

        private void ApplyUserProtection(GameInfo game, string username)
        {
            try
            {
                string identity = username;
                if (!username.StartsWith("*S-"))
                {
                    var sid = ResolveToSid(username);
                    if (sid != null) identity = "*" + sid;
                }
                // (CI)(DE) /T on install path — denies directory deletion at every depth,
                // leaves files untouched so Riot Client operates normally (see SetDenyRuleFolderOnly)
                SetDenyRuleFolderOnly(game.InstallPath, identity);
                if (!string.IsNullOrEmpty(game.ManifestPath))
                    SetDenyRule(game.ManifestPath, identity);
            }
            catch (Exception ex)
            {
                _logger($"[Riot] Failed to lock {game.GameName} for user {username}: {ex.Message}");
            }
        }

        private void RemoveUserProtection(GameInfo game, string username)
        {
            try
            {
                string identity = username;
                if (!username.StartsWith("*S-"))
                {
                    var sid = ResolveToSid(username);
                    if (sid != null) identity = "*" + sid;
                }
                RemoveDenyRule(game.InstallPath, identity);
                if (!string.IsNullOrEmpty(game.ManifestPath))
                    RemoveDenyRule(game.ManifestPath, identity);
            }
            catch (Exception ex) { _debugLogger($"[Riot] RemoveUserProtection({game.GameName}, {username}): {ex.Message}"); }
        }

        private void SetDenyRule(string path, string userOrSid)
        {
            try
            {
                // CRITICAL: Normalize path — backslashes for icacls, trim trailing backslash (icacls returns ERROR_INVALID_NAME otherwise)
                var normalizedPath = path.Replace('/', '\\');
                if (normalizedPath.Length > 3) normalizedPath = normalizedPath.TrimEnd('\\');

                // CRITICAL: Remove existing rule first to prevent duplicates
                RemoveDenyRule(normalizedPath, userOrSid);

                bool isFile = File.Exists(normalizedPath) && !Directory.Exists(normalizedPath);
                if (isFile)
                {
                    var (ec, stderr) = RunCmdCapture("cmd.exe", $"/c icacls \"{normalizedPath}\" /deny \"{userOrSid}:(DE)\" /C /Q");
                    if (ec != 0)
                        _debugLogger($"[Riot] SetDenyRule exit={ec} {userOrSid} on {Path.GetFileName(normalizedPath)} stderr={stderr.Trim()}");
                    else
                        _debugLogger($"[Riot] SetDenyRule exit={ec} {userOrSid} on {Path.GetFileName(normalizedPath)}");
                    return;
                }

                SetDenyRuleParallelSubdirs(normalizedPath, userOrSid);
            }
            catch (Exception ex) { _debugLogger($"[Riot] SetDenyRule({path}, {userOrSid}): {ex.Message}"); }
        }

        /// <summary>
        /// Applies deny rule to a directory using parallel subdirectory icacls for speed.
        /// </summary>
        private void SetDenyRuleParallelSubdirs(string dirPath, string userOrSid)
        {
            // Step 1: root folder itself (no /T — fast)
            RunCmdCapture("cmd.exe", $"/c icacls \"{dirPath}\" /deny \"{userOrSid}:(OI)(CI)(DE,DC)\" /C /Q");

            try
            {
                var subdirs = Directory.GetDirectories(dirPath);
                if (subdirs.Length == 0)
                {
                    _debugLogger($"[Riot] SetDenyRule exit=0 {userOrSid} on {Path.GetFileName(dirPath)}");
                    return;
                }

                var opts = new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4)
                };
                System.Threading.Tasks.Parallel.ForEach(subdirs, opts, subdir =>
                {
                    var (ec, err) = RunCmdCapture("cmd.exe", $"/c icacls \"{subdir}\" /deny \"{userOrSid}:(OI)(CI)(DE,DC)\" /T /C /Q");
                    if (ec != 0)
                        _debugLogger($"[Riot] SetDenyRule FAILED exit={ec} {userOrSid} on {Path.GetFileName(subdir)} stderr={err.Trim()}");
                });
                _debugLogger($"[Riot] SetDenyRule exit=0 {userOrSid} on {Path.GetFileName(dirPath)} ({subdirs.Length} subdirs parallel)");
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] SetDenyRule parallel failed: {ex.Message} — falling back to /T");
                var (ec, err) = RunCmdCapture("cmd.exe", $"/c icacls \"{dirPath}\" /deny \"{userOrSid}:(OI)(CI)(DE,DC)\" /T /C /Q");
                if (ec != 0)
                    _debugLogger($"[Riot] SetDenyRule FAILED (fallback) exit={ec} {userOrSid} on {dirPath} stderr={err.Trim()}");
                else
                    _debugLogger($"[Riot] SetDenyRule exit=0 (fallback) {userOrSid} on {Path.GetFileName(dirPath)}");
            }
        }

        /// <summary>
        /// Denies deletion of all directories in the game folder at every depth — files untouched.
        ///
        /// Used for Riot game install folders (e.g. C:\Riot Games\VALORANT).
        ///
        /// Rule: (CI)(DE) /T
        ///   (CI) = ContainerInherit — all subdirectories inherit the rule
        ///   (DE) = Delete — cannot delete the directory itself
        ///   /T   = recursive to all existing subdirectories
        ///   NO (OI) — files are NOT affected at all
        ///   NO (DC) — does not block deletion of a folder's file contents
        ///
        /// The full (OI)(CI)(DE,DC) /T deny breaks Riot Client: the (DC) flag
        /// (prevent-delete-child) and (OI) (applied to files) block Riot Client and
        /// Vanguard from normal file operations during integrity verification and game
        /// launch, causing the Play button to disappear. Removing (OI) and (DC) while
        /// keeping (CI)(DE)/T gives Riot full file freedom while still blocking all
        /// directory deletion at every depth.
        ///
        /// Blocks:
        ///   - Deletion of the game root folder
        ///   - Deletion of any subfolder (rmdir, Explorer folder delete, uninstaller)
        ///   - Riot Client / Windows uninstaller (both delete the folder tree)
        /// Gap: individual files can be deleted (impractical for 45+ GB in a cafe session).
        /// Manifest YAML is protected separately via SetDenyRule file-level deny.
        /// </summary>
        private void SetDenyRuleFolderOnly(string path, string userOrSid)
        {
            try
            {
                var normalizedPath = path.Replace('/', '\\');
                if (normalizedPath.Length > 3) normalizedPath = normalizedPath.TrimEnd('\\');

                if (!Directory.Exists(normalizedPath)) return;

                // Remove any existing deny rules /T first (prevents stale ACE accumulation)
                RemoveDenyRule(normalizedPath, userOrSid);

                // (CI)(DE) /T: deny directory deletion at every depth, leave files untouched
                var cmd = $"icacls \"{normalizedPath}\" /deny \"{userOrSid}:(CI)(DE)\" /T /C /Q";
                var (exitCode, stderr) = RunCmdCapture("cmd.exe", $"/c {cmd}");
                if (exitCode != 0)
                    _debugLogger($"[Riot] SetDenyRuleFolderOnly exit={exitCode} {userOrSid} on {Path.GetFileName(normalizedPath)} stderr={stderr.Trim()}");
                else
                    _debugLogger($"[Riot] SetDenyRuleFolderOnly exit={exitCode} {userOrSid} on {Path.GetFileName(normalizedPath)}");
            }
            catch (Exception ex) { _debugLogger($"[Riot] SetDenyRuleFolderOnly({path}, {userOrSid}): {ex.Message}"); }
        }

        private void RemoveDenyRule(string path, string userOrSid)
        {
            try
            {
                // CRITICAL: Normalize path — backslashes for icacls, trim trailing backslash (icacls returns ERROR_INVALID_NAME otherwise)
                var normalizedPath = path.Replace('/', '\\');
                if (normalizedPath.Length > 3) normalizedPath = normalizedPath.TrimEnd('\\');

                if (!Directory.Exists(normalizedPath) && !File.Exists(normalizedPath)) return;

                bool isFile = File.Exists(normalizedPath) && !Directory.Exists(normalizedPath);
                if (isFile)
                {
                    var ec = RunCmdWithExitCode("cmd.exe", $"/c icacls \"{normalizedPath}\" /remove:d \"{userOrSid}\" /C /Q");
                    if (ec != 0 && ec != 3 && ec != 1332)
                        _debugLogger($"[Riot] RemoveDenyRule exit={ec} user={userOrSid} path={normalizedPath}");
                    return;
                }

                RemoveDenyRuleDir(normalizedPath, userOrSid);
            }
            catch (Exception ex)
            {
                _logger($"[Riot] RemoveDenyRule error: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes deny rules from a directory using parallel subdirectory icacls for speed.
        /// </summary>
        private void RemoveDenyRuleDir(string dirPath, string userOrSid)
        {
            var ec = RunCmdWithExitCode("cmd.exe", $"/c icacls \"{dirPath}\" /remove:d \"{userOrSid}\" /T /C /Q");
            if (ec != 0 && ec != 3 && ec != 1332)
                _debugLogger($"[Riot] RemoveDenyRuleDir FAILED exit={ec} user={userOrSid} on {Path.GetFileName(dirPath)}");
        }

        /// <summary>
        /// Removes stale explicit deny ACEs from every intermediate directory between a
        /// game's install folder and the Riot root (e.g. C:\Riot Games → LoR → live).
        /// Old code used SetDenyRule(root, ...) /T which stamped EXPLICIT (not inherited)
        /// deny ACEs on every subfolder — they persist even after removing the parent rule.
        /// This method cleans those up one folder at a time (no /T) so children are unaffected.
        ///
        /// When it reaches the Riot root (detected by finding a "Riot Client" sibling), it also
        /// cleans C:\Riot Games\Riot Client\ with /T — otherwise the Riot launcher shows
        /// "COULDN'T UPDATE" because the launcher's own folder still has stale deny rules.
        ///
        /// Works on any drive — the Riot root is detected dynamically, not hardcoded.
        /// </summary>
        private void CleanupParentDenyRules(string gamePath, List<string> blockedUsers, List<string> localUsers)
        {
            var normalized = gamePath.Replace('/', '\\').TrimEnd('\\');

            // Build full set of identities to clean (group + all individual users)
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*S-1-5-32-545" };
            foreach (var u in blockedUsers) identities.Add(u);
            foreach (var u in localUsers) identities.Add(u);

            var current = Path.GetDirectoryName(normalized);
            while (!string.IsNullOrEmpty(current))
            {
                var dir = current.TrimEnd('\\');

                // Stop at drive root (e.g. "C:\") — never touch it
                if (dir.Length <= 3) break;

                if (!Directory.Exists(dir)) break;

                // Clean this single directory (no /T — don't recurse into children)
                foreach (var identity in identities)
                    RemoveDenyRuleSingleFolder(dir, identity);

                // If this is the Riot root (has a "Riot Client" sibling), also clean
                // the Riot Client launcher folder. Old /T rules cascade into it and
                // prevent Riot Client from updating (shows "COULDN'T UPDATE" dialog).
                if (Directory.Exists(Path.Combine(dir, "Riot Client")))
                {
                    var riotClientDir = Path.Combine(dir, "Riot Client");
                    foreach (var identity in identities)
                        RemoveDenyRule(riotClientDir, identity); // uses /T — cleans launcher and all its children
                    _debugLogger($"[Riot] CleanupParent: cleaned Riot Client launcher at {riotClientDir}");
                    break; // We've reached the Riot root — nothing above here is Riot
                }

                current = Path.GetDirectoryName(dir);
            }
        }

        /// <summary>
        /// Removes a deny ACE from a single directory — no /T recursion.
        /// Used by CleanupParentDenyRules to clean intermediate parent folders
        /// without touching their children.
        /// </summary>
        private void RemoveDenyRuleSingleFolder(string path, string userOrSid)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                var normalizedPath = path.Replace('/', '\\').TrimEnd('\\');
                if (normalizedPath.Length <= 3) return; // Never touch drive roots

                var cmd = $"icacls \"{normalizedPath}\" /remove:d \"{userOrSid}\" /C /Q";
                var exitCode = RunCmdWithExitCode("cmd.exe", $"/c {cmd}");

                if (exitCode != 0 && exitCode != 3 && exitCode != 1332)
                    _debugLogger($"[Riot] CleanupParent exit={exitCode} user={userOrSid} on {normalizedPath}");
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] CleanupParent error for {path}: {ex.Message}");
            }
        }

        private void RunCmd(string fileName, string arguments)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30000))
            {
                try { proc.Kill(); } catch { }
            }
        }

        private int RunCmdWithExitCode(string fileName, string arguments)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30000))
            {
                try { proc.Kill(); } catch { }
            }
            return proc.HasExited ? proc.ExitCode : -1;
        }

        private (int exitCode, string stderr) RunCmdCapture(string fileName, string arguments)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30000))
            {
                try { proc.Kill(); } catch { }
            }
            return (proc.HasExited ? proc.ExitCode : -1, stderr);
        }

        private List<string> GetLocalUsersForSync()
        {
            var users = new List<string>();
            try
            {
                if (Directory.Exists(@"C:\Users"))
                {
                    foreach (var dir in Directory.GetDirectories(@"C:\Users"))
                    {
                        var name = Path.GetFileName(dir);
                        if (string.Equals(name, "Public", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "Default User", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "All Users", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "defaultuser0", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (File.Exists(Path.Combine(dir, "NTUSER.DAT")))
                            users.Add(name);
                    }
                }
            }
            catch { }
            return users;
        }

        private string? ResolveToSid(string username)
        {
            try
            {
                var account = new NTAccount(username);
                var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
                return sid.Value;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves a username to its SID-based identity for icacls.
        /// Returns "*S-1-5-xxxxx" if resolvable, otherwise returns the username as-is.
        /// Called once per user upfront to avoid repeated SID lookups in inner loops.
        /// </summary>
        private string ResolveToIdentity(string username)
        {
            if (username.StartsWith("*S-")) return username;
            var sid = ResolveToSid(username);
            return sid != null ? "*" + sid : username;
        }

        private List<string> DiscoverProtectionIdentities(GameInfo game)
        {
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(game.InstallPath))
                identities.UnionWith(AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                    game.InstallPath, recursive: false, _debugLogger));
            if (!string.IsNullOrEmpty(game.ManifestPath) && File.Exists(game.ManifestPath))
                identities.UnionWith(AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                    game.ManifestPath, recursive: false, _debugLogger));
            return identities.ToList();
        }

        private List<string> DiscoverObsoleteProtectionIdentities(
            GameInfo game,
            IEnumerable<string> desiredIdentities)
        {
            var desiredSids = desiredIdentities
                .Select(TryResolveSecurityIdentifier)
                .Where(sid => sid != null)
                .Select(sid => sid!.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return DiscoverProtectionIdentities(game)
                .Where(identity =>
                {
                    var sid = TryResolveSecurityIdentifier(identity);
                    return sid == null || !desiredSids.Contains(sid.Value);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Batch-removes deny rules for multiple identities in one icacls invocation per directory.
        /// Uses the same parallel-subdirectory pattern as RemoveDenyRuleDir but eliminates the
        /// per-user loop: N users → 1 icacls call per subdirectory instead of N calls per subdirectory.
        /// </summary>
        private void RemoveDenyRuleBatch(string dirPath, List<string> identities)
        {
            if (identities.Count == 0) return;
            var normalizedPath = dirPath.Replace('/', '\\');
            if (normalizedPath.Length > 3) normalizedPath = normalizedPath.TrimEnd('\\');
            if (!Directory.Exists(normalizedPath)) return;

            // One icacls call per identity — batching multiple /remove:d flags in one call returns
            // exit 0 but silently processes only a subset of identities.
            foreach (var identity in identities)
            {
                var ec = RunCmdWithExitCode("cmd.exe", $"/c icacls \"{normalizedPath}\" /remove:d \"{identity}\" /T /C /Q");
                if (ec != 0 && ec != 3 && ec != 1332)
                    _debugLogger($"[Riot] RemoveDenyRuleBatch FAILED exit={ec} on {Path.GetFileName(normalizedPath)} for {identity}");
            }
            _debugLogger($"[Riot] RemoveDenyRuleBatch: {identities.Count} identities on {Path.GetFileName(normalizedPath)}");
        }

        /// <summary>
        /// Batch-applies folder-only deny rules for multiple identities in one icacls invocation.
        /// Uses (CI)(DE) /T — denies directory deletion at every depth, files untouched.
        /// This is the Riot-specific flag set (see SetDenyRuleFolderOnly for why NOT (OI)(CI)(DE,DC)).
        /// NOTE: does NOT call RemoveDenyRuleBatch internally — caller must do the remove pass first.
        /// </summary>
        private void SetDenyRuleFolderOnlyBatch(string dirPath, List<string> identities)
        {
            if (identities.Count == 0) return;
            var normalizedPath = dirPath.Replace('/', '\\');
            if (normalizedPath.Length > 3) normalizedPath = normalizedPath.TrimEnd('\\');
            if (!Directory.Exists(normalizedPath)) return;

            // (CI)(DE): deny directory deletion at every depth, leave files untouched (Riot-specific)
            var denyArgs = string.Join(" ", identities.Select(id => $"/deny \"{id}:(CI)(DE)\""));

            // Step 1: root folder itself (no /T — fast)
            RunCmdCapture("cmd.exe", $"/c icacls \"{normalizedPath}\" {denyArgs} /C /Q");

            try
            {
                var subdirs = Directory.GetDirectories(normalizedPath);
                if (subdirs.Length == 0)
                {
                    _debugLogger($"[Riot] SetDenyRuleFolderOnlyBatch: {identities.Count} identities on {Path.GetFileName(normalizedPath)}");
                    return;
                }

                var opts = new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4)
                };
                System.Threading.Tasks.Parallel.ForEach(subdirs, opts, subdir =>
                {
                    var (ec, err) = RunCmdCapture("cmd.exe", $"/c icacls \"{subdir}\" {denyArgs} /T /C /Q");
                    if (ec != 0)
                        _debugLogger($"[Riot] SetDenyRuleFolderOnlyBatch FAILED exit={ec} on {Path.GetFileName(subdir)} stderr={err.Trim()}");
                });
                _debugLogger($"[Riot] SetDenyRuleFolderOnlyBatch: {identities.Count} identities on {Path.GetFileName(normalizedPath)} ({subdirs.Length} subdirs parallel)");
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] SetDenyRuleFolderOnlyBatch parallel failed: {ex.Message} — falling back to /T");
                RunCmdCapture("cmd.exe", $"/c icacls \"{normalizedPath}\" {denyArgs} /T /C /Q");
            }
        }

        /// <summary>
        /// Batch-removes deny rules from a file for multiple identities in one icacls invocation.
        /// File-level only — no /T, no subdirectory enumeration.
        /// </summary>
        private void RemoveDenyRuleFileBatch(string filePath, List<string> identities)
        {
            if (identities.Count == 0) return;
            if (!File.Exists(filePath)) return;
            var normalizedPath = filePath.Replace('/', '\\');
            if (normalizedPath.Length > 3) normalizedPath = normalizedPath.TrimEnd('\\');
            foreach (var identity in identities)
                RunCmdWithExitCode("cmd.exe", $"/c icacls \"{normalizedPath}\" /remove:d \"{identity}\" /C /Q");
        }

        /// <summary>
        /// Batch-applies file-level deny rules for multiple identities in one icacls invocation.
        /// Uses (DE) only — (OI)(CI)(DC) are directory flags that don't apply to files.
        /// NOTE: does NOT call RemoveDenyRuleFileBatch internally — caller must remove first.
        /// </summary>
        private void SetDenyRuleFileBatch(string filePath, List<string> identities)
        {
            if (identities.Count == 0) return;
            if (!File.Exists(filePath)) return;
            var normalizedPath = filePath.Replace('/', '\\');
            if (normalizedPath.Length > 3) normalizedPath = normalizedPath.TrimEnd('\\');
            // (DE) only for files — no (OI)(CI)(DC) which are directory-only flags
            var denyArgs = string.Join(" ", identities.Select(id => $"/deny \"{id}:(DE)\""));
            RunCmdWithExitCode("cmd.exe", $"/c icacls \"{normalizedPath}\" {denyArgs} /C /Q");
        }

        private void SetSharedParentDeleteChildDenyBatch(string protectedManifestPath, List<string> identities)
        {
            foreach (var identity in identities.Distinct(StringComparer.OrdinalIgnoreCase))
                SetSharedParentDeleteChildDeny(protectedManifestPath, identity);
        }

        /// <summary>
        /// A file DELETE deny alone is not sufficient on NTFS: deletion or rename is also
        /// authorized when the parent grants DELETE_CHILD. Riot keeps each product manifest
        /// in its own metadata directory, so this non-inheriting parent rule closes that
        /// alternate authorization route without touching sibling product directories.
        /// </summary>
        private void SetSharedParentDeleteChildDeny(string protectedManifestPath, string identity)
        {
            var parentPath = Path.GetDirectoryName(protectedManifestPath);
            var sid = TryResolveSecurityIdentifier(identity);
            if (string.IsNullOrEmpty(parentPath) || sid == null || !Directory.Exists(parentPath))
            {
                _debugLogger($"[Riot] Cannot set manifest parent DeleteChild deny for {identity} at {protectedManifestPath}");
                return;
            }

            try
            {
                if (HasExplicitDeleteChildDeny(parentPath, sid))
                    return;

                var (ec, stderr) = RunCmdCapture(
                    "cmd.exe",
                    $"/c icacls \"{parentPath}\" /deny \"{identity}:(DC)\" /C /Q");
                if (ec != 0)
                    _debugLogger($"[Riot] Set manifest parent DeleteChild deny FAILED exit={ec} for {identity} at {parentPath}: {stderr.Trim()}");
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] Set manifest parent DeleteChild deny failed for {identity} at {parentPath}: {ex}");
            }
        }

        /// <summary>
        /// Removes the parent DELETE_CHILD deny only after no product-settings manifest in
        /// that directory still has an explicit DELETE deny for the same identity.
        /// </summary>
        private void RemoveSharedParentDeleteChildDenyIfUnused(string manifestPath, string identity)
        {
            var parentPath = Path.GetDirectoryName(manifestPath);
            var sid = TryResolveSecurityIdentifier(identity);
            if (string.IsNullOrEmpty(parentPath) || sid == null || !Directory.Exists(parentPath))
                return;

            try
            {
                var protectedSiblingRemains = Directory
                    .EnumerateFiles(parentPath, "*.product_settings.yaml", SearchOption.TopDirectoryOnly)
                    .Any(path => HasExplicitDeleteDeny(path, sid));
                if (protectedSiblingRemains)
                    return;

                var ec = RunCmdWithExitCode(
                    "cmd.exe",
                    $"/c icacls \"{parentPath}\" /remove:d \"{identity}\" /C /Q");
                if (ec != 0 && ec != 3 && ec != 1332)
                    _debugLogger($"[Riot] Remove manifest parent DeleteChild deny FAILED exit={ec} for {identity} at {parentPath}");
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] Remove manifest parent DeleteChild deny failed for {identity} at {parentPath}: {ex}");
            }
        }

        private static bool HasExplicitDeleteDeny(string path, SecurityIdentifier sid)
        {
            try
            {
                FileSystemSecurity security = Directory.Exists(path)
                    ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                    : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
                return security
                    .GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
                    .OfType<FileSystemAccessRule>()
                    .Any(rule =>
                        rule.AccessControlType == AccessControlType.Deny &&
                        rule.IdentityReference is SecurityIdentifier ruleSid &&
                        sid.Value.Equals(ruleSid.Value, StringComparison.OrdinalIgnoreCase) &&
                        (rule.FileSystemRights & FileSystemRights.Delete) != 0);
            }
            catch
            {
                return true;
            }
        }

        private static bool HasExplicitDeleteChildDeny(string path, SecurityIdentifier sid)
        {
            try
            {
                var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
                return security
                    .GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
                    .OfType<FileSystemAccessRule>()
                    .Any(rule =>
                        rule.AccessControlType == AccessControlType.Deny &&
                        rule.IdentityReference is SecurityIdentifier ruleSid &&
                        sid.Value.Equals(ruleSid.Value, StringComparison.OrdinalIgnoreCase) &&
                        (rule.FileSystemRights & FileSystemRights.DeleteSubdirectoriesAndFiles) != 0);
            }
            catch
            {
                return false;
            }
        }

        private static SecurityIdentifier? TryResolveSecurityIdentifier(string identity)
        {
            try
            {
                var normalized = identity.StartsWith("*", StringComparison.Ordinal) ? identity[1..] : identity;
                if (normalized.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
                    return new SecurityIdentifier(normalized);
                return (SecurityIdentifier)new NTAccount(normalized).Translate(typeof(SecurityIdentifier));
            }
            catch
            {
                return null;
            }
        }

        public void BackupManifests(string libraryPath, List<string> excludedGames)
        {
            if (!Directory.Exists(libraryPath))
                return;

            try
            {
                // IMPORTANT: YAML files are in subfolders
                var yamlFiles = Directory.GetFiles(libraryPath, "*.product_settings.yaml", SearchOption.AllDirectories);

                foreach (var yamlFile in yamlFiles)
                {
                    try
                    {
                        var game = ParseYamlManifest(yamlFile);
                        if (game == null)
                            continue;

                        if (excludedGames.Contains(game.GameName, StringComparer.OrdinalIgnoreCase))
                            continue;

                        var backupFile = Path.Combine(_backupPath, Path.GetFileName(yamlFile));

                        if (!File.Exists(backupFile) || new FileInfo(yamlFile).Length != new FileInfo(backupFile).Length)
                        {
                            File.Copy(yamlFile, backupFile, true);
                        }
                    }
                    catch { }
                }

                var riotInstallsJson = Path.Combine(
                    Path.GetDirectoryName(libraryPath) ?? "",
                    "RiotClientInstalls.json");

                if (File.Exists(riotInstallsJson))
                {
                    var backupJson = Path.Combine(_backupPath, "RiotClientInstalls.json");
                    if (!File.Exists(backupJson) || new FileInfo(riotInstallsJson).Length != new FileInfo(backupJson).Length)
                    {
                        File.Copy(riotInstallsJson, backupJson, true);
                    }
                }

                // Cleanup backups for excluded games (prevents stale backups from triggering detection)
                CleanupBackupForExcludedGames(excludedGames);
            }
            catch { }
        }

        private void CleanupBackupForExcludedGames(List<string> excludedGames)
        {
            try
            {
                if (!Directory.Exists(_backupPath))
                    return;

                foreach (var backupFile in Directory.GetFiles(_backupPath, "*.product_settings.yaml"))
                {
                    try
                    {
                        var game = ParseYamlManifest(backupFile);
                        if (game != null && excludedGames.Contains(game.GameName, StringComparer.OrdinalIgnoreCase))
                        {
                            File.Delete(backupFile);
                            _logger($"[Riot] Deleted backup for excluded game: {game.GameName}");
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void RestoreManifests(string libraryPath, List<string> excludedGames)
        {
            if (!Directory.Exists(_backupPath) || !Directory.Exists(libraryPath))
                return;

            var backupFiles = Directory.GetFiles(_backupPath, "*.yaml");

            foreach (var backupFile in backupFiles)
            {
                try
                {
                    var game = ParseYamlManifest(backupFile);
                    if (game == null)
                        continue;

                    if (excludedGames.Contains(game.GameName, StringComparer.OrdinalIgnoreCase))
                        continue;

                    var targetFile = Path.Combine(libraryPath, Path.GetFileName(backupFile));

                    if (!File.Exists(targetFile) || new FileInfo(backupFile).Length != new FileInfo(targetFile).Length)
                    {
                        File.Copy(backupFile, targetFile, true);
                    }
                }
                catch { }
            }

            var backupJson = Path.Combine(_backupPath, "RiotClientInstalls.json");
            if (File.Exists(backupJson))
            {
                var targetJson = Path.Combine(
                    Path.GetDirectoryName(libraryPath) ?? "",
                    "RiotClientInstalls.json");

                try
                {
                    if (!File.Exists(targetJson) || new FileInfo(backupJson).Length != new FileInfo(targetJson).Length)
                    {
                        File.Copy(backupJson, targetJson, true);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Recovery path for one confirmed Riot uninstall callback. Riot metadata manifests
        /// live in product-specific subdirectories, so restoring every flattened backup into
        /// the metadata root both loses the original path and can replay unrelated games.
        /// Restore only the triggering manifest to its exact recorded path.
        /// </summary>
        public void RestoreGameManifest(GameInfo game, string libraryPath, List<string> excludedGames)
        {
            if (excludedGames.Contains(game.GameName, StringComparer.OrdinalIgnoreCase))
            {
                _debugLogger($"[Riot] RestoreGameManifest skipped excluded game {game.GameName}");
                return;
            }

            if (string.IsNullOrWhiteSpace(game.ManifestPath))
            {
                _debugLogger($"[Riot] RestoreGameManifest rejected missing manifest path for {game.GameName}");
                return;
            }

            var manifestName = Path.GetFileName(game.ManifestPath);
            if (string.IsNullOrWhiteSpace(manifestName) ||
                !manifestName.EndsWith(".product_settings.yaml", StringComparison.OrdinalIgnoreCase))
            {
                _debugLogger($"[Riot] RestoreGameManifest rejected invalid manifest path for {game.GameName}");
                return;
            }

            string destination;
            string metadataRoot;
            try
            {
                destination = Path.GetFullPath(game.ManifestPath);
                metadataRoot = Path.GetFullPath(libraryPath);
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] RestoreGameManifest rejected invalid path for {game.GameName}: {ex.Message}");
                return;
            }

            if (!IsPathSameOrDescendant(destination, metadataRoot) ||
                string.Equals(Path.TrimEndingDirectorySeparator(destination),
                    Path.TrimEndingDirectorySeparator(metadataRoot), StringComparison.OrdinalIgnoreCase))
            {
                _debugLogger($"[Riot] RestoreGameManifest rejected path outside metadata root for {game.GameName}");
                return;
            }

            var backupFile = Path.Combine(_backupPath, manifestName);
            if (!File.Exists(backupFile))
            {
                _debugLogger($"[Riot] RestoreGameManifest backup missing for {game.GameName}: {manifestName}");
                return;
            }

            var backedGame = ParseYamlManifest(backupFile);
            if (backedGame == null ||
                !string.Equals(backedGame.GameName, game.GameName, StringComparison.OrdinalIgnoreCase))
            {
                _debugLogger($"[Riot] RestoreGameManifest rejected ownership mismatch for {game.GameName}");
                return;
            }

            if (File.Exists(destination))
            {
                // A patch may recreate the manifest after the callback was queued. Never
                // overwrite that newer live state with the older protection backup.
                _debugLogger($"[Riot] RestoreGameManifest kept reappeared live manifest for {game.GameName}");
                return;
            }

            var destinationDirectory = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(destinationDirectory))
                return;
            Directory.CreateDirectory(destinationDirectory);
            File.Copy(backupFile, destination, overwrite: false);
            _debugLogger($"[Riot] RestoreGameManifest restored {manifestName} for {game.GameName}");
        }

        public void KillLauncher()
        {
            foreach (var processName in new[] { "RiotClientServices", "RiotClientUx", "RiotClientCrashHandler" })
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(processName))
                    {
                        proc.Kill();
                        proc.WaitForExit(5000);
                    }
                }
                catch { }
            }

            Thread.Sleep(2000);
        }

        public void StartLauncher()
        {
            var launcherExe = FindLauncherPath();

            if (string.IsNullOrEmpty(launcherExe))
            {
                _logger("[Riot] Launcher executable not found");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = launcherExe,
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                _logger($"[Riot] Failed to start launcher: {ex.Message}");
            }
        }

        private string? FindLauncherPath()
        {
            var defaultPath = @"C:\Riot Games\Riot Client\RiotClientServices.exe";
            if (File.Exists(defaultPath))
                return defaultPath;

            try
            {
                using var proc = Process.GetProcessesByName("RiotClientServices").FirstOrDefault();
                if (proc?.MainModule?.FileName != null)
                    return proc.MainModule.FileName;
            }
            catch (Exception ex) { _debugLogger($"[Riot] FindLauncherPath via process: {ex.Message}"); }

            return null;
        }

        public void StartUninstallWatcher(Action<GameInfo> onUninstallDetected)
        {
            _onUninstallDetected = onUninstallDetected;
            _stoppingWatchers = false;

            var metadataPaths = DetectLibraryPaths();
            foreach (var metadataPath in metadataPaths)
            {
                if (!Directory.Exists(metadataPath))
                    continue;
                StartRiotUninstallWatcherForPath(metadataPath, 0);
            }

            var riotGamesPath = @"C:\Riot Games";
            if (Directory.Exists(riotGamesPath))
            {
                StartRiotFolderWatcherForPath(riotGamesPath, 0);
                StartRiotActivityWatcherForPath(riotGamesPath, 0);
            }
        }

        private void StartRiotUninstallWatcherForPath(string metadataPath, int attempt)
        {
            if (_stoppingWatchers) return;
            try
            {
                var watcher = new FileSystemWatcher(metadataPath)
                {
                    Filter = "*.yaml",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = true  // CRITICAL: YAML files are in subfolders (e.g., valorant.live\valorant.live.product_settings.yaml)
                };
                watcher.Deleted += OnManifestDeleted;
                var currentWatcher = watcher;
                watcher.Error += (s, e) =>
                {
                    _debugLogger($"[Riot] Uninstall watcher error: {e.GetException().Message} — scheduling restart");
                    if (_stoppingWatchers) return;
                    lock (_watchers)
                    {
                        try { currentWatcher.EnableRaisingEvents = false; currentWatcher.Dispose(); } catch { }
                        _watchers.Remove(currentWatcher);
                    }
                    ScheduleWatcherRestart(metadataPath, attempt, () => StartRiotUninstallWatcherForPath(metadataPath, attempt + 1), "Riot uninstall");
                };
                watcher.EnableRaisingEvents = true;
                lock (_watchers) { _watchers.Add(watcher); }
                _debugLogger($"[Riot] Uninstall watcher started: {metadataPath}" + (attempt > 0 ? $" (restart attempt {attempt})" : ""));
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] Failed to start uninstall watcher on {metadataPath}: {ex.Message}");
                ScheduleWatcherRestart(metadataPath, attempt, () => StartRiotUninstallWatcherForPath(metadataPath, attempt + 1), "Riot uninstall");
            }
        }

        private void StartRiotFolderWatcherForPath(string riotGamesPath, int attempt)
        {
            if (_stoppingWatchers) return;
            try
            {
                var folderWatcher = new FileSystemWatcher(riotGamesPath)
                {
                    NotifyFilter = NotifyFilters.DirectoryName
                };
                folderWatcher.Deleted += OnGameFolderDeleted;
                var currentWatcher = folderWatcher;
                folderWatcher.Error += (s, e) =>
                {
                    _debugLogger($"[Riot] Folder watcher error: {e.GetException().Message} — scheduling restart");
                    if (_stoppingWatchers) return;
                    lock (_watchers)
                    {
                        try { currentWatcher.EnableRaisingEvents = false; currentWatcher.Dispose(); } catch { }
                        _watchers.Remove(currentWatcher);
                    }
                    ScheduleWatcherRestart(riotGamesPath, attempt, () => StartRiotFolderWatcherForPath(riotGamesPath, attempt + 1), "Riot folder");
                };
                folderWatcher.EnableRaisingEvents = true;
                lock (_watchers) { _watchers.Add(folderWatcher); }
                _debugLogger($"[Riot] Folder watcher started: {riotGamesPath}" + (attempt > 0 ? $" (restart attempt {attempt})" : ""));
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] Failed to start folder watcher: {ex.Message}");
                ScheduleWatcherRestart(riotGamesPath, attempt, () => StartRiotFolderWatcherForPath(riotGamesPath, attempt + 1), "Riot folder");
            }
        }

        private void StartRiotActivityWatcherForPath(string riotGamesPath, int attempt)
        {
            if (_stoppingWatchers) return;
            try
            {
                // Activity watcher: tracks file creation/deletion under C:\Riot Games\.
                // Events are filtered by OnNarrowActivityEvent — only counted if inside a
                // known game install folder AND not under \Riot Client\. The hybrid detector
                // also requires sustained disk-write I/O on RiotClientServices.exe before
                // declaring "patcher active". This prevents Vanguard/anti-cheat noise (and
                // any always-running process) from unlocking games in cafe environments.
                // Buffer raised to 64KB to handle bursts during large updates.
                var activityWatcher = new FileSystemWatcher(riotGamesPath)
                {
                    NotifyFilter = NotifyFilters.FileName,
                    IncludeSubdirectories = true,
                    InternalBufferSize = 65536
                };
                activityWatcher.Created += (s, e) => OnNarrowActivityEvent(e.FullPath, isDeleted: false);
                activityWatcher.Deleted += (s, e) => OnNarrowActivityEvent(e.FullPath, isDeleted: true);
                // Buffer overflow = TOO MUCH activity. Count it as activity so the rolling
                // window stays warm during large patches.
                // This is DIFFERENT from every other watcher: we do NOT restart on error because the
                // error itself carries semantic meaning (= high activity). The watcher keeps running.
                activityWatcher.Error += (s, e) =>
                {
                    OnNarrowActivityEvent(string.Empty, isDeleted: false);
                    _debugLogger("[Riot] Activity watcher buffer overflow — counted as activity");
                };
                activityWatcher.EnableRaisingEvents = true;
                lock (_watchers) { _watchers.Add(activityWatcher); }
                _debugLogger($"[Riot] Activity watcher started: {riotGamesPath} (narrow filter)" + (attempt > 0 ? $" (restart attempt {attempt})" : ""));
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] Failed to start activity watcher: {ex.Message}");
                ScheduleWatcherRestart(riotGamesPath, attempt, () => StartRiotActivityWatcherForPath(riotGamesPath, attempt + 1), "Riot activity");
            }
        }

        public void StartNewGameWatcher(Action<GameInfo> onNewGameDetected)
        {
            _onNewGameDetected = onNewGameDetected;

            var metadataPaths = DetectLibraryPaths();
            foreach (var metadataPath in metadataPaths)
            {
                if (!Directory.Exists(metadataPath))
                    continue;
                StartRiotNewGameWatcherForPath(metadataPath, 0);
            }
        }

        private void StartRiotNewGameWatcherForPath(string metadataPath, int attempt)
        {
            if (_stoppingWatchers) return;
            try
            {
                var watcher = new FileSystemWatcher(metadataPath)
                {
                    Filter = "*.yaml",
                    NotifyFilter = NotifyFilters.FileName,
                    IncludeSubdirectories = true  // CRITICAL: YAML files are in subfolders
                };
                watcher.Created += OnManifestCreated;
                var currentWatcher = watcher;
                watcher.Error += (s, e) =>
                {
                    _debugLogger($"[Riot] New game watcher error: {e.GetException().Message} — scheduling restart");
                    if (_stoppingWatchers) return;
                    lock (_watchers)
                    {
                        try { currentWatcher.EnableRaisingEvents = false; currentWatcher.Dispose(); } catch { }
                        _watchers.Remove(currentWatcher);
                    }
                    ScheduleWatcherRestart(metadataPath, attempt, () => StartRiotNewGameWatcherForPath(metadataPath, attempt + 1), "Riot new-game");
                };
                watcher.EnableRaisingEvents = true;
                lock (_watchers) { _watchers.Add(watcher); }
                _debugLogger($"[Riot] New game watcher started: {metadataPath}" + (attempt > 0 ? $" (restart attempt {attempt})" : ""));
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] Failed to start new game watcher on {metadataPath}: {ex.Message}");
                ScheduleWatcherRestart(metadataPath, attempt, () => StartRiotNewGameWatcherForPath(metadataPath, attempt + 1), "Riot new-game");
            }
        }

        /// <summary>
        /// Schedule a watcher restart with exponential backoff. Without this, a single transient
        /// FileSystemWatcher.Error (buffer overflow, permission change) silently stops detection
        /// until the service restarts. The activity watcher is excluded — its overflow carries
        /// semantic meaning ("update is active") and it keeps running.
        /// </summary>
        private void ScheduleWatcherRestart(string path, int attempt, Action restart, string name)
        {
            if (_stoppingWatchers) return;
            const int MaxAttempts = 10;
            if (attempt >= MaxAttempts)
            {
                _debugLogger($"[{name}] Giving up on watcher for {path} after {attempt} attempts");
                return;
            }
            var backoffMs = Math.Min(60000, 2000 * (1 << Math.Min(5, attempt)));
            _debugLogger($"[{name}] Restart in {backoffMs}ms (attempt {attempt + 1}/{MaxAttempts})");
            Task.Delay(backoffMs).ContinueWith(_ =>
            {
                if (_stoppingWatchers) return;
                try { restart(); }
                catch (Exception ex) { _debugLogger($"[{name}] Restart invocation failed: {ex.Message}"); }
            });
        }

        public void StopWatchers(bool releaseHandles = false)
        {
            _stoppingWatchers = true;
            // Critical #53 Layer 2: only release file handles on full service shutdown.
            // UpdateConfig / ToggleMaintenance restart watchers transiently and used to
            // release handles same-tick they were acquired — disabling no-delete-share
            // protection on exes/dlls after every PushConfig. Default false keeps handles
            // through restart cycles.
            if (releaseHandles)
                ReleaseAllHandles();
            lock (_watchers)
            {
                foreach (var watcher in _watchers)
                {
                    try
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
                    }
                    catch { }
                }
                _watchers.Clear();
            }
        }

        private void OnManifestDeleted(object sender, FileSystemEventArgs e)
        {
            try
            {
                var backupFile = Path.Combine(_backupPath, Path.GetFileName(e.FullPath));
                GameInfo? game = null;

                if (File.Exists(backupFile))
                {
                    game = ParseYamlManifest(backupFile);
                }

                if (game != null)
                {
                    game.ManifestPath = e.FullPath;
                    _onUninstallDetected?.Invoke(game);
                }
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] Error handling manifest deletion: {ex.Message}");
            }
        }

        private void OnGameFolderDeleted(object sender, FileSystemEventArgs e)
        {
            try
            {
                var folderName = Path.GetFileName(e.FullPath);

                var gameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "VALORANT", "Valorant" },
                    { "League of Legends", "League of Legends" },
                    { "Legends of Runeterra", "Legends of Runeterra" },
                    { "Teamfight Tactics", "Teamfight Tactics" }
                };

                if (gameMap.TryGetValue(folderName, out var gameName))
                {
                    var game = new GameInfo
                    {
                        PlatformId = "riot",
                        GameName = gameName,
                        InstallPath = e.FullPath,
                        ManifestPath = ""
                    };

                    _onUninstallDetected?.Invoke(game);
                }
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] Error handling folder deletion: {ex.Message}");
            }
        }

        private void OnManifestCreated(object sender, FileSystemEventArgs e)
        {
            try
            {
                Thread.Sleep(500);

                var game = ParseYamlManifest(e.FullPath);
                if (game != null)
                {
                    _onNewGameDetected?.Invoke(game);
                }
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] Error handling new manifest: {ex.Message}");
            }
        }

        public void HealthCheck(List<GameInfo> protectedGames, List<string> blockedUsers, bool blockAllNonAdminByDefault)
        {
            var riotGames = protectedGames.Where(g => g.PlatformId == PlatformId).ToList();

            // PLATFORM-LEVEL busy check: if either the patcher is actively replacing files OR
            // a game process is currently running, temporarily remove protection from ALL Riot
            // games so the platform can operate freely (patcher updates, in-game asset
            // downloads, repair).
            //
            // Single check covers Valorant, LoL, LoR, TFT and any future Riot titles without
            // per-game wiring.
            if (IsRiotPlatformBusy())
            {
                _debugLogger("[Riot] HealthCheck: platform busy (patcher or game running) — removing protection from all Riot games temporarily");
                foreach (var game in riotGames)
                    RemoveProtection(game, blockedUsers);
                return;
            }

            // Platform idle — re-apply any missing protection
            foreach (var game in riotGames)
            {
                if (!Directory.Exists(game.InstallPath))
                    continue;

                if (!CheckProtectionActive(game.InstallPath))
                {
                    _debugLogger($"[Riot] HealthCheck: protection missing for {game.GameName}, re-applying");
                    ApplyProtection(game, blockedUsers, blockAllNonAdminByDefault);
                }
            }
        }

        /// <summary>
        /// IGamePlatform override: true when Riot's platform is busy with either a patcher
        /// update OR a running game session. Called by PlatformManager.ApplyGameProtection to
        /// skip re-locking, regardless of what triggered ApplyProtection (PushConfig,
        /// maintenance toggle, etc.).
        /// </summary>
        public bool IsUpdateInProgress() => IsRiotPlatformBusy();

        /// <summary>
        /// Known Riot GAME executable names — the actual in-match process, not launcher
        /// UI. Used by IsAnyRiotGameRunning to detect when a Riot game is currently being
        /// played so we can drop protection (Critical #R2, procmon-verified 2026-05-26:
        /// LoR.exe opens its DecodedBanks directory with Delete intent during in-game
        /// asset management; our (CI)(DE) /T blocks it → stuck at 0 MB).
        ///
        /// CRITICAL: include ONLY the actual game executable, NOT the launcher UI
        /// processes (`RiotClientUx`, `LeagueClient`, `LeagueClientUx`). Launcher UI runs
        /// whenever the user has the Riot Client / LoL Client open — including just
        /// browsing friends list or settings. Dropping protection on launcher-open would
        /// be far too aggressive. Only the in-match game process should trigger the drop.
        ///
        /// Cafe-scale note: Process.GetProcessesByName(name) is cheap (single
        /// NtQuerySystemInformation call, filtered by name). Checking a handful of names
        /// every HealthCheck tick is microseconds. Do NOT replace with
        /// Process.GetProcesses() which enumerates the full process table.
        ///
        /// Names verified via Microsoft Q&amp;A and file.net (2026-05-26 web search). If
        /// Riot ships a new game title, add its in-match executable name here.
        /// </summary>
        private static readonly string[] RiotGameProcessNames = new[]
        {
            // Legends of Runeterra
            "LoR",                          // LoR.exe — main game executable (procmon-verified)
            "LoRCrashHandler64",            // LoRCrashHandler64.exe — companion loaded with game

            // Valorant (Unreal Engine 4 shipping build)
            "VALORANT-Win64-Shipping",      // VALORANT-Win64-Shipping.exe — game executable

            // League of Legends (and TFT, which runs through the LoL client — no standalone TFT process)
            "League of Legends",            // "League of Legends.exe" (note the spaces) — in-match game process
        };

        /// <summary>
        /// Returns true if any Riot game process is currently running. Two-stage detection:
        ///
        /// Stage 1 (fast path) — check known game exe names via Process.GetProcessesByName.
        /// Single syscall per name, microseconds. Catches the games we've explicitly
        /// catalogued in RiotGameProcessNames.
        ///
        /// Stage 2 (fallback) — only runs if Stage 1 found nothing. Enumerates all
        /// processes and checks whether any process's executable path starts with a known
        /// Riot game install path (from _knownGameInstallPaths, populated by
        /// GetInstalledGames). Catches:
        ///   • Future Riot game titles not yet added to RiotGameProcessNames
        ///   • Games whose executable name we don't know yet
        ///   • Games whose process name changes between versions
        ///
        /// Without the fallback, an unknown Riot game would silently hit R2 (in-game asset
        /// download stalls at 0 MB). With the fallback, any process running from inside a
        /// known Riot install folder triggers the protection drop, regardless of exe name.
        ///
        /// Cost: Stage 1 is microseconds. Stage 2 is ~20ms (Process.GetProcesses + MainModule
        /// access per process) — only paid when no known game is running, so usually skipped.
        /// On HealthCheck's 10s tick this is negligible.
        /// </summary>
        private bool IsAnyRiotGameRunning()
        {
            var installPaths = _knownGameInstallPaths;

            // Stage 1 — fast path against known names. A matching filename alone is not
            // sufficient when install paths are known: an unrelated LoR.exe elsewhere on
            // the machine must not unlock every protected Riot game.
            foreach (var name in RiotGameProcessNames)
            {
                try
                {
                    var procs = Process.GetProcessesByName(name);
                    try
                    {
                        foreach (var p in procs)
                        {
                            try
                            {
                                if (installPaths.Count == 0)
                                    return true;

                                var exePath = p.MainModule?.FileName;
                                if (!string.IsNullOrEmpty(exePath) &&
                                    installPaths.Any(path => IsPathSameOrDescendant(exePath, path)))
                                {
                                    return true;
                                }
                            }
                            catch { /* protected process or race — try the next match */ }
                        }
                    }
                    finally
                    {
                        foreach (var p in procs) { try { p.Dispose(); } catch { } }
                    }
                }
                catch { /* enumeration race — try next name */ }
            }

            // Stage 2 — fallback: scan all processes, match by image path.
            if (installPaths == null || installPaths.Count == 0)
                return false;

            Process[] allProcesses;
            try { allProcesses = Process.GetProcesses(); }
            catch { return false; }

            try
            {
                foreach (var p in allProcesses)
                {
                    try
                    {
                        // Process.MainModule.FileName requires opening the process with
                        // QueryLimitedInformation. Throws for protected processes (system
                        // services, anti-cheat drivers, etc.) — those are skipped, which
                        // is correct: those aren't Riot games anyway.
                        var exePath = p.MainModule?.FileName;
                        if (string.IsNullOrEmpty(exePath))
                            continue;

                        foreach (var installPath in installPaths)
                        {
                            // Windows paths are case-insensitive. Trim trailing slash
                            // already done when populating _knownGameInstallPaths.
                            if (IsPathSameOrDescendant(exePath, installPath))
                            {
                                _debugLogger($"[Riot] IsAnyRiotGameRunning: fallback matched {Path.GetFileName(exePath)} (install: {installPath}). Add this exe name to RiotGameProcessNames to skip Stage 2 in future.");
                                return true;
                            }
                        }
                    }
                    catch { /* protected process or race — skip */ }
                }
            }
            finally
            {
                // Dispose every Process per Critical #49.
                foreach (var p in allProcesses) { try { p.Dispose(); } catch { } }
            }

            return false;
        }

        /// <summary>
        /// Composite "platform busy" check. Returns true if EITHER the patcher is active
        /// (existing Signal A + B detection) OR a known Riot game process is running.
        /// </summary>
        private bool IsRiotPlatformBusy()
        {
            return IsRiotPlatformPatching() || IsAnyRiotGameRunning();
        }

        /// <summary>
        /// Hybrid patcher detection — both signals must agree.
        ///
        /// Signal A (disk write I/O on RiotClientServices.exe): sampled every HealthCheck tick.
        /// Idle client writes ~0 KB/s. Active patcher sustains MB/s. Threshold = 512 KB/s for
        /// 2 consecutive ticks (~20s sustained).
        ///
        /// Signal B (sustained narrow FSW): file events under C:\Riot Games\, filtered to
        /// known game install folders only (excludes \Riot Client\). Threshold = 5 events
        /// in a 10s rolling window. Filters single-event noise from anti-cheat/integrity checks.
        ///
        /// Both must be true → patcher detected. This eliminates the cafe-environment failure
        /// where Riot Client auto-starts and Vanguard/anti-cheat noise made the prior single-FSW
        /// signal effectively always true.
        ///
        /// Trade-off: if the patcher's download phase has zero local I/O for an extended period
        /// (e.g. very slow connection, long pause between chunks), Signal A drops false and the
        /// next file delete attempt will be blocked. Admin should use Maintenance Mode for
        /// planned large updates on slow connections.
        /// </summary>
        private bool IsRiotPlatformPatching()
        {
            try
            {
                if (Process.GetProcessesByName("RiotClientServices").Length == 0)
                {
                    // Cleanup state when client is not running so the next launch starts clean.
                    _sustainedHighIoTicks = 0;
                    _lastIoSampleTime = DateTime.MinValue;
                    return false;
                }

                bool ioActive = SampleIoCounters();
                bool fswActive = HasSustainedFswActivity();

                if (ioActive && fswActive)
                {
                    _debugLogger("[Riot] Patcher detected: I/O sustained + FSW events");
                    return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Polls disk write counters on RiotClientServices.exe. Returns true if write rate
        /// has exceeded HighIoThresholdBytesPerSec for RequiredSustainedTicks consecutive samples.
        /// First call after process start (or after process exit) primes the baseline and returns false.
        /// </summary>
        private bool SampleIoCounters()
        {
            var procs = Process.GetProcessesByName("RiotClientServices");
            if (procs.Length == 0)
            {
                _sustainedHighIoTicks = 0;
                _lastIoSampleTime = DateTime.MinValue;
                return false;
            }

            long totalWriteBytes = 0;
            foreach (var p in procs)
            {
                try
                {
                    if (GetProcessIoCounters(p.Handle, out var ioc))
                        totalWriteBytes += (long)ioc.WriteTransferCount;
                }
                catch { /* process exited between enumeration and handle access */ }
                finally { try { p.Dispose(); } catch { } }
            }

            var now = DateTime.Now;
            if (_lastIoSampleTime == DateTime.MinValue)
            {
                _lastIoWriteBytes = totalWriteBytes;
                _lastIoSampleTime = now;
                return false;
            }

            var elapsedSec = (now - _lastIoSampleTime).TotalSeconds;
            if (elapsedSec < 1) return _sustainedHighIoTicks >= RequiredSustainedTicks;

            var writeDelta = totalWriteBytes - _lastIoWriteBytes;
            var writeRate = writeDelta / elapsedSec;
            _lastIoWriteBytes = totalWriteBytes;
            _lastIoSampleTime = now;

            if (writeRate > HighIoThresholdBytesPerSec)
                _sustainedHighIoTicks++;
            else
                _sustainedHighIoTicks = 0;

            return _sustainedHighIoTicks >= RequiredSustainedTicks;
        }

        /// <summary>
        /// Returns true if at least FswActivityMinEvents file events have fired in the last
        /// FswActivityWindowSeconds. Prunes expired events as a side effect.
        /// </summary>
        private bool HasSustainedFswActivity()
        {
            lock (_fswEventLock)
            {
                var cutoff = DateTime.Now.Ticks - TimeSpan.FromSeconds(FswActivityWindowSeconds).Ticks;
                while (_recentFswEventTicks.Count > 0 && _recentFswEventTicks.Peek() < cutoff)
                    _recentFswEventTicks.Dequeue();
                return _recentFswEventTicks.Count >= FswActivityMinEvents;
            }
        }

        /// <summary>
        /// FSW event handler — narrow filter. Counts an event toward HasSustainedFswActivity
        /// only if the path is inside a known game install folder and not under \Riot Client\.
        /// Empty path = FSW buffer overflow, counted as activity.
        /// isDeleted = true for Deleted events, false for Created events.
        /// </summary>
        private void OnNarrowActivityEvent(string fullPath, bool isDeleted = false)
        {
            // Exclude Riot Client's own folder — its writes are runtime noise, not patcher activity.
            if (!string.IsNullOrEmpty(fullPath) &&
                fullPath.IndexOf("\\Riot Client\\", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            // Buffer overflow (empty path) always counts. Otherwise require path inside a known
            // game install folder.
            bool inGameFolder = string.IsNullOrEmpty(fullPath);
            if (!inGameFolder)
            {
                var paths = _knownGameInstallPaths;
                foreach (var gamePath in paths)
                {
                    if (IsPathSameOrDescendant(fullPath, gamePath))
                    {
                        inGameFolder = true;
                        break;
                    }
                }
            }
            if (!inGameFolder) return;

            lock (_fswEventLock)
            {
                _recentFswEventTicks.Enqueue(DateTime.Now.Ticks);
                var cutoff = DateTime.Now.Ticks - TimeSpan.FromSeconds(FswActivityWindowSeconds).Ticks;
                while (_recentFswEventTicks.Count > 0 && _recentFswEventTicks.Peek() < cutoff)
                    _recentFswEventTicks.Dequeue();
            }

            // Feed the uninstall detector only for definite events inside a game that
            // GamePinned currently protects. Known paths intentionally remain broader so
            // unlocked games still contribute to legitimate patch-activity detection.
            if (!string.IsNullOrEmpty(fullPath) && IsProtectedGamePath(fullPath))
                CheckUninstallPattern(isDeleted);
        }

        private bool IsProtectedGamePath(string fullPath)
        {
            var paths = _protectedGameInstallPaths;
            foreach (var gamePath in paths)
            {
                if (IsPathSameOrDescendant(fullPath, gamePath))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Component-aware containment for Windows paths. A raw StartsWith comparison would
        /// incorrectly treat a sibling such as "LoRBackup" as being inside "LoR", causing
        /// unrelated activity or processes to suppress protection.
        /// </summary>
        private static bool IsPathSameOrDescendant(string candidatePath, string directoryPath)
        {
            try
            {
                var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
                var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));

                if (string.Equals(candidate, directory, StringComparison.OrdinalIgnoreCase))
                    return true;

                var prefix = directory.EndsWith(Path.DirectorySeparatorChar)
                    ? directory
                    : directory + Path.DirectorySeparatorChar;
                return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tracks file deletion/creation ratio in a rolling window.
        /// When deletions far outnumber creates (uninstall signature) and the patcher is not
        /// active, kills RiotClientServices to abort the uninstall.
        /// </summary>
        private void CheckUninstallPattern(bool isDeleted)
        {
            if (_uninstallKillInProgress != 0) return;

            long nowTicks = DateTime.Now.Ticks;
            long cutoff = nowTicks - TimeSpan.FromSeconds(UninstallWindowSeconds).Ticks;

            int deletions, creates;
            lock (_uninstallDetectLock)
            {
                if (isDeleted)
                    _uninstallDeleteTicks.Enqueue(nowTicks);
                else
                    _uninstallCreateTicks.Enqueue(nowTicks);

                // Prune expired events
                while (_uninstallDeleteTicks.Count > 0 && _uninstallDeleteTicks.Peek() < cutoff)
                    _uninstallDeleteTicks.Dequeue();
                while (_uninstallCreateTicks.Count > 0 && _uninstallCreateTicks.Peek() < cutoff)
                    _uninstallCreateTicks.Dequeue();

                deletions = _uninstallDeleteTicks.Count;
                creates = _uninstallCreateTicks.Count;
            }

            // Not enough signal yet
            if (deletions < UninstallMinDeletions) return;

            // Creates must be much fewer than deletions — updates have balanced ratio
            double ratio = creates == 0 ? double.MaxValue : (double)deletions / creates;
            if (ratio < UninstallMinDeleteRatio) return;

            // Don't fire during a known Riot patch — that's a legitimate high-deletion operation
            if (IsRiotPlatformPatching()) return;

            // Uninstall pattern confirmed — try to become the one thread that kills the launcher
            if (Interlocked.CompareExchange(ref _uninstallKillInProgress, 1, 0) != 0) return;

            Task.Run(() =>
            {
                try
                {
                    _logger($"BLOCKED: Riot uninstall attempt detected ({deletions} deletions, {creates} creates in {UninstallWindowSeconds}s).");
                    _debugLogger($"[Riot] Uninstall pattern: {deletions} deletes / {creates} creates in {UninstallWindowSeconds}s — killing launcher");

                    KillLauncher();

                    // Reset counters so a second attempt gets a clean window
                    lock (_uninstallDetectLock)
                    {
                        _uninstallDeleteTicks.Clear();
                        _uninstallCreateTicks.Clear();
                    }

                    // Brief pause before re-enable to let the kill take effect and
                    // prevent a kill-storm if the user relaunches and tries again immediately
                    Thread.Sleep(5000);
                }
                catch (Exception ex)
                {
                    _debugLogger($"[Riot] CheckUninstallPattern kill task: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _uninstallKillInProgress, 0);
                }
            });
        }

        public bool CheckProtectionActive(string path)
        {
            try
            {
                var manifestPath = FindManifestPathForInstallPath(path);
                return CheckProtectionActiveCore(path, manifestPath);
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] CheckProtectionActive error on {path}: {ex.Message}");
                return false;
            }
        }

        private string? FindManifestPathForInstallPath(string installPath)
        {
            foreach (var metadataPath in DetectLibraryPaths())
            {
                foreach (var yamlFile in Directory.EnumerateFiles(
                    metadataPath,
                    "*.product_settings.yaml",
                    SearchOption.AllDirectories))
                {
                    try
                    {
                        var yamlText = File.ReadAllText(yamlFile);
                        var pathMatch = System.Text.RegularExpressions.Regex.Match(
                            yamlText,
                            @"product_install_full_path:\s*[""']?(.+?)[""']?\s*$",
                            System.Text.RegularExpressions.RegexOptions.Multiline);
                        if (!pathMatch.Success)
                        {
                            pathMatch = System.Text.RegularExpressions.Regex.Match(
                                yamlText,
                                @"product_install_root:\s*[""']?(.+?)[""']?\s*$",
                                System.Text.RegularExpressions.RegexOptions.Multiline);
                        }
                        if (!pathMatch.Success)
                            continue;

                        var basePath = pathMatch.Groups[1].Value.Trim().Trim('"', '\'')
                            .Replace('/', '\\').TrimEnd('\\');
                        var gameName = GetGameNameFromYamlFile(yamlFile);
                        var refinedPath = RefineRiotInstallPath(gameName, basePath);
                        if (refinedPath != null &&
                            refinedPath.Equals(installPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        {
                            return yamlFile;
                        }
                    }
                    catch (Exception ex)
                    {
                        _debugLogger($"[Riot] Manifest contract lookup skipped {Path.GetFileName(yamlFile)}: {ex.Message}");
                    }
                }
            }
            return null;
        }

        private bool CheckProtectionActiveCore(string installPath, string? manifestPath)
        {
            try
            {
                if (!Directory.Exists(installPath) ||
                    string.IsNullOrEmpty(manifestPath) ||
                    !File.Exists(manifestPath))
                {
                    return false;
                }

                var gameSecurity = new DirectoryInfo(installPath)
                    .GetAccessControl(AccessControlSections.Access);
                var protectedSids = gameSecurity
                    .GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
                    .OfType<FileSystemAccessRule>()
                    .Where(rule =>
                        rule.AccessControlType == AccessControlType.Deny &&
                        rule.IdentityReference is SecurityIdentifier &&
                        (rule.FileSystemRights & FileSystemRights.Delete) != 0 &&
                        (rule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0 &&
                        (rule.InheritanceFlags & InheritanceFlags.ObjectInherit) == 0)
                    .Select(rule => (SecurityIdentifier)rule.IdentityReference)
                    .ToHashSet();
                if (protectedSids.Count == 0)
                    return false;

                if (protectedSids.Any(sid => !HasExplicitDeleteDeny(manifestPath, sid)))
                    return false;

                var manifestParent = Path.GetDirectoryName(manifestPath);
                if (string.IsNullOrEmpty(manifestParent) ||
                    protectedSids.Any(sid => !HasExplicitDeleteChildDeny(manifestParent, sid)))
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] Protection contract check failed on {installPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns true only when the game root and product manifest have exactly the desired
        /// explicit deny identities and the manifest parent has every DeleteChild boundary
        /// required by its protected manifests. This avoids tearing down a correct ACL contract
        /// during service startup or a healthy periodic re-apply. Handles are process-local and
        /// are deliberately reacquired by the caller even when this returns true.
        /// </summary>
        private bool IsDesiredProtectionContractActive(GameInfo game, IEnumerable<string> desiredIdentities)
        {
            try
            {
                if (string.IsNullOrEmpty(game.ManifestPath) || !File.Exists(game.ManifestPath))
                    return false;

                var desiredSids = desiredIdentities
                    .Select(TryResolveSecurityIdentifier)
                    .Where(sid => sid != null)
                    .Select(sid => sid!.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (desiredSids.Count == 0)
                    return false;

                var gameSids = GetExplicitDenySidValues(
                    game.InstallPath,
                    FileSystemRights.Delete,
                    requireContainerInherit: true,
                    forbidObjectInherit: true);
                if (!gameSids.SetEquals(desiredSids))
                    return false;

                var manifestSids = GetExplicitDenySidValues(
                    game.ManifestPath,
                    FileSystemRights.Delete);
                if (!manifestSids.SetEquals(desiredSids))
                    return false;

                var manifestParent = Path.GetDirectoryName(game.ManifestPath);
                if (string.IsNullOrEmpty(manifestParent))
                    return false;

                var parentSids = GetExplicitDenySidValues(
                    manifestParent,
                    FileSystemRights.DeleteSubdirectoriesAndFiles);
                var requiredParentSids = Directory
                    .EnumerateFiles(manifestParent, "*.product_settings.yaml", SearchOption.TopDirectoryOnly)
                    .SelectMany(path => GetExplicitDenySidValues(path, FileSystemRights.Delete))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return parentSids.SetEquals(requiredParentSids) &&
                       CheckProtectionActiveCore(game.InstallPath, game.ManifestPath);
            }
            catch (Exception ex)
            {
                _debugLogger($"[Riot] Desired protection contract check failed for {game.GameName}: {ex.Message}");
                return false;
            }
        }

        private static HashSet<string> GetExplicitDenySidValues(
            string path,
            FileSystemRights requiredRights,
            bool requireContainerInherit = false,
            bool forbidObjectInherit = false)
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access);

            return security
                .GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Where(rule =>
                    rule.AccessControlType == AccessControlType.Deny &&
                    rule.IdentityReference is SecurityIdentifier &&
                    (rule.FileSystemRights & requiredRights) != 0 &&
                    (!requireContainerInherit ||
                     (rule.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0) &&
                    (!forbidObjectInherit ||
                     (rule.InheritanceFlags & InheritanceFlags.ObjectInherit) == 0))
                .Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public bool IsGameStable(string installPath)
        {
            // Riot stability is assessed at the platform level via IsRiotPlatformPatching()
            // in HealthCheck — not per-game. This method is kept for IGamePlatform compliance.
            // HealthCheck no longer calls this; it uses the platform-level check instead.
            return true;
        }

        /// <summary>
        /// Clear all Riot Client login/session data so next launch shows a fresh login screen.
        /// Deletes: RiotClientPrivateSettings.yaml and RiotGamesPrivateSettings.yaml.
        /// </summary>
        public void ClearSession(string currentUserLocalAppData, string? currentUserSid)
        {
            var riotDataPath = Path.Combine(currentUserLocalAppData, "Riot Games", "Riot Client", "Data");
            if (!Directory.Exists(riotDataPath))
            {
                _logger("Clear Session: Riot Client data folder not found.");
                return;
            }

            // 1. Kill Riot client first
            KillLauncher();
            Thread.Sleep(2000);

            int deleted = 0;

            // 2. Delete RiotClientPrivateSettings.yaml (OAuth refresh token — primary session file)
            var privateSettings = Path.Combine(riotDataPath, "RiotClientPrivateSettings.yaml");
            if (File.Exists(privateSettings))
            {
                try { File.Delete(privateSettings); deleted++; _debugLogger("[Riot] ClearSession: deleted RiotClientPrivateSettings.yaml"); }
                catch (Exception ex) { _debugLogger($"[Riot] ClearSession: failed to delete RiotClientPrivateSettings.yaml — {ex.Message}"); }
            }

            // 3. Delete RiotGamesPrivateSettings.yaml (secondary session data)
            var riotPrivateSettings = Path.Combine(riotDataPath, "RiotGamesPrivateSettings.yaml");
            if (File.Exists(riotPrivateSettings))
            {
                try { File.Delete(riotPrivateSettings); deleted++; _debugLogger("[Riot] ClearSession: deleted RiotGamesPrivateSettings.yaml"); }
                catch (Exception ex) { _debugLogger($"[Riot] ClearSession: failed to delete RiotGamesPrivateSettings.yaml — {ex.Message}"); }
            }

            _logger($"Riot session cleared ({deleted} item(s) removed). Next launch will show a fresh login screen.");
        }
    }
}
