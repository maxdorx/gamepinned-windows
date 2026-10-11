using System;
using System.IO;
using System.Text.Json;
using GamePinned.Shared.Models;

namespace GamePinned.Shared
{
    /// <summary>
    /// Performance optimization manager for caching expensive operations
    /// Caches hardware ID to avoid repeated expensive generation
    /// </summary>
    public static class CacheManager
    {
        private static string? _cachedHardwareId;
        private static DateTime _hwIdGenerated = DateTime.MinValue;
        private static readonly object _hwIdLock = new object();

        private const int HW_ID_CACHE_DAYS = 30;
        private static readonly string CACHE_FILE = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GamePinned", "cache.json");

        /// <summary>
        /// Get cached hardware ID or generate new one if expired
        /// </summary>
        public static string GetHardwareId()
        {
            lock (_hwIdLock)
            {
                // Use cached value if present and not expired
                if (!string.IsNullOrEmpty(_cachedHardwareId) && DateTime.Now <= _hwIdGenerated.AddDays(HW_ID_CACHE_DAYS))
                    return _cachedHardwareId;

                var generated = HardwareFingerprint.Generate();
                if (generated != null)
                {
                    _cachedHardwareId = generated;
                    _hwIdGenerated = DateTime.Now;
                    SaveCache();
                }
                // If generation failed, do NOT update _hwIdGenerated so the next call retries immediately.
                return _cachedHardwareId ?? "";
            }
        }

        /// <summary>
        /// Invalidate all caches (call after major configuration changes)
        /// </summary>
        public static void InvalidateAll()
        {
            lock (_hwIdLock)
            {
                _cachedHardwareId = null;
                _hwIdGenerated = DateTime.MinValue;
            }
            
            SaveCache();
        }

        /// <summary>
        /// Load cache from disk on startup
        /// </summary>
        public static void LoadCache()
        {
            try
            {
                if (!File.Exists(CACHE_FILE)) return;

                var json = File.ReadAllText(CACHE_FILE);
                var cache = JsonSerializer.Deserialize(json, GamePinnedJsonContext.Default.CacheData);
                
                if (cache != null)
                {
                    lock (_hwIdLock)
                    {
                        if (!string.IsNullOrEmpty(cache.HardwareId) && 
                            DateTime.Now < cache.HwIdGenerated.AddDays(HW_ID_CACHE_DAYS))
                        {
                            _cachedHardwareId = cache.HardwareId;
                            _hwIdGenerated = cache.HwIdGenerated;
                        }
                    }
                }
            }
            catch
            {
                // Cache corruption - start fresh
                InvalidateAll();
            }
        }

        /// <summary>
        /// Save cache to disk
        /// </summary>
        private static void SaveCache()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CACHE_FILE)!);
                
                var cacheData = new CacheData
                {
                    HardwareId = _cachedHardwareId,
                    HwIdGenerated = _hwIdGenerated
                };

                var json = JsonSerializer.Serialize(cacheData, GamePinnedJsonContext.Default.CacheData);
                File.WriteAllText(CACHE_FILE, json);
            }
            catch
            {
                // Ignore cache save failures - non-critical
            }
        }
    }

    /// <summary>
    /// License information cache structure
    /// </summary>
    public class LicenseInfo
    {
        public string HardwareId { get; set; } = "";
        public bool IsValid { get; set; }
        public string LicenseType { get; set; } = "Free";
        public DateTime? ExpiryDate { get; set; }
    }

    /// <summary>
    /// Cache data structure for JSON serialization
    /// </summary>
    public class CacheData
    {
        public string? HardwareId { get; set; }
        public DateTime HwIdGenerated { get; set; }
    }
}
