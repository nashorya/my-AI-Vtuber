using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AIVTuber.Core.Memory;

/// <summary>
/// Embedding engine using bge-small-zh ONNX model.
/// Encodes text to float vectors and computes cosine similarity.
/// </summary>
public sealed class EmbeddingEngine : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _modelDir;
    private bool _disposed;

    public EmbeddingEngine(string modelDir)
    {
        _modelDir = modelDir;
        var modelPath = Path.Combine(modelDir, "model.onnx");
        _session = new InferenceSession(modelPath);
    }

    /// <summary>
    /// Encodes text to a float embedding vector using bge-small-zh.
    /// Applies mean pooling over token embeddings and L2 normalization.
    /// </summary>
    public float[] Encode(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Simple tokenization: split on whitespace and CJK characters
        var tokens = Tokenize(text);
        if (tokens.Count == 0) tokens = ["[PAD]"];

        // Build input tensors
        var inputIds = tokens.Select(t => GetTokenId(t)).ToArray();
        var attentionMask = Enumerable.Repeat(1L, tokens.Count).ToArray();

        var dimensions = new[] { 1, tokens.Count };
        var inputTensor = new DenseTensor<long>(inputIds.Select(l => (long)l).ToArray(), dimensions);
        var maskTensor = new DenseTensor<long>(attentionMask, dimensions);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
        };

        using var results = _session.Run(inputs);
        var output = results.First().AsEnumerable<float>().ToArray();

        // output shape: [1, seq_len, hidden_size]
        // Mean pooling + L2 normalize
        return L2Normalize(MeanPool(output, tokens.Count));
    }

    /// <summary>Computes cosine similarity between two embedding vectors.</summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vectors must have same length");

        float dotProduct = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0 ? 0 : dotProduct / denominator;
    }

    /// <summary>Serializes a float array to bytes (fixed byte order for BLOB storage).</summary>
    public static byte[] EncodeToBytes(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Deserializes bytes back to float array from BLOB storage.</summary>
    public static float[] DecodeFromBytes(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static float[] MeanPool(float[] output, int seqLen)
    {
        // Output is [1, seq_len, hidden_size], we assume hidden_size = output.Length / seqLen
        int hiddenSize = output.Length / seqLen;
        var result = new float[hiddenSize];

        for (int h = 0; h < hiddenSize; h++)
        {
            float sum = 0;
            for (int s = 0; s < seqLen; s++) sum += output[s * hiddenSize + h];
            result[h] = sum / seqLen;
        }

        return result;
    }

    private static float[] L2Normalize(float[] vector)
    {
        float norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm < 1e-10f) return vector;
        return vector.Select(v => v / norm).ToArray();
    }

    /// <summary>Simple tokenizer for Chinese + English text.</summary>
    private List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ["[PAD]"];

        var tokens = new List<string> { "[CLS]" };
        var current = new System.Text.StringBuilder();

        foreach (char c in text)
        {
            if (c > 0x4E00 || char.IsPunctuation(c) || char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                if (!char.IsWhiteSpace(c)) tokens.Add(c.ToString());
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        tokens.Add("[SEP]");

        return tokens;
    }

    /// <summary>Maps a token string to an integer ID. Uses a simple hash-based approach for demo.</summary>
    private int GetTokenId(string token)
    {
        // In production, load the model's vocab.txt for proper mapping
        return Math.Abs(token.GetHashCode()) % 50000 + 1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}