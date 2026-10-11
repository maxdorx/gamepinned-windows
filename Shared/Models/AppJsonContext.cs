using System.Text.Json.Serialization;

namespace GamePinned.Shared.Models
{
    /// <summary>
    /// JSON serialization context for Native AOT compatibility
    /// Required because PublishAot=true disables reflection
    /// </summary>
    [JsonSerializable(typeof(RefreshData))]
    [JsonSerializable(typeof(GameInfo))]
    [JsonSerializable(typeof(LicenseInfo))]
    [JsonSerializable(typeof(DashboardStats))]
    [JsonSerializable(typeof(NvidiaConfigInfo))]
    [JsonSerializable(typeof(GpuInfo))]
    [JsonSerializable(typeof(NvidiaMeta))]
    [JsonSerializable(typeof(DisplayMonitorConfig))]
    [JsonSerializable(typeof(List<DisplayMonitorConfig>))]
    [JsonSerializable(typeof(NvidiaProfile))]
    [JsonSerializable(typeof(NvidiaProfilesIndex))]
    [JsonSerializable(typeof(List<NvidiaProfile>))]
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    public partial class AppJsonContext : JsonSerializerContext
    {
    }
}
