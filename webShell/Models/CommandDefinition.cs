namespace webShell.Models;

public sealed class CommandDefinition
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Shell { get; init; }
    public required string TaskType { get; init; }
    public required string Script { get; init; }
    public required string WorkingDirectory { get; init; }
    public bool IsFrontendCommand => Shell is "URL" or "JavaScript";
    public string? Error { get; init; }
}
