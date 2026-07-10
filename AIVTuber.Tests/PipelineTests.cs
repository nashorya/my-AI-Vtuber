using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;

namespace AIVTuber.Tests;

public class LlmClientTests
{
    [Fact]
    public void ContainsSentenceBoundary_Chinese()
    {
        Assert.True(LlmClient.ContainsSentenceBoundary("你好。世界", out var sentence, out var remainder));
        Assert.Equal("你好。", sentence);
        Assert.Equal("世界", remainder);
    }

    [Fact]
    public void ContainsSentenceBoundary_Question()
    {
        Assert.True(LlmClient.ContainsSentenceBoundary("怎么了吗？", out var sentence, out var remainder));
        Assert.Equal("怎么了吗？", sentence);
        Assert.Equal("", remainder);
    }

    [Fact]
    public void ContainsSentenceBoundary_NoBoundary()
    {
        Assert.False(LlmClient.ContainsSentenceBoundary("你好世界", out _, out _));
    }

    [Fact]
    public void ContainsSentenceBoundary_Newline()
    {
        Assert.True(LlmClient.ContainsSentenceBoundary("第一行\n第二行", out var sentence, out var remainder));
        Assert.Equal("第一行\n", sentence);
        Assert.Equal("第二行", remainder);
    }

    [Fact]
    public void ContainsSentenceBoundary_EnglishPeriod()
    {
        Assert.True(LlmClient.ContainsSentenceBoundary("Hello. World", out var sentence, out var remainder));
        Assert.Equal("Hello.", sentence);
        Assert.Equal(" World", remainder);
    }

    [Fact]
    public void StripEmotionTags_RemovesCompleteTag()
    {
        // Emotion tags must never reach TTS.
        Assert.Equal("你好世界", LlmClient.StripEmotionTags("[emotion:happy]你好世界"));
        Assert.Equal("你好世界", LlmClient.StripEmotionTags("你好[emotion:sad]世界"));
    }

    [Fact]
    public void StripEmotionTags_LeavesPartialTagUntouched()
    {
        // Incomplete tags (still streaming) are kept so they can be stripped once complete.
        Assert.Equal("你好[emotion:ha", LlmClient.StripEmotionTags("你好[emotion:ha"));
    }

    [Fact]
    public void StripEmotionTags_NoTag_ReturnsUnchanged()
    {
        Assert.Equal("你好世界", LlmClient.StripEmotionTags("你好世界"));
    }

    [Fact]
    public void ExtractActionTags_EmitsActionAndRemovesTag()
    {
        var actions = new List<string>();

        var text = LlmClient.ExtractActionTags(
            "才不是呢[action:head_shake]。", actions.Add);

        Assert.Equal("才不是呢。", text);
        Assert.Equal(["head_shake"], actions);
    }

    [Fact]
    public void StripControlTags_RemovesEmotionAndActionTags()
    {
        Assert.Equal("你好。", LlmClient.StripControlTags(
            "[emotion:happy]你好[action:wave]。"));
    }

    [Fact]
    public void Constructor_SetsProperties()
    {
        using var client = new LlmClient("https://api.test.com", "test-key", "test-model", "You are a bot");
        Assert.NotNull(client);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var client = new LlmClient("https://api.test.com", "test-key", "test-model", "prompt");
        client.Dispose();
        client.Dispose();
    }
}

public class ConversationManagerTests
{
    private static ConversationManager CreateManager(int maxTokens = 4096)
    {
        var config = new LlmConfig { MaxHistoryTokens = maxTokens, SystemPrompt = "You are a helpful assistant." };
        return new ConversationManager(config);
    }

    [Fact]
    public void AddUserMessage_IncreasesHistory()
    {
        var mgr = CreateManager();
        mgr.AddUserMessage("Hello");
        var history = mgr.GetHistory();
        Assert.Single(history);
        Assert.Equal(MessageRole.User, history[0].Role);
        Assert.Equal("Hello", history[0].Content);
    }

    [Fact]
    public void AddAssistantMessage_IncreasesHistory()
    {
        var mgr = CreateManager();
        mgr.AddAssistantMessage("Hi there!");
        var history = mgr.GetHistory();
        Assert.Single(history);
        Assert.Equal(MessageRole.Assistant, history[0].Role);
    }

