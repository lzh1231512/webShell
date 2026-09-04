using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using webShell.Services;

namespace webShell.Pages
{
    public class IndexModel : PageModel
    {
        private readonly CommandCatalog _catalog;
        private readonly ShellTaskService _tasks;

        public IndexModel(CommandCatalog catalog, ShellTaskService tasks)
        {
            _catalog = catalog;
            _tasks = tasks;
        }

        public void OnGet()
        {
        }

        public IActionResult OnGetCommands()
        {
            return new JsonResult(new
            {
                commands = _catalog.Load(),
                tasks = _tasks.GetAll()
            });
        }

        public async Task<IActionResult> OnPostStartAsync([FromForm] string commandId)
        {
            var command = _catalog.Load().FirstOrDefault(x => x.Id == commandId);
            if (command is null || command.Error is not null)
            {
                return BadRequest(new { error = command?.Error ?? "÷∏¡Ó≤ª¥Ê‘⁄°£" });
            }

            return new JsonResult(await _tasks.StartAsync(command));
        }

        public IActionResult OnPostStop([FromForm] string taskId)
        {
            return _tasks.Stop(taskId) ? new JsonResult(new { success = true }) : NotFound();
        }

        public IActionResult OnGetState()
        {
            return new JsonResult(_tasks.GetAll());
        }
    }
}
