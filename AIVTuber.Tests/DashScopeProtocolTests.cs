using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public class DashScopeProtocolTests
{
    [Fact]
    public void RunTaskAsr_HasAsrTaskAndPcmParams()
    {
        var json = DashScopeProtocol.RunTaskAsr("task1", "paraformer-realtime-v2", 16000);
        Assert.Contains("\"action\":\"run-task\"", json);
        Assert.Contains("\"task_id\":\"task1\"", json);
        Assert.Contains("\"task\":\"asr\"", json);
        Assert.Contains("\"function\":\"recognition\"", json);
        Assert.Contains("\"model\":\"paraformer-realtime-v2\"", json);
        Assert.Contains("\"format\":\"pcm\"", json);
        Assert.Contains("\"sample_rate\":16000", json);
    }

    [Fact]
    public void RunTaskTts_HasTtsTaskVoiceAndRate()
    {
        var json = DashScopeProtocol.RunTaskTts("t2", "cosyvoice-v3-flash", "longanyang", 44100, 1.0);
        Assert.Contains("\"task\":\"tts\"", json);
        Assert.Contains("\"function\":\"SpeechSynthesizer\"", json);
        Assert.Contains("\"voice\":\"longanyang\"", json);
        Assert.Contains("\"sample_rate\":44100", json);
        Assert.Contains("\"format\":\"pcm\"", json);
    }

    [Fact]
    public void ContinueTask_CarriesText()
    {
        var json = DashScopeProtocol.ContinueTask("t3", "床前明月光");
        Assert.Contains("\"action\":\"continue-task\"", json);
        Assert.Contains("\"text\":\"床前明月光\"", json);
    }

    [Fact]
    public void FinishTask_HasFinishAction()
    {
        Assert.Contains("\"action\":\"finish-task\"", DashScopeProtocol.FinishTask("t4"));
    }

    [Fact]
    public void ParseEvent_ReadsEventAndError()
    {
        var (ev, err) = DashScopeProtocol.ParseEvent("""{"header":{"event":"task-started"}}""");
        Assert.Equal("task-started", ev);
        Assert.Null(err);

        var (ev2, err2) = DashScopeProtocol.ParseEvent("""{"header":{"event":"task-failed","error_message":"boom"}}""");
        Assert.Equal("task-failed", ev2);
        Assert.Equal("boom", err2);
    }

    [Fact]
    public void ParseAsrSentence_ReadsTextAndEndFlag()
    {
        var json = """{"payload":{"output":{"sentence":{"text":"你好世界","sentence_end":true}}}}""";
        var (text, end) = DashScopeProtocol.ParseAsrSentence(json);
        Assert.Equal("你好世界", text);
        Assert.True(end);

        var (text2, end2) = DashScopeProtocol.ParseAsrSentence("""{"payload":{"output":{"sentence":{"text":"半句","sentence_end":false}}}}""");
        Assert.Equal("半句", text2);
        Assert.False(end2);
    }

    [Fact]
    public void ParseAsrSentence_MissingPath_ReturnsEmpty()
    {
        var (text, end) = DashScopeProtocol.ParseAsrSentence("""{"header":{"event":"task-started"}}""");
        Assert.Equal("", text);
        Assert.False(end);
    }
}
