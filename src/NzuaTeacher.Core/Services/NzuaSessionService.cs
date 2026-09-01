using NzuaMcp.Nzua;

namespace NzuaTeacher.Core.Services;

/// <summary>
/// Обгортка навколо NzuaClient/NzuaSessionStore: статус сесії, ручний вхід із
/// міжпроцесним single-flight (той самий патерн, що в nzua-mcp Program.cs).
/// </summary>
public sealed class NzuaSessionService
{
    private readonly NzuaSessionStore _store;
    private readonly NzuaClient _client;

    public event Action? Changed;

    public NzuaSessionService(NzuaSessionStore store, NzuaClient client)
    {
        _store = store;
        _client = client;
    }

    public bool HasSession => _client.Session is not null;

    public DateTimeOffset? ExpiresAt =>
        _client.Session?.ExpiresAt is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    /// <summary>Відкриває вікно входу (або підхоплює сесію іншого процесу). Викликається з UI.</summary>
    public async Task LoginAsync()
    {
        var session = await ManualAuthenticate();
        _client.SetSession(session);
        Changed?.Invoke();
    }

    public void Logout()
    {
        _store.Clear();
        _client.SetSession(null);
        Changed?.Invoke();
    }

    /// <summary>Callback для NzuaClient при протуханні сесії під час запиту.</summary>
    public async Task<NzuaSession> OnSessionExpired()
    {
        var session = await ManualAuthenticate();
        Changed?.Invoke();
        return session;
    }

    private async Task<NzuaSession> ManualAuthenticate()
    {
        // Single-flight між процесами: якщо інший інстанс (напр., MCP сервер Claude Desktop)
        // уже відкрив вікно входу — чекаємо і підхоплюємо його сесію з диска.
        using var loginLock = await CrossProcessLock.TryAcquireAsync(
            _store.LoginLockFilePath, timeout: TimeSpan.FromMinutes(6), pollInterval: TimeSpan.FromMilliseconds(500));

        var fromDisk = _store.Load();
        if (fromDisk is not null && !Equals(fromDisk, _client.Session))
            return fromDisk;

        var session = await NzuaAuth.ManualLogin();
        _store.Save(session);
        return session;
    }
}
