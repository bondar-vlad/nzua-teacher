namespace NzuaTeacher.Core.Abstractions;

/// <summary>Захищене сховище секретів (API-ключі). У застосунку — Windows DPAPI через SecureStorage.</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    void Remove(string key);
}

/// <summary>Прості налаштування (не секрети).</summary>
public interface IAppPrefs
{
    string Get(string key, string defaultValue);
    void Set(string key, string value);
    bool GetBool(string key, bool defaultValue);
    void SetBool(string key, bool value);
}
