using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using webShell.Models;

namespace webShell.Services;

public sealed class ShellTaskService
{
    private const int MaximumOutputLines = 500;
    private const int MaximumCompletedTasks = 20;
    private readonly ConcurrentDictionary<string, TaskEntry> _tasks = new();
    private readonly ILogger<ShellTaskService> _logger;
    private readonly IConfiguration _configuration;

    public ShellTaskService(ILogger<ShellTaskService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<TaskSnapshot> StartAsync(CommandDefinition command)
    {
        var entry = new TaskEntry(Guid.NewGuid().ToString("N"), command);
        _tasks[entry.Id] = entry;
        entry.RunTask = RunAsync(entry);
        if (command.TaskType == "Short") await entry.RunTask;
        return entry.Snapshot;
    }

    public bool Stop(string id)
    {
        if (!_tasks.TryGetValue(id, out var entry) || entry.Status != "Running") return false;
        entry.StopRequested = true;
        try
        {
            if (entry.Process is { HasExited: false } process)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停止任务 {TaskId} 失败", id);
        }
        return true;
    }

    public IReadOnlyList<TaskSnapshot> GetAll() => _tasks.Values
        .OrderByDescending(x => x.StartedAt)
        .Take(MaximumCompletedTasks + 50)
        .Select(x => x.Snapshot)
        .ToList();

    private async Task RunAsync(TaskEntry entry)
    {
        var extension = entry.Command.Shell == "CMD" ? ".cmd" : ".ps1";
        var scriptPath = Path.Combine(Path.GetTempPath(), $"webshell-{entry.Id}{extension}");
        try
        {
            var outputEncoding = GetOutputEncoding();
            var codePage = outputEncoding.CodePage == Encoding.UTF8.CodePage ? "65001" : "936";
            var script = entry.Command.Shell == "CMD"
                ? $"@chcp {codePage} > nul{Environment.NewLine}{entry.Command.Script}"
                : $"[Console]::OutputEncoding = [System.Text.Encoding]::GetEncoding({outputEncoding.CodePage}){Environment.NewLine}" +
                  $"$OutputEncoding = [System.Text.Encoding]::GetEncoding({outputEncoding.CodePage})" + Environment.NewLine +
                  entry.Command.Script;
            var scriptEncoding = entry.Command.Shell == "CMD"
                ? outputEncoding
                : new UTF8Encoding(true);
            await File.WriteAllTextAsync(scriptPath, script, scriptEncoding);
            var startInfo = new ProcessStartInfo
            {
                FileName = entry.Command.Shell == "CMD" ? "cmd.exe" : "powershell.exe",
                WorkingDirectory = entry.Command.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = outputEncoding,
                StandardErrorEncoding = outputEncoding
            };
            if (entry.Command.Shell == "CMD")
            {
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(scriptPath);
            }

            else
            {
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-ExecutionPolicy");
                startInfo.ArgumentList.Add("Bypass");
                startInfo.ArgumentList.Add("-File");
                startInfo.ArgumentList.Add(scriptPath);
            }

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            entry.Process = process;
            process.OutputDataReceived += (_, e) => AddOutput(entry, e.Data, false);
            process.ErrorDataReceived += (_, e) => AddOutput(entry, e.Data, true);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();
            entry.ExitCode = process.ExitCode;
            entry.Status = entry.StopRequested ? "Stopped" : process.ExitCode == 0 ? "Completed" : "Failed";
            entry.FinishedAt = DateTime.UtcNow;
            _logger.LogInformation("任务 {TaskId} ({CommandId}) 结束，状态 {Status}，退出码 {ExitCode}",
                entry.Id, entry.Command.Id, entry.Status, entry.ExitCode);
        }
        catch (Exception ex)
        {
            entry.Status = entry.StopRequested ? "Stopped" : "Failed";
            entry.FinishedAt = DateTime.UtcNow;
            AddOutput(entry, ex.Message, true);
            _logger.LogError(ex, "任务 {TaskId} ({CommandId}) 执行失败", entry.Id, entry.Command.Id);
        }
        finally
        {
            entry.Process = null;
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
            TrimCompletedTasks();
        }
    }

    private Encoding GetOutputEncoding()
    {
        return _configuration["Shell:OutputEncoding"]?.Equals("UTF8", StringComparison.OrdinalIgnoreCase) == true
            ? new UTF8Encoding(false)
            : Encoding.GetEncoding(936);
    }

    private void AddOutput(TaskEntry entry, string? text, bool isError)
    {
        if (text is null) return;
        lock (entry.Output)
        {
            entry.Output.Enqueue(new TaskOutputLine { Text = text, IsError = isError });
            while (entry.Output.Count > MaximumOutputLines) entry.Output.Dequeue();
        }
        _logger.LogInformation("任务 {TaskId} {Stream}: {Text}", entry.Id, isError ? "ERR" : "OUT", text);
    }

    private void TrimCompletedTasks()
    {
        var completed = _tasks.Values.Where(x => x.Status != "Running")
            .OrderByDescending(x => x.FinishedAt).Skip(MaximumCompletedTasks).ToList();
        foreach (var item in completed) _tasks.TryRemove(item.Id, out _);
    }

    private sealed class TaskEntry
    {
        public TaskEntry(string id, CommandDefinition command) { Id = id; Command = command; }
        public string Id { get; }
        public CommandDefinition Command { get; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
        public string Status { get; set; } = "Running";
        public int? ExitCode { get; set; }
        public bool StopRequested { get; set; }
        public Process? Process { get; set; }
        public Task? RunTask { get; set; }
        public Queue<TaskOutputLine> Output { get; } = new();
        public TaskSnapshot Snapshot
        {
            get
            {
                lock (Output)
                {
                    return new TaskSnapshot
                    {
                        Id = Id, CommandId = Command.Id, Title = Command.Title, Status = Status, ExitCode = ExitCode,
                        Output = Output.ToArray()
                    };
                }
            }
        }
    }
}
