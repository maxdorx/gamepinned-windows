using System;
using System.Collections.Generic;

namespace GamePinned.Shared
{
    public static class PipeConstants
    {
        public const string PIPE_NAME = "GamePinnedServicePipe";

        /// <summary>
        /// Pipe protocol version. Increment when adding or changing commands.
        /// GUI checks this on connect — version mismatch means one side needs to update.
        /// </summary>
        public const int PROTOCOL_VERSION = 7;
    }

    public enum PipeCommand
    {
        GetStatus,
        GetAllData, // New batch command for performance optimization
        ToggleMaintenance,
        RefreshProtection,
        ActivateLicense,
        PushConfig,
        RestartService,
        ToggleStartup,
        ClearLogs,
        CheckForUpdate, // New command for automatic updates
        SaveNvidiaConfig,      // Save current NVIDIA DRS files as a new named profile
        GetNvidiaStatus,       // Get NVIDIA profile list + GPU info
        RestoreNvidiaConfig,   // Restore the active (auto-restore) profile
        ClearNvidiaConfig,     // Delete ALL saved profiles
        RenameNvidiaProfile,   // Rename a profile  — data: "profileId|newName"
        DeleteNvidiaProfile,   // Delete a single profile — data: profileId
        SetActiveNvidiaProfile,// Set the auto-restore target — data: profileId
        RestoreNvidiaProfile,  // Restore a specific profile by ID — data: profileId
        SetNvidiaAutoRestore,  // Enable/disable auto-restore on startup — data: "true" or "false"
        ClearPlatformSession,  // Clear login/session data for a platform — data: platformId ("steam", "epic", "riot")
        GetDiagnosticLogs      // Read last 2 debug_*.log files as SYSTEM and return raw content (encrypted blobs)
    }

    public enum ServiceNotificationType
    {
        UninstallBlocked,
        NewGameDetected,
        ProtectionApplied,
        ProtectionRemoved,
        MaintenanceToggled,
        LicenseExpiring,
        ServiceStarted,
        ServiceStopped,
        UpdateAvailable,
        Info, // Added for generic notifications like Steam Restart
        ProtectionProgress // Fired per-game during ApplyProtection/RemoveProtection; title = "Locking/Unlocking GameName (n/total)"
    }

    public static class PipeMessage
    {
        public static string CreateCommand(PipeCommand cmd, string data = "")
        {
            return $"CMD|{cmd}|{data}";
        }

        public static string CreateResponse(string status, string data = "")
        {
            return $"RSP|{status}|{data}";
        }

        public static string CreateNotification(ServiceNotificationType type, string title, string message)
        {
            return $"NTF|{type}|{title}|{message}";
        }

        public static (string type, string[] parts) Parse(string raw)
        {
            // Limit split to 3 parts: TYPE|COMMAND|DATA
            // This preserves any pipes inside the DATA payload
            var parts = raw.Split('|', 3);
            if (parts.Length < 2) return ("INVALID", parts);
            return (parts[0], parts);
        }

        public static (string status, string data) ParseResponse(string raw)
        {
            // Limit split to 3 parts: RSP|STATUS|DATA
            var parts = raw.Split('|', 3);
            if (parts[0] != "RSP" || parts.Length < 2) return ("ERROR", "Invalid response");
            return (parts[1], parts.Length > 2 ? parts[2] : "");
        }
    }
}
