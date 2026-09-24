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
    /// <summary>Normal vs PK interaction (wake keywords become name aliases).</summary>
    public InteractionConfig Interaction { get; set; } = new();
    /// <summary>Display names for 使用者 / 对方主播 / 直播间弹幕.</summary>
    public IdentityConfig Identity { get; set; } = new();
    /// <summary>Realtime pipeline overhaul switches (RT-00+). Defaults are all legacy/off:
    /// existing behaviour is unchanged until a flag is explicitly enabled.</summary>
    public RealtimeConfig Realtime { get; set; } = new();
    /// <summary>In-process PNG avatar + backend selection (vts / pixel / both).</summary>
    public AvatarRuntimeConfig Avatar { get; set; } = new();
    /// <summary>Window-capture visual observation (VIS-01/VIS-02). Defaults fully OFF.</summary>
    public AIVTuber.Core.Vision.VisionConfig Vision { get; set; } = new();
}


/// <summary>
/// Versioned realtime-pipeline configuration (docs: AGENT_PLAN_ASR_LLM_TTS_VISION.md §8).
/// All feature flags default to legacy/off so existing deployments keep their exact
/// semantics after upgrade. <see cref="SchemaVersion"/> lets future migrations run in order;
/// configs from a newer schema version load but must not be silently overwritten.
/// </summary>
public sealed class RealtimeConfig
{
    /// <summary>Current realtime config schema version understood by this build.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Version of the realtime section; migrated stepwise by ConfigManager.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>"legacy" (default) keeps local-sidecar / current behaviour;
    /// "cloud_only" enables vendor-API-only inference gating (no Python ASR sidecar,
    /// no local ONNX embedding, no local model downloads).</summary>
    public string InferenceMode { get; set; } = "legacy";

    /// <summary>Reserved for RT-02+: realtime streaming ASR sessions. Off = current path.</summary>
    public bool StreamingAsrEnabled { get; set; } = false;

    /// <summary>捕获→发送通道容量（毫秒音频）。超限停止并重建会话，明确上报断流。</summary>
    public int BufferCapacityMs { get; set; } = 4000;
    /// <summary>会话重建时回放的预录缓冲（毫秒），避免省费重连丢开头。</summary>
    public int PrerollMs { get; set; } = 1000;
    /// <summary>连续静音多久后结束会话省费（0=不断开）。恢复时回放预录并计入冷启动指标。</summary>
    public int IdleDisconnectMs { get; set; } = 30000;
    /// <summary>厂商发包粒度（毫秒），腾讯文档建议约 200ms。</summary>
    public int SendPacketMs { get; set; } = 200;

    /// <summary>Reserved for RT-04+: new turn manager. Off = current ConversationTurnGate.</summary>
    public bool TurnManagerV2Enabled { get; set; } = false;

    /// <summary>Reserved for RT-04: speculative early generation. Default closed.</summary>
    public bool SpeculativeGenerationEnabled { get; set; } = false;

    /// <summary>Chain tracing (RT-00). Off by default; when on, only monotonic/wall stamps and
    /// event names are recorded — never raw audio, transcripts, images or secrets.</summary>
    public bool TraceEnabled { get; set; } = false;

    public bool IsCloudOnly =>
        string.Equals(InferenceMode, "cloud_only", StringComparison.OrdinalIgnoreCase);
}

public sealed class IdentityConfig
{
    public string SelfName { get; set; } = "";
    public string SelfUid { get; set; } = "";
    public string OpponentName { get; set; } = "";
    public string DanmakuLabel { get; set; } = "直播间弹幕";
    public string ExtraNotes { get; set; } = "";
}

/// <summary>
/// Live interaction policy. In <c>pk</c> mode the bot stays silent until a wake
/// keyword appears in mic / loopback / danmaku / PK-announce text (or within the hold window).
/// </summary>
public sealed class InteractionConfig
{
    /// <summary>"normal" replies to every turn; "pk" requires wake keywords.</summary>
    public string Mode { get; set; } = "normal";

    /// <summary>Case-insensitive substrings that unlock speech in PK mode.</summary>
    public List<string> WakeKeywords { get; set; } = [];

    /// <summary>
    /// After a wake hit, allow keyword-free follow-ups for this many seconds.
    /// 0 means every turn must contain a keyword.
    /// </summary>
    public double WakeHoldSec { get; set; } = 45;

    public bool IsPkMode =>
        string.Equals(Mode, "pk", StringComparison.OrdinalIgnoreCase);

