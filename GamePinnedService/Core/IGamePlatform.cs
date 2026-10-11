using System;
using System.Collections.Generic;
using GamePinned.Shared.Models;

namespace GamePinned.Service.Core
{
    /// <summary>
    /// Interface for game platform providers (Steam, Epic Games, Riot, etc.)
    /// Implement this interface to add support for a new platform
    /// </summary>
    public interface IGamePlatform
    {
        /// <summary>
        /// Platform name (e.g., "Steam", "Epic Games", "Riot Client")
        /// </summary>
        string PlatformName { get; }

        /// <summary>
        /// Platform identifier for config (e.g., "steam", "epic", "riot")
        /// </summary>
        string PlatformId { get; }

        /// <summary>
        /// Check if this platform is installed on the system
        /// </summary>
        bool IsInstalled();

        /// <summary>
        /// Detect all library paths for this platform
        /// For Steam: all steamapps folders across drives
        /// For Epic: Unreal Engine vault locations
        /// For Riot: Riot Games installation folders
        /// </summary>
        List<string> DetectLibraryPaths();

        /// <summary>
        /// Get all installed games from a specific library path
        /// </summary>
        List<GameInfo> GetInstalledGames(string libraryPath);

        /// <summary>
        /// Apply deny-delete protection to a specific game
        /// </summary>
        /// <param name="game">Game to protect</param>
        /// <param name="blockedUsers">List of Windows usernames to block</param>
        /// <param name="blockAllNonAdminByDefault">If true, blocks all members of the Windows Users group (S-1-5-32-545) when blockedUsers is empty. This includes any admin accounts in that group.</param>
        void ApplyProtection(GameInfo game, List<string> blockedUsers, bool blockAllNonAdminByDefault);

        /// <summary>
        /// Remove protection from a specific game
        /// </summary>
        void RemoveProtection(GameInfo game, List<string> blockedUsers);

        /// <summary>
        /// Removes explicit delete-deny residue from each game's install path (and manifest, if
        /// present), plus the platform's own shared root path if one exists (Riot only). Launcher,
        /// vendor, and administrator allow ACEs, inheritance, and ownership must be preserved;
        /// icacls /reset is forbidden because it means "inherit from the parent," not "restore the
        /// launcher's defaults." Deliberately separate from RemoveProtection (which stays fast and
        /// identity-targeted for routine maintenance-mode/config calls). Call only at lifecycle
        /// transition points: uninstall and a detected version change on startup. See Critical #59.
        /// </summary>
        /// <param name="games">Games to clean. May be empty. Platforms with a shared root path
        /// (Riot) must still clean that root even when this list is empty.</param>
        void ResetToDefault(List<GameInfo> games);

        /// <summary>
        /// Backup manifest files for this platform
        /// </summary>
        /// <param name="libraryPath">Library path to backup</param>
        /// <param name="excludedGames">List of game names to EXCLUDE from backup (and cleanup from existing backup)</param>
        void BackupManifests(string libraryPath, List<string> excludedGames);

        /// <summary>
        /// Restore manifest files from backup
        /// </summary>
        /// <param name="libraryPath">Library path to restore to</param>
        /// <param name="excludedGames">List of game names to EXCLUDE from restoration</param>
        void RestoreManifests(string libraryPath, List<string> excludedGames);

        /// <summary>
        /// Restore only the manifest belonging to a single uninstall callback. Platforms that
        /// cannot address one manifest may fall back to their existing library restore, but
        /// Steam and Riot override this because restoring unrelated backups can resurrect
        /// games that were not part of the triggering recovery; Riot must also preserve its
        /// product-specific nested metadata path.
        /// </summary>
        void RestoreGameManifest(GameInfo game, string libraryPath, List<string> excludedGames)
            => RestoreManifests(libraryPath, excludedGames);

        /// <summary>
        /// Kill the platform launcher process
        /// </summary>
        void KillLauncher();

        /// <summary>
        /// Start the platform launcher as the current logged-in user (not admin)
        /// </summary>
        void StartLauncher();

        /// <summary>
        /// Start watching for uninstall attempts (manifest file deletions)
        /// </summary>
        /// <param name="onUninstallDetected">Callback when uninstall detected</param>
        void StartUninstallWatcher(Action<GameInfo> onUninstallDetected);

        /// <summary>
        /// Start watching for newly installed games
        /// </summary>
        /// <param name="onNewGameDetected">Callback when new game detected</param>
        void StartNewGameWatcher(Action<GameInfo> onNewGameDetected);

        /// <summary>
        /// Stop all file watchers.
        /// </summary>
        /// <param name="releaseHandles">
        /// When true, also release any platform-held resources (e.g. Riot's no-delete-share file
        /// handles on game exes/dlls). Pass true ONLY on full service shutdown. Pass false (default)
        /// for the transient stop+start cycle inside UpdateConfig / ToggleMaintenance, otherwise
        /// Layer 2 of Riot protection (Critical #53) is silently disabled after every PushConfig.
        /// </param>
        void StopWatchers(bool releaseHandles = false);

        /// <summary>
        /// Perform periodic health check (verify protection is still active)
        /// </summary>
        /// <param name="blockAllNonAdminByDefault">Default protection policy</param>
        void HealthCheck(List<GameInfo> protectedGames, List<string> blockedUsers, bool blockAllNonAdminByDefault);
        
        /// <summary>
        /// Check if a specific path is currently protected by NTFS deny rules
        /// </summary>
        bool CheckProtectionActive(string path);

        /// <summary>
        /// Check if a game folder is "stable" (not being mutated by launcher)
        /// </summary>
        bool IsGameStable(string installPath);

        /// <summary>
        /// Returns true if the platform is currently applying an update.
        /// When true, ApplyGameProtection skips this platform's games so the
        /// patcher is not blocked by re-applied deny rules from PushConfig,
        /// maintenance mode toggle, or any other ApplyProtection trigger.
        /// Default implementation returns false — only RiotPlatform overrides this.
        /// </summary>
        bool IsUpdateInProgress() => false;

        /// <summary>
        /// Clear all login/session data for this platform so the next launch shows a fresh login screen.
        /// Kills the launcher, deletes session tokens and user data files.
        /// </summary>
        /// <param name="currentUserLocalAppData">%LOCALAPPDATA% of the currently logged-in Windows user</param>
        /// <param name="currentUserSid">SID of the currently logged-in Windows user (used for registry writes)</param>
        void ClearSession(string currentUserLocalAppData, string? currentUserSid);
    }
}
