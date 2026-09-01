using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NzuaMcp.Nzua;
using NzuaMcp.Nzua.Api;
using NzuaTeacher.Core;
using NzuaTeacher.Core.Abstractions;
using NzuaTeacher.Core.AI;
using NzuaTeacher.Core.Data;
using NzuaTeacher.Core.Services;
using NzuaTeacher.Services;
using Plugin.Maui.Audio;

namespace NzuaTeacher;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        builder.Logging.AddProvider(new FileLoggerProvider());
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            FileLoggerProvider.Write("UnhandledException", "Необроблена помилка", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            FileLoggerProvider.Write("UnobservedTaskException", "Помилка у фоновій задачі", e.Exception);
            e.SetObserved();
        };

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Записи в NZ.UA дозволені на рівні клієнта: кожен запис і так підтверджується
        // вчителем у UI (діалоги синхронізації та підтвердження MCP-тулів).
        Environment.SetEnvironmentVariable("NZUA_ALLOW_WRITES", "true");

        NzuaAuth.CleanupStaleProfiles();

        // Той самий файл сесії, що й у nzua-mcp: спільна сесія з Claude Desktop тощо.
        var sessionStore = new NzuaSessionStore();
        NzuaSessionService sessionService = null!;
        var client = new NzuaClient(
            sessionStore.Load(),
            () => sessionService!.OnSessionExpired(),
            sessionStore.Save);
        sessionService = new NzuaSessionService(sessionStore, client);

        builder.Services.AddSingleton(sessionStore);
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton(sessionService);
        builder.Services.AddSingleton<JournalApi>();
        builder.Services.AddSingleton<MarksApi>();
        builder.Services.AddSingleton<LessonsApi>();
        builder.Services.AddSingleton<HomeTasksApi>();

        builder.Services.AddDbContextFactory<TeacherDbContext>(options =>
            options.UseSqlite($"Data Source={AppPaths.DbPath}"));

        builder.Services.AddSingleton<JournalStore>();
        builder.Services.AddSingleton<OutboxService>();
        builder.Services.AddSingleton<SyncService>();
        builder.Services.AddSingleton<PrivacyDisplayService>();

        builder.Services.AddSingleton<ISecretStore, SecureStorageSecretStore>();
        builder.Services.AddSingleton<IAppPrefs, MauiAppPrefs>();
        builder.Services.AddSingleton<AiSettingsService>();
        builder.Services.AddSingleton<McpChatHost>();
        builder.Services.AddSingleton<LocalChatTools>();
        builder.Services.AddSingleton<ChatService>();
        builder.Services.AddSingleton<TranscriptionService>();
        builder.Services.AddSingleton<AssignmentGenerator>();

        builder.Services.AddSingleton(AudioManager.Current);

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TeacherDbContext>>();
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
        }

        app.Services.GetRequiredService<AiSettingsService>().ApplyPrivacyEnv();

        return app;
    }
}
