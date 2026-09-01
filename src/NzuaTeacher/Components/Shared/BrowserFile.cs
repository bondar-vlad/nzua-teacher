namespace NzuaTeacher.Components.Shared;

/// <summary>Файл, отриманий із браузера (вставка Ctrl+V або перетягування).</summary>
public sealed class BrowserFile
{
    public string Name { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string Base64 { get; set; } = "";
    public bool TooLarge { get; set; }
}
