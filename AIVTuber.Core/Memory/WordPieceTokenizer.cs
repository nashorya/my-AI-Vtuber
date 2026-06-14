using System.Globalization;
using System.Text;

namespace AIVTuber.Core.Memory;

/// <summary>
/// BERT-style WordPiece tokenizer backed by a vocab.txt (one token per line, the
/// line number is the token id). Matches the bge-small-zh / bert-base-chinese
/// scheme: text is lower-cased and accent-stripped (do_lower_case = true), CJK
/// characters are emitted one token each, and remaining runs go through greedy
/// longest-match WordPiece with '##' continuation. Unknown pieces map to [UNK].
///
/// This replaces the previous hash-based placeholder, which fed random token ids to
/// the model (meaningless embeddings) and was unstable across process restarts.
/// </summary>
public sealed class WordPieceTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _unkId;
    private readonly int _clsId;
    private readonly int _sepId;
    private const int MaxInputCharsPerWord = 100;

    public WordPieceTokenizer(IReadOnlyDictionary<string, int> vocab)
    {
        _vocab = new Dictionary<string, int>(vocab.Count);
        foreach (var kv in vocab) _vocab[kv.Key] = kv.Value;
        // Standard BERT ids as fallback if the vocab somehow omits the specials.
        _unkId = _vocab.TryGetValue("[UNK]", out var u) ? u : 100;
        _clsId = _vocab.TryGetValue("[CLS]", out var c) ? c : 101;
        _sepId = _vocab.TryGetValue("[SEP]", out var s) ? s : 102;
    }

    /// <summary>Loads a vocab.txt where each line is a token and the line index is its id.</summary>
    public static WordPieceTokenizer FromFile(string vocabPath)
    {
        var vocab = new Dictionary<string, int>();
        int i = 0;
        foreach (var raw in File.ReadLines(vocabPath))
        {
            var token = raw.TrimEnd('\r', '\n');
            vocab.TryAdd(token, i);
            i++;
        }
        if (vocab.Count == 0) throw new InvalidDataException($"Empty vocab file: {vocabPath}");
        return new WordPieceTokenizer(vocab);
    }

    /// <summary>Tokenizes text into WordPiece sub-tokens (without [CLS]/[SEP]).</summary>
    public IReadOnlyList<string> Tokenize(string text)
    {
        var output = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return output;

        foreach (var basicToken in BasicTokenize(text))
        {
            if (basicToken.Length > MaxInputCharsPerWord) { output.Add("[UNK]"); continue; }

            // Greedy longest-match-first WordPiece.
            int start = 0;
            var subTokens = new List<string>();
            bool bad = false;
            while (start < basicToken.Length)
            {
                int end = basicToken.Length;
                string? cur = null;
                while (start < end)
                {
                    var piece = basicToken[start..end];
                    if (start > 0) piece = "##" + piece;
                    if (_vocab.ContainsKey(piece)) { cur = piece; break; }
                    end--;
                }
                if (cur is null) { bad = true; break; }
                subTokens.Add(cur);
                start = end;
            }
            if (bad) output.Add("[UNK]");
            else output.AddRange(subTokens);
        }
        return output;
    }

    /// <summary>Encodes text to token ids wrapped with [CLS]/[SEP], truncated to maxLen.</summary>
    public long[] Encode(string text, int maxLen = 512)
    {
        var tokens = Tokenize(text);
        int contentLimit = Math.Max(0, maxLen - 2);
        int n = Math.Min(tokens.Count, contentLimit);

        var ids = new long[n + 2];
        ids[0] = _clsId;
        for (int i = 0; i < n; i++)
            ids[i + 1] = _vocab.TryGetValue(tokens[i], out var id) ? id : _unkId;
        ids[n + 1] = _sepId;
        return ids;
    }

    /// <summary>Whitespace/punctuation split; each CJK char becomes its own token; lower-cased.</summary>
    private static IEnumerable<string> BasicTokenize(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var c in Normalize(text))
        {
            if (char.IsWhiteSpace(c)) sb.Append(' ');
            else if (IsCjk(c) || IsPunctuation(c)) sb.Append(' ').Append(c).Append(' ');
            else sb.Append(c);
        }
        return sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Lower-case and strip combining marks (do_lower_case + strip_accents).</summary>
    private static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    private static bool IsCjk(char c)
        => (c >= 0x4E00 && c <= 0x9FFF)   // CJK Unified Ideographs
        || (c >= 0x3400 && c <= 0x4DBF)   // Extension A
        || (c >= 0xF900 && c <= 0xFAFF)   // CJK Compatibility Ideographs
        || (c >= 0x3000 && c <= 0x303F);  // CJK symbols & punctuation

    private static bool IsPunctuation(char c)
    {
        // ASCII punctuation (BERT treats these as punctuation) plus Unicode categories.
        if ((c >= '!' && c <= '/') || (c >= ':' && c <= '@') ||
            (c >= '[' && c <= '`') || (c >= '{' && c <= '~')) return true;
        return char.IsPunctuation(c) || char.IsSymbol(c);
    }
}
