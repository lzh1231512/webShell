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
                    throw new InvalidDataException("文件至少需要三行元数据和一行脚本内容。");

                var title = lines[0].Trim();
                var shell = lines[1].Trim();
                var taskType = lines[2].Trim();
                var script = string.Join(Environment.NewLine, lines.Skip(3));
                if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("标题不能为空。");
                if (!shell.Equals("CMD", StringComparison.OrdinalIgnoreCase) &&
                    !shell.Equals("PowerShell", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Shell 类型必须是 CMD 或 PowerShell。");
                if (!taskType.Equals("Short", StringComparison.OrdinalIgnoreCase) &&
                    !taskType.Equals("Long", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("任务类型必须是 Short 或 Long。");
                if (string.IsNullOrWhiteSpace(script)) throw new InvalidDataException("脚本内容为空。");

                commands.Add(new CommandDefinition
                {
                    Id = id,
                    Title = title,
                    Shell = shell.Equals("CMD", StringComparison.OrdinalIgnoreCase) ? "CMD" : "PowerShell",
                    TaskType = taskType.Equals("Short", StringComparison.OrdinalIgnoreCase) ? "Short" : "Long",
                    Script = script,
                    WorkingDirectory = directory
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
