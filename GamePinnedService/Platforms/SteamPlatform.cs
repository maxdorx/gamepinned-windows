using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Principal;
using GamePinned.Service.Core;
using GamePinned.Shared.Models;

namespace GamePinned.Service.Platforms
{
    /// <summary>
    /// Steam platform implementation for GamePinned
    /// Handles Steam game detection, protection, and manifest backup/restore
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class SteamPlatform : IGamePlatform
    {
        private List<FileSystemWatcher> _watchers = new();
        private volatile bool _stoppingWatchers = false;
        private string _backupPath;
        private Action<string> _logger;
        private Action<string> _debugLogger;
        private Action<GameInfo>? _onUninstallDetected;
        private Action<GameInfo>? _onNewGameDetected;
        private readonly HashSet<string> _staleDownloadLogged = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _aclMutationLock = new();

        public string PlatformName => "Steam";
        public string PlatformId => "steam";

        /// <summary>
        /// Initialize SteamPlatform with backup directory
        /// </summary>
        /// <param name="backupPath">Directory to store manifest backups</param>
        public SteamPlatform(string backupPath, Action<string> logger, Action<string>? debugLogger = null)
        {
            _backupPath = backupPath;
            _logger = logger;
            _debugLogger = debugLogger ?? (_ => { });
            Directory.CreateDirectory(_backupPath);
        }

        /// <summary>
        /// Check if Steam is installed on the system
        /// </summary>
        public bool IsInstalled()
        {
            // Check registry (WOW6432Node)
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                var installPath = key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(installPath) && Directory.Exists(Path.Combine(installPath, "steamapps")))
                    return true;
            }
            catch { }

