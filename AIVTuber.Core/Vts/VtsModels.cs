using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVTuber.Core.Vts;

/// <summary>
/// VTS WebSocket API response (JSON-RPC style).
/// </summary>
public sealed class VtsResponse
{
    [JsonPropertyName("apiName")]
    public string? ApiName { get; set; }

    [JsonPropertyName("requestID")]
    public string? RequestId { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    [JsonPropertyName("messageType")]
    public string? MessageType { get; set; }
}

/// <summary>
/// VTS authentication token in response.
/// </summary>
public sealed class VtsAuthResponse
{
    [JsonPropertyName("authenticationToken")]
    public string? AuthenticationToken { get; set; }
}

/// <summary>
/// A single hotkey entry from VTS.
/// </summary>
public sealed class VtsHotkeyInfo
{
    [JsonPropertyName("hotkeyID")]
    public string HotkeyId { get; set; } = string.Empty;

    [JsonPropertyName("hotkeyName")]
    public string HotkeyName { get; set; } = string.Empty;
}

/// <summary>
/// Response data for HotkeysInCurrentModelRequest.
/// </summary>
public sealed class VtsHotkeyListResponse
{
    [JsonPropertyName("availableHotkeys")]
    public List<VtsHotkeyInfo>? Hotkeys { get; set; }
}