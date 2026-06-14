using AIVTuber.Core.Memory;

namespace AIVTuber.Tests;

public class WordPieceTokenizerTests
{
    private static WordPieceTokenizer Build() => new(new Dictionary<string, int>
    {
        ["[PAD]"] = 0,
        ["[UNK]"] = 100,
        ["[CLS]"] = 101,
        ["[SEP]"] = 102,
        ["你"] = 200,
        ["好"] = 201,
        ["世"] = 202,
        ["界"] = 203,
        ["play"] = 300,
        ["##ing"] = 301,
        ["?"] = 302,
    });

    [Fact]
    public void Tokenize_CjkCharactersOnePerToken()
    {
        Assert.Equal(new[] { "你", "好", "世", "界" }, Build().Tokenize("你好世界"));
    }

    [Fact]
    public void Tokenize_WordPieceSplitsKnownSubwords()
    {
        Assert.Equal(new[] { "play", "##ing" }, Build().Tokenize("playing"));
    }

    [Fact]
    public void Tokenize_LowercasesInput()
    {
        // do_lower_case: "PLAYING" must tokenize the same as "playing".
        Assert.Equal(new[] { "play", "##ing" }, Build().Tokenize("PLAYING"));
    }

    [Fact]
    public void Tokenize_UnknownWordMapsToUnk()
    {
        Assert.Equal(new[] { "[UNK]" }, Build().Tokenize("xyz"));
    }

    [Fact]
    public void Tokenize_SplitsPunctuation()
    {
        Assert.Equal(new[] { "你", "好", "?" }, Build().Tokenize("你好?"));
    }

    [Fact]
    public void Encode_WrapsWithClsAndSep()
    {
        Assert.Equal(new long[] { 101, 200, 201, 102 }, Build().Encode("你好"));
    }

    [Fact]
    public void Encode_EmptyText_IsClsSepOnly()
    {
        Assert.Equal(new long[] { 101, 102 }, Build().Encode(""));
    }

    [Fact]
    public void Encode_IsStableAcrossInstances()
    {
        // Unlike the old GetHashCode-based ids, the same text always maps to the same ids.
        Assert.Equal(Build().Encode("你好世界"), Build().Encode("你好世界"));
    }
}
