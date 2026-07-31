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
    /// <summary>In-process PNG avatar + backend selection (vts / pixel / both).</summary>
    public AvatarRuntimeConfig Avatar { get; set; } = new();
}

/// <summary>
/// Runtime avatar settings (config.json <c>avatar</c> section).
/// Distinct from <c>assets/avatar/avatar.json</c> pack config.
/// </summary>
public sealed class AvatarRuntimeConfig
{
    /// <summary>"vts" | "pixel" | "both". Default keeps existing VTS-only behaviour.</summary>
    public string Backend { get; set; } = "vts";

    /// <summary>Directory containing avatar.json, sprites/, stickers/, dev_placeholder/.</summary>
    public string AssetsPath { get; set; } = "assets/avatar";

    /// <summary>Keep the avatar window above other windows.</summary>
    public bool Topmost { get; set; } = true;

    /// <summary>Solid chroma-key colour (e.g. #00FF00). Used when <see cref="AllowsTransparency"/> is false.</summary>
    public string BackgroundColor { get; set; } = "#00FF00";

    /// <summary>
    /// When true, use a fully transparent WPF window (AllowsTransparency).
    /// Default false — solid chroma key is the safer OBS path and keeps hardware acceleration.
    /// </summary>
    public bool AllowsTransparency { get; set; } = false;

    /// <summary>Window width in DIPs. 0 = derive from pack canvas (clamped).</summary>
    public double WindowWidth { get; set; } = 480;

    /// <summary>Window height in DIPs. 0 = derive from pack canvas (clamped).</summary>
    public double WindowHeight { get; set; } = 480;

    /// <summary>
    /// LLM emotion word → avatar.json state name.
    /// Unknown emotions fall back to direct state match, then neutral.
    /// </summary>
    public Dictionary<string, string> EmotionMap { get; set; } = new()
    {
        ["happy"] = "happy",
        ["开心"] = "happy",
        ["shy"] = "shy",
        ["害羞"] = "shy",
        ["angry"] = "angry",
        ["生气"] = "angry",
        ["upset"] = "upset",
        ["无语"] = "upset",
        ["surprised"] = "surprised",
        ["惊讶"] = "surprised",
        ["sad"] = "sad",
        ["难过"] = "sad",
        ["sleep"] = "sleep",
        ["困"] = "sleep",
    };

    /// <summary>
    /// Bitmap scaling: "auto" (nearest for placeholder sheet, linear for HD sprites),
    /// "nearest", or "linear".
    /// </summary>
    public string ScalingMode { get; set; } = "auto";

    /// <summary>When true, snap motion offsets to whole pixels (helps chunky pixel art).</summary>
    public bool SnapMotionToPixels { get; set; } = false;

    public bool UsesVts =>
        string.Equals(Backend, "vts", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Backend, "both", StringComparison.OrdinalIgnoreCase);

    public bool UsesPixel =>
        string.Equals(Backend, "pixel", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Backend, "both", StringComparison.OrdinalIgnoreCase);
}

public sealed class AudioConfig
{
    public int InputDeviceIndex { get; set; } = 0;
    /// <summary>Output device index for TTS playback. -1 = system default.
    /// Set this to your real speakers/headphones so you can hear the AI even when
    /// the system default output is a virtual cable used for streaming.</summary>
    public int OutputDeviceIndex { get; set; } = -1;
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
    /// <summary>Managed Python executable used to launch the packaged local ASR sidecar.</summary>
    public string PythonPath { get; set; } = "sidecar/python/python.exe";
    /// <summary>When true, reuse a long-lived WebSocket across recognitions instead of opening a
    /// fresh connection (with TLS+auth handshake) per call. Saves ~200-600ms per utterance.
    /// Only applies to WebSocket-based providers (DashScope/Qwen). Default true.</summary>
    public bool PersistConnection { get; set; } = true;
    /// <summary>When true, stream audio frames to the ASR service while the user is still
    /// speaking, instead of waiting for the full VAD segment. Returns incremental results.
    /// Only applies to WebSocket-based providers (DashScope/Qwen). Default true.</summary>
    public bool Streaming { get; set; } = true;
}

public sealed class LlmConfig
{
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "deepseek-chat";
    public string SystemPrompt { get; set; } =
        "你是直播中的 AI VTuber。口语短句回答，正文不超过80字（控制标记不计入），一句顶十句，别啰嗦、别列点。";
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
    /// <summary>Semantic action name (as emitted by the LLM in "[action:name]") -> VTS hotkeyID.
    /// Keep this as an explicit allow-list so the LLM cannot invoke arbitrary VTS hotkeys.</summary>
    public Dictionary<string, string> ActionMap { get; set; } = new();

    /// <summary>
    /// Appends auto-generated control-tag vocabulary so the LLM emits exactly the
    /// [emotion:] / [pose:] / [action:] tokens we can parse. Extra emotion/pose lists
    /// (pixel avatar pack) merge with VTS maps.
    /// Prefer short replies: one spoken sentence keeps one emotion/pose naturally aligned with TTS.
    /// </summary>
    public string BuildSystemPrompt(
        string basePrompt,
        IEnumerable<string>? extraEmotions = null,
        IEnumerable<string>? poses = null)
    {
        var instructions = new List<string>();

        instructions.Add(
            "回复要短：正文（去掉所有 [emotion:]/[pose:]/[action:] 标记后）尽量不超过 80 个字；" +
            "默认一句说完，最多两句。标记紧挨该句句号前写，不计入字数。" +
            "若超长会在 。！？ 处截断，保证念完完整一句。");

        var emotionWords = EmotionMap.Keys
            .Concat(extraEmotions ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (emotionWords.Count > 0)
        {
            var words = string.Join("、", emotionWords);
            instructions.Add(
                "需要换表情时，在该句句号前插入 [emotion:词]（驱动立绘/VTS）。" +
                $"可用情绪词只有：{words}。每句最多一个，不要列表外的词。" +
                "标记不会被读出；TTS 只念正文，情绪另传参数。");
        }

        var poseWords = (poses ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (poseWords.Count > 0)
        {
            var words = string.Join("、", poseWords);
            instructions.Add(
                "需要换整图姿态时，在该句句号前插入 [pose:姿态名]。" +
                $"可用姿态名只有：{words}。每句最多一个。" +
                "示例：好呀[emotion:shy][pose:tilt_left]。");
        }

        if (ActionMap.Count > 0)
        {
            var actions = string.Join("、", ActionMap.Keys);
            instructions.Add(
                $"需要动作时在该句句号前插入 [action:动作名]。可用：{actions}。" +
                "每句最多一个，不要列表外的动作。标记不会被读出。");
        }

        if (instructions.Count == 0) return basePrompt;
        return string.Join("\n\n", new[] { basePrompt }.Concat(instructions));
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
