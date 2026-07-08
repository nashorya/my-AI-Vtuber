using AIVTuber.Core.Config;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.Tests;

public class ConfigViewModelTests
{
    private static ConfigViewModel Make(
        AppConfig? current = null,
        Action<AppConfig>? save = null,
        Func<AppConfig, Task>? apply = null)
        => new(current ?? new AppConfig(), ["麦克风 A", "麦克风 B"],
               save ?? (_ => { }), apply ?? (_ => Task.CompletedTask));

    [Fact]
    public void Working_IsDeepCopy_NotSameInstance()
    {
        var current = new AppConfig();
        current.Llm.Model = "orig";
        var vm = Make(current);
        vm.Working.Llm.Model = "edited";
        Assert.Equal("orig", current.Llm.Model);
        Assert.NotSame(current, vm.Working);
        Assert.NotSame(current.Llm, vm.Working.Llm);
    }

    [Fact]
    public void InputDevices_AreExposed()
    {
        var vm = Make();
        Assert.Equal(2, vm.InputDevices.Count);
        Assert.Equal("麦克风 A", vm.InputDevices[0]);
    }

    [Fact]
    public async Task SaveAsync_SavesAndAppliesWorkingCopy()
    {
        AppConfig? saved = null, applied = null;
        var vm = Make(save: c => saved = c, apply: c => { applied = c; return Task.CompletedTask; });
        vm.Working.Llm.Model = "new-model";

        await vm.SaveAsync();

        Assert.Same(vm.Working, saved);
        Assert.Same(vm.Working, applied);
        Assert.Equal("new-model", saved!.Llm.Model);
        Assert.Contains("已保存并应用", vm.Status);
    }

    [Fact]
    public async Task SaveAsync_ApplyFails_StillSaves_AndReportsError()
    {
        AppConfig? saved = null;
        var vm = Make(save: c => saved = c, apply: _ => throw new InvalidOperationException("boom"));

        await vm.SaveAsync();

        Assert.NotNull(saved);
        Assert.Contains("应用失败", vm.Status);
    }

    [Fact]
    public async Task SaveAsync_DropsReentrantCallWhileInFlight()
    {
        var gate = new TaskCompletionSource();
        var applyCount = 0;
        var vm = Make(apply: async _ => { applyCount++; await gate.Task; });

        var first = vm.SaveAsync();   // enters, awaits the gate
        Assert.True(vm.IsSaving);
        await vm.SaveAsync();         // re-entrant -> dropped immediately
        gate.SetResult();
        await first;

        Assert.Equal(1, applyCount);
        Assert.False(vm.IsSaving);
    }

    [Fact]
    public void Working_DeepCopies_EmotionMapDictionary()
    {
        var current = new AppConfig();
        current.Vts.EmotionMap["happy"] = "1";
        var vm = Make(current);
        vm.Working.Vts.EmotionMap["happy"] = "999";
        Assert.Equal("1", current.Vts.EmotionMap["happy"]); // original dictionary untouched
        Assert.NotSame(current.Vts.EmotionMap, vm.Working.Vts.EmotionMap);
    }

    [Fact]
    public async Task SaveAsync_RaisesPropertyChangedForStatus()
    {
        var vm = Make();
        var statusNotified = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ConfigViewModel.Status)) statusNotified = true; };
        await vm.SaveAsync();
        Assert.True(statusNotified);
    }
}
