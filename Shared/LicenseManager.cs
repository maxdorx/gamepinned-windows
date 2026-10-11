using System;
using System.Text;

namespace GamePinned.Shared
{
    /// <summary>
    /// Handles public license parsing and hardware matching.
    /// CRITICAL: Sensitive validation and SECRET_KEY have been moved to the Native AOT Service.
    /// This file is part of the Shared project and can be decompiled.
    /// </summary>
    public static class LicenseManager
    {
        /// <summary>
        /// Check if two hardware IDs match (using 2-out-of-3 fuzzy logic)
        /// </summary>
        public static bool IsHardwareMatch(string id1, string id2)
        {
            if (id1 == id2) return true; // Direct match (legacy or exact)
            if (string.IsNullOrEmpty(id1) || string.IsNullOrEmpty(id2)) return false;

            var parts1 = id1.Split('|');
            var parts2 = id2.Split('|');

            // If one is not multi-part, fallback to strict match
            if (parts1.Length < 3 || parts2.Length < 3) return id1 == id2;

            int matches = 0;
            if (parts1[0] == parts2[0]) matches++;
            if (parts1[1] == parts2[1]) matches++;
            if (parts1[2] == parts2[2]) matches++;

            return matches >= 2;
        }

        /// <summary>
        /// Get human-readable license information (Fast parsing)
        /// </summary>
        public static string GetLicenseInfo(string licenseKey)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(licenseKey)) return "Free";

                // New format: base64(json).base64(signature)
                if (licenseKey.Contains("."))
                {
                    try
                    {
                        var blob = licenseKey.Split('.')[0];
                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(blob));
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var modernType = doc.RootElement.GetProperty("type").GetString() ?? "PERPETUAL";

                        if (modernType == "SUBSCRIPTION") return "Subscription";
                        if (modernType == "PERPETUAL") return "Perpetual";
                        if (modernType == "TRIAL") return "Trial";
                        if (modernType == "FREE") return "Free";
                        return modernType;
                    }
                    catch { return "Free"; } // Parsing failed — display conservatively as Free; actual access gating is in the service
                }

                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(licenseKey.Trim()));
                var parts = decoded.Split('|');
                if (parts.Length < 4) return "Free";

                var legacyType = parts[parts.Length - 3];
                var legacyExpiry = parts[parts.Length - 2];

                if (legacyType == "PERPETUAL") return "Perpetual";
                if (legacyType == "SUBSCRIPTION") return "Subscription";
                if (legacyType == "TRIAL") return "Trial";
                if (legacyType == "FREE") return "Free";
                return "Free";
            }
            catch { return "Free"; }
        }

        /// <summary>
        /// Get license expiry date, or null if perpetual/invalid
        /// </summary>
        public static DateTime? GetExpiryDate(string licenseKey)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(licenseKey)) return null;

                // New format: base64(json).base64(signature)
                if (licenseKey.Contains("."))
                {
                    try
                    {
                        var blob = licenseKey.Split('.')[0];
                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(blob));
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("expiry", out var modernExp) && DateTime.TryParse(modernExp.GetString(), out var modernDate))
                            return modernDate;
                        return null;
                    }
                    catch { return null; }
                }

                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(licenseKey.Trim()));
                var parts = decoded.Split('|');
                if (parts.Length < 4) return null;

                var legacyType = parts[parts.Length - 3];
                var legacyExpiry = parts[parts.Length - 2];

                if ((legacyType == "SUBSCRIPTION" || legacyType == "TRIAL") && DateTime.TryParse(legacyExpiry, out var legacyDate))
                    return legacyDate;

                return null;
            }
            catch { return null; }
        }
    }
}
