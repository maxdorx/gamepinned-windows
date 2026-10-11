using System.Collections.Generic;

namespace GamePinned.Shared.Models
{
    /// <summary>
    /// Platform-specific configuration
    /// </summary>
    public class PlatformConfig
    {
        /// <summary>
        /// Platform identifier ("steam", "epic", "riot")
        /// </summary>
        public string PlatformId { get; set; } = "";

        /// <summary>
        /// Whether protection is enabled for this platform
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Library paths for this platform
        /// Steam: steamapps folders
        /// Epic: Unreal vault folders
        /// Riot: Riot Games folders
        /// </summary>
        public List<string> LibraryPaths { get; set; } = new();

        /// <summary>
        /// Games to exclude from protection on this platform
        /// </summary>
        public List<string> ExcludedGames { get; set; } = new();

        /// <summary>
        /// Games explicitly selected for protection by user
        /// If empty, no games are protected by default (user must manually select)
        /// FREE tier: Only 1 game can be in this list
        /// PAID tier: Unlimited games can be in this list
        /// </summary>
        public List<string> ProtectedGames { get; set; } = new();

    }
}