    [Fact]
    public void GetHistory_ReturnsSnapshot()
    {
        var mgr = CreateManager();
        mgr.AddUserMessage("Hi");
        var history = mgr.GetHistory();
        mgr.AddUserMessage("Bye");
        // Original snapshot should not change
        Assert.Single(history);
        Assert.Equal(2, mgr.GetHistory().Count);
    }

    [Fact]
    public void Clear_EmptiesHistory()
    {
        var mgr = CreateManager();
        mgr.AddUserMessage("Hello");
        mgr.AddAssistantMessage("Hi");
        mgr.Clear();
        Assert.Empty(mgr.GetHistory());
    }

    [Fact]
    public void EstimateTokens_WithChinese()
    {
        var tokens = ConversationManager.EstimateTokens("你好世界");
        Assert.True(tokens > 0);
    }

    [Fact]
    public void EstimateTokens_WithEmptyString()
    {
        Assert.Equal(0, ConversationManager.EstimateTokens(""));
        Assert.Equal(0, ConversationManager.EstimateTokens(null!));
    }

    [Fact]
    public void ReplaceWithSummary_TrimsHistory()
    {
        var mgr = CreateManager(10000);
        mgr.AddUserMessage("first");
        mgr.AddAssistantMessage("second");
        mgr.AddUserMessage("third");
        mgr.AddAssistantMessage("fourth");
        Assert.Equal(4, mgr.GetHistory().Count);

        mgr.ReplaceWithSummary("Summary of early conversation");
        var history = mgr.GetHistory();
        Assert.True(history.Count <= 3);
        Assert.Contains("对话摘要", history[0].Content);
    }

    [Fact]
    public void TrimHistory_RemovesOldMessages()
    {
        // Very small token limit to force trimming
        var mgr = CreateManager(20);
        for (int i = 0; i < 10; i++)
        {
            mgr.AddUserMessage($"This is message number {i}");
        }
        Assert.True(mgr.GetHistory().Count < 10);
    }

    [Fact]
    public void BuildMessages_IncludesSystemPrompt()
    {
        var mgr = CreateManager();
        mgr.AddUserMessage("Hello");

        var messages = mgr.BuildMessages();
        Assert.True(messages.Count >= 2);
        Assert.Equal(MessageRole.System, messages[0].Role);
        Assert.Contains("You are a helpful assistant", messages[0].Content);
        Assert.Equal(MessageRole.User, messages[1].Role);
    }

    [Fact]
    public void BuildMessages_WithoutSystemPrompt()
    {
        var config = new LlmConfig { SystemPrompt = "", MaxHistoryTokens = 4096 };
        var mgr = new ConversationManager(config);
        mgr.AddUserMessage("Hello");

        var messages = mgr.BuildMessages();
        Assert.Single(messages);
        Assert.Equal(MessageRole.User, messages[0].Role);
    }
}

public class AsrClientTests
{
    [Fact]
    public void PcmToWav_ProducesValidWav()
    {
        var pcm = new byte[3200]; // 100ms of 16kHz 16-bit mono
        var wav = AsrClient.PcmToWav(pcm, 16000, 1, 16);

        // Check RIFF header
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);

        // Check WAVE header
        Assert.Equal((byte)'W', wav[8]);
        Assert.Equal((byte)'A', wav[9]);
        Assert.Equal((byte)'V', wav[10]);
        Assert.Equal((byte)'E', wav[11]);

        // Check fmt chunk
        Assert.Equal((byte)'f', wav[12]);
        Assert.Equal((byte)'m', wav[13]);
        Assert.Equal((byte)'t', wav[14]);

