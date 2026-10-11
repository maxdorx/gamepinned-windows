using System;
using System.Security.Cryptography;
using System.Text;
using GamePinned.Shared;

namespace GamePinned.Service.Core
{
    /// <summary>
    /// License validation result status
    /// </summary>
    public enum LicenseStatus
    {
        Active,              // Valid license, hardware matches, not expired
        HardwareMismatch,    // Valid license but hardware changed
        Expired,             // Subscription license past expiry date
        Invalid,             // Signature mismatch or corrupt license
        Free                 // No license key provided
    }

    /// <summary>
    /// Internal licensing logic that remains secure inside the Native AOT binary.
    /// This contains the HMAC secret key which is NOT present in the Shared or GUI projects.
    /// </summary>
    public static class LicenseManagerInternal
    {
        // CRITICAL: This secret key is now ONLY in the Service (AOT) and Generator.
        private const string SECRET_KEY = "REPLACE_WITH_YOUR_SIGNING_KEY";

        public static bool ValidateLicense(string licenseKey, string hardwareId)
        {
            return ValidateLicenseDetailed(licenseKey, hardwareId) == LicenseStatus.Active;
        }

        public static LicenseStatus ValidateLicenseDetailed(string licenseKey, string hardwareId)
        {
            if (string.IsNullOrWhiteSpace(licenseKey)) return LicenseStatus.Free;

            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(licenseKey.Trim()));
                var parts = decoded.Split('|');
                if (parts.Length < 4) return LicenseStatus.Invalid;

                // The last 3 parts are ALWAYS Type, Expiry, and Signature
                // Everything before them is the multi-part Hardware ID
                var signature = parts[parts.Length - 1];
                var expiry = parts[parts.Length - 2];
                var licenseType = parts[parts.Length - 3];
                var licenseHwId = string.Join("|", parts.Take(parts.Length - 3));

                // 1. Verify signature with the PRIVATE key
                var expectedSig = ComputeSignature(licenseHwId, licenseType, expiry);
                if (signature != expectedSig) return LicenseStatus.Invalid;

                // 2. Perform Fuzzy Hardware Matching
                if (!LicenseManager.IsHardwareMatch(licenseHwId, hardwareId))
                    return LicenseStatus.HardwareMismatch;

                // 3. Expiry check
                if (licenseType == "SUBSCRIPTION" || licenseType == "TRIAL")
                {
                    if (!DateTime.TryParse(expiry, out var expiryDate)) return LicenseStatus.Invalid;
                    if (DateTime.Now > expiryDate) return LicenseStatus.Expired;
                }

                return LicenseStatus.Active;
            }
            catch { return LicenseStatus.Invalid; }
        }

        public static string ComputeSignature(string hwId, string type, string expiry)
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SECRET_KEY)))
            {
                var data = Encoding.UTF8.GetBytes($"{hwId}{type}{expiry}");
                var hash = hmac.ComputeHash(data);
                return Convert.ToBase64String(hash);
            }
        }

        public static string GetLicenseTier(string licenseKey)
        {
            if (string.IsNullOrWhiteSpace(licenseKey)) return "FREE";

            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(licenseKey.Trim()));
                var parts = decoded.Split('|');
                if (parts.Length < 4) return "FREE";

                // The license type is the third-to-last part
                var licenseType = parts[parts.Length - 3];
                return licenseType ?? "FREE";
            }
            catch
            {
                return "FREE";
            }
        }
    }
}
