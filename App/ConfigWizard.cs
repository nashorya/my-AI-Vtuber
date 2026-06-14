using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;

namespace AIVTuber.App;

/// <summary>
/// Interactive first-run configuration wizard.
/// Prompts user for essential settings when config.json doesn't exist
/// or has empty API keys.
/// </summary>
public static class ConfigWizard
{
    public static void Run(AppConfig config)
    {
        Console.WriteLine("=== 首次启动配置向导 ===");
        Console.WriteLine("逐步配置 AIVTuber 所需参数，配置将保存到 config.json");
        Console.WriteLine();

        PromptLlm(config);
        PromptAsr(config);
        PromptTts(config);
        PromptVts(config);
        PromptAudio(config);
        PromptBilibili(config);
        PromptObs(config);

        Console.WriteLine();
    }

    private static void PromptLlm(AppConfig config)
    {
        Console.WriteLine("[LLM 配置]");
        config.Llm.BaseUrl = Prompt("LLM API Base URL", config.Llm.BaseUrl);
        config.Llm.ApiKey = Prompt("LLM API Key", config.Llm.ApiKey);
        config.Llm.Model = Prompt("LLM Model", config.Llm.Model);
        config.Llm.SystemPrompt = Prompt("System Prompt", config.Llm.SystemPrompt);
        Console.WriteLine();
    }

    private static void PromptAsr(AppConfig config)
    {
        Console.WriteLine("[ASR 配置]");
        config.Asr.Provider = Prompt("ASR Provider", config.Asr.Provider);
        config.Asr.ApiKey = Prompt("ASR API Key", config.Asr.ApiKey);
        Console.WriteLine();
    }

    private static void PromptTts(AppConfig config)
    {
        Console.WriteLine("[TTS 配置]");
        config.Tts.Provider = Prompt("TTS Provider", config.Tts.Provider);
        config.Tts.ApiKey = Prompt("TTS API Key", config.Tts.ApiKey);
        config.Tts.VoiceId = Prompt("TTS Voice ID", config.Tts.VoiceId);
        Console.WriteLine();
    }

    private static void PromptVts(AppConfig config)
    {
        Console.WriteLine("[VTube Studio 配置]");
        config.Vts.Host = Prompt("VTS Host", config.Vts.Host);
        Console.WriteLine();
    }

    private static void PromptAudio(AppConfig config)
    {
        Console.WriteLine("[音频配置]");
        var devices = MicrophoneCapture.ListDevices();
        for (int i = 0; i < devices.Length; i++)
            Console.WriteLine($"  [{i}] {devices[i]}");

        if (int.TryParse(Prompt($"选择麦克风设备编号", config.Audio.InputDeviceIndex.ToString()), out var idx)
            && idx >= 0 && idx < devices.Length)
            config.Audio.InputDeviceIndex = idx;
        Console.WriteLine();
    }

    private static void PromptBilibili(AppConfig config)
    {
        Console.WriteLine("[B站弹幕配置] (留空跳过)");
        var roomStr = Prompt("直播间房间号", "");
        if (int.TryParse(roomStr, out var roomId) && roomId > 0)
        {
            config.Bilibili.Enable = true;
            config.Bilibili.RoomId = roomId;
            config.Bilibili.Sessdata = Prompt("SESSDATA", "");
            config.Bilibili.BiliJct = Prompt("bili_jct", "");
            config.Bilibili.Buvid3 = Prompt("buvid3", "");
        }
        Console.WriteLine();
    }

    private static void PromptObs(AppConfig config)
    {
        Console.WriteLine("[OBS 字幕配置] (留空跳过)");
        var enable = Prompt("启用 OBS 字幕? (y/N)", "n").ToLowerInvariant();
        if (enable == "y")
        {
            config.Obs.Enable = true;
            if (int.TryParse(Prompt("OBS WebSocket 端口", config.Obs.Port.ToString()), out var port))
                config.Obs.Port = port;
            config.Obs.Password = Prompt("OBS WebSocket 密码", "");
        }
        Console.WriteLine();
    }

    private static string Prompt(string label, string defaultValue)
    {
        if (string.IsNullOrEmpty(defaultValue))
            Console.Write($"{label}: ");
        else
            Console.Write($"{label} (默认: {defaultValue}): ");

        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrWhiteSpace(input) ? defaultValue : input;
    }
}