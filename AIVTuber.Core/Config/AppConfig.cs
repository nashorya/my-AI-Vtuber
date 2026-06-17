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
    public InputTemplateConfig Input { get; set; } = new();
}

public sealed class AudioConfig
{
    public int InputDeviceIndex { get; set; } = 0;
    public bool UseLoopback { get; set; } = false;
    public string LoopbackDeviceName { get; set; } = string.Empty;
    /// <summary>When true, simultaneously captures system audio as a second channel.
    /// Detected speech is transcribed and injected as "对面说：..." context for the LLM.</summary>
    public bool EnableLoopbackListen { get; set; } = false;
    /// <summary>Process name to capture (e.g. "chrome", "msedge", "obs64").
    /// When set, uses the Windows 11 per-process loopback API — no virtual sound card needed.
    /// When empty, captures the entire speaker mix (whole-system loopback).</summary>
    public string LoopbackProcessName { get; set; } = string.Empty;
    /// <summary>When true, mixes mic + AI TTS and writes to <see cref="VirtualMicDeviceName"/>
    /// so streaming software can pick up both voices from a single virtual microphone input.</summary>
    public bool EnableVirtualMic { get; set; } = false;
    /// <summary>Friendly name of the render device to write the mix into (e.g. "CABLE Input (VB-Audio Virtual Cable)").
    /// Empty = first available render device.</summary>
    public string VirtualMicDeviceName { get; set; } = string.Empty;

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
    /// <summary>Model name. Provider-specific; empty = the provider's default
    /// (e.g. aliyun/dashscope → paraformer-realtime-v2).</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>Base URL of the local ASR HTTP service. Only used when Provider = "local".</summary>
    public string LocalAsrUrl { get; set; } = "http://localhost:8765";
    /// <summary>Python executable used to launch asr_server.py. Defaults to "python".</summary>
    public string PythonPath { get; set; } = "python";
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
    /// <summary>Model name. Provider-specific; empty = the provider's default
    /// (fish → s1, minimax → speech-2.8-hd, aliyun → cosyvoice-v3-flash).</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>MiniMax only: no longer required — new platform (api.minimaxi.com) uses Bearer-only auth.</summary>
    public string GroupId { get; set; } = string.Empty;
    /// <summary>Synthesis speed multiplier (0.5–2.0).</summary>
    public double Speed { get; set; } = 1.0;
}

public sealed class VtsConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 8001;
    public float MouthScale { get; set; } = 1.5f;
    /// <summary>Emotion word (as emitted by the LLM in "[emotion:word]") -> VTS hotkeyID.
    /// Editable from the Config tab; see <see cref="BuildSystemPrompt"/>.</summary>
    public Dictionary<string, string> EmotionMap { get; set; } = new();

    /// <summary>
    /// Appends an auto-generated "[emotion:word]" vocabulary hint built from EmotionMap's keys,
    /// so the prompt always advertises exactly the emotion words that are actually wired to a
    /// VTS hotkey — no need to hand-edit the system prompt every time a mapping is added.
    /// </summary>
    public string BuildSystemPrompt(string basePrompt)
    {
        if (EmotionMap.Count == 0) return basePrompt;
        var words = string.Join("、", EmotionMap.Keys);
        return $"{basePrompt}\n\n你可以在合适的时候在句子里插入 [emotion:词] 标记来触发对应的表情/动作（这个标记不会被读出来或显示给观众）。可用的词只有：{words}。每句话最多用一个，没有合适的情绪就不要加。";
    }
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

public sealed class InputTemplateConfig
{
    /// <summary>Wraps mic-captured speech before sending to LLM. Use {text} as placeholder.</summary>
    public string MicTemplate { get; set; } = "（你的创造者对你说：{text}）";
    /// <summary>Wraps loopback-captured speech (opponent streamer). Use {text} as placeholder.</summary>
    public string LoopbackTemplate { get; set; } = "（你听到对面说：{text}）";
    /// <summary>Wraps danmaku. Use {username} and {content} as placeholders.</summary>
    public string DanmakuTemplate { get; set; } = "（弹幕 {username}：{content}）";
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