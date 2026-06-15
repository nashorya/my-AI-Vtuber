using AIVTuber.Core.Vts;

namespace AIVTuber.Tests;

public class VtsProtocolTests
{
    [Fact]
    public void BuildMessage_HasCorrectEnvelope()
    {
        var json = VtsProtocol.BuildMessage("AuthenticationTokenRequest", "req-1", new Dictionary<string, object>
        {
            ["pluginName"] = "AIVTuber",
            ["pluginDeveloper"] = "AIVTuberDev",
        });

        Assert.Contains("\"apiName\":\"VTubeStudioPublicAPI\"", json);
        Assert.Contains("\"apiVersion\":\"1.0\"", json);
        Assert.Contains("\"messageType\":\"AuthenticationTokenRequest\"", json);
        Assert.Contains("\"requestID\":\"req-1\"", json);
        // data keys must stay verbatim camelCase (no snake_case mangling)
        Assert.Contains("\"pluginName\":\"AIVTuber\"", json);
        Assert.DoesNotContain("plugin_name", json);
        Assert.DoesNotContain("api_name", json);
    }

    [Fact]
    public void BuildMessage_EmptyDataSerializesEmptyObject()
    {
        var json = VtsProtocol.BuildMessage("HotkeysInCurrentModelRequest", "r2");
        Assert.Contains("\"data\":{}", json);
    }

    [Fact]
    public void InjectParameterData_UsesModeAndParameterValuesArray()
    {
        var data = VtsProtocol.InjectParameterData("ParamMouthOpenY", 0.7f);
        Assert.Equal("set", data["mode"]);

        var json = VtsProtocol.BuildMessage("InjectParameterDataRequest", "r3", data);
        Assert.Contains("\"mode\":\"set\"", json);
        Assert.Contains("\"parameterValues\":[", json);
        Assert.Contains("\"id\":\"ParamMouthOpenY\"", json);
        Assert.Contains("\"value\":0.7", json);
        // the old wrong fields must not appear
        Assert.DoesNotContain("parameterId", json);
        Assert.DoesNotContain("injectionMode", json);
    }
}
