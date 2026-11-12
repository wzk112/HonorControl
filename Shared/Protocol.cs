using System.Text.Json;

namespace HonorControl.Shared;

public sealed record NetworkConfig(bool Enabled = false, string Scene = "auto", bool LimitBackgroundUploads = false, double BackgroundUploadMbps = 8, bool SmartWifi = false, string[]? CustomPriorityApps = null, bool RecoverWifi = false);
public sealed record ControlRequest(string Action, string? Command = null, NetworkConfig? Network = null, string? InterfaceId = null);
public sealed record ControlResponse(bool Success, JsonElement? Data = null, string? Error = null);

public static class Wire
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException("Empty control response.");
}
