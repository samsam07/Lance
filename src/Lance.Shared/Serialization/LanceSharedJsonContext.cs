using System.Text.Json.Serialization;
using Lance.Shared.Dtos;

namespace Lance.Shared.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AllocateRequest))]
[JsonSerializable(typeof(SlotDto))]
[JsonSerializable(typeof(SlotsResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(ConfigUrlResponse))]
public sealed partial class LanceSharedJsonContext : JsonSerializerContext { }
