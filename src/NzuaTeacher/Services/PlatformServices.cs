using NzuaTeacher.Core.Abstractions;

namespace NzuaTeacher.Services;

/// <summary>API-ключі у Windows DPAPI-сховищі поточного користувача.</summary>
public sealed class SecureStorageSecretStore : ISecretStore
{
    public Task<string?> GetAsync(string key) => SecureStorage.Default.GetAsync(key);

    public Task SetAsync(string key, string value) => SecureStorage.Default.SetAsync(key, value);

    public void Remove(string key) => SecureStorage.Default.Remove(key);
}

public sealed class MauiAppPrefs : IAppPrefs
{
    public string Get(string key, string defaultValue) => Preferences.Default.Get(key, defaultValue);

    public void Set(string key, string value) => Preferences.Default.Set(key, value);

    public bool GetBool(string key, bool defaultValue) => Preferences.Default.Get(key, defaultValue);

    public void SetBool(string key, bool value) => Preferences.Default.Set(key, value);
}
