namespace webShell.Models;

public sealed class TaskOutputLine
{
    public required string Text { get; init; }
    public bool IsError { get; init; }
}

public sealed class TaskSnapshot
{
    public required string Id { get; init; }
    public required string CommandId { get; init; }
    public required string Title { get; init; }
    public required string TaskType { get; init; }
    public required string Status { get; init; }
    public int? ProcessId { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public int? ExitCode { get; init; }
    public IReadOnlyList<TaskOutputLine> Output { get; init; } = [];
}

public sealed class PersistedServiceState
{
    public required string ServiceId { get; init; }
    public required string CommandId { get; init; }
    public required string Status { get; set; }
    public int? ProcessId { get; set; }
    public DateTime? ProcessStartTimeUtc { get; set; }
    public string? ProcessName { get; set; }
    public string? ProcessCommandLine { get; set; }
    public int? Port { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
}
