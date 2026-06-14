namespace AIVTuber.Core.Config;

/// <summary>
/// Application configuration model, serialized to/from config.json.
/// </summary>
public sealed class AppConfig
{
    public AudioConfig Audio { get; set; } = new();
    public AsrConfig Asr { get; set; } = new();
    public LlmConfig Llm { get; set; } = new();
    public TtsConfig Tts { get; set; } = new();
    public VtsConfig Vts { get; set; } = new();
    public ObsConfig Obs { get; set; } = new();
    public MemoryConfig Memory { get; set; } = new();
    public BilibiliConfig Bilibili { get; set; } = new();
}

public sealed class AudioConfig
{
    public int InputDeviceIndex { get; set; } = 0;
    public bool UseLoopback { get; set; } = false;
    public string LoopbackDeviceName { get; set; } = string.Empty;

    // VAD parameters
    public int VadAggressiveness { get; set; } = 2; // 0-3
    public int PreSpeechPaddingMs { get; set; } = 200;
    public int PostSpeechSilenceMs { get; set; } = 500;
}

public sealed class AsrConfig
{
    public string Provider { get; set; } = "aliyun";
    public string ApiKey { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
}

public sealed class LlmConfig
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "deepseek-chat";
    public string SystemPrompt { get; set; } = "你是一个VTuber...";
    public int MaxHistoryTokens { get; set; } = 4096;
}

public sealed class TtsConfig
{
    public string Provider { get; set; } = "fish-audio";
    public string ApiKey { get; set; } = string.Empty;
    public string VoiceId { get; set; } = string.Empty;
}

public sealed class VtsConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 8001;
    public float MouthScale { get; set; } = 1.5f;
    public Dictionary<string, string> EmotionMap { get; set; } = new();
}

public sealed class ObsConfig
{
    public bool Enable { get; set; } = false;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 4455;
    public string Password { get; set; } = string.Empty;
    public string AssistantTextComponent { get; set; } = "AssistantText";
    public string UserTextComponent { get; set; } = "UserText";
    /// <summary>Typewriter effect interval in milliseconds per character. 0 = instant.</summary>
    public int TypewriterIntervalMs { get; set; } = 50;
}

public sealed class MemoryConfig
{
    public string DatabasePath { get; set; } = "memory.db";
    public int ExtractEveryNTurns { get; set; } = 5;
    /// <summary>Path to the bge-small-zh ONNX model directory.</summary>
    public string EmbeddingModelPath { get; set; } = "models/bge-small-zh";
}

public sealed class BilibiliConfig
{
    public bool Enable { get; set; } = false;
    public int RoomId { get; set; } = 0;
    public string Sessdata { get; set; } = string.Empty;
    public string BiliJct { get; set; } = string.Empty;
    public string Buvid3 { get; set; } = string.Empty;
    /// <summary>Push port for the local HTTP endpoint receiving danmaku from Python bridge.</summary>
    public int PushPort { get; set; } = 19876;
    /// <summary>Seconds between danmaku selections (avoid over-replying).</summary>
    public int SelectionIntervalSec { get; set; } = 8;
    /// <summary>Python executable path. Defaults to "python".</summary>
    public string PythonPath { get; set; } = "python";
}