        // Total size = 44 header + pcm data
        Assert.Equal(44 + 3200, wav.Length);
    }

    [Fact]
    public void PcmToWav_DifferentSampleRates()
    {
        var pcm = new byte[1600];
        // 8kHz mono 16-bit
        var wav = AsrClient.PcmToWav(pcm, 8000, 1, 16);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal(44 + 1600, wav.Length);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var client = new AsrClient("https://api.test.com", "test-key");
        client.Dispose();
        client.Dispose();
    }

    [Theory]
    [InlineData(null, "whisper-1")]
    [InlineData("", "whisper-1")]
    [InlineData("  custom-asr  ", "custom-asr")]
    public void NormalizeModel_UsesConfiguredModelOrDefault(string? configured, string expected)
    {
        Assert.Equal(expected, AsrClient.NormalizeModel(configured));
    }
}

public class MessageTests
{
    [Fact]
    public void Message_DefaultValues()
    {
        var msg = new Message();
        Assert.Equal(string.Empty, msg.Content);
        Assert.Equal(MessageRole.System, msg.Role);
    }

    [Fact]
    public void MessageRole_Values()
    {
        Assert.Equal(0, (int)MessageRole.System);
        Assert.Equal(1, (int)MessageRole.User);
        Assert.Equal(2, (int)MessageRole.Assistant);
    }
}

public class TtsClientTests
{
    [Fact]
    public void Constructor_SetsProvider()
    {
        using var client = new TtsClient(new TtsConfig { Provider = "fish-audio", ApiKey = "test-key" });
        Assert.NotNull(client);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var client = new TtsClient(new TtsConfig { Provider = "fish-audio", ApiKey = "test-key" });
        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void BuildFishRequestJson_PutsSpeedUnderProsody_NotParams_NoBitrate()
    {
        var json = TtsClient.BuildFishRequestJson("你好", "voice-1", speed: 1.2, sampleRate: 44100);
        Assert.Contains("\"prosody\"", json);
        Assert.Contains("\"speed\":1.2", json);
        Assert.Contains("\"reference_id\":\"voice-1\"", json);
        Assert.Contains("\"sample_rate\":44100", json);
        Assert.Contains("\"format\":\"pcm\"", json);
        Assert.DoesNotContain("bitrate", json);   // old bogus field gone
        Assert.DoesNotContain("params", json);     // speed must NOT be under `params`
    }

    [Fact]
    public void BuildMiniMaxRequestJson_HasVoiceAndAudioSettings()
    {
        var json = TtsClient.BuildMiniMaxRequestJson("你好", "male-qn", "speech-02-hd", speed: 1.0, sampleRate: 44100);
        Assert.Contains("\"model\":\"speech-02-hd\"", json);
        Assert.Contains("\"voice_setting\"", json);
        Assert.Contains("\"voice_id\":\"male-qn\"", json);
        Assert.Contains("\"audio_setting\"", json);
        Assert.Contains("\"sample_rate\":44100", json);
        Assert.Contains("\"format\":\"pcm\"", json);
    }

    [Fact]
    public void ParseMiniMaxAudio_HexDecodesData()
    {
        var json = """{"data":{"audio":"0a0b0c","status":2},"base_resp":{"status_code":0,"status_msg":"success"}}""";
        Assert.Equal(new byte[] { 0x0a, 0x0b, 0x0c }, TtsClient.ParseMiniMaxAudio(json));
    }

    [Fact]
    public void ParseMiniMaxAudio_ThrowsOnNonZeroStatus()
    {
        var json = """{"base_resp":{"status_code":1004,"status_msg":"insufficient balance"}}""";
        var ex = Assert.Throws<InvalidOperationException>(() => TtsClient.ParseMiniMaxAudio(json));
        Assert.Contains("insufficient balance", ex.Message);
    }

    [Fact]
    public void ParseMiniMaxAudio_ReturnsEmptyWhenNoAudio()
    {
        var json = """{"data":{"status":2},"base_resp":{"status_code":0}}""";
        Assert.Empty(TtsClient.ParseMiniMaxAudio(json));
    }
}

public class InterfaceTests
{
    [Fact]
    public void IAsrClient_InterfaceExists()
    {
        Assert.NotNull(typeof(IAsrClient));
    }

    [Fact]
    public void ILlmClient_InterfaceExists()
    {
        Assert.NotNull(typeof(ILlmClient));
    }

    [Fact]
    public void ITtsClient_InterfaceExists()
    {
        Assert.NotNull(typeof(ITtsClient));
    }
}