            // Check Uninstall key (often more reliable)
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam");
                var installPath = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(installPath) && Directory.Exists(Path.Combine(installPath, "steamapps")))
                    return true;
            }
            catch { }

            // Check common paths
            foreach (var path in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam", @"D:\Steam", @"E:\Steam" })
            {
                if (Directory.Exists(Path.Combine(path, "steamapps")))
                    return true;
            }

            // Check running process
            try
            {
                var proc = Process.GetProcessesByName("steam").FirstOrDefault();
                if (proc != null)
                {
                    var dir = Path.GetDirectoryName(proc.MainModule?.FileName);
                    if (dir != null && Directory.Exists(Path.Combine(dir, "steamapps")))
                        return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Detect all Steam library paths (main installation + additional libraries)
        /// </summary>
        public List<string> DetectLibraryPaths()
        {
            var libraryPaths = new List<string>();

            // Check registry
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                var installPath = key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(installPath) && Directory.Exists(Path.Combine(installPath, "steamapps")))
                    libraryPaths.Add(installPath);
            }
            catch { }

             // Check Uninstall key
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam");
                var installPath = key?.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(installPath) && Directory.Exists(Path.Combine(installPath, "steamapps")) && !libraryPaths.Contains(installPath, StringComparer.OrdinalIgnoreCase))
                    libraryPaths.Add(installPath);
            }
            catch { }

            // Check common paths
            foreach (var path in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam", @"D:\Steam", @"D:\SteamLibrary", @"E:\Steam", @"E:\SteamLibrary" })
            {
                if (!libraryPaths.Contains(path, StringComparer.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(path, "steamapps")))
                    libraryPaths.Add(path);
            }

            // Check running process
            try
            {
                var proc = Process.GetProcessesByName("steam").FirstOrDefault();
                if (proc != null)
                {
                    var dir = Path.GetDirectoryName(proc.MainModule?.FileName);
                    if (dir != null && Directory.Exists(Path.Combine(dir, "steamapps")) && !libraryPaths.Contains(dir, StringComparer.OrdinalIgnoreCase))
                        libraryPaths.Add(dir);
                }
            }
            catch { }

            // Find additional libraries from libraryfolders.vdf
            var additionalPaths = new List<string>();
            foreach (var path in libraryPaths)
            {
                var vdfPath = Path.Combine(path, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    try
                    {
                        var lines = File.ReadAllLines(vdfPath);
                        foreach (var line in lines)
                        {
                            if (line.Contains("\"path\""))
                            {
                                var parts = line.Split('"');
                                if (parts.Length >= 4)
                                {
                                    var libraryPath = parts[3].Replace("\\\\", "\\");
                                    if (Directory.Exists(libraryPath) &&
                                        !libraryPaths.Contains(libraryPath, StringComparer.OrdinalIgnoreCase) &&
                                        !additionalPaths.Contains(libraryPath, StringComparer.OrdinalIgnoreCase))
                                    {
                                        additionalPaths.Add(libraryPath);
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            return libraryPaths.Concat(additionalPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Get all installed games from a specific Steam library path
        /// </summary>
        public List<GameInfo> GetInstalledGames(string libraryPath)
        {
            var games = new List<GameInfo>();
            var steamappsPath = Path.Combine(libraryPath, "steamapps");
            var commonPath = Path.Combine(steamappsPath, "common");

            if (!Directory.Exists(commonPath))
                return games;

            // Get all game folders
            foreach (var gameFolder in Directory.GetDirectories(commonPath))
            {
                var gameName = Path.GetFileName(gameFolder);

                // Try to find corresponding .acf manifest file
                var manifestPath = FindManifestForGame(steamappsPath, gameName);

                var game = new GameInfo
                {
                    PlatformId = PlatformId,
                    GameName = gameName,
                    InstallPath = gameFolder,
                    ManifestPath = manifestPath ?? "",
                    GameId = ExtractAppIdFromManifest(manifestPath) ?? "",
                    IsProtected = CheckProtectionActive(gameFolder)
                };

                games.Add(game);
            }

            return games;
        }

        /// <summary>
        /// Find the .acf manifest file for a specific game by name
        /// </summary>
        private string? FindManifestForGame(string steamappsPath, string gameName)
        {
            try
            {
                foreach (var acfFile in Directory.GetFiles(steamappsPath, "appmanifest_*.acf"))
                {
                    var lines = File.ReadAllLines(acfFile);
                    foreach (var line in lines)
                    {
                        if (line.Contains("\"installdir\""))
                        {
                            var parts = line.Split('"');
                            if (parts.Length >= 4 && parts[3].Trim().Equals(gameName, StringComparison.OrdinalIgnoreCase))
                            {
                                return acfFile;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { _debugLogger($"[Steam] FindAcfByGameName({gameName}): {ex.Message}"); }

            return null;
        }

        /// <summary>
        /// Extract Steam AppID from .acf manifest file
        /// </summary>
        private string? ExtractAppIdFromManifest(string? manifestPath)
        {
            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                return null;

            try
            {
                foreach (var line in File.ReadAllLines(manifestPath))
                {
                    if (line.Contains("\"appid\""))
                    {
                        var parts = line.Split('"');
                        if (parts.Length >= 4)
                            return parts[3].Trim();
                    }
                }
            }
            catch (Exception ex) { _debugLogger($"[Steam] ExtractAppIdFromManifest({Path.GetFileName(manifestPath ?? "")}): {ex.Message}"); }

            return null;
        }

        /// <summary>
        /// Apply NTFS deny-delete protection to a game folder using icacls
        /// </summary>
        public void ApplyProtection(GameInfo game, List<string> blockedUsers, bool blockAllNonAdminByDefault)
        {
            lock (_aclMutationLock)
            {
                ApplyProtectionCore(game, blockedUsers, blockAllNonAdminByDefault);
            }
        }

        private void ApplyProtectionCore(GameInfo game, List<string> blockedUsers, bool blockAllNonAdminByDefault)
        {
            if (!Directory.Exists(game.InstallPath))
            {
                _logger($"[LOCK] Skipping {game.GameName} - install path does not exist");
                return;
            }

            _debugLogger($"[Steam] ApplyProtection: {game.GameName} at {game.InstallPath} | blockedUsers={blockedUsers.Count} blockAll={blockAllNonAdminByDefault}");

            // Build resolved identity map for local users (done once — avoids repeated SID lookups)
            var localUsers = GetLocalUsersForSync();
            var localUserToIdentity = localUsers.ToDictionary(
                u => u,
                u => ResolveToIdentity(u),
                StringComparer.OrdinalIgnoreCase);
            var usersToBlockSet = new HashSet<string>(blockedUsers, StringComparer.OrdinalIgnoreCase);

            if (blockedUsers.Count == 0)
            {
                var localIdentities = localUserToIdentity.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (blockAllNonAdminByDefault)
                {
                    var desiredIdentities = new List<string> { "*S-1-5-32-545" };
                    RemoveSteamManifestDenyRules(game.ManifestPath);
                    if (HasExactProtectionContract(game.InstallPath, desiredIdentities))
                    {
                        _debugLogger($"[Steam] ApplyProtection: exact protection contract already active for {game.GameName}; skipping ACL refresh");
                        return;
                    }

                    // Establish the complete desired contract before removing obsolete identities.
                    // A blocked user must remain covered throughout a per-user <-> group policy
                    // transition; removing the old tree rules first creates a live delete window.
                    SetSharedParentDeleteChildDenyBatch(game.InstallPath, desiredIdentities);
                    SetDenyRuleBatch(game.InstallPath, desiredIdentities);

                    // Discover obsolete identities from the ACL itself. Profile directory names
                    // are not account names (for example Standard-User can own C:\Users\test),
                    // so C:\Users enumeration cannot be the cleanup authority.
                    var obsoleteIdentities = DiscoverObsoleteProtectionIdentities(
                        game.InstallPath, desiredIdentities);
                    if (obsoleteIdentities.Count > 0)
                    {
                        RemoveDenyRuleBatch(game.InstallPath, obsoleteIdentities);
                        foreach (var identity in obsoleteIdentities)
                            RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity);
                    }
                    // NOTE: manifest (.acf) is intentionally NOT deny-ruled for Steam.
                    // Steam's protection uses FSW + kill/restore, not a manifest deny rule.
                    // A deny rule on the .acf blocks Steam's own update mechanism (Steam needs
                    // to delete+rewrite the .acf when applying patches).
                }
                else
                {
                    // Remove group rule + all per-user rules in one batch tree walk
                    var allToRemove = new List<string> { "*S-1-5-32-545" };
                    allToRemove.AddRange(localIdentities);
                    allToRemove.AddRange(DiscoverProtectionIdentities(game.InstallPath));
                    if (!string.IsNullOrEmpty(game.ManifestPath) && File.Exists(game.ManifestPath))
                        allToRemove.AddRange(AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                            game.ManifestPath, recursive: false, _debugLogger));
                    allToRemove = allToRemove.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    RemoveDenyRuleBatch(game.InstallPath, allToRemove);
                    if (!string.IsNullOrEmpty(game.ManifestPath))
                        RemoveDenyRuleFileBatch(game.ManifestPath, allToRemove);
                    foreach (var identity in allToRemove)
                        RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity);
                }
                return;
            }

            // Specific users mode: collect who to add and who to remove
            var toApplyIdentities = new List<string>();
            var toRemoveIdentities = new List<string> { "*S-1-5-32-545" }; // always remove group rule

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

            // Users in blocked list not found locally
            foreach (var blockName in blockedUsers)
            {
                if (!localUsers.Any(u => string.Equals(u, blockName, StringComparison.OrdinalIgnoreCase) ||
                                       blockName.EndsWith("\\" + u, StringComparison.OrdinalIgnoreCase)))
                {
                    toApplyIdentities.Add(ResolveToIdentity(blockName));
                }
            }

            toApplyIdentities = toApplyIdentities.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            toRemoveIdentities = toRemoveIdentities
                .Where(id => !toApplyIdentities.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            RemoveSteamManifestDenyRules(game.ManifestPath);
            if (toApplyIdentities.Count > 0 &&
                HasExactProtectionContract(game.InstallPath, toApplyIdentities))
            {
                _debugLogger($"[Steam] ApplyProtection: exact protection contract already active for {game.GameName}; skipping ACL refresh");
                return;
            }

            // Make-before-break transition: establish the desired parent and tree contracts
            // while the obsolete identities still protect the game. Only then remove the old
            // identities. This prevents a blocked user from deleting payload during a policy
            // change, as reproduced by the r15 physical Cards! transition attack.
            if (toApplyIdentities.Count > 0)
            {
                SetSharedParentDeleteChildDenyBatch(game.InstallPath, toApplyIdentities);
                SetDenyRuleBatch(game.InstallPath, toApplyIdentities);
            }
            var obsoleteSpecificIdentities = DiscoverObsoleteProtectionIdentities(
                game.InstallPath, toApplyIdentities);
            if (obsoleteSpecificIdentities.Count > 0)
            {
                RemoveDenyRuleBatch(game.InstallPath, obsoleteSpecificIdentities);
                foreach (var identity in obsoleteSpecificIdentities)
                    RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity);
            }
            // NOTE: manifest (.acf) intentionally NOT deny-ruled — see note above.

            // Lock status logged by PlatformManager.ApplyGameProtection (avoids duplicate/contradicting messages)
        }

        /// <summary>
        /// Remove NTFS deny-delete protection from a game folder
        /// </summary>
        public void RemoveProtection(GameInfo game, List<string> blockedUsers)
        {
            lock (_aclMutationLock)
            {
                RemoveProtectionCore(game, blockedUsers);
            }
        }

        private void RemoveProtectionCore(GameInfo game, List<string> blockedUsers)
        {
            // Collect all identities to remove into one batch — 1 tree walk regardless of user count
            var allIdentities = new List<string> { "*S-1-5-32-545" };
            foreach (var user in GetLocalUsersForSync())
                allIdentities.Add(ResolveToIdentity(user));
            foreach (var username in blockedUsers)
                allIdentities.Add(ResolveToIdentity(username));
            allIdentities.AddRange(DiscoverProtectionIdentities(game.InstallPath));
            if (!string.IsNullOrEmpty(game.ManifestPath) && File.Exists(game.ManifestPath))
                allIdentities.AddRange(AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                    game.ManifestPath, recursive: false, _debugLogger));
            allIdentities = allIdentities.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (Directory.Exists(game.InstallPath))
                RemoveDenyRuleBatch(game.InstallPath, allIdentities);
            if (!string.IsNullOrEmpty(game.ManifestPath))
                RemoveDenyRuleFileBatch(game.ManifestPath, allIdentities);
            foreach (var identity in allIdentities)
                RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity);
        }

        /// <summary>
        /// Removes every explicit delete-deny identity from each game and manifest while
        /// preserving launcher/vendor allow ACEs. See IGamePlatform.ResetToDefault and
        /// Critical #59.
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
            foreach (var game in games)
            {
                try
                {
                    if (Directory.Exists(game.InstallPath))
                    {
                        var identities = AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                            game.InstallPath, recursive: true, _debugLogger);
                        RemoveDenyRuleBatch(game.InstallPath, identities);
                        foreach (var identity in identities)
                            RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity);
                        _debugLogger($"[Steam] ResetToDefault removed {identities.Count} deny identity/identities from {game.GameName} while preserving allow ACEs");
                    }
                    if (!string.IsNullOrEmpty(game.ManifestPath) && File.Exists(game.ManifestPath))
                    {
                        var identities = AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                            game.ManifestPath, recursive: false, _debugLogger);
                        RemoveDenyRuleFileBatch(game.ManifestPath, identities);
                    }
                }
                catch (Exception ex)
                {
                    _debugLogger($"[Steam] ResetToDefault({game.GameName}) threw: {ex}");
                }
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
                RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity);
                if (!string.IsNullOrEmpty(game.ManifestPath))
                    RemoveDenyRule(game.ManifestPath, identity);
            }
            catch (Exception ex) { _debugLogger($"[Steam] RemoveUserProtection({game.GameName}, {username}): {ex.Message}"); }
        }

        private List<string> DiscoverProtectionIdentities(string installPath)
        {
            return AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                installPath, recursive: false, _debugLogger);
        }

        private List<string> DiscoverObsoleteProtectionIdentities(
            string installPath,
            IEnumerable<string> desiredIdentities)
        {
            var desiredSids = desiredIdentities
                .Select(TryResolveSecurityIdentifier)
                .Where(sid => sid != null)
                .Select(sid => sid!.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return DiscoverProtectionIdentities(installPath)
                .Where(identity =>
                {
                    var sid = TryResolveSecurityIdentifier(identity);
                    return sid == null || !desiredSids.Contains(sid.Value);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Steam owns and rewrites appmanifest ACF files during updates. They must never
        /// retain a GamePinned delete deny, including one for the newly desired identity.
        /// </summary>
        private void RemoveSteamManifestDenyRules(string? manifestPath)
        {
            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                return;

            var identities = AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                manifestPath, recursive: false, _debugLogger);
            RemoveDenyRuleFileBatch(manifestPath, identities);
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
            catch (Exception ex) { _debugLogger($"[Steam] GetLocalUsersForSync: {ex.Message}"); }
            return users;
        }

        /// <summary>
        /// Set deny-delete rule using icacls (CRITICAL: only reliable way to set permissions)
        /// (OI) = ObjectInherit - applies to files
        /// (CI) = ContainerInherit - applies to subfolders
        /// (DE) = Delete - can't delete the item
        /// (DC) = DeleteChild - can't delete children
        ///
        /// For directories uses parallel subdirectory processing (~3-5x faster than single /T traversal).
        /// </summary>
        private void SetDenyRule(string path, string username)
        {
            // CRITICAL: Remove existing rule first to prevent duplicates
            RemoveDenyRule(path, username);

            // Files need only (DE). (OI)(CI)(DC) are directory-only inheritance flags —
            // applying them to a file creates an inherit-only ACE that doesn't protect the file itself.
            bool isFile = File.Exists(path) && !Directory.Exists(path);
            if (isFile)
            {
                var (ec, stderr) = RunIcacls($"/c icacls \"{path}\" /deny \"{username}:(DE)\" /C /Q");
                if (ec != 0)
                    _debugLogger($"[Steam] SetDenyRule FAILED exit={ec} {username} on {path} stderr={stderr.Trim()}");
                else
                    _debugLogger($"[Steam] SetDenyRule exit=0 {username} on {Path.GetFileName(path)}");
                return;
            }

            // Directory: parallel subdirectory processing.
            // Step 1 applies the rule to the root folder itself (no /T, fast).
            // Step 2 applies /T to each immediate subdirectory in parallel.
            // Files directly in root are protected by inherited ACEs from step 1.
            SetDenyRuleParallelSubdirs(path, username);
        }

        /// <summary>
        /// Applies deny rule to a directory using parallel subdirectory icacls for speed.
        /// Expected ~3-5x speedup over a single /T traversal on SSD/NVMe.
        /// Falls back to single /T if subdirectory enumeration fails.
        /// </summary>
        private void SetDenyRuleParallelSubdirs(string dirPath, string username)
        {
            // Step 1: apply to root folder itself (no /T — fast, sets (OI)(CI) inheritance)
            RunIcacls($"/c icacls \"{dirPath}\" /deny \"{username}:(OI)(CI)(DE,DC)\" /C /Q");

            try
            {
                var subdirs = Directory.GetDirectories(dirPath);
                if (subdirs.Length == 0)
                {
                    _debugLogger($"[Steam] SetDenyRule exit=0 {username} on {Path.GetFileName(dirPath)}");
                    return;
                }

                // Step 2: /T on each immediate subdir in parallel (capped to avoid system overload)
                var opts = new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4)
                };
                System.Threading.Tasks.Parallel.ForEach(subdirs, opts, subdir =>
                {
                    var (ec, err) = RunIcacls($"/c icacls \"{subdir}\" /deny \"{username}:(OI)(CI)(DE,DC)\" /T /C /Q");
                    if (ec != 0)
                        _debugLogger($"[Steam] SetDenyRule FAILED exit={ec} {username} on {Path.GetFileName(subdir)} stderr={err.Trim()}");
                });
                _debugLogger($"[Steam] SetDenyRule exit=0 {username} on {Path.GetFileName(dirPath)} ({subdirs.Length} subdirs parallel)");
            }
            catch (Exception ex)
            {
                _debugLogger($"[Steam] SetDenyRule parallel failed: {ex.Message} — falling back to /T on {Path.GetFileName(dirPath)}");
                var (ec, err) = RunIcacls($"/c icacls \"{dirPath}\" /deny \"{username}:(OI)(CI)(DE,DC)\" /T /C /Q");
                if (ec != 0)
                    _debugLogger($"[Steam] SetDenyRule FAILED (fallback) exit={ec} {username} on {dirPath} stderr={err.Trim()}");
                else
                    _debugLogger($"[Steam] SetDenyRule exit=0 (fallback) {username} on {Path.GetFileName(dirPath)}");
            }
        }

        /// <summary>
        /// Remove deny-delete rule using icacls.
        /// For directories uses parallel subdirectory processing for speed.
        /// </summary>
        private void RemoveDenyRule(string path, string username)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return;

            bool isFile = File.Exists(path) && !Directory.Exists(path);
            if (isFile)
            {
                var (ec, _) = RunIcacls($"/c icacls \"{path}\" /remove:d \"{username}\" /C /Q");
                // Suppress expected exit codes (3 / 1332 = rule didn't exist)
                if (ec != 0 && ec != 3 && ec != 1332)
                    _debugLogger($"[Steam] RemoveDenyRule exit={ec} user={username} path={path}");
                return;
            }

            // Directory: parallel subdirectory processing.
            // Step 1: remove from root itself (no /T) — clears the explicit ACE on the root folder;
            //         child inherited ACEs disappear automatically since they came from this parent.
            // Step 2: remove /T from each immediate subdir in parallel — clears any explicit ACEs
            //         that were written to existing children by previous SetDenyRule /T calls.
            RunIcacls($"/c icacls \"{path}\" /remove:d \"{username}\" /C /Q");

            try
            {
                var subdirs = Directory.GetDirectories(path);
                if (subdirs.Length == 0) return;

                var opts = new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4)
                };
                System.Threading.Tasks.Parallel.ForEach(subdirs, opts, subdir =>
                {
                    var (ec, _) = RunIcacls($"/c icacls \"{subdir}\" /remove:d \"{username}\" /T /C /Q");
                    // Suppress expected exit codes
                    if (ec != 0 && ec != 3 && ec != 1332)
                        _debugLogger($"[Steam] RemoveDenyRule exit={ec} user={username} on {Path.GetFileName(subdir)}");
                });
            }
            catch (Exception ex)
            {
                _debugLogger($"[Steam] RemoveDenyRule parallel failed: {ex.Message} — falling back to /T on {Path.GetFileName(path)}");
                RunIcacls($"/c icacls \"{path}\" /remove:d \"{username}\" /T /C /Q");
            }
        }

        /// <summary>
        /// Runs cmd.exe with the given args, drains stdout/stderr, waits up to 30s, kills if hung.
        /// Returns (exitCode, stderr).
        /// </summary>
        private (int exitCode, string stderr) RunIcacls(string args)
        {
            var psi = new ProcessStartInfo("cmd.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "");
            _ = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(30000)) { try { proc.Kill(); } catch { } }
            return (proc.HasExited ? proc.ExitCode : -1, stderr);
        }

        /// <summary>
        /// Resolves a username to a "*SID" icacls identity, or returns the username as-is if resolution fails.
        /// </summary>
        private string ResolveToIdentity(string username)
        {
            if (username.StartsWith("*S-")) return username;
            var sid = ResolveToSid(username);
            return sid != null ? "*" + sid : username;
        }

        /// <summary>
        /// Batch remove of deny rules for multiple identities on a directory.
        /// All identities removed in a single icacls invocation per subdirectory — one tree walk
        /// instead of N separate walks (N = number of users).
        /// </summary>
        private void RemoveDenyRuleBatch(string dirPath, List<string> identities)
        {
            if (identities.Count == 0 || !Directory.Exists(dirPath)) return;
            // One icacls call per identity — batching multiple /remove:d flags in one call returns
            // exit 0 but silently processes only a subset of identities.
            foreach (var identity in identities)
            {
                var (ec, err) = RunIcacls($"/c icacls \"{dirPath}\" /remove:d \"{identity}\" /T /C /Q");
                if (ec != 0 && ec != 3 && ec != 1332)
                    _debugLogger($"[Steam] RemoveDenyRuleBatch FAILED exit={ec} on {Path.GetFileName(dirPath)} for {identity}: {err.Trim()}");
            }
            _debugLogger($"[Steam] RemoveDenyRuleBatch: {identities.Count} identities on {Path.GetFileName(dirPath)}");
        }

        /// <summary>
        /// Batch apply of deny rules for multiple identities on a directory.
        /// All identities applied in a single icacls invocation per subdirectory — one tree walk.
        /// Adds or repairs the desired identities without removing existing identities first.
        /// This is required for make-before-break policy transitions.
        /// </summary>
        private void SetDenyRuleBatch(string dirPath, List<string> identities)
        {
            if (identities.Count == 0 || !Directory.Exists(dirPath)) return;
            var denyArgs = string.Join(" ", identities.Select(id => $"/deny \"{id}:(OI)(CI)(DE,DC)\""));
            RunIcacls($"/c icacls \"{dirPath}\" {denyArgs} /C /Q");
            try
            {
                var subdirs = Directory.GetDirectories(dirPath);
                if (subdirs.Length == 0)
                {
                    _debugLogger($"[Steam] SetDenyRuleBatch: {identities.Count} identities on {Path.GetFileName(dirPath)}");
                    return;
                }
                var opts = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4) };
                System.Threading.Tasks.Parallel.ForEach(subdirs, opts, subdir =>
                {
                    var (ec, err) = RunIcacls($"/c icacls \"{subdir}\" {denyArgs} /T /C /Q");
                    if (ec != 0)
                        _debugLogger($"[Steam] SetDenyRuleBatch FAILED exit={ec} on {Path.GetFileName(subdir)}: {err.Trim()}");
                });
                _debugLogger($"[Steam] SetDenyRuleBatch: {identities.Count} identities on {Path.GetFileName(dirPath)} ({subdirs.Length} subdirs parallel)");
            }
            catch (Exception ex)
            {
                _debugLogger($"[Steam] SetDenyRuleBatch parallel failed: {ex.Message} — falling back to /T");
                RunIcacls($"/c icacls \"{dirPath}\" {denyArgs} /T /C /Q");
            }
        }

        /// <summary>
        /// Remove deny rules from a file (manifest) for multiple identities. One icacls call per identity —
        /// batching multiple /remove:d flags in one call returns exit 0 but silently skips ACEs.
        /// </summary>
        private void RemoveDenyRuleFileBatch(string filePath, List<string> identities)
        {
            if (identities.Count == 0 || !File.Exists(filePath)) return;
            foreach (var identity in identities)
            {
                var (ec, err) = RunIcacls($"/c icacls \"{filePath}\" /remove:d \"{identity}\" /C /Q");
                if (ec != 0 && ec != 3 && ec != 1332)
                    _debugLogger($"[Steam] RemoveDenyRuleFileBatch FAILED exit={ec} on {Path.GetFileName(filePath)} for {identity}: {err.Trim()}");
            }
        }

        /// <summary>
        /// Batch apply of deny rules on a single file (manifest). Uses (DE) only — no directory flags.
        /// NOTE: does NOT remove old rules first — caller must call RemoveDenyRuleFileBatch before this.
        /// </summary>
        private void SetDenyRuleFileBatch(string filePath, List<string> identities)
        {
            if (identities.Count == 0 || !File.Exists(filePath)) return;
            var denyArgs = string.Join(" ", identities.Select(id => $"/deny \"{id}:(DE)\""));
            var (ec, err) = RunIcacls($"/c icacls \"{filePath}\" {denyArgs} /C /Q");
            if (ec != 0)
                _debugLogger($"[Steam] SetDenyRuleFileBatch FAILED exit={ec} on {Path.GetFileName(filePath)}: {err.Trim()}");
        }

        private void SetSharedParentDeleteChildDenyBatch(string protectedGamePath, List<string> identities)
        {
            foreach (var identity in identities.Distinct(StringComparer.OrdinalIgnoreCase))
                SetSharedParentDeleteChildDeny(protectedGamePath, identity);
        }

        /// <summary>
        /// Windows permits a directory rename/delete when either the directory grants DELETE
        /// or its parent grants DELETE_CHILD. Steam's recursive game-root deny closes only the
        /// first route, so steamapps/common needs one non-inheriting shared boundary per blocked
        /// identity. Ordinary siblings retain their own object-level Delete allow.
        /// </summary>
        private void SetSharedParentDeleteChildDeny(string protectedGamePath, string identity)
        {
            var parentPath = Path.GetDirectoryName(protectedGamePath);
            var sid = TryResolveSecurityIdentifier(identity);
            if (string.IsNullOrEmpty(parentPath) || sid == null || !Directory.Exists(parentPath))
            {
                _debugLogger($"[Steam] Cannot set shared parent DeleteChild deny for {identity} at {protectedGamePath}");
                return;
            }

            try
            {
                if (HasExplicitDeleteChildDeny(parentPath, sid))
                    return;

                var (ec, stderr) = RunIcacls(
                    $"/c icacls \"{parentPath}\" /deny \"{identity}:(DC)\" /C /Q");
                if (ec != 0)
                    _debugLogger($"[Steam] Set shared parent DeleteChild deny FAILED exit={ec} for {identity} at {parentPath}: {stderr.Trim()}");
            }
            catch (Exception ex)
            {
                _debugLogger($"[Steam] Set shared parent DeleteChild deny failed for {identity} at {parentPath}: {ex}");
            }
        }

        /// <summary>
        /// Keep the shared steamapps/common boundary while any sibling game root still has the
        /// matching explicit Delete deny. ACL operations are serialized by _aclMutationLock.
        /// </summary>
        private void RemoveSharedParentDeleteChildDenyIfUnused(string gamePath, string identity)
        {
            var parentPath = Path.GetDirectoryName(gamePath);
            var sid = TryResolveSecurityIdentifier(identity);
            if (string.IsNullOrEmpty(parentPath) || sid == null || !Directory.Exists(parentPath))
                return;

            try
            {
                if (Directory.EnumerateDirectories(parentPath, "*", SearchOption.TopDirectoryOnly)
                    .Any(path => HasExplicitDeleteDeny(path, sid)))
                    return;

                var (ec, stderr) = RunIcacls(
                    $"/c icacls \"{parentPath}\" /remove:d \"{identity}\" /C /Q");
                if (ec != 0 && ec != 3 && ec != 1332)
                    _debugLogger($"[Steam] Remove shared parent DeleteChild deny FAILED exit={ec} for {identity} at {parentPath}: {stderr.Trim()}");
            }
            catch (Exception ex)
            {
                // Unreadable sibling state must retain the parent boundary rather than expose
                // another protected game root.
                _debugLogger($"[Steam] Remove shared parent DeleteChild deny failed for {identity} at {parentPath}: {ex}");
            }
        }

        private static bool HasExplicitDeleteDeny(string path, SecurityIdentifier sid)
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

        /// <summary>
        /// Startup and configuration refreshes commonly call ApplyProtection for games whose
        /// ACL contract is already exact. Avoiding a remove/re-add cycle keeps protection
        /// continuous while the service restarts.
        /// </summary>
        private static bool HasExactProtectionContract(string path, IEnumerable<string> desiredIdentities)
        {
            if (!Directory.Exists(path))
                return false;

            try
            {
                var desiredSids = desiredIdentities
                    .Select(TryResolveSecurityIdentifier)
                    .Where(sid => sid != null)
                    .Select(sid => sid!.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (desiredSids.Count == 0)
                    return false;

                var protectedSids = GetExplicitProtectedSids(path);
                if (!protectedSids.Select(sid => sid.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(desiredSids))
                    return false;

                var parentPath = Path.GetDirectoryName(path);
                return !string.IsNullOrEmpty(parentPath) &&
                       protectedSids.All(sid => HasExplicitDeleteChildDeny(parentPath, sid));
            }
            catch
            {
                return false;
            }
        }

        private static List<SecurityIdentifier> GetExplicitProtectedSids(string path)
        {
            const FileSystemRights requiredRights =
                FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
            const InheritanceFlags requiredInheritance =
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
            return security
                .GetAccessRules(includeExplicit: true, includeInherited: false,
                    targetType: typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Where(rule =>
                    rule.AccessControlType == AccessControlType.Deny &&
                    (rule.FileSystemRights & requiredRights) == requiredRights &&
                    (rule.InheritanceFlags & requiredInheritance) == requiredInheritance &&
                    rule.IdentityReference is SecurityIdentifier)
                .Select(rule => (SecurityIdentifier)rule.IdentityReference)
                .GroupBy(sid => sid.Value, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        /// <summary>
        /// Backup Steam manifest files (.acf files)
        /// </summary>
        public void BackupManifests(string libraryPath, List<string> excludedGames)
        {
            var steamappsPath = Path.Combine(libraryPath, "steamapps");
            if (!Directory.Exists(steamappsPath))
                return;

            // Create backup directory for this library path
            var backupDir = Path.Combine(_backupPath, SanitizePath(libraryPath));
            Directory.CreateDirectory(backupDir);

            // 1. Cleanup existing backups for games that are now EXCLUDED
            try
            {
                foreach (var backupFile in Directory.GetFiles(backupDir, "appmanifest_*.acf"))
                {
                    var gameName = GetGameNameFromManifest(backupFile);
                    if (gameName != null && IsExcluded(gameName, excludedGames))
                    {
                        File.Delete(backupFile);
                    }
                }
            }
            catch { }

            // 2. Backup all CURRENTLY PROTECTED .acf manifest files.
            try
            {
                foreach (var acfFile in Directory.GetFiles(steamappsPath, "appmanifest_*.acf"))
                {
                    var gameName = GetGameNameFromManifest(acfFile);
                    if (gameName != null && !IsExcluded(gameName, excludedGames))
                    {
                        var destPath = Path.Combine(backupDir, Path.GetFileName(acfFile));
                        File.Copy(acfFile, destPath, overwrite: true);
                    }
                }
            }
            catch { }

            // 3. Backup libraryfolders.vdf (CRITICAL for Steam to recognize library contents promptly)
            try
            {
                var vdfPath = Path.Combine(steamappsPath, "libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    var vdfBackup = Path.Combine(backupDir, "libraryfolders.vdf");
                    File.Copy(vdfPath, vdfBackup, overwrite: true);
                }
            }
            catch { }
        }

        /// <summary>
        /// Helper to check exclusion with case-insensitivity
        /// </summary>
        private bool IsExcluded(string gameName, List<string> excludedGames)
        {
            return excludedGames.Any(g => g.Trim().Equals(gameName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Restore Steam manifest files from backup
        /// </summary>
        public void RestoreManifests(string libraryPath, List<string> excludedGames)
        {
            var steamappsPath = Path.Combine(libraryPath, "steamapps");
            var backupDir = Path.Combine(_backupPath, SanitizePath(libraryPath));

            if (!Directory.Exists(backupDir))
                return;

            // First, ensure backup is clean by running backup cleanup
            // This removes manifests for games that are now excluded
            CleanupBackupForExcludedGames(backupDir, excludedGames);

            // Restore all .acf files THAT ARE NOT EXCLUDED
            try
            {
                foreach (var backupFile in Directory.GetFiles(backupDir, "appmanifest_*.acf"))
                {
                    var gameName = GetGameNameFromManifest(backupFile);
                    if (gameName != null && !IsExcluded(gameName, excludedGames))
                    {
                        // Only restore if the manifest doesn't exist or is different
                        var destPath = Path.Combine(steamappsPath, Path.GetFileName(backupFile));
                        if (File.Exists(destPath))
                        {
                            // Compare file sizes to avoid unnecessary restores
                            var backupSize = new FileInfo(backupFile).Length;
                            var destSize = new FileInfo(destPath).Length;
                            if (backupSize != destSize)
                            {
                                File.Copy(backupFile, destPath, overwrite: true);
                            }
                        }
                        else
                        {
                            File.Copy(backupFile, destPath, overwrite: true);
                        }
                    }
                }
            }
            catch { }

            // 2. Restore libraryfolders.vdf
            try
            {
                var vdfBackup = Path.Combine(backupDir, "libraryfolders.vdf");
                if (File.Exists(vdfBackup))
                {
                    var vdfDest = Path.Combine(steamappsPath, "libraryfolders.vdf");
                    if (File.Exists(vdfDest))
                    {
                        // Compare file sizes to avoid unnecessary restores
                        var backupSize = new FileInfo(vdfBackup).Length;
                        var destSize = new FileInfo(vdfDest).Length;
                        if (backupSize != destSize)
                        {
                            File.Copy(vdfBackup, vdfDest, overwrite: true);
                        }
                    }
                    else
                    {
                        File.Copy(vdfBackup, vdfDest, overwrite: true);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Recovery path for one confirmed uninstall callback. Restore exactly the triggering
        /// appmanifest instead of replaying every non-excluded backup in the library. The broad
        /// RestoreManifests method remains available for deliberate whole-library recovery.
        /// </summary>
        public void RestoreGameManifest(GameInfo game, string libraryPath, List<string> excludedGames)
        {
            if (IsExcluded(game.GameName, excludedGames))
            {
                _debugLogger($"[Steam] RestoreGameManifest skipped excluded game {game.GameName}");
                return;
            }

            var steamappsPath = Path.Combine(libraryPath, "steamapps");
            var backupDir = Path.Combine(_backupPath, SanitizePath(libraryPath));
            var manifestName = Path.GetFileName(game.ManifestPath);
            if (string.IsNullOrWhiteSpace(manifestName) ||
                !manifestName.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase) ||
                !manifestName.EndsWith(".acf", StringComparison.OrdinalIgnoreCase))
            {
                _debugLogger($"[Steam] RestoreGameManifest rejected invalid manifest path for {game.GameName}");
                return;
            }

            var backupFile = Path.Combine(backupDir, manifestName);
            if (!File.Exists(backupFile))
            {
                _debugLogger($"[Steam] RestoreGameManifest backup missing for {game.GameName}: {manifestName}");
                return;
            }

            var backedGameName = GetGameNameFromManifest(backupFile);
            if (!string.Equals(backedGameName, game.GameName, StringComparison.OrdinalIgnoreCase))
            {
                _debugLogger($"[Steam] RestoreGameManifest rejected name mismatch for {game.GameName}: backup contains {backedGameName ?? "unknown"}");
                return;
            }

            Directory.CreateDirectory(steamappsPath);
            var destination = Path.Combine(steamappsPath, manifestName);
            if (!File.Exists(destination))
            {
                File.Copy(backupFile, destination, overwrite: false);
                _debugLogger($"[Steam] RestoreGameManifest restored {manifestName} for {game.GameName}");
            }
            else
            {
                // A patch may have recreated the manifest after the uninstall callback's
                // three-second classification window. Never overwrite that newer live file.
                _debugLogger($"[Steam] RestoreGameManifest kept reappeared live manifest for {game.GameName}");
            }

            // Preserve the previous recognition-recovery behavior without restoring any other
            // game's appmanifest. libraryfolders.vdf is shared launcher metadata, not a game
            // manifest; copy only when absent or when the legacy size check detects divergence.
            var libraryFoldersBackup = Path.Combine(backupDir, "libraryfolders.vdf");
            if (File.Exists(libraryFoldersBackup))
            {
                var libraryFoldersDestination = Path.Combine(steamappsPath, "libraryfolders.vdf");
                if (!File.Exists(libraryFoldersDestination) ||
                    new FileInfo(libraryFoldersBackup).Length != new FileInfo(libraryFoldersDestination).Length)
                {
                    File.Copy(libraryFoldersBackup, libraryFoldersDestination, overwrite: true);
                }
            }
        }

        /// <summary>
        /// Clean up backup directory by removing manifests for excluded games
        /// This ensures we don't restore manifests for games that are no longer protected
        /// </summary>
        private void CleanupBackupForExcludedGames(string backupDir, List<string> excludedGames)
        {
            try
            {
                foreach (var backupFile in Directory.GetFiles(backupDir, "appmanifest_*.acf"))
                {
                    var gameName = GetGameNameFromManifest(backupFile);
                    if (gameName != null && IsExcluded(gameName, excludedGames))
                    {
                        // Delete backup for excluded game
                        File.Delete(backupFile);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Kill all Steam processes
        /// </summary>
        public void KillLauncher()
        {
            foreach (var processName in new[] { "steam", "steamwebhelper" })
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            proc.Kill();
                            proc.WaitForExit(5000);
                        }
                        catch { /* race guard: process may have exited between GetProcessesByName and Kill */ }
                        finally { try { proc.Dispose(); } catch { } }
                    }
                }
                catch (Exception ex) { _debugLogger($"[Steam] KillLauncher({processName}): {ex.Message}"); }
            }
        }

        /// <summary>
        /// Start Steam launcher as the logged-in user (NOT as admin)
        /// CRITICAL: Must launch via explorer.exe to avoid inheriting admin privileges
        /// If Steam runs as admin, it bypasses our per-user deny rules
        /// </summary>
        public void StartLauncher()
        {
            var steamPaths = DetectLibraryPaths();
            var steamExe = steamPaths
                .Select(p => Path.Combine(p, "steam.exe"))
                .FirstOrDefault(File.Exists);

            if (!string.IsNullOrEmpty(steamExe))
            {
                try
                {
                    // Launch via explorer.exe to run as user, not admin
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = steamExe,
                        UseShellExecute = false
                    });
                }
                catch (Exception ex) { _debugLogger($"[Steam] StartLauncher: failed to launch {steamExe} — {ex.Message}"); }
            }
        }

        /// <summary>
        /// Start watching for .acf file deletions (uninstall attempts)
        /// </summary>
        public void StartUninstallWatcher(Action<GameInfo> onUninstallDetected)
        {
            _onUninstallDetected = onUninstallDetected;
            _stoppingWatchers = false;

            var libraryPaths = DetectLibraryPaths();
            foreach (var libraryPath in libraryPaths)
            {
                var steamappsPath = Path.Combine(libraryPath, "steamapps");
                if (!Directory.Exists(steamappsPath))
                {
                    _debugLogger($"[Steam] steamapps path not found: {steamappsPath}");
                    continue;
                }
                StartSteamUninstallWatcherForPath(steamappsPath, 0);
            }
        }

        private void StartSteamUninstallWatcherForPath(string steamappsPath, int attempt)
        {
            if (_stoppingWatchers) return;
            try
            {
                var watcher = new FileSystemWatcher(steamappsPath)
                {
                    Filter = "appmanifest_*.acf",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };

                watcher.Deleted += OnManifestDeleted;
                watcher.Created += OnManifestCreated;
                var currentWatcher = watcher;
                watcher.Error += (s, e) =>
                {
                    _debugLogger($"[Steam] Uninstall watcher error on {steamappsPath}: {e.GetException().Message} — scheduling restart");
                    if (_stoppingWatchers) return;
                    lock (_watchers)
                    {
                        try { currentWatcher.EnableRaisingEvents = false; currentWatcher.Dispose(); } catch { }
                        _watchers.Remove(currentWatcher);
                    }
                    ScheduleWatcherRestart(steamappsPath, attempt, () => StartSteamUninstallWatcherForPath(steamappsPath, attempt + 1), "Steam uninstall");
                };
                watcher.EnableRaisingEvents = true;

                lock (_watchers) { _watchers.Add(watcher); }
                _debugLogger($"[Steam] Uninstall watcher started: {steamappsPath}" + (attempt > 0 ? $" (restart attempt {attempt})" : ""));
            }
            catch (Exception ex)
            {
                _debugLogger($"[Steam] Failed to start uninstall watcher for {steamappsPath}: {ex.Message}");
                ScheduleWatcherRestart(steamappsPath, attempt, () => StartSteamUninstallWatcherForPath(steamappsPath, attempt + 1), "Steam uninstall");
            }
        }

        /// <summary>
        /// Schedule a watcher restart with exponential backoff. Used when FileSystemWatcher.Error fires
        /// (buffer overflow, permission change, etc.) — without this, a single transient error
        /// silently stops detection until the service restarts.
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
            // Exponential backoff: 2s, 4s, 8s, 16s, 32s, then 60s cap
            var backoffMs = Math.Min(60000, 2000 * (1 << Math.Min(5, attempt)));
            _debugLogger($"[{name}] Restart in {backoffMs}ms (attempt {attempt + 1}/{MaxAttempts})");
            Task.Delay(backoffMs).ContinueWith(_ =>
            {
                if (_stoppingWatchers) return;
                try { restart(); }
                catch (Exception ex) { _debugLogger($"[{name}] Restart invocation failed: {ex.Message}"); }
            });
        }

        /// <summary>
        /// Stop all file system watchers.
        /// </summary>
        /// <param name="releaseHandles">Unused for Steam — Steam holds no per-game handles. Parameter exists for IGamePlatform compliance.</param>
        public void StopWatchers(bool releaseHandles = false)
        {
            _stoppingWatchers = true;
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

        /// <summary>
        /// Start watching for newly installed games (manifest file creation)
        /// </summary>
        public void StartNewGameWatcher(Action<GameInfo> onNewGameDetected)
        {
            _onNewGameDetected = onNewGameDetected;

            var libraryPaths = DetectLibraryPaths();
            foreach (var libraryPath in libraryPaths)
            {
                var steamappsPath = Path.Combine(libraryPath, "steamapps");
                if (!Directory.Exists(steamappsPath))
                {
                    _debugLogger($"[Steam] steamapps path not found: {steamappsPath}");
                    continue;
                }
                StartSteamNewGameWatcherForPath(steamappsPath, 0);
                var commonPath = Path.Combine(steamappsPath, "common");
                if (Directory.Exists(commonPath))
                    StartSteamFolderWatcherForPath(commonPath, 0);
            }
        }

        private void StartSteamNewGameWatcherForPath(string steamappsPath, int attempt)
        {
            if (_stoppingWatchers) return;
            try
            {
                var watcher = new FileSystemWatcher(steamappsPath)
                {
                    Filter = "appmanifest_*.acf",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };
                watcher.Created += OnManifestCreated;
                var currentWatcher = watcher;
                watcher.Error += (s, e) =>
                {
                    _debugLogger($"[Steam] New game watcher error on {steamappsPath}: {e.GetException().Message} — scheduling restart");
                    if (_stoppingWatchers) return;
                    lock (_watchers)
                    {
                        try { currentWatcher.EnableRaisingEvents = false; currentWatcher.Dispose(); } catch { }
                        _watchers.Remove(currentWatcher);
                    }
                    ScheduleWatcherRestart(steamappsPath, attempt, () => StartSteamNewGameWatcherForPath(steamappsPath, attempt + 1), "Steam new-game");
                };
                watcher.EnableRaisingEvents = true;
                lock (_watchers) { _watchers.Add(watcher); }
                _debugLogger($"[Steam] New game watcher started: {steamappsPath}" + (attempt > 0 ? $" (restart attempt {attempt})" : ""));
            }
            catch (Exception ex)
            {
                _debugLogger($"[Steam] Failed to start new game watcher for {steamappsPath}: {ex.Message}");
                ScheduleWatcherRestart(steamappsPath, attempt, () => StartSteamNewGameWatcherForPath(steamappsPath, attempt + 1), "Steam new-game");
            }
        }

        private void StartSteamFolderWatcherForPath(string commonPath, int attempt)
        {
            if (_stoppingWatchers) return;
            try
            {
                var folderWatcher = new FileSystemWatcher(commonPath)
                {
                    NotifyFilter = NotifyFilters.DirectoryName
                };
                folderWatcher.Created += OnGameFolderCreated;
                var currentWatcher = folderWatcher;
                folderWatcher.Error += (s, e) =>
                {
                    _debugLogger($"[Steam] Folder watcher error on {commonPath}: {e.GetException().Message} — scheduling restart");
                    if (_stoppingWatchers) return;
                    lock (_watchers)
                    {
                        try { currentWatcher.EnableRaisingEvents = false; currentWatcher.Dispose(); } catch { }
                        _watchers.Remove(currentWatcher);
                    }
                    ScheduleWatcherRestart(commonPath, attempt, () => StartSteamFolderWatcherForPath(commonPath, attempt + 1), "Steam folder");
                };
                folderWatcher.EnableRaisingEvents = true;
                lock (_watchers) { _watchers.Add(folderWatcher); }
                _debugLogger($"[Steam] Folder watcher started: {commonPath}" + (attempt > 0 ? $" (restart attempt {attempt})" : ""));
            }
            catch (Exception ex)
            {
                _debugLogger($"[Steam] Failed to start folder watcher for {commonPath}: {ex.Message}");
                ScheduleWatcherRestart(commonPath, attempt, () => StartSteamFolderWatcherForPath(commonPath, attempt + 1), "Steam folder");
            }
        }

        /// <summary>
        /// Perform health check on protected games (re-apply protection if needed)
        /// </summary>
        public void HealthCheck(List<GameInfo> protectedGames, List<string> blockedUsers, bool blockAllNonAdminByDefault)
        {
            foreach (var game in protectedGames)
            {
                if (game.PlatformId != PlatformId)
                    continue;

                if (!Directory.Exists(game.InstallPath))
                    continue;

                // Check if game is stable (not being updated/downloaded)
                if (!IsGameStable(game.InstallPath))
                {
                    _debugLogger($"[Steam] HealthCheck: {game.GameName} is updating, removing protection temporarily");
                    RemoveProtection(game, blockedUsers);
                    continue;
                }

                // Only re-apply protection if it's actually missing (avoids spawning icacls every 60s)
                if (!CheckProtectionActive(game.InstallPath))
                {
                    _debugLogger($"[Steam] HealthCheck: protection missing for {game.GameName}, re-applying");
                    ApplyProtection(game, blockedUsers, blockAllNonAdminByDefault);
                }
            }
        }

        public bool CheckProtectionActive(string path)
        {
            if (!Directory.Exists(path))
                return false;

            try
            {
                const FileSystemRights requiredRights =
                    FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
                const InheritanceFlags requiredInheritance =
                    InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit;
                var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
                var protectedSids = security
                    .GetAccessRules(includeExplicit: true, includeInherited: false,
                        targetType: typeof(SecurityIdentifier))
                    .OfType<FileSystemAccessRule>()
                    .Where(rule =>
                        rule.AccessControlType == AccessControlType.Deny &&
                        (rule.FileSystemRights & requiredRights) == requiredRights &&
                        (rule.InheritanceFlags & requiredInheritance) == requiredInheritance &&
                        rule.IdentityReference is SecurityIdentifier)
                    .Select(rule => (SecurityIdentifier)rule.IdentityReference)
                    .GroupBy(sid => sid.Value, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                if (protectedSids.Count == 0)
                    return false;

                var parentPath = Path.GetDirectoryName(path);
                return !string.IsNullOrEmpty(parentPath) &&
                       protectedSids.All(sid => HasExplicitDeleteChildDeny(parentPath, sid));
            }
            catch (Exception ex) { _debugLogger($"[Steam] CheckProtectionActive({path}): {ex.Message}"); }
            return false;
        }

        /// <summary>
        /// Check if a game folder is stable (not being downloaded/updated)
        /// Steam downloads updates into steamapps\downloading\[AppId], NOT into the game folder directly.
        /// </summary>
        public bool IsGameStable(string gameFolder)
        {
            try
            {
                var gameName = Path.GetFileName(gameFolder);
                var commonPath = Path.GetDirectoryName(gameFolder);
                if (string.IsNullOrEmpty(commonPath)) return true;

                var steamappsPath = Path.GetDirectoryName(commonPath);
                if (string.IsNullOrEmpty(steamappsPath)) return true;

                // 1. Find the AppID from the manifest to accurately check the downloading folder
                var manifestPath = FindManifestForGame(steamappsPath, gameName);
                if (!string.IsNullOrEmpty(manifestPath))
                {
                    var appId = ExtractAppIdFromManifest(manifestPath);
                    if (!string.IsNullOrEmpty(appId))
                    {
                        var downloadingPath = Path.Combine(steamappsPath, "downloading", appId);

                        // Only treat as "updating" if the downloading folder exists AND a file
                        // inside it was written in the last 90 seconds or is still held open.
                        //
                        // IMPORTANT: do NOT use Directory.GetLastWriteTime(folder) — on Windows
                        // the folder's mtime is updated by any process that touches the directory
                        // entry (anti-cheat, Ubisoft Connect, Steam bookkeeping). This caused
                        // R6 Siege to show "updating" for 3+ hours on a machine where a stale
                        // downloading folder existed from an interrupted update while BattlEye
                        // kept the folder mtime fresh. Check individual files instead.
                        //
                        // Steam can preallocate a staged file and keep the same LastWriteTime for
                        // longer than 90 seconds while slowly filling an open handle. Timestamp-only
                        // detection then re-locks the game before Steam commits the patch. An open
                        // staged file is active even when its timestamp looks old; a closed old file
                        // in an abandoned downloading directory remains stale.
                        if (Directory.Exists(downloadingPath))
                        {
                            bool activelyWriting = false;
                            try
                            {
                                var cutoff = DateTime.Now.AddSeconds(-90);
                                // TopDirectoryOnly: fast, avoids deep scan; active downloads
                                // write files at the top level of the downloading/<appId>/ folder.
                                foreach (var file in Directory.EnumerateFiles(
                                    downloadingPath, "*", SearchOption.TopDirectoryOnly))
                                {
                                    try
                                    {
                                        if (new FileInfo(file).LastWriteTime > cutoff || IsFileHeldOpen(file))
                                        {
                                            activelyWriting = true;
                                            break;
                                        }
                                    }
                                    catch { }
                                }
                            }
                            catch { }

                            if (activelyWriting)
                            {
                                _staleDownloadLogged.Remove(gameName); // reset so stale msg re-fires if download stops
                                return false; // Steam is actively writing download chunks
                            }

                            // Folder exists but has no recent file writes or open staged files —
                            // stale from an interrupted update. Treat as stable so protection is
                            // not permanently disabled.
                            // Log only once per game name to avoid repeating every health check tick.
                            if (_staleDownloadLogged.Add(gameName))
                                _debugLogger($"[Steam] IsGameStable: stale downloading folder for {gameName} (no recent or open staged files), treating as stable");
                        }
                    }
                }

                // fallback: Game folder exists and has files = stable.
                // EnumerateFiles stops at the first match instead of building a full array —
                // avoids scanning thousands of files on large games just to check > 0.
                return Directory.EnumerateFiles(gameFolder, "*", SearchOption.AllDirectories).Any();
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFileHeldOpen(string path)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
                return false;
            }
            catch (IOException)
            {
                // Steam keeps its preallocated staging file open while download data is
                // still arriving. A disappearing file can race this probe too; treating
                // that transient state as active is the conservative result.
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                // The service normally runs as SYSTEM, so this is unexpected. Fail open
                // for the updater instead of applying delete protection to an unknown
                // active staging state.
                return true;
            }
        }

        /// <summary>
        /// Event handler for .acf file deletion (uninstall detected)
        /// </summary>
        private void OnManifestDeleted(object sender, FileSystemEventArgs e)
        {
            try
            {
                _debugLogger($"[WATCHER] Manifest deleted: {Path.GetFileName(e.FullPath)}");

                if (_onUninstallDetected == null)
                {
                    _debugLogger($"[WATCHER] No uninstall handler set");
                    return;
                }

                // Find which game was uninstalled
                var gameName = GetGameNameFromManifest(e.FullPath);
                if (gameName == null)
                {
                    _logger($"[WATCHER] Could not determine game name from: {Path.GetFileName(e.FullPath)}");
                    return;
                }

                var libraryPath = FindLibraryPathForManifest(e.FullPath);
                if (libraryPath == null)
                {
                    _logger($"[WATCHER] Could not find library path for: {Path.GetFileName(e.FullPath)}");
                    return;
                }

                _debugLogger($"[WATCHER] Game uninstall detected: {gameName}");

                var game = new GameInfo
                {
                    PlatformId = PlatformId,
                    GameName = gameName,
                    InstallPath = Path.Combine(libraryPath, "steamapps", "common", gameName),
                    ManifestPath = e.FullPath,
                    GameId = "" // Will be extracted from backup during restore
                };

                _onUninstallDetected(game);
            }
            catch (Exception ex)
            {
                _logger($"[WATCHER] Error in OnManifestDeleted: {ex.Message}");
            }
        }

        /// <summary>
        /// Event handler for .acf file creation (new game installed or restored)
        /// </summary>
        private void OnManifestCreated(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (_onNewGameDetected == null)
                    return;

                // Wait a bit for file to be fully written
                Thread.Sleep(500);

                var gameName = GetGameNameFromManifest(e.FullPath);
                if (gameName == null)
                    return;

                var libraryPath = FindLibraryPathForManifest(e.FullPath);
                if (libraryPath == null)
                    return;

                var game = new GameInfo
                {
                    PlatformId = PlatformId,
                    GameName = gameName,
                    InstallPath = Path.Combine(libraryPath, "steamapps", "common", gameName),
                    ManifestPath = e.FullPath,
                    GameId = ExtractAppIdFromManifest(e.FullPath) ?? ""
                };

                _onNewGameDetected(game);
            }
            catch (Exception ex)
            {
                _logger($"[WATCHER] Error in OnManifestCreated: {ex.Message}");
            }
        }

        /// <summary>
        /// Event handler for new game folder creation
        /// </summary>
        private void OnGameFolderCreated(object sender, FileSystemEventArgs e)
        {
            if (_onNewGameDetected == null)
                return;

            var gameName = Path.GetFileName(e.FullPath);
            var libraryPath = FindLibraryPathForGameFolder(e.FullPath);

            if (libraryPath == null)
                return;

            var game = new GameInfo
            {
                PlatformId = PlatformId,
                GameName = gameName,
                InstallPath = e.FullPath,
                ManifestPath = "", // Will be set later when .acf is created
                GameId = ""
            };

            // Notify in a background thread to wait for download completion
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Wait for game to finish downloading (up to 5 minutes)
                for (int i = 0; i < 60; i++)
                {
                    Thread.Sleep(5000);

                    if (!Directory.Exists(e.FullPath))
                        return; // Game folder deleted, abort

                    if (IsGameStable(e.FullPath))
                    {
                        _onNewGameDetected?.Invoke(game);
                        return;
                    }
                }

                // Timeout - notify anyway
                _onNewGameDetected?.Invoke(game);
            });
        }

        /// <summary>
        /// Extract game name from .acf manifest file (checks backup if original is deleted)
        /// </summary>
        private string? GetGameNameFromManifest(string manifestPath)
        {
            // Try to read from the file directly
            if (File.Exists(manifestPath))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(manifestPath))
                    {
                        if (line.Contains("\"installdir\""))
                        {
                            var parts = line.Split('"');
                            if (parts.Length >= 4)
                                return parts[3].Trim();
                        }
                    }
                }
                catch { }
            }

            // File deleted - check backup
            var libraryPath = FindLibraryPathForManifest(manifestPath);
            if (libraryPath == null)
            {
                _debugLogger($"[Steam] Fallback: library path not found for {Path.GetFileName(manifestPath)}");
                return TryExtractGameNameFromPath(manifestPath);
            }

            var backupPath = Path.Combine(_backupPath, SanitizePath(libraryPath), Path.GetFileName(manifestPath));
            if (!File.Exists(backupPath))
            {
                _debugLogger($"[Steam] Fallback: backup not found for {Path.GetFileName(manifestPath)}, trying folder detection");
                return TryExtractGameNameFromPath(manifestPath);
            }

            try
            {
                foreach (var line in File.ReadAllLines(backupPath))
                {
                    if (line.Contains("\"installdir\""))
                    {
                        var parts = line.Split('"');
                        if (parts.Length >= 4)
                            return parts[3].Trim();
                    }
                }
            }
            catch { }

            _debugLogger($"[Steam] Fallback: could not parse backup for {Path.GetFileName(manifestPath)}, trying folder detection");
            return TryExtractGameNameFromPath(manifestPath);
        }

        /// <summary>
        /// Fallback method: Try to determine game name by checking existing game folders
        /// This is used when the manifest and backup are both unavailable
        /// </summary>
        private string? TryExtractGameNameFromPath(string manifestPath)
        {
            try
            {
                // Find library path from manifest path (e.g., C:\SteamLibrary\steamapps\appmanifest_730.acf)
                var libraryPath = FindLibraryPathForManifest(manifestPath);
                if (libraryPath == null) return null;

                var commonPath = Path.Combine(libraryPath, "steamapps", "common");
                if (!Directory.Exists(commonPath)) return null;

                // Check if there's only ONE game folder - if so, it's probably the one being uninstalled
                var gameFolders = Directory.GetDirectories(commonPath);
                if (gameFolders.Length == 1)
                {
                    var gameName = Path.GetFileName(gameFolders[0]);
                    _debugLogger($"[Steam] Fallback: detected single game folder: {gameName}");
                    return gameName;
                }

                _debugLogger($"[Steam] Fallback: multiple game folders found, cannot identify uninstalled game without manifest");
                return null;
            }
            catch (Exception ex)
            {
                _debugLogger($"[Steam] Fallback detection error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Find which library path contains this manifest file
        /// </summary>
        private string? FindLibraryPathForManifest(string manifestPath)
        {
            var libraryPaths = DetectLibraryPaths();
            return libraryPaths.FirstOrDefault(lp =>
                manifestPath.StartsWith(Path.Combine(lp, "steamapps"), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Find which library path contains this game folder
        /// </summary>
        private string? FindLibraryPathForGameFolder(string gameFolderPath)
        {
            var libraryPaths = DetectLibraryPaths();
            return libraryPaths.FirstOrDefault(lp =>
                gameFolderPath.StartsWith(Path.Combine(lp, "steamapps", "common"), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Sanitize path for use as directory name (replace : \ / with _)
        /// </summary>
        private string SanitizePath(string path)
        {
            return path.Replace(":", "_").Replace("\\", "_").Replace("/", "_");
        }

        /// <summary>
        /// Resolve a username (partial or full) to its SID string
        /// </summary>
        private string? ResolveToSid(string username)
        {
            try
            {
                // Try direct translation (handles Domain\User and LocalUser)
                var account = new NTAccount(username);
                var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
                return sid.Value;
            }
            catch
            {
                // If direct resolution fails (e.g. name is ambiguous), try prefixing with machine name
                try
                {
                    var account = new NTAccount(Environment.MachineName, username);
                    var sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
                    return sid.Value;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Clear all Steam login/session data so next launch shows a fresh login screen.
        /// Deletes: loginusers.vdf, config.vdf, ssfn* files, userdata\ contents.
        /// Clears registry: AutoLoginUser and RememberPassword under the logged-in user's hive.
        /// </summary>
        public void ClearSession(string currentUserLocalAppData, string? currentUserSid)
        {
            // Get Steam install root from registry
            string? steamRoot = null;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                steamRoot = key?.GetValue("InstallPath") as string;
            }
            catch (Exception ex) { _debugLogger($"[Steam] ClearSession: registry read failed — {ex.Message}"); }
            if (string.IsNullOrEmpty(steamRoot) || !Directory.Exists(steamRoot))
            {
                _logger("Clear Session: Steam install path not found.");
                return;
            }

            // 1. Kill Steam first
            KillLauncher();
            Thread.Sleep(2000);

            int deleted = 0;

            // 2. Delete config\loginusers.vdf
            var loginUsersPath = Path.Combine(steamRoot, "config", "loginusers.vdf");
            if (File.Exists(loginUsersPath))
            {
                try { File.Delete(loginUsersPath); deleted++; _debugLogger("[Steam] ClearSession: deleted loginusers.vdf"); }
                catch (Exception ex) { _debugLogger($"[Steam] ClearSession: failed to delete loginusers.vdf — {ex.Message}"); }
            }

            // 3. Delete config\config.vdf (contains auth tokens)
            var configVdfPath = Path.Combine(steamRoot, "config", "config.vdf");
            if (File.Exists(configVdfPath))
            {
                try { File.Delete(configVdfPath); deleted++; _debugLogger("[Steam] ClearSession: deleted config.vdf"); }
                catch (Exception ex) { _debugLogger($"[Steam] ClearSession: failed to delete config.vdf — {ex.Message}"); }
            }

            // 4. Delete all ssfn* files (Steam Guard machine auth tokens)
            try
            {
                foreach (var ssfn in Directory.GetFiles(steamRoot, "ssfn*"))
                {
                    try { File.Delete(ssfn); deleted++; _debugLogger($"[Steam] ClearSession: deleted {Path.GetFileName(ssfn)}"); }
                    catch (Exception ex) { _debugLogger($"[Steam] ClearSession: failed to delete {Path.GetFileName(ssfn)} — {ex.Message}"); }
                }
            }
            catch { }

            // 5. Delete contents of userdata\ (accumulated per-user data from all past logins)
            var userDataDir = Path.Combine(steamRoot, "userdata");
            if (Directory.Exists(userDataDir))
            {
                try
                {
                    foreach (var subDir in Directory.GetDirectories(userDataDir))
                    {
                        try { Directory.Delete(subDir, true); deleted++; _debugLogger($"[Steam] ClearSession: deleted userdata\\{Path.GetFileName(subDir)}"); }
                        catch (Exception ex) { _debugLogger($"[Steam] ClearSession: failed to delete userdata\\{Path.GetFileName(subDir)} — {ex.Message}"); }
                    }
                }
                catch { }
            }

            // 6. Clear AutoLoginUser and RememberPassword registry values for the logged-in user
            // Service runs as SYSTEM so we use HKU\<SID>\... instead of HKCU\...
            if (!string.IsNullOrEmpty(currentUserSid))
            {
                try
                {
                    using var userKey = Microsoft.Win32.Registry.Users.OpenSubKey(
                        $@"{currentUserSid}\Software\Valve\Steam", writable: true);
                    if (userKey != null)
                    {
                        userKey.SetValue("AutoLoginUser", "", Microsoft.Win32.RegistryValueKind.String);
                        userKey.SetValue("RememberPassword", 0, Microsoft.Win32.RegistryValueKind.DWord);
                        _debugLogger("[Steam] ClearSession: cleared AutoLoginUser and RememberPassword registry values");
                    }
                    else
                    {
                        _debugLogger("[Steam] ClearSession: Steam registry key not found under user hive — skipping registry clear");
                    }
                }
                catch (Exception ex)
                {
                    _debugLogger($"[Steam] ClearSession: registry clear failed — {ex.Message}");
                }
            }
            else
            {
                _debugLogger("[Steam] ClearSession: no user SID available — skipping registry clear");
            }

            _logger($"Steam session cleared ({deleted} item(s) removed). Next launch will show a fresh login screen.");
        }
    }
}
