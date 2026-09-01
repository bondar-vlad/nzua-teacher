using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NzuaMcp.Mcp;
using NzuaMcp.Mcp.Tools;
using NzuaMcp.Nzua;
using NzuaMcp.Nzua.Api;

namespace NzuaTeacher.Core.AI;

/// <summary>
/// Вбудований MCP-сервер nzua-mcp + клієнт в одному процесі через in-memory pipe-транспорт.
/// Використовує ті самі singleton-и NzuaClient/JournalApi, що й прямий UI —
/// спільний кеш сторінок і спільна інвалідація після записів.
/// </summary>
public sealed class McpChatHost : IAsyncDisposable
{
    private readonly NzuaSessionStore _sessionStore;
    private readonly NzuaClient _nzuaClient;
    private readonly JournalApi _journalApi;
    private readonly MarksApi _marksApi;
    private readonly LessonsApi _lessonsApi;
    private readonly HomeTasksApi _homeTasksApi;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private ServiceProvider? _serverProvider;
    private McpClient? _client;

    public McpChatHost(
        NzuaSessionStore sessionStore,
        NzuaClient nzuaClient,
        JournalApi journalApi,
        MarksApi marksApi,
        LessonsApi lessonsApi,
        HomeTasksApi homeTasksApi)
    {
        _sessionStore = sessionStore;
        _nzuaClient = nzuaClient;
        _journalApi = journalApi;
        _marksApi = marksApi;
        _lessonsApi = lessonsApi;
        _homeTasksApi = homeTasksApi;
    }

    public async Task<McpClient> GetClientAsync(CancellationToken ct = default)
    {
        if (_client is not null) return _client;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_client is not null) return _client;

            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(_sessionStore);
            services.AddSingleton(_nzuaClient);
            services.AddSingleton(_journalApi);
            services.AddSingleton(_marksApi);
            services.AddSingleton(_lessonsApi);
            services.AddSingleton(_homeTasksApi);
            services.AddSingleton<JournalTools>();

            services
                .AddMcpServer(options =>
                {
                    var version = typeof(JournalTools).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
                    options.ServerInfo = new Implementation { Name = "nzua-mcp", Version = version };
                    options.ServerInstructions =
                        "Вбудований MCP-сервер журналів NZ.UA у застосунку «НЗ Вчитель». " +
                        "Перед записом читайте актуальний стан (nzua_get_journal), масові зміни робіть одним викликом entriesJson, " +
                        "ID типів/часу/кабінетів беріть лише з nzua_get_form. Семестрові оцінки не виставляйте автоматично.";
                })
                .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
                .WithToolsFromAssembly(typeof(JournalTools).Assembly)
                .WithPromptsFromAssembly(typeof(JournalTools).Assembly)
                .WithResourcesFromAssembly(typeof(JournalTools).Assembly)
                .WithCompleteHandler((ctx, _) =>
                    ValueTask.FromResult(NzuaCompletions.Resolve(ctx.Params, _journalApi.CachedJournals)));

            _serverProvider = services.BuildServiceProvider();
            foreach (var hosted in _serverProvider.GetServices<IHostedService>())
                await hosted.StartAsync(ct);

            var transport = new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream());

            _client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            return _client;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<IList<McpClientTool>> ListToolsAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        return await client.ListToolsAsync(cancellationToken: ct);
    }

    public async Task<IList<McpClientPrompt>> ListPromptsAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        return await client.ListPromptsAsync(cancellationToken: ct);
    }

    public async Task<GetPromptResult> GetPromptAsync(string name, IReadOnlyDictionary<string, object?>? args, CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        return await client.GetPromptAsync(name, args, cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        if (_serverProvider is not null)
        {
            foreach (var hosted in _serverProvider.GetServices<IHostedService>())
                await hosted.StopAsync(CancellationToken.None);
            await _serverProvider.DisposeAsync();
        }
    }
}
