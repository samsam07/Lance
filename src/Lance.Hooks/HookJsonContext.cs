using System.Text.Json.Serialization;

namespace Lance.Hooks;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HookFileDto))]
internal sealed partial class HookJsonContext : JsonSerializerContext { }
