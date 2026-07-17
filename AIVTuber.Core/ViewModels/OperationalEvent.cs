namespace AIVTuber.Core.ViewModels;

/// <summary>A bounded, display-oriented record of an event already reported by the runtime.</summary>
public sealed record OperationalEvent(DateTimeOffset Timestamp, string Source, string Message, bool IsError = false);
