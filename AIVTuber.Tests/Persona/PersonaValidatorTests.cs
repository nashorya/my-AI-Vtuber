using AIVTuber.Core.Persona;

namespace AIVTuber.Tests.Persona;

public class PersonaValidatorTests
{
    private const string Good =
        "你是小咪，老大的猫娘搭档，正在陪老大直播。\n" +
        "性格：傲娇、爱吐槽。\n" +
        "行为方式：被夸会嘴硬；看不惯就直说。\n" +
        "例子（只体现语气，不要照搬原句）：\n" +
        "- 老大夸你今天很乖 → “哼，本喵一直都很乖好吗。”";

    private static PersonaComposeResult R(string persona, string title = "老大") => new(persona, ["小咪"], title, []);

    [Fact]
    public void Accepts_AShortPersona() => Assert.Null(PersonaValidator.Validate(R(Good)));

    [Theory]
    [InlineData("你是小咪，正在陪老大直播。\n不想说话时只输出【PASS】")]
    [InlineData("你是小咪，正在陪老大直播。\n心里话用全角括号包裹")]
    [InlineData("你是小咪，正在陪老大直播。\n回答控制在30字以内")]
    [InlineData("你是小咪，正在陪老大直播。\n你必须先判断是否被叫到")]
    public void Rejects_WorkflowRules(string persona) => Assert.NotNull(PersonaValidator.Validate(R(persona)));

    [Fact]
    public void IgnoresForbiddenWords_InsideExampleQuotes() =>
        Assert.Null(PersonaValidator.Validate(R(Good + "\n- 对面主播耍赖 → “你必须给我道歉！”")));

    [Fact]
    public void Rejects_MoreThanTwoBehaviours() =>
        Assert.NotNull(PersonaValidator.Validate(R(Good.Replace("看不惯就直说。", "看不惯就直说；爱打哈欠。"))));

    [Fact]
    public void Rejects_MoreThanTwoExamples() =>
        Assert.NotNull(PersonaValidator.Validate(R(Good + "\n- 甲 → “乙”\n- 丙 → “丁”")));

    [Fact]
    public void Rejects_WhenNameMissingFromPersona() =>
        Assert.NotNull(PersonaValidator.Validate(R(Good.Replace("小咪", "小猫"))));

    [Fact]
    public void Rejects_WhenGivenTitleMissingFromPersona() =>
        Assert.NotNull(PersonaValidator.Validate(R(Good.Replace("老大", "主播"))));

    [Fact]
    public void Accepts_EmptyTitle() =>
        Assert.Null(PersonaValidator.Validate(R(Good.Replace("老大", "主播"), title: "")));
}
