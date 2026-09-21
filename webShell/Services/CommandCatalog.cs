using System.Text;
using webShell.Models;

namespace webShell.Services;

public sealed class CommandCatalog
{
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public CommandCatalog(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _environment = environment;
        _configuration = configuration;
    }

    public IReadOnlyList<CommandDefinition> Load()
    {
        var directory = Path.Combine(_environment.ContentRootPath,
            _configuration["Shell:CommandsDirectory"] ?? "commands");
        Directory.CreateDirectory(directory);
        var commands = new List<CommandDefinition>();

        foreach (var path in Directory.EnumerateFiles(directory, "*.ws").OrderBy(x => x))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            try
            {
                var bytes = File.ReadAllBytes(path);
                var scriptFile = new UTF8Encoding(false, true).GetString(bytes);
                var lines = scriptFile.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                if (lines.Length < 4)
                    throw new InvalidDataException("文件至少需要三个元数据和一行脚本内容。");

                var title = lines[0].Trim();
                var shell = lines[1].Trim();
                var taskType = lines[2].Trim();
                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var scriptStart = 3;
                if (scriptStart < lines.Length &&
                    lines[scriptStart].Trim().Equals("[Metadata]", StringComparison.OrdinalIgnoreCase))
                {
                    scriptStart++;
                    var metadataClosed = false;
                    for (; scriptStart < lines.Length; scriptStart++)
                    {
                        var metadataLine = lines[scriptStart].Trim();
                        if (metadataLine.Equals("[/Metadata]", StringComparison.OrdinalIgnoreCase))
                        {
                            metadataClosed = true;
                            scriptStart++;
                            break;
                        }
                        if (string.IsNullOrWhiteSpace(metadataLine)) continue;

                        var separator = metadataLine.IndexOf('=');
                        if (separator <= 0)
                            throw new InvalidDataException("元数据项必须使用 Key=Value 格式。");
                        var key = metadataLine[..separator].Trim();
                        var value = metadataLine[(separator + 1)..].Trim();
                        if (!metadata.TryAdd(key, value))
                            throw new InvalidDataException($"元数据键重复：{key}。");
                    }

                    if (!metadataClosed)
                        throw new InvalidDataException("元数据区块缺少 [/Metadata] 结束标记。");
                }

                var script = string.Join(Environment.NewLine, lines.Skip(scriptStart));
                var supportedTypes = new[] { "CMD", "PowerShell", "URL", "JavaScript" };

                if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("标题不能为空。");
                if (!supportedTypes.Any(x => x.Equals(shell, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("类型必须是 CMD、PowerShell、URL 或 JavaScript。");
                if (!taskType.Equals("Short", StringComparison.OrdinalIgnoreCase) &&
                    !taskType.Equals("Long", StringComparison.OrdinalIgnoreCase) &&
                    !taskType.Equals("Service", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Task type must be Short, Long, or Service.");
                if (taskType.Equals("Service", StringComparison.OrdinalIgnoreCase) &&
                    (shell.Equals("URL", StringComparison.OrdinalIgnoreCase) ||
                     shell.Equals("JavaScript", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Service commands must use CMD or PowerShell.");
                if (string.IsNullOrWhiteSpace(script)) throw new InvalidDataException("脚本不能为空。");

                commands.Add(new CommandDefinition
                {
                    Id = id,
                    Title = title,
                    Shell = supportedTypes.First(x => x.Equals(shell, StringComparison.OrdinalIgnoreCase)),
                    TaskType = taskType.Equals("Short", StringComparison.OrdinalIgnoreCase)
                        ? "Short"
                        : taskType.Equals("Service", StringComparison.OrdinalIgnoreCase) ? "Service" : "Long",
                    Script = script,
                    WorkingDirectory = directory,
                    Metadata = metadata
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidDataException)
            {
                commands.Add(new CommandDefinition
                {
                    Id = id,
                    Title = id,
                    Shell = "",
                    TaskType = "",
                    Script = "",
                    WorkingDirectory = directory,
                    Error = ex.Message
                });
            }
        }

        return commands;
    }
}
