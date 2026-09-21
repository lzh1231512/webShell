using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using webShell.Models;

namespace webShell.Services;

public sealed class ShellTaskService
{
    private const int MaximumOutputLines = 500;
    private const int MaximumCompletedTasks = 20;
    private readonly ConcurrentDictionary<string, TaskEntry> _tasks = new();
    private readonly ConcurrentDictionary<string, object> _serviceLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PersistedServiceState> _serviceStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _serviceStateLock = new();
    private readonly ILogger<ShellTaskService> _logger;
    private readonly IConfiguration _configuration;
    private readonly CommandCatalog _catalog;
    private readonly string _serviceStatePath;

    public ShellTaskService(
        ILogger<ShellTaskService> logger,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        CommandCatalog catalog)
    {
        _logger = logger;
        _configuration = configuration;
        _catalog = catalog;
        var configuredPath = _configuration["Shell:ServiceStateFile"] ?? Path.Combine("App_Data", "services.json");
        _serviceStatePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath);
        LoadServiceStates();
        RestoreServices();
    }

    public async Task<TaskSnapshot> StartAsync(CommandDefinition command)
    {
        if (IsService(command)) return await StartServiceAsync(command);

        var entry = new TaskEntry(Guid.NewGuid().ToString("N"), command);
        _tasks[entry.Id] = entry;
        entry.RunTask = RunAsync(entry);
        if (command.TaskType == "Short") await entry.RunTask;
        return entry.Snapshot;
    }

    public async Task<TaskSnapshot> RestartAsync(CommandDefinition command)
    {
        if (!IsService(command)) return await StartAsync(command);

        var taskId = GetServiceTaskId(command);
        if (_tasks.TryGetValue(taskId, out var entry))
        {
            var runTask = entry.RunTask;
            if (entry.Status == "Running") Stop(taskId);
            if (runTask is not null)
            {
                try { await runTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
            }
        }

        return await StartServiceAsync(command);
    }

    public bool Stop(string id)
    {
        if (!_tasks.TryGetValue(id, out var entry) || entry.Status != "Running") return false;
        entry.StopRequested = true;
        var process = GetTrackedProcess(entry, out var disposeProcess);
        try
        {
            if (process is null)
            {
                if (entry.IsService) MarkServiceStopped(entry);
                return entry.IsService;
            }

            process.Kill(entireProcessTree: true);
            if (entry.IsService && entry.RunTask is null) MarkServiceStopped(entry);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop task {TaskId}.", id);
            return false;
        }
        finally
        {
            if (disposeProcess) process?.Dispose();
        }
    }

    public IReadOnlyList<TaskSnapshot> GetAll()
    {
        RefreshServiceStates();
        var services = _tasks.Values
            .Where(x => x.IsService)
            .OrderByDescending(x => x.StartedAt);
        var tasks = _tasks.Values
            .Where(x => !x.IsService)
            .OrderByDescending(x => x.StartedAt)
            .Take(MaximumCompletedTasks + 50);
        return services.Concat(tasks).Select(x => x.Snapshot).ToList();
    }

    private async Task<TaskSnapshot> StartServiceAsync(CommandDefinition command)
    {
        var gate = _serviceLocks.GetOrAdd(command.Id, _ => new object());
        TaskEntry entry;
        lock (gate)
        {
            var taskId = GetServiceTaskId(command);
            if (_tasks.TryGetValue(taskId, out entry!))
            {
                RefreshServiceState(entry, discoverStopped: true);
                if (entry.Status == "Running") return entry.Snapshot;
                entry.Command = command;
                entry.ServicePort = GetServicePort(command);
                entry.ResetForStart();
            }
            else
            {
                entry = new TaskEntry(taskId, command)
                {
                    ServicePort = GetServicePort(command)
                };
                _tasks[taskId] = entry;
            }

            entry.RunTask = RunAsync(entry);
        }

        try { await entry.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        return entry.Snapshot;
    }

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
            var scriptEncoding = entry.Command.Shell == "CMD" ? outputEncoding : new UTF8Encoding(true);
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
            process.OutputDataReceived += (_, e) => AddOutput(entry, e.Data, false);
            process.ErrorDataReceived += (_, e) => AddOutput(entry, e.Data, true);
            process.Start();
            entry.Process = process;
            entry.ProcessId = process.Id;
            entry.ProcessName = process.ProcessName;
            entry.ProcessCommandLine = BuildProcessCommandLine(startInfo);
            entry.ProcessStartTimeUtc = GetProcessStartTimeUtc(process);
            if (entry.IsService) PersistService(entry);
            entry.Started.TrySetResult(true);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();
            entry.ExitCode = process.ExitCode;
            entry.Status = entry.StopRequested
                ? "Stopped"
                : entry.IsService ? "Failed" : process.ExitCode == 0 ? "Completed" : "Failed";
            entry.FinishedAt = DateTime.UtcNow;
            _logger.LogInformation("Task {TaskId} ({CommandId}) finished with status {Status} and exit code {ExitCode}.",
                entry.Id, entry.Command.Id, entry.Status, entry.ExitCode);
        }
        catch (Exception ex)
        {
            entry.Started.TrySetException(ex);
            entry.Status = entry.StopRequested ? "Stopped" : "Failed";
            entry.FinishedAt = DateTime.UtcNow;
            AddOutput(entry, ex.Message, true);
            _logger.LogError(ex, "Task {TaskId} ({CommandId}) failed.", entry.Id, entry.Command.Id);
        }
        finally
        {
            entry.Process = null;
            if (entry.IsService) PersistService(entry);
            try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
            if (!entry.IsService) TrimCompletedTasks();
        }
    }

    private void RestoreServices()
    {
        var commands = _catalog.Load()
            .Where(x => IsService(x) && x.Error is null)
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands.Values)
        {
            _serviceStates.TryGetValue(command.Id, out var state);
            var entry = new TaskEntry(GetServiceTaskId(command), command)
            {
                Status = state?.Status ?? "Stopped",
                ProcessId = state?.ProcessId,
                ProcessStartTimeUtc = state?.ProcessStartTimeUtc,
                ProcessName = state?.ProcessName,
                ProcessCommandLine = state?.ProcessCommandLine,
                ServicePort = GetServicePort(command),
                StartedAt = state?.StartedAtUtc ?? DateTime.UtcNow,
                FinishedAt = state?.FinishedAtUtc
            };

            if (IsTrackedProcessAlive(entry))
            {
                var stateChanged = entry.Status != "Running" || state?.ProcessId != entry.ProcessId;
                entry.Status = "Running";
                entry.FinishedAt = null;
                if (stateChanged) PersistService(entry);
            }
            else if (entry.Status == "Running")
            {
                entry.Status = "Failed";
                entry.FinishedAt = DateTime.UtcNow;
                PersistService(entry);
            }
            _tasks[entry.Id] = entry;
        }
    }

    private void RefreshServiceStates()
    {
        foreach (var entry in _tasks.Values.Where(x => x.IsService && x.Status != "Stopped"))
        {
            RefreshServiceState(entry);
        }
    }

    private void RefreshServiceState(TaskEntry entry, bool discoverStopped = false)
    {
        if (entry.Status == "Stopped" && !discoverStopped) return;
        if (IsTrackedProcessAlive(entry))
        {
            if (entry.Status != "Running")
            {
                entry.Status = "Running";
                entry.FinishedAt = null;
                PersistService(entry);
            }
            return;
        }
        if (entry.Status == "Running")
        {
            entry.Status = "Failed";
            entry.FinishedAt = DateTime.UtcNow;
            PersistService(entry);
        }
    }

    private void LoadServiceStates()
    {
        try
        {
            if (!File.Exists(_serviceStatePath)) return;
            var states = JsonSerializer.Deserialize<List<PersistedServiceState>>(File.ReadAllText(_serviceStatePath));
            if (states is null) return;
            foreach (var state in states.Where(x => !string.IsNullOrWhiteSpace(x.CommandId)))
                _serviceStates[state.CommandId] = state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load service state from {Path}.", _serviceStatePath);
        }
    }

    private void PersistService(TaskEntry entry)
    {
        lock (_serviceStateLock)
        {
            _serviceStates[entry.Command.Id] = new PersistedServiceState
            {
                ServiceId = entry.Command.Id,
                CommandId = entry.Command.Id,
                Status = entry.Status,
                ProcessId = entry.ProcessId,
                ProcessStartTimeUtc = entry.ProcessStartTimeUtc,
                ProcessName = entry.ProcessName,
                ProcessCommandLine = entry.ProcessCommandLine,
                Port = entry.ServicePort,
                StartedAtUtc = entry.StartedAt,
                FinishedAtUtc = entry.FinishedAt
            };
            SaveServiceStatesLocked();
        }
    }

    private void SaveServiceStates()
    {
        lock (_serviceStateLock) SaveServiceStatesLocked();
    }

    private void SaveServiceStatesLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_serviceStatePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporaryPath = _serviceStatePath + ".tmp";
            var json = JsonSerializer.Serialize(_serviceStates.Values.OrderBy(x => x.CommandId), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, _serviceStatePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist service state to {Path}.", _serviceStatePath);
        }
    }

    private void MarkServiceStopped(TaskEntry entry)
    {
        entry.Status = "Stopped";
        entry.FinishedAt = DateTime.UtcNow;
        PersistService(entry);
    }

    private Process? GetTrackedProcess(TaskEntry entry, out bool disposeProcess)
    {
        disposeProcess = false;
        if (entry.Process is { HasExited: false } process) return process;
        if (entry.ProcessId is int processId)
        {
            try
            {
                var candidate = Process.GetProcessById(processId);
                if (!candidate.HasExited && MatchesProcess(entry, candidate))
                {
                    disposeProcess = true;
                    return candidate;
                }
                candidate.Dispose();
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        if (entry.IsService && TryRecoverServiceProcess(entry))
            return GetTrackedProcess(entry, out disposeProcess);

        return null;
    }

    private bool IsTrackedProcessAlive(TaskEntry entry)
    {
        var process = GetTrackedProcess(entry, out var disposeProcess);
        if (disposeProcess) process?.Dispose();
        return process is not null;
    }

    private bool MatchesProcess(TaskEntry entry, Process process)
    {
        if (!string.IsNullOrEmpty(entry.ProcessName) &&
            !string.Equals(entry.ProcessName, process.ProcessName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (entry.ProcessStartTimeUtc is not DateTime expectedStart) return true;
        try
        {
            var actualStart = process.StartTime.ToUniversalTime();
            return (actualStart - expectedStart).Duration() <= TimeSpan.FromSeconds(5);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static DateTime? GetProcessStartTimeUtc(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static string GetServiceTaskId(CommandDefinition command) => $"service:{command.Id}";

    private bool TryRecoverServiceProcess(TaskEntry entry)
    {
        var identity = FindServiceProcess(entry.Command, entry.ServicePort);
        if (identity is null) return false;
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited) return false;
            entry.ProcessId = process.Id;
            entry.ProcessName = process.ProcessName;
            entry.ProcessCommandLine = identity.CommandLine;
            entry.ProcessStartTimeUtc = GetProcessStartTimeUtc(process);
            entry.ServicePort ??= GetServicePort(entry.Command);
            PersistService(entry);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private ProcessIdentity? FindServiceProcess(CommandDefinition command, int? port)
    {
        var snapshot = ReadWindowsProcessSnapshot(port);
        if (snapshot is null) return null;

        var expectedName = GetMetadata(command, "ProcessName");
        var commandLineHint = GetMetadata(command, "CommandLineContains");
        if (port is null && string.IsNullOrWhiteSpace(expectedName) && string.IsNullOrWhiteSpace(commandLineHint))
            return null;

        var candidates = snapshot.Processes
            .Where(x => x.ProcessId > 0)
            .Where(x => MatchesServiceSignature(x, expectedName, commandLineHint))
            .ToList();
        var portOwners = snapshot.PortOwners.ToHashSet();
        return candidates.FirstOrDefault(x => portOwners.Contains(x.ProcessId))
            ?? candidates.FirstOrDefault();
    }

    private static bool MatchesServiceSignature(
        ProcessIdentity process,
        string? expectedName,
        string? commandLineHint)
    {
        if (!string.IsNullOrWhiteSpace(expectedName) &&
            !NormalizeProcessName(process.Name).Equals(NormalizeProcessName(expectedName), StringComparison.OrdinalIgnoreCase))
            return false;
        return string.IsNullOrWhiteSpace(commandLineHint) ||
            process.CommandLine?.Contains(commandLineHint, StringComparison.OrdinalIgnoreCase) == true;
    }

    private WindowsProcessSnapshot? ReadWindowsProcessSnapshot(int? port)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var portValue = port.GetValueOrDefault();
        var script = $"$owners = @(); if ({portValue} -gt 0) {{ try {{ $owners = @(Get-NetTCPConnection -State Listen -LocalPort {portValue} -ErrorAction Stop | Select-Object -ExpandProperty OwningProcess | Sort-Object -Unique) }} catch {{ $owners = @() }} }}; $processes = @(Get-CimInstance Win32_Process | Select-Object ProcessId,Name,CommandLine); [pscustomobject]@{{ PortOwners = $owners; Processes = $processes }} | ConvertTo-Json -Compress -Depth 4";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return JsonSerializer.Deserialize<WindowsProcessSnapshot>(output);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            _logger.LogDebug(ex, "Unable to inspect Windows service processes.");
            return null;
        }
    }

    private int? GetServicePort(CommandDefinition command)
    {
        var value = GetMetadata(command, "Port");
        return int.TryParse(value, out var port) && port is > 0 and <= 65535 ? port : null;
    }

    private static string? GetMetadata(CommandDefinition command, string key) =>
        command.Metadata.TryGetValue(key, out var value) ? value : null;

    private static string NormalizeProcessName(string? name) =>
        Path.GetFileNameWithoutExtension(name ?? string.Empty);

    private static string BuildProcessCommandLine(ProcessStartInfo startInfo) =>
        string.Join(" ", new[] { startInfo.FileName }.Concat(startInfo.ArgumentList));

    private static bool IsService(CommandDefinition command) =>
        command.TaskType.Equals("Service", StringComparison.OrdinalIgnoreCase);

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
        _logger.LogInformation("Task {TaskId} {Stream}: {Text}", entry.Id, isError ? "ERR" : "OUT", text);
    }

    private void TrimCompletedTasks()
    {
        var completed = _tasks.Values.Where(x => !x.IsService && x.Status != "Running")
            .OrderByDescending(x => x.FinishedAt).Skip(MaximumCompletedTasks).ToList();
        foreach (var item in completed) _tasks.TryRemove(item.Id, out _);
    }

    private sealed class TaskEntry
    {
        public TaskEntry(string id, CommandDefinition command) { Id = id; Command = command; }
        public string Id { get; }
        public CommandDefinition Command { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
        public string Status { get; set; } = "Running";
        public int? ExitCode { get; set; }
        public bool StopRequested { get; set; }
        public Process? Process { get; set; }
        public int? ProcessId { get; set; }
        public DateTime? ProcessStartTimeUtc { get; set; }
        public string? ProcessName { get; set; }
        public string? ProcessCommandLine { get; set; }
        public int? ServicePort { get; set; }
        public Task? RunTask { get; set; }
        public TaskCompletionSource<bool> Started { get; private set; } = CreateStartedSource();
        public Queue<TaskOutputLine> Output { get; } = new();
        public bool IsService => Command.TaskType.Equals("Service", StringComparison.OrdinalIgnoreCase);

        public void ResetForStart()
        {
            Started = CreateStartedSource();
            StartedAt = DateTime.UtcNow;
            FinishedAt = null;
            Status = "Running";
            ExitCode = null;
            StopRequested = false;
            Process = null;
            ProcessId = null;
            ProcessStartTimeUtc = null;
            ProcessName = null;
            ProcessCommandLine = null;
            lock (Output) Output.Clear();
        }

        public TaskSnapshot Snapshot
        {
            get
            {
                lock (Output)
                {
                    return new TaskSnapshot
                    {
                        Id = Id,
                        CommandId = Command.Id,
                        Title = Command.Title,
                        TaskType = Command.TaskType,
                        Status = Status,
                        ProcessId = ProcessId,
                        StartedAt = StartedAt,
                        FinishedAt = FinishedAt,
                        ExitCode = ExitCode,
                        Output = Output.ToArray()
                    };
                }
            }
        }

        private static TaskCompletionSource<bool> CreateStartedSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class WindowsProcessSnapshot
    {
        public List<int> PortOwners { get; set; } = new();
        public List<ProcessIdentity> Processes { get; set; } = new();
    }

    private sealed class ProcessIdentity
    {
        public int ProcessId { get; set; }
        public string? Name { get; set; }
        public string? CommandLine { get; set; }
    }
}
