using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using System.Security.Principal;
using GamePinned.Service.Core;
using GamePinned.Shared.Models;

namespace GamePinned.Service.Platforms
{
    /// <summary>
    /// Epic Games Store platform implementation for GamePinned
    /// Handles Epic game detection, protection, and manifest backup/restore
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class EpicPlatform : IGamePlatform
    {
        private List<FileSystemWatcher> _watchers = new();
        private volatile bool _stoppingWatchers = false;
        private string _backupPath;
        private Action<string> _logger;
        private Action<string> _debugLogger;
        private Action<GameInfo>? _onUninstallDetected;
        private Action<GameInfo>? _onNewGameDetected;
        private readonly object _aclMutationLock = new();

        private const string EpicMetadataDirectoryName = ".egstore";
        private const string EpicMovedMarkerName = "$movedMarker";
        private const string EpicManifestDirectory = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

        public string PlatformName => "Epic Games";
        public string PlatformId => "epic";

        public EpicPlatform(string backupPath, Action<string> logger, Action<string>? debugLogger = null)
        {
            _backupPath = backupPath;
            _logger = logger;
            _debugLogger = debugLogger ?? (_ => { });
            Directory.CreateDirectory(_backupPath);
        }

        public bool IsInstalled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\EpicGames\EpicGamesLauncher");
                var appDataPath = key?.GetValue("AppDataPath") as string;
                if (!string.IsNullOrEmpty(appDataPath) && Directory.Exists(appDataPath))
                    return true;
            }
            catch { }

            foreach (var path in new[] {
                @"C:\Program Files (x86)\Epic Games\Launcher",
                @"C:\Program Files\Epic Games\Launcher"
            })
            {
                if (Directory.Exists(path))
                    return true;
            }

            if (Directory.Exists(@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests"))
                return true;

            try
            {
                var proc = Process.GetProcessesByName("EpicGamesLauncher").FirstOrDefault();
                if (proc != null)
                    return true;
            }
            catch { }

            return false;
        }

        public List<string> DetectLibraryPaths()
        {
            // Epic auto-detects the central manifests folder (like Steam/Riot)
            var paths = new List<string>();
            var manifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
            if (Directory.Exists(manifestsPath))
            {
                paths.Add(manifestsPath);
            }
            return paths;
        }

        public List<GameInfo> GetInstalledGames(string libraryPath)
        {
            var games = new List<GameInfo>();

            if (!Directory.Exists(libraryPath))
                return games;

            var manifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

            // MODE 1: libraryPath is the ProgramData manifest folder (scan all manifests)
            // This is used for auto-detection if ever enabled, or for admin who adds manifest folder
            if (libraryPath.Equals(manifestsPath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var itemFiles = Directory.GetFiles(libraryPath, "*.item");

                    foreach (var itemFile in itemFiles)
                    {
                        try
                        {
                            var game = ParseItemManifest(itemFile);
                            if (game == null)
                                continue;

                            if (!Directory.Exists(game.InstallPath))
                                continue;

                            games.Add(game);
                        }
                        catch (Exception ex)
                        {
                            _logger($"[Epic] Failed to parse {Path.GetFileName(itemFile)}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger($"[Epic] Error scanning library: {ex.Message}");
                }

                return games;
            }

            // MODE 2: libraryPath is a specific game folder (user-selected)
            // Find the manifest for this specific folder
            var singleGame = FindGameByInstallPath(libraryPath);
            if (singleGame != null)
            {
                games.Add(singleGame);
            }

            return games;
        }

        /// <summary>
        /// Find Epic game by its installation folder
        /// Scans manifests in ProgramData to find matching InstallLocation
        /// </summary>
        private GameInfo? FindGameByInstallPath(string installPath)
        {
            var manifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
            if (!Directory.Exists(manifestsPath))
            {
                _debugLogger($"[Epic] Manifests folder not found at {manifestsPath}");
                return null;
            }

            var normalizedInstallPath = Path.GetFullPath(installPath).TrimEnd('\\', '/').ToLowerInvariant();

            try
            {
                var itemFiles = Directory.GetFiles(manifestsPath, "*.item");
                foreach (var itemFile in itemFiles)
                {
                    try
                    {
                        var game = ParseItemManifest(itemFile);
                        if (game == null) continue;

                        var normalizedGamePath = Path.GetFullPath(game.InstallPath).TrimEnd('\\', '/').ToLowerInvariant();

                        if (normalizedGamePath == normalizedInstallPath)
                        {
                            _debugLogger($"[Epic] Found matching manifest: {Path.GetFileName(itemFile)}");
                            return game;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger($"[Epic] Error searching for game manifest: {ex.Message}");
            }

            return null;
        }

        private GameInfo? ParseItemManifest(string itemFile)
        {
            try
            {
                var jsonText = File.ReadAllText(itemFile);
                using var doc = JsonDocument.Parse(jsonText);
                var root = doc.RootElement;

                string? displayName = null;
                string? installLocation = null;
                string? appName = null;
                string? launchExecutable = null;

                if (root.TryGetProperty("DisplayName", out var displayNameProp))
                    displayName = displayNameProp.GetString();

                if (root.TryGetProperty("InstallLocation", out var installLocProp))
                    installLocation = installLocProp.GetString();

                if (root.TryGetProperty("AppName", out var appNameProp))
                    appName = appNameProp.GetString();

                if (root.TryGetProperty("LaunchExecutable", out var launchExeProp))
                    launchExecutable = launchExeProp.GetString();

                // Filter out non-games (launcher, redistributables, tools)
                // Real games have LaunchExecutable defined
                if (string.IsNullOrEmpty(launchExecutable))
                    return null;

                // Also filter by AppName patterns (common non-game items)
                if (!string.IsNullOrEmpty(appName))
                {
                    var appNameLower = appName.ToLowerInvariant();
                    if (appNameLower.Contains("launcher") ||
                        appNameLower.Contains("prereq") ||
                        appNameLower.Contains("redist") ||
                        appNameLower.Contains("directx") ||
                        appNameLower.Contains("vcredist") ||
                        appNameLower.Contains("dotnet"))
                    {
                        return null;
                    }
                }

                var gameName = displayName ?? appName ?? Path.GetFileNameWithoutExtension(itemFile);

                if (string.IsNullOrEmpty(installLocation))
                    return null;

                return new GameInfo
                {
                    PlatformId = "epic",
                    GameName = gameName,
                    InstallPath = installLocation,
                    ManifestPath = itemFile,
                    IsProtected = CheckProtectionActive(installLocation, itemFile)
                };
            }
            catch (Exception ex)
            {
                _logger($"[Epic] Exception parsing {Path.GetFileName(itemFile)}: {ex.Message}");
                return null;
            }
        }

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

            _debugLogger($"[Epic] ApplyProtection: {game.GameName} at {game.InstallPath} | blockedUsers={blockedUsers.Count} blockAll={blockAllNonAdminByDefault}");

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
                    const string usersGroupIdentity = "*S-1-5-32-545";
                    var desiredIdentities = new List<string> { usersGroupIdentity };
                    RemoveEpicMutableMetadataDenies(game.InstallPath);
                    if (IsDesiredProtectionContractActive(game, new[] { usersGroupIdentity }))
                    {
                        _debugLogger($"[Epic] ApplyProtection: exact contract already active for {game.GameName}; no ACL mutation needed");
                        return;
                    }

                    // Establish every desired boundary while the old identity still protects
                    // the payload and manifest, then remove obsolete identities discovered
                    // from the ACL itself rather than inferred from profile folder names.
                    SetSharedParentDeleteChildDenyBatch(game.InstallPath, desiredIdentities);
                    if (!string.IsNullOrEmpty(game.ManifestPath))
                        SetSharedParentDeleteChildDenyBatch(game.ManifestPath, desiredIdentities);
                    SetDenyRuleBatch(game.InstallPath, desiredIdentities);
                    if (!string.IsNullOrEmpty(game.ManifestPath))
                        SetDenyRuleFileBatch(game.ManifestPath, desiredIdentities);

                    var obsoleteIdentities = DiscoverObsoleteProtectionIdentities(
                        game, desiredIdentities);
                    if (obsoleteIdentities.Count > 0)
                    {
                        RemoveDenyRuleBatch(game.InstallPath, obsoleteIdentities);
                        if (!string.IsNullOrEmpty(game.ManifestPath))
                            RemoveDenyRuleFileBatch(game.ManifestPath, obsoleteIdentities);
                        foreach (var identity in obsoleteIdentities)
                        {
                            RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity, childIsFile: false);
                            if (!string.IsNullOrEmpty(game.ManifestPath))
                                RemoveSharedParentDeleteChildDenyIfUnused(game.ManifestPath, identity, childIsFile: true);
                        }
                    }
                }
                else
                {
                    var allToRemove = new List<string> { "*S-1-5-32-545" };
                    allToRemove.AddRange(localIdentities);
                    allToRemove.AddRange(DiscoverProtectionIdentities(game));
                    allToRemove = allToRemove.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    RemoveDenyRuleBatch(game.InstallPath, allToRemove);
                    if (!string.IsNullOrEmpty(game.ManifestPath))
                        RemoveDenyRuleFileBatch(game.ManifestPath, allToRemove);
                    foreach (var identity in allToRemove)
                    {
                        RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity, childIsFile: false);
                        if (!string.IsNullOrEmpty(game.ManifestPath))
                            RemoveSharedParentDeleteChildDenyIfUnused(game.ManifestPath, identity, childIsFile: true);
                    }
                }
                return;
            }

            var toApplyIdentities = new List<string>();
            var toRemoveIdentities = new List<string> { "*S-1-5-32-545" };

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

            // Routine service restarts must not tear down an already-correct contract merely
            // to recreate the same ACEs. The former remove-then-add cycle exposed payload files
            // to blocked users for several seconds on every restart. Compare both the complete
            // Epic boundary contract and the exact desired root SID set before mutating.
            RemoveEpicMutableMetadataDenies(game.InstallPath);
            if (IsDesiredProtectionContractActive(game, toApplyIdentities))
            {
                _debugLogger($"[Epic] ApplyProtection: exact contract already active for {game.GameName}; no ACL mutation needed");
                return;
            }

            if (toApplyIdentities.Count > 0)
            {
                SetSharedParentDeleteChildDenyBatch(game.InstallPath, toApplyIdentities);
                if (!string.IsNullOrEmpty(game.ManifestPath))
                    SetSharedParentDeleteChildDenyBatch(game.ManifestPath, toApplyIdentities);
                SetDenyRuleBatch(game.InstallPath, toApplyIdentities);
                if (!string.IsNullOrEmpty(game.ManifestPath))
                    SetDenyRuleFileBatch(game.ManifestPath, toApplyIdentities);
            }

            var obsoleteSpecificIdentities = DiscoverObsoleteProtectionIdentities(
                game, toApplyIdentities);
            if (obsoleteSpecificIdentities.Count > 0)
            {
                RemoveDenyRuleBatch(game.InstallPath, obsoleteSpecificIdentities);
                if (!string.IsNullOrEmpty(game.ManifestPath))
                    RemoveDenyRuleFileBatch(game.ManifestPath, obsoleteSpecificIdentities);
                foreach (var identity in obsoleteSpecificIdentities)
                {
                    RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity, childIsFile: false);
                    if (!string.IsNullOrEmpty(game.ManifestPath))
                        RemoveSharedParentDeleteChildDenyIfUnused(game.ManifestPath, identity, childIsFile: true);
                }
            }
            // Lock status logged by PlatformManager.ApplyGameProtection (avoids duplicate/contradicting messages)
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
            var allIdentities = new List<string> { "*S-1-5-32-545" };
            foreach (var user in GetLocalUsersForSync())
                allIdentities.Add(ResolveToIdentity(user));
            foreach (var username in blockedUsers)
                allIdentities.Add(ResolveToIdentity(username));
            allIdentities.AddRange(DiscoverProtectionIdentities(game));
            allIdentities = allIdentities.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (Directory.Exists(game.InstallPath))
                RemoveDenyRuleBatch(game.InstallPath, allIdentities);
            if (!string.IsNullOrEmpty(game.ManifestPath))
                RemoveDenyRuleFileBatch(game.ManifestPath, allIdentities);
            foreach (var identity in allIdentities)
            {
                RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity, childIsFile: false);
                if (!string.IsNullOrEmpty(game.ManifestPath))
                    RemoveSharedParentDeleteChildDenyIfUnused(game.ManifestPath, identity, childIsFile: true);
            }
        }

