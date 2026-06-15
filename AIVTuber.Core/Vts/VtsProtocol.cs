using System.Text.Json;

namespace AIVTuber.Core.Vts;

/// <summary>
/// Builds VTube Studio Plugin API request envelopes. Per the official protocol every request is:
/// { "apiName":"VTubeStudioPublicAPI", "apiVersion":"1.0", "requestID":..., "messageType":..., "data":{...} }
/// `data` is serialized from a Dictionary so its keys stay verbatim camelCase (e.g. pluginName) —
/// no naming policy is applied. Pure function, unit-testable.
/// </summary>
internal static class VtsProtocol
{
    public const string ApiName = "VTubeStudioPublicAPI";
    public const string ApiVersion = "1.0";

    public static string BuildMessage(string messageType, string requestId, IDictionary<string, object>? data = null)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["apiName"] = ApiName,
            ["apiVersion"] = ApiVersion,
            ["requestID"] = requestId,
            ["messageType"] = messageType,
            ["data"] = data ?? new Dictionary<string, object>(),
        });

    /// <summary>data for InjectParameterDataRequest: a single parameter set to a value.</summary>
    public static Dictionary<string, object> InjectParameterData(string parameterId, float value)
        => new()
        {
            ["mode"] = "set",
            ["parameterValues"] = new[]
            {
                new Dictionary<string, object> { ["id"] = parameterId, ["value"] = value },
            },
        };
}
