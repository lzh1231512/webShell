using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using webShell.Services;

namespace webShell
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Logging.AddLog4Net("log4net.config");
            builder.Services.AddRazorPages();
            builder.Services.AddAuthorization(options =>
                options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser().Build());
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.LoginPath = "/Login";
                    options.AccessDeniedPath = "/Login";
                    options.SlidingExpiration = false;
                    options.ExpireTimeSpan = TimeSpan.FromDays(3650);
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SecurePolicy = builder.Configuration.GetValue<bool>("Shell:SecureCookies")
                        ? CookieSecurePolicy.Always
                        : CookieSecurePolicy.SameAsRequest;
                });

            var keyDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
            Directory.CreateDirectory(keyDirectory);
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

            builder.Services.AddSingleton<CommandCatalog>();
            builder.Services.AddSingleton<ShellTaskService>();

            var app = builder.Build();

            var logsDirectory = Path.Combine(app.Environment.ContentRootPath,
                app.Configuration["Shell:LogsDirectory"] ?? "logs");
            Directory.CreateDirectory(logsDirectory);
            foreach (var file in Directory.EnumerateFiles(logsDirectory, "*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMonths(-1))
                {
                    File.Delete(file);
                }
            }

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }
            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.MapRazorPages();

            app.Run();
        }
    }
}
