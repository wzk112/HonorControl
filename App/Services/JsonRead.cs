using System.Text.Json;
namespace HonorControl;
internal static class JsonRead
{
    public static string Text(this JsonElement element, string property, string fallback = "—")
    {
        string text = element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString() : fallback;
        return property is "Note" or "Error" or "MicStatus" or "WifiNote" or "ConstraintReason" or "SceneSource" ? L.T(text) : text;
    }
    public static int Number(this JsonElement element, string property, int fallback = -1) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : fallback;
}