        /// <summary>
        /// Removes every explicit delete-deny identity from each game and manifest while
        /// preserving Epic's explicit allow ACEs. A full icacls /reset breaks launcher writes
        /// under Program Files. See IGamePlatform.ResetToDefault and Critical #59.
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
            games = GetLifecycleResetGames(games);
            var discoveredByGame = new Dictionary<GameInfo, List<string>>();
            foreach (var game in games)
            {
                try
                {
                    var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (Directory.Exists(game.InstallPath))
                    {
                        var identities = AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                            game.InstallPath, recursive: true, _debugLogger);
                        discovered.UnionWith(identities);
                        RemoveDenyRuleBatch(game.InstallPath, identities);
                    }
                    if (!string.IsNullOrEmpty(game.ManifestPath) && File.Exists(game.ManifestPath))
                    {
                        var identities = AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                            game.ManifestPath, recursive: false, _debugLogger);
                        discovered.UnionWith(identities);
                        RemoveDenyRuleFileBatch(game.ManifestPath, identities);
                    }
                    discoveredByGame[game] = discovered.ToList();
                    _debugLogger($"[Epic] ResetToDefault removed {discovered.Count} deny identity/identities from {game.GameName} while preserving allow ACEs");
                }
                catch (Exception ex)
                {
                    _debugLogger($"[Epic] ResetToDefault({game.GameName}) threw: {ex}");
                }
            }

            foreach (var game in games)
            {
                if (!discoveredByGame.TryGetValue(game, out var identities))
                    continue;
                foreach (var identity in identities)
                {
                    RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity, childIsFile: false);
                    if (!string.IsNullOrEmpty(game.ManifestPath))
                        RemoveSharedParentDeleteChildDenyIfUnused(game.ManifestPath, identity, childIsFile: true);
                }
            }
        }

        /// <summary>
        /// Live Epic discovery intentionally ignores games whose .item manifest is gone. During
        /// lifecycle cleanup that is not enough: a blocked/failed uninstall can remove the live
        /// manifest while leaving the old GamePinned deny on the game directory. GamePinned's
        /// own manifest backup is the authoritative path record for this exact recovery case.
        ///
        /// Only add a backup-only path when it still exists and its root currently carries an
        /// explicit delete deny. This keeps cleanup scoped to abandoned protection residue and
        /// avoids recursively inspecting every historical backup target.
        /// </summary>
        private List<GameInfo> GetLifecycleResetGames(List<GameInfo> discoveredGames)
        {
            var result = new Dictionary<string, GameInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var game in discoveredGames)
            {
                try
                {
                    result[Path.GetFullPath(game.InstallPath).TrimEnd('\\', '/')] = game;
                }
                catch (Exception ex)
                {
                    _debugLogger($"[Epic] ResetToDefault ignored invalid discovered path for {game.GameName}: {ex.Message}");
                }
            }

            if (!Directory.Exists(_backupPath))
                return result.Values.ToList();

            try
            {
                foreach (var backupManifest in Directory.EnumerateFiles(_backupPath, "*.item", SearchOption.AllDirectories))
                {
                    var backupGame = ParseItemManifest(backupManifest);
                    if (backupGame == null || !Directory.Exists(backupGame.InstallPath))
                        continue;

                    string normalizedInstallPath;
                    try
                    {
                        normalizedInstallPath = Path.GetFullPath(backupGame.InstallPath).TrimEnd('\\', '/');
                    }
                    catch (Exception ex)
                    {
                        _debugLogger($"[Epic] ResetToDefault ignored invalid backup path in {Path.GetFileName(backupManifest)}: {ex.Message}");
                        continue;
                    }

                    if (string.Equals(normalizedInstallPath, Path.GetPathRoot(normalizedInstallPath)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                        result.ContainsKey(normalizedInstallPath))
                    {
                        continue;
                    }

                    var rootDenyIdentities = AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                        normalizedInstallPath, recursive: false, _debugLogger);
                    if (rootDenyIdentities.Count == 0)
                        continue;

                    result[normalizedInstallPath] = new GameInfo
                    {
                        PlatformId = PlatformId,
                        GameName = backupGame.GameName,
                        InstallPath = normalizedInstallPath,
                        ManifestPath = Path.Combine(EpicManifestDirectory, Path.GetFileName(backupManifest)),
                        IsProtected = true
                    };
                    _debugLogger($"[Epic] ResetToDefault recovered orphan path from backup manifest {Path.GetFileName(backupManifest)}: {normalizedInstallPath}");
                }
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] ResetToDefault could not enumerate backup manifests at {_backupPath}: {ex.Message}");
            }

            return result.Values.ToList();
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
                SetDenyRule(game.InstallPath, identity);
                if (!string.IsNullOrEmpty(game.ManifestPath))
                    SetDenyRule(game.ManifestPath, identity);
                SetSharedParentDeleteChildDeny(game.InstallPath, identity);
                if (!string.IsNullOrEmpty(game.ManifestPath))
                    SetSharedParentDeleteChildDeny(game.ManifestPath, identity);
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] ApplyUserProtection({game.GameName}, {username}) threw: {ex}");
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
                RemoveSharedParentDeleteChildDenyIfUnused(game.InstallPath, identity, childIsFile: false);
                if (!string.IsNullOrEmpty(game.ManifestPath))
                    RemoveSharedParentDeleteChildDenyIfUnused(game.ManifestPath, identity, childIsFile: true);
            }
            catch (Exception ex) { _debugLogger($"[Epic] RemoveUserProtection({game.GameName}, {username}): {ex.Message}"); }
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

        private void RemoveEpicMutableMetadataDenies(string installPath)
        {
            if (!Directory.Exists(installPath))
                return;

            try
            {
                foreach (var entry in new DirectoryInfo(installPath)
                    .EnumerateFileSystemInfos()
                    .Where(entry => IsEpicMutableMetadataEntry(entry.Name)))
                {
                    var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                    var identities = AclDenyCleanup.DiscoverExplicitDeleteDenyIdentities(
                        entry.FullName, recursive: false, _debugLogger);
                    if (isDirectory)
                        RemoveDenyRuleBatch(entry.FullName, identities);
                    else
                        RemoveDenyRuleFileBatch(entry.FullName, identities);
                }
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] Mutable metadata deny cleanup failed for {installPath}: {ex.Message}");
            }
        }

        private void SetDenyRule(string path, string userOrSid)
        {
            try
            {
                // CRITICAL: Remove existing rule first to prevent duplicates
                RemoveDenyRule(path, userOrSid);

                bool isFile = File.Exists(path) && !Directory.Exists(path);
                if (isFile)
                {
                    var (ec, stderr) = RunCmdCapture("cmd.exe", $"/c icacls \"{path}\" /deny \"{userOrSid}:(DE)\" /C /Q");
                    if (ec != 0)
                        _debugLogger($"[Epic] SetDenyRule exit={ec} {userOrSid} on {Path.GetFileName(path)} stderr={stderr.Trim()}");
                    else
                        _debugLogger($"[Epic] SetDenyRule exit={ec} {userOrSid} on {Path.GetFileName(path)}");
                    return;
                }

                SetDenyRuleParallelSubdirs(path, userOrSid);
            }
            catch (Exception ex) { _debugLogger($"[Epic] SetDenyRule({path}, {userOrSid}): {ex.Message}"); }
        }

        /// <summary>
        /// Protects an Epic game without inheriting GamePinned delete denies into
        /// launcher-owned transaction state. Epic creates and deletes .egstore content and
        /// a root-level $movedMarker during normal launcher transactions.
        /// </summary>
        private void SetDenyRuleParallelSubdirs(string dirPath, string userOrSid)
        {
            // The root rule is deliberately non-inheriting. Payload entries receive their
            // own rules below; .egstore and future root transaction markers stay mutable.
            RunCmdCapture("cmd.exe", $"/c icacls \"{dirPath}\" /deny \"{userOrSid}:(DE,DC)\" /C /Q");

            try
            {
                var entries = new DirectoryInfo(dirPath)
                    .EnumerateFileSystemInfos()
                    .Where(entry => !IsEpicMutableMetadataEntry(entry.Name))
                    .ToArray();
                if (entries.Length == 0)
                {
                    _debugLogger($"[Epic] SetDenyRule exit=0 {userOrSid} on {Path.GetFileName(dirPath)}");
                    return;
                }

                var opts = new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4)
                };
                System.Threading.Tasks.Parallel.ForEach(entries, opts, entry =>
                {
                    var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                    var isReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    var rights = isDirectory ? "(DE,DC)" : "(DE)";
                    var inheritance = isDirectory && !isReparsePoint ? "(OI)(CI)" : string.Empty;
                    var recursive = isDirectory && !isReparsePoint ? " /T" : string.Empty;
                    var (ec, err) = RunCmdCapture("cmd.exe",
                        $"/c icacls \"{entry.FullName}\" /deny \"{userOrSid}:{inheritance}{rights}\"{recursive} /C /Q");
                    if (ec != 0)
                        _debugLogger($"[Epic] SetDenyRule FAILED exit={ec} {userOrSid} on {entry.Name} stderr={err.Trim()}");
                });
                _debugLogger($"[Epic] SetDenyRule exit=0 {userOrSid} on {Path.GetFileName(dirPath)} ({entries.Length} payload entries parallel)");
            }
            catch (Exception ex)
            {
                // A recursive fallback on the root would recreate the proven .egstore bug.
                // Keep the root protected and report the incomplete payload application so
                // verification can retry without corrupting launcher metadata.
                _debugLogger($"[Epic] Selective SetDenyRule failed after root protection: {ex}");
            }
        }

        private void RemoveDenyRule(string path, string userOrSid)
        {
            try
            {
                if (!Directory.Exists(path) && !File.Exists(path)) return;

                bool isFile = File.Exists(path) && !Directory.Exists(path);
                if (isFile)
                {
                    var ec = RunCmdWithExitCode("cmd.exe", $"/c icacls \"{path}\" /remove:d \"{userOrSid}\" /C /Q");
                    if (ec != 0 && ec != 3 && ec != 1332)
                        _debugLogger($"[Epic] RemoveDenyRule exit={ec} user={userOrSid} path={path}");
                    return;
                }

                RemoveDenyRuleDir(path, userOrSid);
            }
            catch (Exception ex)
            {
                _logger($"[Epic] RemoveDenyRule error: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes deny rules from a directory and every top-level entry. Selective Epic
        /// protection puts explicit rules on root-level payload files as well as directories,
        /// so enumerating directories alone would strand file denies during per-user removal.
        /// </summary>
        private void RemoveDenyRuleDir(string dirPath, string userOrSid)
        {
            // Step 1: remove from root itself (no /T)
            RunCmdWithExitCode("cmd.exe", $"/c icacls \"{dirPath}\" /remove:d \"{userOrSid}\" /C /Q");

            try
            {
                var entries = new DirectoryInfo(dirPath).EnumerateFileSystemInfos().ToArray();
                if (entries.Length == 0) return;

                var opts = new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4)
                };
                System.Threading.Tasks.Parallel.ForEach(entries, opts, entry =>
                {
                    var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                    var isReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    var recursive = isDirectory && !isReparsePoint ? " /T" : string.Empty;
                    var ec = RunCmdWithExitCode(
                        "cmd.exe",
                        $"/c icacls \"{entry.FullName}\" /remove:d \"{userOrSid}\"{recursive} /C /Q");
                    if (ec != 0 && ec != 3 && ec != 1332)
                        _debugLogger($"[Epic] RemoveDenyRule exit={ec} user={userOrSid} on {entry.Name}");
                });
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] RemoveDenyRule parallel failed: {ex.Message} — falling back to /T");
                RunCmdWithExitCode("cmd.exe", $"/c icacls \"{dirPath}\" /remove:d \"{userOrSid}\" /T /C /Q");
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

        private string ResolveToIdentity(string username)
        {
            if (username.StartsWith("*S-")) return username;
            var sid = ResolveToSid(username);
            return sid != null ? "*" + sid : username;
        }

        private void RemoveDenyRuleBatch(string dirPath, List<string> identities)
        {
            if (identities.Count == 0 || !Directory.Exists(dirPath)) return;
            // One icacls call per identity — batching multiple /remove:d flags in one call returns
            // exit 0 but silently processes only a subset of identities.
            foreach (var identity in identities)
            {
                var ec = RunCmdWithExitCode("cmd.exe", $"/c icacls \"{dirPath}\" /remove:d \"{identity}\" /T /C /Q");
                if (ec != 0 && ec != 3 && ec != 1332)
                    _debugLogger($"[Epic] RemoveDenyRuleBatch FAILED exit={ec} on {Path.GetFileName(dirPath)} for {identity}");
            }
            _debugLogger($"[Epic] RemoveDenyRuleBatch: {identities.Count} identities on {Path.GetFileName(dirPath)}");
        }

        private void SetDenyRuleBatch(string dirPath, List<string> identities)
        {
            if (identities.Count == 0 || !Directory.Exists(dirPath)) return;
            var rootDenyArgs = string.Join(" ", identities.Select(id => $"/deny \"{id}:(DE,DC)\""));
            RunCmdWithExitCode("cmd.exe", $"/c icacls \"{dirPath}\" {rootDenyArgs} /C /Q");
            try
            {
                var entries = new DirectoryInfo(dirPath)
                    .EnumerateFileSystemInfos()
                    .Where(entry => !IsEpicMutableMetadataEntry(entry.Name))
                    .ToArray();
                if (entries.Length == 0) { _debugLogger($"[Epic] SetDenyRuleBatch: {identities.Count} identities on {Path.GetFileName(dirPath)}"); return; }
                var opts = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4) };
                System.Threading.Tasks.Parallel.ForEach(entries, opts, entry =>
                {
                    var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                    var isReparsePoint = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    var rights = isDirectory ? "(DE,DC)" : "(DE)";
                    var inheritance = isDirectory && !isReparsePoint ? "(OI)(CI)" : string.Empty;
                    var recursive = isDirectory && !isReparsePoint ? " /T" : string.Empty;
                    var denyArgs = string.Join(" ", identities.Select(id => $"/deny \"{id}:{inheritance}{rights}\""));
                    var (ec, err) = RunCmdCapture("cmd.exe", $"/c icacls \"{entry.FullName}\" {denyArgs}{recursive} /C /Q");
                    if (ec != 0)
                        _debugLogger($"[Epic] SetDenyRuleBatch FAILED exit={ec} on {entry.Name}: {err.Trim()}");
                });
                _debugLogger($"[Epic] SetDenyRuleBatch: {identities.Count} identities on {Path.GetFileName(dirPath)} ({entries.Length} payload entries parallel)");
            }
            catch (Exception ex)
            {
                // Never fall back to a recursive root rule: that would reintroduce the
                // launcher transaction failure this selective policy exists to prevent.
                _debugLogger($"[Epic] Selective SetDenyRuleBatch failed after root protection: {ex}");
            }
        }

        private void RemoveDenyRuleFileBatch(string filePath, List<string> identities)
        {
            if (identities.Count == 0 || !File.Exists(filePath)) return;
            foreach (var identity in identities)
            {
                var ec = RunCmdWithExitCode("cmd.exe", $"/c icacls \"{filePath}\" /remove:d \"{identity}\" /C /Q");
                if (ec != 0 && ec != 3 && ec != 1332)
                    _debugLogger($"[Epic] RemoveDenyRuleFileBatch FAILED exit={ec} on {Path.GetFileName(filePath)} for {identity}");
            }
        }

        private void SetDenyRuleFileBatch(string filePath, List<string> identities)
        {
            if (identities.Count == 0 || !File.Exists(filePath)) return;
            var denyArgs = string.Join(" ", identities.Select(id => $"/deny \"{id}:(DE)\""));
            var (ec, err) = RunCmdCapture("cmd.exe", $"/c icacls \"{filePath}\" {denyArgs} /C /Q");
            if (ec != 0)
                _debugLogger($"[Epic] SetDenyRuleFileBatch FAILED exit={ec} on {Path.GetFileName(filePath)}: {err.Trim()}");
        }

        private static bool IsEpicMutableMetadataEntry(string name)
        {
            return name.Equals(EpicMetadataDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                   name.Equals(EpicMovedMarkerName, StringComparison.OrdinalIgnoreCase);
        }

        private void SetSharedParentDeleteChildDenyBatch(string protectedChildPath, List<string> identities)
        {
            foreach (var identity in identities.Distinct(StringComparer.OrdinalIgnoreCase))
                SetSharedParentDeleteChildDeny(protectedChildPath, identity);
        }

        /// <summary>
        /// Windows permits deletion when either the child grants DELETE or its parent grants
        /// DELETE_CHILD. The child deny alone therefore does not protect Epic manifests or
        /// game roots whose parent grants broad access. This non-inheriting parent rule closes
        /// the second authorization route without contaminating ordinary sibling objects.
        /// </summary>
        private void SetSharedParentDeleteChildDeny(string protectedChildPath, string identity)
        {
            var parentPath = Path.GetDirectoryName(protectedChildPath);
            var sid = TryResolveSecurityIdentifier(identity);
            if (string.IsNullOrEmpty(parentPath) || sid == null || !Directory.Exists(parentPath))
            {
                _debugLogger($"[Epic] Cannot set shared parent DeleteChild deny for {identity} at {protectedChildPath}");
                return;
            }

            try
            {
                if (HasExplicitDeleteChildDeny(parentPath, sid))
                    return;

                // Keep ACL mutation on the same proven icacls path as all other protection
                // changes. The .NET ACL API is used only for exact read-side inspection.
                var (ec, stderr) = RunCmdCapture(
                    "cmd.exe",
                    $"/c icacls \"{parentPath}\" /deny \"{identity}:(DC)\" /C /Q");
                if (ec != 0)
                    _debugLogger($"[Epic] Set shared parent DeleteChild deny FAILED exit={ec} for {identity} at {parentPath}: {stderr.Trim()}");
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] Set shared parent DeleteChild deny failed for {identity} at {parentPath}: {ex}");
            }
        }

        /// <summary>
        /// Remove the shared parent rule only after no sibling still carries the explicit
        /// protected-child Delete deny for the same identity. The ACL mutation lock serializes
        /// multi-game apply/remove operations so two removals cannot strand or prematurely
        /// remove the shared rule.
        /// </summary>
        private void RemoveSharedParentDeleteChildDenyIfUnused(string childPath, string identity, bool childIsFile)
        {
            var parentPath = Path.GetDirectoryName(childPath);
            var sid = TryResolveSecurityIdentifier(identity);
            if (string.IsNullOrEmpty(parentPath) || sid == null || !Directory.Exists(parentPath))
                return;

            try
            {
                IEnumerable<string> siblings = childIsFile
                    ? Directory.EnumerateFiles(parentPath, "*.item", SearchOption.TopDirectoryOnly)
                    : Directory.EnumerateDirectories(parentPath, "*", SearchOption.TopDirectoryOnly);

                if (siblings.Any(path => HasExplicitDeleteDeny(path, sid)))
                    return;

                var ec = RunCmdWithExitCode(
                    "cmd.exe",
                    $"/c icacls \"{parentPath}\" /remove:d \"{identity}\" /C /Q");
                if (ec != 0 && ec != 3 && ec != 1332)
                    _debugLogger($"[Epic] Remove shared parent DeleteChild deny FAILED exit={ec} for {identity} at {parentPath}");
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] Remove shared parent DeleteChild deny failed for {identity} at {parentPath}: {ex}");
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
                // Unreadable ACLs must keep the shared parent rule. Removing it on uncertainty
                // could expose another protected sibling.
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

        public void BackupManifests(string libraryPath, List<string> excludedGames)
        {
            if (!Directory.Exists(libraryPath))
                return;

            try
            {
                var itemFiles = Directory.GetFiles(libraryPath, "*.item");

                foreach (var itemFile in itemFiles)
                {
                    try
                    {
                        var game = ParseItemManifest(itemFile);
                        if (game == null)
                            continue;

                        if (excludedGames.Contains(game.GameName, StringComparer.OrdinalIgnoreCase))
                            continue;

                        var backupFile = Path.Combine(_backupPath, Path.GetFileName(itemFile));

                        if (!File.Exists(backupFile) || new FileInfo(itemFile).Length != new FileInfo(backupFile).Length)
                        {
                            File.Copy(itemFile, backupFile, true);
                        }
                    }
                    catch { }
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

                foreach (var backupFile in Directory.GetFiles(_backupPath, "*.item"))
                {
                    try
                    {
                        var game = ParseItemManifest(backupFile);
                        if (game != null && excludedGames.Contains(game.GameName, StringComparer.OrdinalIgnoreCase))
                        {
                            File.Delete(backupFile);
                            _logger($"[Epic] Deleted backup for excluded game: {game.GameName}");
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

            var backupFiles = Directory.GetFiles(_backupPath, "*.item");

            foreach (var backupFile in backupFiles)
            {
                try
                {
                    var game = ParseItemManifest(backupFile);
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
        }

        public void KillLauncher()
        {
            foreach (var processName in new[] { "EpicGamesLauncher", "EpicWebHelper" })
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
                _logger("[Epic] Launcher executable not found");
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
                _logger($"[Epic] Failed to start launcher: {ex.Message}");
            }
        }

        private string? FindLauncherPath()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\EpicGames\EpicGamesLauncher");
                var appDataPath = key?.GetValue("AppDataPath") as string;
                if (!string.IsNullOrEmpty(appDataPath))
                {
                    var launcherPath = Path.Combine(appDataPath, "..", "..", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe");
                    if (File.Exists(launcherPath))
                        return Path.GetFullPath(launcherPath);
                }
            }
            catch { }

            foreach (var path in new[] {
                @"C:\Program Files (x86)\Epic Games Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
                @"C:\Program Files\Epic Games Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"
            })
            {
                if (File.Exists(path))
                    return path;
            }

            try
            {
                using var proc = Process.GetProcessesByName("EpicGamesLauncher").FirstOrDefault();
                if (proc?.MainModule?.FileName != null)
                    return proc.MainModule.FileName;
            }
            catch (Exception ex) { _debugLogger($"[Epic] FindLauncherPath via process: {ex.Message}"); }

            return null;
        }

        public void StartUninstallWatcher(Action<GameInfo> onUninstallDetected)
        {
            _onUninstallDetected = onUninstallDetected;
            _stoppingWatchers = false;

            // CRITICAL: Epic watchers MUST watch the ProgramData manifest folder
            // DetectLibraryPaths() returns empty (correct for GUI), but watchers need the fixed manifest location
            var manifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

            if (!Directory.Exists(manifestsPath))
            {
                _logger($"[Epic] Manifests folder not found");
                return;
            }
            StartEpicUninstallWatcherForPath(manifestsPath, 0);
        }

        private void StartEpicUninstallWatcherForPath(string manifestsPath, int attempt)
        {
            if (_stoppingWatchers) return;
            try
            {
                var watcher = new FileSystemWatcher(manifestsPath)
                {
                    Filter = "*.item",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };
                watcher.Deleted += OnManifestDeleted;
                var currentWatcher = watcher;
                watcher.Error += (s, e) =>
                {
                    _debugLogger($"[Epic] Uninstall watcher error: {e.GetException().Message} — scheduling restart");
                    if (_stoppingWatchers) return;
                    lock (_watchers)
                    {
                        try { currentWatcher.EnableRaisingEvents = false; currentWatcher.Dispose(); } catch { }
                        _watchers.Remove(currentWatcher);
                    }
                    ScheduleWatcherRestart(manifestsPath, attempt, () => StartEpicUninstallWatcherForPath(manifestsPath, attempt + 1), "Epic uninstall");
                };
                watcher.EnableRaisingEvents = true;
                lock (_watchers) { _watchers.Add(watcher); }
                _debugLogger($"[Epic] Uninstall watcher started: {manifestsPath}" + (attempt > 0 ? $" (restart attempt {attempt})" : ""));
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] Failed to start uninstall watcher on {manifestsPath}: {ex.Message}");
                ScheduleWatcherRestart(manifestsPath, attempt, () => StartEpicUninstallWatcherForPath(manifestsPath, attempt + 1), "Epic uninstall");
            }
        }

        public void StartNewGameWatcher(Action<GameInfo> onNewGameDetected)
        {
            _onNewGameDetected = onNewGameDetected;

            // CRITICAL: Epic watchers MUST watch the ProgramData manifest folder
            // DetectLibraryPaths() returns empty (correct for GUI), but watchers need the fixed manifest location
            var manifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

            if (!Directory.Exists(manifestsPath))
            {
                _logger($"[Epic] Manifests folder not found");
                return;
            }
            StartEpicNewGameWatcherForPath(manifestsPath, 0);
        }

        private void StartEpicNewGameWatcherForPath(string manifestsPath, int attempt)
        {
            if (_stoppingWatchers) return;
            try
            {
                var watcher = new FileSystemWatcher(manifestsPath)
                {
                    Filter = "*.item",
                    NotifyFilter = NotifyFilters.FileName
                };
                watcher.Created += OnManifestCreated;
                var currentWatcher = watcher;
                watcher.Error += (s, e) =>
                {
                    _debugLogger($"[Epic] New game watcher error: {e.GetException().Message} — scheduling restart");
                    if (_stoppingWatchers) return;
                    lock (_watchers)
                    {
                        try { currentWatcher.EnableRaisingEvents = false; currentWatcher.Dispose(); } catch { }
                        _watchers.Remove(currentWatcher);
                    }
                    ScheduleWatcherRestart(manifestsPath, attempt, () => StartEpicNewGameWatcherForPath(manifestsPath, attempt + 1), "Epic new-game");
                };
                watcher.EnableRaisingEvents = true;
                lock (_watchers) { _watchers.Add(watcher); }
                _debugLogger($"[Epic] New game watcher started: {manifestsPath}" + (attempt > 0 ? $" (restart attempt {attempt})" : ""));
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] Failed to start new game watcher on {manifestsPath}: {ex.Message}");
                ScheduleWatcherRestart(manifestsPath, attempt, () => StartEpicNewGameWatcherForPath(manifestsPath, attempt + 1), "Epic new-game");
            }
        }

        /// <summary>
        /// Schedule a watcher restart with exponential backoff. Without this, a single transient
        /// FileSystemWatcher.Error (buffer overflow, permission change) silently stops detection
        /// until the service restarts.
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

        /// <summary>
        /// Stop all file system watchers.
        /// </summary>
        /// <param name="releaseHandles">Unused for Epic — Epic holds no per-game handles. Parameter exists for IGamePlatform compliance.</param>
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

        private void OnManifestDeleted(object sender, FileSystemEventArgs e)
        {
            try
            {
                _debugLogger($"[Epic] Manifest deleted: {Path.GetFileName(e.FullPath)}");

                var backupFile = Path.Combine(_backupPath, Path.GetFileName(e.FullPath));
                GameInfo? game = null;

                if (File.Exists(backupFile))
                {
                    game = ParseItemManifest(backupFile);
                }

                if (game != null)
                {
                    game.ManifestPath = e.FullPath;
                    _onUninstallDetected?.Invoke(game);
                }
            }
            catch (Exception ex)
            {
                _logger($"[Epic] Error handling manifest deletion: {ex.Message}");
            }
        }

        private void OnManifestCreated(object sender, FileSystemEventArgs e)
        {
            try
            {
                Thread.Sleep(500);

                var game = ParseItemManifest(e.FullPath);
                if (game != null)
                {
                    _debugLogger($"[Epic] New game detected: {game.GameName}");
                    _onNewGameDetected?.Invoke(game);
                }
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] Error handling new manifest: {ex.Message}");
            }
        }

        public void HealthCheck(List<GameInfo> protectedGames, List<string> blockedUsers, bool blockAllNonAdminByDefault)
        {
            foreach (var game in protectedGames)
            {
                if (game.PlatformId != this.PlatformId)
                    continue;

                if (!Directory.Exists(game.InstallPath))
                    continue;

                // CRITICAL: Check if game is stable (not being updated/downloaded)
                // Same pattern as Steam - temporarily remove protection during updates
                if (!IsGameStable(game.InstallPath))
                {
                    _debugLogger($"[Epic] HealthCheck: {game.GameName} is updating, removing protection temporarily");
                    RemoveProtection(game, blockedUsers);
                    continue;
                }

                // Only re-apply protection if it's actually missing (avoids spawning icacls every 60s)
                if (!CheckProtectionActive(game))
                {
                    _debugLogger($"[Epic] HealthCheck: protection missing for {game.GameName}, re-applying");
                    ApplyProtection(game, blockedUsers, blockAllNonAdminByDefault);
                }
            }
        }

        public bool CheckProtectionActive(string path)
        {
            lock (_aclMutationLock)
            {
                return CheckProtectionActiveCore(path, manifestPath: null);
            }
        }

        private bool CheckProtectionActive(GameInfo game)
        {
            lock (_aclMutationLock)
            {
                return CheckProtectionActiveCore(game.InstallPath, game.ManifestPath);
            }
        }

        private bool CheckProtectionActive(string installPath, string manifestPath)
        {
            lock (_aclMutationLock)
            {
                return CheckProtectionActiveCore(installPath, manifestPath);
            }
        }

        /// <summary>
        /// Returns true only when the complete selective Epic contract is active for exactly
        /// the identities currently requested by configuration. This is stricter than the
        /// public status check: an obsolete or unrelated root deny must not make an apply pass
        /// look idempotent.
        /// </summary>
        private bool IsDesiredProtectionContractActive(GameInfo game, IEnumerable<string> desiredIdentities)
        {
            try
            {
                var desiredSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var identity in desiredIdentities)
                {
                    var sid = TryResolveSecurityIdentifier(identity);
                    if (sid == null)
                        return false;
                    desiredSids.Add(sid.Value);
                }

                if (desiredSids.Count == 0)
                    return false;

                var currentSids = GetExplicitDenySids(
                        game.InstallPath,
                        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
                        requireNonInheriting: true)
                    .Select(sid => sid.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return currentSids.SetEquals(desiredSids) &&
                       CheckProtectionActiveCore(game.InstallPath, game.ManifestPath);
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] Desired-contract check failed for {game.GameName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Verifies the Epic-specific ACL contract, not merely the presence of an arbitrary
        /// deny ACE. Every identity protected at the root must also be protected through the
        /// parent DeleteChild route and across every payload boundary, while launcher-owned
        /// .egstore metadata remains free of that identity's delete denies.
        /// </summary>
        private bool CheckProtectionActiveCore(string installPath, string? manifestPath)
        {
            try
            {
                if (!Directory.Exists(installPath))
                    return false;

                var protectedSids = GetExplicitDenySids(
                    installPath,
                    FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
                    requireNonInheriting: true);
                if (protectedSids.Count == 0)
                    return false;

                var parentPath = Path.GetDirectoryName(installPath);
                if (string.IsNullOrEmpty(parentPath) ||
                    protectedSids.Any(sid => !HasExplicitDeleteChildDeny(parentPath, sid)))
                    return false;

                var payloadEntries = new DirectoryInfo(installPath)
                    .EnumerateFileSystemInfos()
                    .Where(entry => !IsEpicMutableMetadataEntry(entry.Name))
                    .ToArray();
                if (payloadEntries.Length == 0)
                    return false;

                foreach (var entry in payloadEntries)
                {
                    var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                    var required = isDirectory
                        ? FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                        : FileSystemRights.Delete;
                    if (protectedSids.Any(sid => !HasExplicitDeny(entry.FullName, sid, required)))
                        return false;
                }

                var metadataPath = Path.Combine(installPath, EpicMetadataDirectoryName);
                if (Directory.Exists(metadataPath) &&
                    protectedSids.Any(sid => HasDeleteDeny(metadataPath, sid, includeInherited: true)))
                    return false;

                if (!string.IsNullOrEmpty(manifestPath))
                {
                    if (!File.Exists(manifestPath) ||
                        protectedSids.Any(sid => !HasExplicitDeny(manifestPath, sid, FileSystemRights.Delete)))
                        return false;

                    var manifestParent = Path.GetDirectoryName(manifestPath);
                    if (string.IsNullOrEmpty(manifestParent) ||
                        protectedSids.Any(sid => !HasExplicitDeleteChildDeny(manifestParent, sid)))
                        return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] CheckProtectionActive error on {installPath}: {ex.Message}");
                return false;
            }
        }

        private static List<SecurityIdentifier> GetExplicitDenySids(
            string path,
            FileSystemRights requiredRights,
            bool requireNonInheriting)
        {
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
            return security
                .GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Where(rule =>
                    rule.AccessControlType == AccessControlType.Deny &&
                    (!requireNonInheriting || rule.InheritanceFlags == InheritanceFlags.None) &&
                    (rule.FileSystemRights & requiredRights) == requiredRights &&
                    rule.IdentityReference is SecurityIdentifier)
                .Select(rule => (SecurityIdentifier)rule.IdentityReference)
                .GroupBy(sid => sid.Value, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static bool HasExplicitDeny(
            string path,
            SecurityIdentifier sid,
            FileSystemRights requiredRights)
        {
            return GetAccessRules(path, includeInherited: false).Any(rule =>
                rule.AccessControlType == AccessControlType.Deny &&
                rule.IdentityReference is SecurityIdentifier ruleSid &&
                sid.Value.Equals(ruleSid.Value, StringComparison.OrdinalIgnoreCase) &&
                (rule.FileSystemRights & requiredRights) == requiredRights);
        }

        private static bool HasDeleteDeny(string path, SecurityIdentifier sid, bool includeInherited)
        {
            const FileSystemRights deleteRights =
                FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;
            return GetAccessRules(path, includeInherited).Any(rule =>
                rule.AccessControlType == AccessControlType.Deny &&
                rule.IdentityReference is SecurityIdentifier ruleSid &&
                sid.Value.Equals(ruleSid.Value, StringComparison.OrdinalIgnoreCase) &&
                (rule.FileSystemRights & deleteRights) != 0);
        }

        private static IEnumerable<FileSystemAccessRule> GetAccessRules(string path, bool includeInherited)
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            return security
                .GetAccessRules(includeExplicit: true, includeInherited, targetType: typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>();
        }

        /// <summary>
        /// Determine whether an Epic game install folder is "stable" (safe to keep protection on)
        /// vs "actively being updated/repaired" (need to temporarily drop protection so the
        /// launcher can replace files).
        ///
        /// Critical #E1 (procmon-verified 2026-05-26): the previous heuristic checked any file
        /// modified in .egstore in the last 10 seconds. That triggered "is updating" during
        /// UNINSTALL too (EGS writes to .egstore\bps\m\$resumeData during uninstall as well as
        /// update). Result: we voluntarily dropped protection mid-uninstall, EGS finished the
        /// uninstall successfully, our FSW fired OnUninstallDetected too late — files gone.
        ///
        /// New heuristic differentiates by what's happening at .egstore\bps\c\backingstore.
        /// The current launcher represents backingstore as a file, not a directory:
        ///   - Update/Repair: EGS creates or writes backingstore while staging changes.
        ///   - Uninstall: EGS opens the old backingstore for Delete and removes it without
        ///     writing it (live Procmon trace, 2026-09-06).
        ///
        /// So: if backingstore itself, or a future directory-form child, was created/modified
        /// in the last 10s, the game is actively receiving a download → return false.
        /// Otherwise return true (stable, keep protection). The .egstore\bps\m\$resumeData
        /// writes alone — which fire during BOTH update and uninstall — no longer trigger
        /// "is updating". This closes the silent-uninstall bypass.
        ///
        /// Trade-off: if Epic ever does a no-download repair (rare — repair almost always
        /// re-downloads something), this would keep protection on and the repair would hit
        /// ACCESS DENIED. The "Repair" UX is already broken under our `(OI)(CI)(DE,DC) /T`
        /// rule (E3 finding) and requires Maintenance Mode regardless. So this trade-off
        /// doesn't lose us anything.
        /// </summary>
        public bool IsGameStable(string installPath)
        {
            try
            {
                if (!Directory.Exists(installPath))
                    return true;

                var shortCutoff = DateTime.Now.AddSeconds(-10);

                // Signal A — write activity at .egstore\bps\c\backingstore (precise path).
                // On the launcher build captured 2026-09-06 this is a file. Support a
                // directory form too, but do not assume that form as the old code did.
                var backingStorePath = Path.Combine(installPath, ".egstore", "bps", "c", "backingstore");
                var hasBackingStoreActivity = false;
                if (File.Exists(backingStorePath))
                {
                    var info = new FileInfo(backingStorePath);
                    hasBackingStoreActivity =
                        info.LastWriteTime > shortCutoff || info.CreationTime > shortCutoff;
                }
                else if (Directory.Exists(backingStorePath))
                {
                    hasBackingStoreActivity = Directory.EnumerateFiles(backingStorePath, "*", SearchOption.AllDirectories)
                        .Any(f =>
                        {
                            try
                            {
                                var info = new FileInfo(f);
                                return info.LastWriteTime > shortCutoff || info.CreationTime > shortCutoff;
                            }
                            catch { return false; }
                        });
                }
                if (hasBackingStoreActivity)
                    return false;
                // Do not fall back to "any new file in .egstore" when backingstore is
                // missing. The 2026-09-06 live uninstall trace proved that uninstall itself
                // creates .lock and $resumeData files for several seconds before deleting
                // them. Treating those as update activity opens a health-tick race that
                // voluntarily removes protection during uninstall. Unknown/non-BPS layouts
                // therefore fail closed here and require Maintenance Mode for writes.

                // Signal B — fresh install folder.
                // Between EGS creating the install folder and EGS starting to populate
                // backingstore there is a short window where Signal A is empty but the
                // install is clearly in progress. Without this signal, HealthCheck races
                // ahead and applies protection during that window, blocking the install
                // (Bug E2, procmon-verified 2026-05-26).
                //
                // Use a 5-minute window on folder CreationTime. After 5 min of being
                // installed, the game is stable enough that we fall back to Signal A only.
                // 5 min is also long enough to cover Epic's slow-start cases where the
                // first backingstore write doesn't happen for 30-60 seconds after folder
                // creation (observed on cafe-grade HDDs).
                var folderInfo = new DirectoryInfo(installPath);
                var folderCutoff = DateTime.Now.AddMinutes(-5);
                if (folderInfo.CreationTime > folderCutoff)
                    return false;

                return true;
            }
            catch (Exception ex)
            {
                _debugLogger($"[Epic] IsGameStable error on {installPath}: {ex.Message}");
                // On error, default to "stable" (keep protection). Safer to over-protect
                // (one missed update cycle, retried next tick) than to under-protect
                // (silent uninstall bypass).
                return true;
            }
        }

        /// <summary>
        /// Clear all Epic Games Launcher login/session data so next launch shows a fresh login screen.
        /// Kills all Epic processes (EpicWebHelper first — it holds Cookies file open with exclusive lock),
        /// then deletes: webcache* folders, GameUserSettings.ini, EOS cache folders.
        /// Also clears registry account ID under the logged-in user's hive.
        /// </summary>
        public void ClearSession(string currentUserLocalAppData, string? currentUserSid)
        {
            var epicSavedPath = Path.Combine(currentUserLocalAppData, "EpicGamesLauncher", "Saved");
            if (!Directory.Exists(epicSavedPath))
            {
                _logger("Clear Session: Epic Games Launcher data folder not found.");
                _debugLogger($"[Epic] ClearSession: Saved path not found at {epicSavedPath}");
                return;
            }

            // 1. Kill EpicWebHelper FIRST — it holds the Cookies SQLite file open with an exclusive lock.
            //    Deleting webcache* while it runs silently fails or leaves a corrupted partial delete.
            //    Then kill remaining Epic processes.
            foreach (var processName in new[] {
                "EpicWebHelper",                        // MUST be first — holds Cookies file open
                "EpicGamesLauncher",
                "EpicOnlineServices",
                "EpicOnlineServicesUserManager",
                "EOSOverlayRenderer-Win64-Shipping",
                "EpicCrashReportClient" })
            {
                try
                {
                    foreach (var proc in Process.GetProcessesByName(processName))
                    {
                        try { proc.Kill(); proc.WaitForExit(3000); } catch { }
                    }
                }
                catch { }
            }
            Thread.Sleep(2000);

            int deleted = 0;

            // 2. Delete ALL webcache* folders (Chromium CEF auth session cookies and tokens).
            //    Folder name varies by embedded Chromium version: webcache, webcache_4147, webcache_4430, etc.
            //    Delete all of them — if only the top-level folder is deleted and a numbered variant exists,
            //    the session cookies survive in the numbered folder.
            try
            {
                foreach (var dir in Directory.GetDirectories(epicSavedPath, "webcache*"))
                {
                    try { Directory.Delete(dir, true); deleted++; _debugLogger($"[Epic] ClearSession: deleted {Path.GetFileName(dir)}\\"); }
                    catch (Exception ex) { _debugLogger($"[Epic] ClearSession: failed to delete {Path.GetFileName(dir)} — {ex.Message}"); }
                }
            }
            catch { }

            // 3. Delete GameUserSettings.ini — contains [RememberMe] and [Offline] sections which the
            //    launcher uses to silently re-authenticate regardless of webcache state.
            //    The subfolder varies by launcher version: Windows, WindowsEditor, WindowsNoEditor.
            //    Search all subdirs under Config\ to handle any version.
            var configPath = Path.Combine(epicSavedPath, "Config");
            if (Directory.Exists(configPath))
            {
                foreach (var iniFile in Directory.GetFiles(configPath, "GameUserSettings.ini", SearchOption.AllDirectories))
                {
                    try { File.Delete(iniFile); deleted++; _debugLogger($"[Epic] ClearSession: deleted GameUserSettings.ini ({Path.GetDirectoryName(iniFile)?.Split('\\').LastOrDefault()})"); }
                    catch (Exception ex) { _debugLogger($"[Epic] ClearSession: failed to delete GameUserSettings.ini — {ex.Message}"); }
                }
            }

            // 4. Delete %APPDATA%\Epic\EpicOnlineServices\ (Roaming AppData — EOS auth cache)
            var appDataRoaming = Path.Combine(Path.GetDirectoryName(currentUserLocalAppData) ?? "", "Roaming");
            var eosRoamingPath = Path.Combine(appDataRoaming, "Epic", "EpicOnlineServices");
            if (Directory.Exists(eosRoamingPath))
            {
                try { Directory.Delete(eosRoamingPath, true); deleted++; _debugLogger("[Epic] ClearSession: deleted %APPDATA%\\Epic\\EpicOnlineServices\\"); }
                catch (Exception ex) { _debugLogger($"[Epic] ClearSession: failed to delete EOS roaming cache — {ex.Message}"); }
            }

            // 5. Delete %LOCALAPPDATA%\Epic Games\EpicOnlineServices\ (EOS UI helper cache — separate from launcher folder)
            var eosLocalPath = Path.Combine(currentUserLocalAppData, "Epic Games", "EpicOnlineServices");
            if (Directory.Exists(eosLocalPath))
            {
                try { Directory.Delete(eosLocalPath, true); deleted++; _debugLogger("[Epic] ClearSession: deleted %LOCALAPPDATA%\\Epic Games\\EpicOnlineServices\\"); }
                catch (Exception ex) { _debugLogger($"[Epic] ClearSession: failed to delete EOS local cache — {ex.Message}"); }
            }

            // 6. Clear last-known account ID from registry (service runs as SYSTEM — use HKU\<SID>)
            if (!string.IsNullOrEmpty(currentUserSid))
            {
                try
                {
                    using var launcherKey = Microsoft.Win32.Registry.Users.OpenSubKey(
                        $@"{currentUserSid}\Software\Epic Games\Launcher", writable: true);
                    if (launcherKey != null)
                    {
                        // Delete the AutoLoginUserName and any stored account values
                        foreach (var valueName in new[] { "AutoLoginUserName", "AccountId", "LastLoggedIn" })
                        {
                            try { launcherKey.DeleteValue(valueName, throwOnMissingValue: false); } catch { }
                        }
                        _debugLogger("[Epic] ClearSession: cleared launcher registry values");
                    }
                }
                catch (Exception ex)
                {
                    _debugLogger($"[Epic] ClearSession: registry clear failed — {ex.Message}");
                }
            }

            _debugLogger($"[Epic] ClearSession: done — {deleted} item(s) removed. epicSavedPath={epicSavedPath}");
            _logger($"Epic session cleared ({deleted} item(s) removed). Next launch will show a fresh login screen.");
        }
    }
}
