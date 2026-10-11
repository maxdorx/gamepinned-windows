using System;

namespace GamePinned.Shared.Models
{
    /// <summary>
    /// Represents a game from any platform (Steam, Epic, Riot, etc.)
    /// </summary>
    public class GameInfo
    {
        /// <summary>
        /// Platform this game belongs to (e.g., "steam", "epic", "riot")
        /// </summary>
        public string PlatformId { get; set; } = "";

        /// <summary>
        /// Display name of the game
        /// </summary>
        public string GameName { get; set; } = "";

        /// <summary>
        /// Full path to the game installation folder
        /// </summary>
        public string InstallPath { get; set; } = "";

        /// <summary>
        /// Path to the manifest file (.acf for Steam, .item for Epic, etc.)
        /// </summary>
        public string ManifestPath { get; set; } = "";

        /// <summary>
        /// Platform-specific game ID (AppID for Steam, etc.)
        /// </summary>
        public string GameId { get; set; } = "";

        /// <summary>
        /// Whether this game should be protected
        /// </summary>
        public bool IsProtected { get; set; } = true;

        /// <summary>
        /// Whether this game is currently excluded from protection
        /// </summary>
        public bool IsExcluded { get; set; } = false;

        public override string ToString()
        {
            return $"{GameName} ({PlatformId})";
        }

        public override bool Equals(object? obj)
        {
            if (obj is GameInfo other)
            {
                return PlatformId == other.PlatformId &&
                       GameId == other.GameId &&
                       InstallPath == other.InstallPath;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(PlatformId, GameId, InstallPath);
        }
    }
}
