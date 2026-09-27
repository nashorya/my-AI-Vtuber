using System.Text.RegularExpressions;

namespace AIVTuber.Core.Cortico;

/// <summary>Local mirror of the upstream parser's text rules: 【…】 and &lt;…&gt; are markup, not speech.
/// Square voice tags never reach here (<see cref="CorticoReplyAdapter.Sanitize"/> strips them).</summary>
internal static class CorticoScript
{
    private static readonly Regex Markup = new(@"【[^】]*】|<[^>\r\n]{0,32}>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static string Clean(string script) => Normalize(Markup.Replace(script, " "));

    public static string Normalize(string text) => Spaces.Replace(text, " ").Trim();
}
