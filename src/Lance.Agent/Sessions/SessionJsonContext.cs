using System.Text.Json.Serialization;

namespace Lance.Agent.Sessions;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionRecord))]
internal sealed partial class SessionJsonContext : JsonSerializerContext { }
