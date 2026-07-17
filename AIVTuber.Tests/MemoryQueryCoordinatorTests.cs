using AIVTuber.Core.Memory;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.Tests;

public class MemoryQueryCoordinatorTests
{
    [Fact]
    public void NewerSearchGeneration_InvalidatesAnOlderResponse()
    {
        var coordinator = new MemoryQueryCoordinator();
        var first = coordinator.Begin();
        var second = coordinator.Begin();

        Assert.False(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(second));
    }

    [Fact]
    public void TabChange_InvalidatesThePendingSearchResponse()
    {
        var coordinator = new MemoryQueryCoordinator();
        var pendingSearch = coordinator.Begin();

        coordinator.Invalidate();

        Assert.False(coordinator.IsCurrent(pendingSearch));
    }

    [Fact]
    public async Task ConfirmedFactDelete_CallbackRunsExactlyOnceWithoutDelay()
    {
        var calls = 0;
        var row = new FactRowViewModel(new() { Id = "fact-1", Content = "test" }, _ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await row.DeleteAsync();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ConcurrentDeleteRequests_InvokeTheDeleteCallbackOnlyOnce()
    {
        var calls = 0;
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var row = new FactRowViewModel(new() { Id = "fact-1", Content = "test" }, async _ =>
        {
            calls++;
            await releaseDelete.Task;
        });

        var firstDelete = row.DeleteAsync();
        await row.DeleteAsync();

        Assert.Equal(1, calls);
        releaseDelete.SetResult();
        await firstDelete;
    }

    [Fact]
    public async Task NewerFactResponse_WinsWhenAnOlderQueryFinishesLast()
    {
        var firstResponse = new TaskCompletionSource<List<Fact>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponse = new TaskCompletionSource<List<Fact>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestMemoryDataSource();
        source.FactReads.Enqueue(firstResponse.Task);
        source.FactReads.Enqueue(secondResponse.Task);
        var vm = new MemoryViewModel(source, action => action());

        var firstRefresh = vm.RefreshFactsAsync();
        var secondRefresh = vm.RefreshFactsAsync();
        secondResponse.SetResult([new Fact { Id = "latest", Content = "latest fact" }]);
        await secondRefresh;
        firstResponse.SetResult([new Fact { Id = "stale", Content = "stale fact" }]);
        await firstRefresh;

        var fact = Assert.Single(vm.Facts);
        Assert.Equal("latest", fact.Id);
    }

    [Fact]
    public async Task TabSwitch_DiscardsTheResponseThatBeganOnThePreviousTab()
    {
        var factResponse = new TaskCompletionSource<List<Fact>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TestMemoryDataSource();
        source.FactReads.Enqueue(factResponse.Task);
        source.Viewers = [new Viewer { Uid = "viewer", Platform = "test" }];
        var vm = new MemoryViewModel(source, action => action());

        var factRefresh = vm.RefreshFactsAsync();
        await vm.ActivateTabAsync(MemoryTab.Viewers);
        factResponse.SetResult([new Fact { Id = "stale", Content = "old fact" }]);
        await factRefresh;

        Assert.Empty(vm.Facts);
        Assert.Single(vm.Viewers);
    }

    [Fact]
    public async Task Refresh_PreservesTheCurrentFactSelectionById()
    {
        var source = new TestMemoryDataSource
        {
            Facts = [new Fact { Id = "selected", Content = "selected fact" }]
        };
        var vm = new MemoryViewModel(source, action => action());

        await vm.RefreshFactsAsync();
        vm.SelectedFact = Assert.Single(vm.Facts);
        await vm.RefreshFactsAsync();

        Assert.NotNull(vm.SelectedFact);
        Assert.Equal("selected", vm.SelectedFact.Id);
    }

    [Fact]
    public async Task ConfirmedDelete_PerformsOneDeletionAndOneFollowUpRefresh()
    {
        var source = new TestMemoryDataSource
        {
            Facts = [new Fact { Id = "delete-me", Content = "remove this" }]
        };
        var vm = new MemoryViewModel(source, action => action());
        await vm.RefreshFactsAsync();

        await Assert.Single(vm.Facts).DeleteAsync();

        Assert.Equal(1, source.DeleteCalls);
        Assert.Equal(2, source.FactReadCalls);
        Assert.Empty(vm.Facts);
    }

    [Fact]
    public async Task FailedDelete_ReportsAFactErrorWithoutEscapingTheUiOperation()
    {
        var source = new TestMemoryDataSource
        {
            Facts = [new Fact { Id = "delete-me", Content = "remove this" }],
            DeleteFailure = new InvalidOperationException("database unavailable")
        };
        var vm = new MemoryViewModel(source, action => action());
        await vm.RefreshFactsAsync();

        await Assert.Single(vm.Facts).DeleteAsync();

        Assert.Contains("删除失败", vm.FactsError);
        Assert.Contains("database unavailable", vm.FactsError);
        Assert.Equal(1, source.FactReadCalls);
        Assert.Single(vm.Facts);
    }

    private sealed class TestMemoryDataSource : IMemoryDataSource
    {
        public Queue<Task<List<Fact>>> FactReads { get; } = [];
        public List<Fact> Facts { get; set; } = [];
        public List<Viewer> Viewers { get; set; } = [];
        public int FactReadCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public Exception? DeleteFailure { get; set; }

        public Task<List<Fact>> GetFactsAsync()
        {
            FactReadCalls++;
            return FactReads.TryDequeue(out var read) ? read : Task.FromResult(Facts.ToList());
        }
        public Task<List<Viewer>> GetViewersAsync() => Task.FromResult(Viewers);
        public Task DeleteFactAsync(string factId)
        {
            DeleteCalls++;
            if (DeleteFailure is not null) throw DeleteFailure;
            Facts.RemoveAll(fact => fact.Id == factId);
            return Task.CompletedTask;
        }
        public Task ForceExtractAsync() => Task.CompletedTask;
    }
}