    public void SetPkMode(bool pk) => Mode = pk ? "pk" : "normal";
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
    public bool AllowsTransparency { get; set; } = true;

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
    /// <summary>Silence needed to close a speech segment. 500ms cut people off mid-thought:
    /// a pause for wording or a breath routinely runs longer than that, and the detector
    /// only counts silence, it cannot tell "finished" from "thinking". Raising this trades
    /// reply latency for not being interrupted.</summary>
    public int PostSpeechSilenceMs { get; set; } = 800;
}

public sealed class AsrConfig
{
    public string Provider { get; set; } = "aliyun";
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Keys keyed by <see cref="Provider"/> so switching vendors keeps the previous secret.</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new();
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
    /// <summary>腾讯云 SecretId（Provider = "tencent_realtime" 时使用；SecretKey 走 ApiKey）。
    /// 密钥只进内存与签名计算，不进日志。</summary>
    public string SecretId { get; set; } = string.Empty;
    /// <summary>豆包/火山的资源 ID（Provider = "volcano_realtime" 时使用）。</summary>
    public string ResourceId { get; set; } = string.Empty;
    /// <summary>热词候选（AI 昵称/别名、当前对手称呼等）。预留配置位——实时 provider 尚未接入
    /// 厂商热词能力，当前只作为候选登记，不做强制同音替换。</summary>
    public List<string> Hotwords { get; set; } = [];

    internal string VendorId => ProviderSecrets.Slot(Provider, "aliyun");

    public void RememberActiveKey() => ProviderSecrets.Remember(ApiKeys, VendorId, ApiKey);

    public void ActivateStoredKey()
    {
        if (ProviderSecrets.TryActivate(ApiKeys, VendorId, out var key))
            ApiKey = key;
    }

    public void StoreKey(string key)
    {
        ProviderSecrets.Remember(ApiKeys, VendorId, key);
        ApiKey = key;
    }
}

public sealed class LlmConfig
{
    /// <summary>deepseek / gemini / custom. Known vendors fill the official Base URL and default model.</summary>
    public string Provider { get; set; } = "deepseek";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Keys keyed by vendor (deepseek / gemini / host) so switching models keeps the previous secret.</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new();
    public string Model { get; set; } = "deepseek-chat";
    /// <summary>Last model name per vendor so switching DeepSeek ↔ Gemini restores each side.</summary>
    public Dictionary<string, string> Models { get; set; } = new();
    public string SystemPrompt { get; set; } =
        "你是直播中的 AI VTuber。口语短句回答，正文不超过80字（控制标记不计入），一句顶十句，别啰嗦、别列点。";
    public int MaxHistoryTokens { get; set; } = 4096;
    /// <summary>Reply protocol for the main dialogue LLM: "legacy" = one structured JSON
    /// object per turn (whole-reply buffering, kept as rollback path); "v2" = streamed
    /// NDJSON events (decision/speech/control/end) so the first approved segment reaches
    /// TTS before the model finishes. Default legacy — migration safety switch.</summary>
    public string ReplyProtocol { get; set; } = "legacy";

    internal string VendorId => ProviderSecrets.InferLlmVendor(Provider, BaseUrl);

    public void ApplyProvider(string provider) => ProviderSecrets.ApplyLlmProvider(this, provider);

    public void RememberActiveKey() => ProviderSecrets.Remember(ApiKeys, VendorId, ApiKey);

    public void ActivateStoredKey()
    {
        if (ProviderSecrets.TryActivate(ApiKeys, VendorId, out var key))
            ApiKey = key;
    }

    public void StoreKey(string key)
    {
        ProviderSecrets.Remember(ApiKeys, VendorId, key);
        ApiKey = key;
    }
}

