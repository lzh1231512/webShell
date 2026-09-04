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
    public required string Status { get; init; }
    public int? ExitCode { get; init; }
    public IReadOnlyList<TaskOutputLine> Output { get; init; } = [];
}
