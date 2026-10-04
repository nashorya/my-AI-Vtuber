using System.Runtime.CompilerServices;
using AIVTuber.Core.Persona;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests.Persona;

public class PersonaAssistantTests
{
    private const string Persona =
        "你是小咪，老大的猫娘搭档，正在陪老大直播。\n讨厌香菜。\n性格：傲娇、爱吐槽。\n行为方式：被夸会嘴硬。";

    private static readonly PersonaDraftInput Empty = new("", [], "");

    private static string CheckJson(string? name = null, string? title = null, bool needsStyle = false,
        string nameSuggestions = "\"小咪\",\"团子\",\"咪咪\"") =>
        "{\"aiName\":" + Q(name) + ",\"streamerTitle\":" + Q(title) + ",\"needsStyle\":" + (needsStyle ? "true" : "false") +
        ",\"suggestions\":{\"aiName\":[" + nameSuggestions + "],\"streamerTitle\":[\"老大\",\"主播\",\"哥哥\"],\"style\":[\"温柔\",\"元气\",\"毒舌\"]}}";

    private static string ComposeJson(string persona, string names = "\"小咪\"", string title = "老大") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            persona,
            aiNames = System.Text.Json.JsonSerializer.Deserialize<string[]>($"[{names}]"),
            streamerTitle = title,
            supplemented = Array.Empty<string>(),
        });

    private static string Q(string? s) => s is null ? "null" : $"\"{s}\"";

    private static PersonaCheckResult NoQuestions(string? name = null, string? title = null) => new([], name, title);

    [Fact]
    public async Task Check_AsksNameAndTitle_WhenFieldsEmptyAndDescriptionSilent()
    {
        var llm = new ScriptedLlm(CheckJson());
        var result = await new PersonaAssistant(() => llm).CheckAsync(new("傲娇猫娘，爱吐槽", [], ""), default);
        Assert.Equal(["aiName", "streamerTitle"], result.Questions.Select(q => q.Id));
        Assert.Equal("它叫什么名字？", result.Questions[0].Text);
        Assert.Equal("它怎么称呼你？", result.Questions[1].Text);
    }

    [Fact]
    public async Task Check_SkipsQuestions_ForFilledFieldsOrNamesInDescription()
    {
        var llm = new ScriptedLlm(CheckJson(title: "老板"));
        var result = await new PersonaAssistant(() => llm).CheckAsync(new("叫我老板的小鱼", ["大肥鱼"], ""), default);
        Assert.Empty(result.Questions);
        Assert.Equal("老板", result.FoundStreamerTitle);
    }

    [Fact]
    public async Task Check_AsksStyle_OnlyWhenModelSaysNeeded()
    {
        var withStyle = await new PersonaAssistant(() => new ScriptedLlm(CheckJson(needsStyle: true)))
            .CheckAsync(new("可爱", ["小咪"], "老大"), default);
        Assert.Equal(["style"], withStyle.Questions.Select(q => q.Id));
        Assert.Equal("它说话更像哪种？", withStyle.Questions[0].Text);

        var without = await new PersonaAssistant(() => new ScriptedLlm(CheckJson()))
            .CheckAsync(new("傲娇猫娘", ["小咪"], "老大"), default);
        Assert.Empty(without.Questions);
    }

    [Fact]
    public async Task Check_PadsSuggestions_ToAtLeastTwo_AndCapsAtFour()
    {
        var few = await new PersonaAssistant(() => new ScriptedLlm(CheckJson(nameSuggestions: "\"阿咪\"")))
            .CheckAsync(Empty, default);
        Assert.Equal(["阿咪", "小鱼"], few.Questions.Single(q => q.Id == "aiName").Suggestions);

        var many = await new PersonaAssistant(() => new ScriptedLlm(CheckJson(nameSuggestions: "\"a\",\"b\",\"c\",\"d\",\"e\"")))
            .CheckAsync(Empty, default);
        Assert.Equal(4, many.Questions.Single(q => q.Id == "aiName").Suggestions.Count);
    }

    [Fact]
    public async Task Check_RejectsDescriptionsOverLimit_WithoutCallingModel()
    {
        var llm = new ScriptedLlm(CheckJson());
        var ex = await Assert.ThrowsAsync<PersonaAssistantException>(() =>
            new PersonaAssistant(() => llm).CheckAsync(new(new string('啊', PersonaAssistant.MaxDescriptionChars + 1), [], ""), default));
        Assert.Equal("描述太长了，请精简到 2000 字以内", ex.Message);
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task Check_ParsesJsonInsideCodeFenceOrProse()
    {
        var fenced = await new PersonaAssistant(() => new ScriptedLlm("好的：\n```json\n" + CheckJson(name: "小咪") + "\n```"))
            .CheckAsync(new("小咪", [], "老大"), default);
        Assert.Equal("小咪", fenced.FoundAiName);
        Assert.Empty(fenced.Questions);
    }

    [Fact]
    public async Task Compose_PassesDescriptionAndSettledNamesToModel()
    {
        var llm = new ScriptedLlm(ComposeJson(Persona));
        await new PersonaAssistant(() => llm).ComposeAsync(new("傲娇猫娘，讨厌香菜", [], ""), Empty2(),
            [new("aiName", "小咪"), new("streamerTitle", "老大")], default);
        var sent = llm.Inputs[0];
        Assert.Contains("讨厌香菜", sent);
        Assert.Contains("小咪", sent);
        Assert.Contains("老大", sent);
        Assert.Equal(PersonaPrompts.Compose, llm.SystemPrompts[0]);
    }

    [Fact]
    public async Task Compose_SkippedTitle_FallsBackToStreamer()
    {
        var llm = new ScriptedLlm(ComposeJson(Persona.Replace("老大", "主播"), title: "随便"));
        var result = await new PersonaAssistant(() => llm).ComposeAsync(new("傲娇猫娘", [], ""), Empty2(),
            [new("aiName", "小咪"), new("streamerTitle", null)], default);
        Assert.Equal("主播", result.StreamerTitle);
        Assert.Contains("主播", llm.Inputs[0]);
    }

    [Fact]
    public async Task Compose_NormalisesAliases()
    {
        var llm = new ScriptedLlm(ComposeJson(Persona, names: "\"咪咪, 小咪 \",\"小咪\""));
        var result = await new PersonaAssistant(() => llm).ComposeAsync(new("傲娇猫娘", [], ""), Empty2(),
            [new("aiName", "小咪"), new("streamerTitle", "老大")], default);
        Assert.Equal(["小咪", "咪咪"], result.AiNames);
    }

    [Fact]
    public async Task Compose_RetriesOnceWithReason_ThenSucceeds()
    {
        var llm = new ScriptedLlm(ComposeJson(Persona + "\n不想说话时输出【PASS】"), ComposeJson(Persona));
        var result = await new PersonaAssistant(() => llm).ComposeAsync(new("傲娇猫娘", ["小咪"], "老大"), Empty2(), [], default);
        Assert.Equal(Persona, result.Persona);
        Assert.Equal(2, llm.Calls);
        Assert.Contains("上次结果不合格", llm.SystemPrompts[1]);
    }

    [Fact]
    public async Task Compose_FailsAfterSecondRejection()
    {
        var bad = ComposeJson(Persona + "\n回答控制在30字以内");
        var llm = new ScriptedLlm(bad, bad);
        var ex = await Assert.ThrowsAsync<PersonaAssistantException>(() =>
            new PersonaAssistant(() => llm).ComposeAsync(new("傲娇猫娘", ["小咪"], "老大"), Empty2(), [], default));
        Assert.Equal("没生成好，请改改描述再试", ex.Message);
    }

    [Fact]
    public async Task Compose_UnparsableReply_CountsAsARejection()
    {
        var llm = new ScriptedLlm("我觉得这个角色很可爱", ComposeJson(Persona));
        var result = await new PersonaAssistant(() => llm).ComposeAsync(new("傲娇猫娘", ["小咪"], "老大"), Empty2(), [], default);
        Assert.Equal(Persona, result.Persona);
    }

    private static PersonaCheckResult Empty2() => NoQuestions();

    private sealed class ScriptedLlm(params string[] replies) : ILlmClient
    {
        public int Calls { get; private set; }
        public List<string> Inputs { get; } = [];
        public List<string> SystemPrompts { get; } = [];
        public event EventHandler<string>? OnSentenceReady;
        public event EventHandler<string>? OnEmotionDetected;
        public event EventHandler<string>? OnActionDetected;
        public event EventHandler<string>? OnPoseDetected;

        public async IAsyncEnumerable<string> StreamAsync(List<Message> history, string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var reply = replies[Calls++];
            Inputs.Add(userInput);
            SystemPrompts.Add(string.Join("\n", history.Where(m => m.Role == MessageRole.System).Select(m => m.Content)));
            await Task.Yield();
            yield return reply;
            OnSentenceReady?.Invoke(this, reply);
        }
    }
}
