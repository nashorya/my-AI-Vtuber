using AIVTuber.Core.Memory;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Core.ViewModels;

/// <summary>Provides the memory operations displayed by <see cref="MemoryViewModel"/>.</summary>
public interface IMemoryDataSource
{
    Task<List<Fact>> GetFactsAsync();
    Task<List<Viewer>> GetViewersAsync();
    Task<List<PkTurn>> GetPkTurnsAsync();
    Task DeleteFactAsync(string factId);
    Task DeletePkTurnAsync(string turnId);
    Task ForceExtractAsync();
}

/// <summary>Adapts the runtime-owned repositories for the memory UI.</summary>
public sealed class RuntimeMemoryDataSource(BotRuntime runtime) : IMemoryDataSource
{
    public Task<List<Fact>> GetFactsAsync() => runtime.FactRepository.GetAllAsync();
    public Task<List<Viewer>> GetViewersAsync() => runtime.ViewerRepository.GetAllAsync();
    public Task<List<PkTurn>> GetPkTurnsAsync() => runtime.PkTurnRepository.ListAllAsync();
    public Task DeleteFactAsync(string factId) => runtime.FactRepository.DeleteAsync(factId);
    public Task DeletePkTurnAsync(string turnId) => runtime.PkTurnRepository.DeleteTurnAsync(turnId);
    public Task ForceExtractAsync() => runtime.ForceExtractMemoryAsync();
}
