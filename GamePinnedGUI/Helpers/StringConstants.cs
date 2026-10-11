namespace GamePinned.GUI.Helpers
{
    /// <summary>
    /// String constants to prevent repeated string allocations
    /// Uses string interning for frequently used values
    /// </summary>
    public static class StringConstants
    {
        // Status strings
        public static readonly string Protected = "Locked";
        public static readonly string Exempt = "Unlocked";
        public static readonly string Running = "Running";
        public static readonly string Stopped = "Stopped";
        public static readonly string Active = "Active";
        public static readonly string Free = "Free";

        // Tier display names (premium branding — never show "Subscription" or "Perpetual" in UI)
        public static readonly string TierElite = "ELITE";   // Perpetual / legacy Pro
        public static readonly string TierPro = "PRO";       // Subscription
        public static readonly string TierTrial = "TRIAL";   // 30-day free trial
        public static readonly string TierFree = "FREE";

        // Platform strings
        public static readonly string Steam = "steam";
        public static readonly string Epic = "epic";
        public static readonly string Riot = "riot";

        // Notification messages
        public static readonly string ProtectionActiveMessage = "GamePinned: Protection Active";
        public static readonly string ProtectionInactiveMessage = "GamePinned: Protection off";
    }
}