public sealed class TtsConfig
{
    public string Provider { get; set; } = "fish-audio";
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Keys keyed by <see cref="Provider"/> so switching vendors keeps the previous secret.</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new();
    public string VoiceId { get; set; } = string.Empty;
    /// <summary>Model name. Provider-specific; empty = the provider's default
    /// (fish → s1, minimax → speech-2.8-hd, aliyun → cosyvoice-v3-flash, mimo → mimo-v2.5-tts).</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>MiniMax only: no longer required — new platform (api.minimaxi.com) uses Bearer-only auth.</summary>
    public string GroupId { get; set; } = string.Empty;
    /// <summary>Synthesis speed multiplier (0.5–2.0).</summary>
    public double Speed { get; set; } = 1.0;
    /// <summary>PCM sample rate requested from the provider and used by the player.
    /// 24000 is the safe default: it is the only rate all three cloud providers accept
    /// (CosyVoice rejects 44100). A self-hosted dots.tts generates 48000 natively, so
    /// setting 48000 there avoids resampling entirely.</summary>
    public int SampleRate { get; set; } = AIVTuber.Core.Audio.AudioPlayer.DefaultSampleRate;
    /// <summary>dots.tts only: base URL of the self-hosted service.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:6006";
    /// <summary>dots.tts only: synthesis language code (e.g. "ZH", "EN").</summary>
    public string Language { get; set; } = "ZH";
    /// <summary>dots.tts only: sampling seed; fixed so a line reads the same way twice.</summary>
    public int Seed { get; set; } = 42;
    /// <summary>dots.tts only: diffusion steps. Higher is slower and slightly cleaner.</summary>
    public int NumSteps { get; set; } = 10;
    /// <summary>dots.tts only: classifier-free guidance scale.</summary>
    public double GuidanceScale { get; set; } = 1.2;
    /// <summary>MiniMax only: HTTP transport selection (RT-01/RT-06).
    /// "legacy" (default) keeps the previous routing/behavior unchanged;
    /// "streaming" uses the t2a_v2 HTTP streaming response (stream=true, SSE audio chunks);
    /// "bidi" uses the /ws/v1/t2a_v2_bidi bidirectional session (requires tts.bidi_host —
    /// fails loudly with fallback guidance when unset; never silently switches transport).
    /// bidi is UNVERIFIED against the real endpoint (no key/account validation performed).
    /// The non-streaming stream=false implementation remains in TtsClient as an explicit code-level fallback.</summary>
    public string Transport { get; set; } = "legacy";

    /// <summary>RT-06 bidi: account-region official WSS host — intentionally EMPTY by default;
    /// the plan does not guess CN/intl hosts.</summary>
    public string BidiHost { get; set; } = string.Empty;
    /// <summary>RT-06 bidi: how long the cancel barrier waits for the vendor task_cancel ack
    /// before dropping the old socket and rebuilding the connection epoch.</summary>
    public int BidiCancelAckTimeoutMs { get; set; } = 2000;
    /// <summary>RT-06 bidi: pause submitting text once the estimated pending playback exceeds
    /// this many seconds; control messages still bypass the backlog.</summary>
    public double BidiMaxBacklogSeconds { get; set; } = 30;
    /// <summary>RT-06 bidi: estimated speech seconds per character (backlog estimation).</summary>
    public double BidiSecondsPerCharEstimate { get; set; } = 0.075;
    /// <summary>RT-06 bidi: idle keep-alive interval in ms; 0 = off (keepalive message semantics
    /// NOT verified against the real service — leave off until verified).</summary>
    public int BidiKeepAliveIntervalMs { get; set; } = 0;

    internal string VendorId => ProviderSecrets.Slot(Provider, "fish-audio");

    public void RememberActiveKey() => ProviderSecrets.Remember(ApiKeys, VendorId, ApiKey);

    public void ActivateStoredKey()
    {
        if (ProviderSecrets.TryActivate(ApiKeys, VendorId, out var key))
            ApiKey = key;
    }

    public void StoreKey(string key)
    {
        ProviderSecrets.Remember(ApiKeys, VendorId, key);
        ApiKey = key;
    }
}

public sealed class VtsConfig
{
    public AIVTuber.Core.Avatar.ContinuousControlConfig ContinuousControl { get; set; } = new();
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
            "默认一句说完，最多两句。标记紧挨句末写，不计入字数。");

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
                "用户点名要表情/情绪时必须带标记，不要只写文字描述。" +
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
                "有 [emotion:] 时不要同时写 side_*/tilt_*（会盖住表情）；示例：好呀[emotion:shy]。" +
                "单独侧身示例：[pose:side_right]。");
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
    /// <summary>Wraps a PK match start. Use {uname}, {follower}, {uid} and {roomid} as placeholders.</summary>
    public string PkTemplate { get; set; } = "（PK 开始了，对手是 {uname}，有 {follower} 个粉丝）";
    /// <summary>Wraps a manually announced PK match, used when the opponent could not be
    /// resolved. No opponent placeholders are available on this path.</summary>
    public string PkManualTemplate { get; set; } = "（新的一场 PK 开始了，还不知道对手是谁）";
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
    /// <summary>Fallback Python executable when danmaku_bridge.exe is not beside the app.</summary>
    public string PythonPath { get; set; } = "python";
    /// <summary>Announce the opposing streamer when a PK match starts. Read by the bridge
    /// at startup, so changing it respawns the bridge process.</summary>
    public bool PkNotice { get; set; } = false;
}
