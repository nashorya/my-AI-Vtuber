namespace AIVTuber.Core.ViewModels;

/// <summary>Keeps only the most recent asynchronous query eligible to update a memory tab.</summary>
public sealed class MemoryQueryCoordinator
{
    private long _generation;

    public long Begin() => Interlocked.Increment(ref _generation);
    public void Invalidate() => Interlocked.Increment(ref _generation);
    public bool IsCurrent(long generation) => generation == Volatile.Read(ref _generation);
}
