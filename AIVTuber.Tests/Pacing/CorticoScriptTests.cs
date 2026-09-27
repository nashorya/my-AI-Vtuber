using AIVTuber.Core.Cortico;

namespace AIVTuber.Tests.Pacing;

public sealed class CorticoScriptTests
{
    [Theory]
    [InlineData("<微笑>你好【点头】再见。", "你好 再见。")]
    [InlineData("【点头】", "")]
    [InlineData("  hello   <开心> world ", "hello world")]
    public void Clean_RemovesMarkupAndNormalizesWhitespace(string script, string clean) =>
        Assert.Equal(clean, CorticoScript.Clean(script));
}
