using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AIVTuber.Core.Memory;

/// <summary>
/// Embedding engine using the bge-small-zh ONNX model. Encodes text to float vectors
/// (real WordPiece tokenization via <see cref="WordPieceTokenizer"/>, [CLS]-token
/// pooling, L2 normalization) and computes cosine similarity.
/// </summary>
public sealed class EmbeddingEngine : IDisposable
{
    private readonly InferenceSession _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly bool _needsTokenTypeIds;
    private bool _disposed;

    /// <summary>
    /// Loads model.onnx and vocab.txt from <paramref name="modelDir"/>. Both are required;
    /// missing files throw so the caller can fall back to string similarity.
    /// </summary>
    public EmbeddingEngine(string modelDir)
    {
        var modelPath = Path.Combine(modelDir, "model.onnx");
        var vocabPath = Path.Combine(modelDir, "vocab.txt");
        if (!File.Exists(modelPath)) throw new FileNotFoundException("ONNX model not found", modelPath);
        if (!File.Exists(vocabPath)) throw new FileNotFoundException("vocab.txt not found", vocabPath);

        _tokenizer = WordPieceTokenizer.FromFile(vocabPath);
        _session = new InferenceSession(modelPath);
        _needsTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    /// <summary>
    /// Encodes text to a normalized embedding vector. Uses [CLS]-token pooling, which is
    /// what bge models are trained to use for sentence representations.
    /// </summary>
    public float[] Encode(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var ids = _tokenizer.Encode(text);
        int seqLen = ids.Length;
        var dims = new[] { 1, seqLen };

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, dims)),
            NamedOnnxValue.CreateFromTensor("attention_mask",
                new DenseTensor<long>(Enumerable.Repeat(1L, seqLen).ToArray(), dims)),
        };
        if (_needsTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(new long[seqLen], dims)));

        using var results = _session.Run(inputs);
        // First output is last_hidden_state with shape [1, seq_len, hidden_size].
        var output = results.First().AsEnumerable<float>().ToArray();

        int hiddenSize = output.Length / seqLen;
        var cls = new float[hiddenSize];
        Array.Copy(output, 0, cls, 0, hiddenSize); // [CLS] is the first token.
        return L2Normalize(cls);
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

    private static float[] L2Normalize(float[] vector)
    {
        float norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm < 1e-10f) return vector;
        return vector.Select(v => v / norm).ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
