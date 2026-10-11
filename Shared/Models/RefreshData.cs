using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GamePinned.Shared.Models
{
    /// <summary>
    /// Batch data structure for efficient GUI refresh operations
    /// Contains all data needed for a complete UI refresh in single service call
    /// </summary>
    public class RefreshData
    {
        /// <summary>
        /// List of all protected games across platforms
        /// </summary>
        public List<GameInfo> Games { get; set; } = new();

        /// <summary>
        /// License information including validity and type
        /// </summary>
        public LicenseInfo LicenseInfo { get; set; } = new();

        /// <summary>
        /// Current service status
        /// </summary>
        public bool ServiceRunning { get; set; }

        /// <summary>
        /// List of blocked users
        /// </summary>
        public List<string> BlockedUsers { get; set; } = new();

        /// <summary>
        /// Current maintenance mode status
        /// </summary>
        public bool MaintenanceMode { get; set; }

        /// <summary>
        /// Auto-update enabled status
        /// </summary>
        public bool AutoUpdateEnabled { get; set; }

        /// <summary>
        /// Whether GamePinned is set to start with Windows
        /// </summary>
        public bool StartWithWindows { get; set; }

        /// <summary>
        /// Whether an NVIDIA GPU is detected on this machine
        /// </summary>
        public bool NvidiaPresent { get; set; }

        /// <summary>
        /// Whether NVIDIA profile auto-restore on startup is enabled
        /// </summary>
        public bool NvidiaAutoRestore { get; set; } = true;

        /// <summary>
        /// Recent log entries (last 100 lines)
        /// </summary>
        public List<string> RecentLogs { get; set; } = new();

        /// <summary>
        /// Pipe protocol version from the service. GUI compares this against
        /// PipeConstants.PROTOCOL_VERSION — a mismatch means service and GUI are
        /// on different versions and the user should restart to finish updating.
        /// </summary>
        public int ProtocolVersion { get; set; }

        /// <summary>
        /// Statistics for dashboard
        /// </summary>
        public DashboardStats Stats { get; set; } = new();

        /// <summary>
        /// Whether all members of the Windows Users group (S-1-5-32-545) are blocked by default (when BlockedUsers list is empty).
        /// Any account in that group is blocked — including admin accounts that are also members of it.
        /// </summary>
        public bool BlockAllNonAdminByDefault { get; set; } = true;
    }

    /// <summary>
    /// Dashboard statistics for quick display
    /// </summary>
    public class DashboardStats
    {
        public int ProtectedGamesCount { get; set; }
        public int TotalGamesCount { get; set; }
        public int BlockedUsersCount { get; set; }
        public int ActivePlatformsCount { get; set; }
    }
}
