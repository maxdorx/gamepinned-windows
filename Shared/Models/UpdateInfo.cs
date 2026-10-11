using System;

namespace GamePinned.Shared.Models
{
    /// <summary>
    /// Update information from the update server
    /// </summary>
    public class UpdateInfo
    {
        public string Version { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public bool Required { get; set; } = false;
        public bool IsPrerelease { get; set; }
        public string ReleaseDate { get; set; } = "";
        public long FileSize { get; set; } = 0;
        public string Sha256Hash { get; set; } = "";
    }
}
