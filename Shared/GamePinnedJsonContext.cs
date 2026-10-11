using System.Text.Json.Serialization;
using GamePinned.Shared.Models;

namespace GamePinned.Shared
{
    [JsonSerializable(typeof(Config))]
    [JsonSerializable(typeof(UpdateInfo))]
    [JsonSerializable(typeof(CacheData))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    public partial class GamePinnedJsonContext : JsonSerializerContext
    {
    }
}
