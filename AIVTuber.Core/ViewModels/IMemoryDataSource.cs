using AIVTuber.Core.Memory;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Core.ViewModels;

/// <summary>Provides the memory operations displayed by <see cref="MemoryViewModel"/>.</summary>
public interface IMemoryDataSource
{
    Task<List<Fact>> GetFactsAsync();
    Task<List<Viewer>> GetViewersAsync();
    Task DeleteFactAsync(string factId);
    Task ForceExtractAsync();
}

/// <summary>Adapts the runtime-owned repositories for the memory UI.</summary>
public sealed class RuntimeMemoryDataSource(BotRuntime runtime) : IMemoryDataSource
{
    public Task<List<Fact>> GetFactsAsync() => runtime.FactRepository.GetAllAsync();
    public Task<List<Viewer>> GetViewersAsync() => runtime.ViewerRepository.GetAllAsync();
    public Task DeleteFactAsync(string factId) => runtime.FactRepository.DeleteAsync(factId);
    public Task ForceExtractAsync() => runtime.ForceExtractMemoryAsync();
}
