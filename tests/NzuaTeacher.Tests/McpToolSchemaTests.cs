using System.Text.Json;
using Microsoft.Extensions.AI;
using NzuaMcp.Nzua;
using NzuaMcp.Nzua.Api;
using NzuaTeacher.Core.AI;
using NzuaTeacher.Core.Data;
using NzuaTeacher.Core.Services;
using Xunit;

namespace NzuaTeacher.Tests;

/// <summary>Піднімає вбудований MCP-сервер у пам'яті й перевіряє схеми інструментів (без мережі).</summary>
public class McpToolSchemaTests
{
    private static McpChatHost CreateHost()
    {
        var store = new NzuaSessionStore(Path.Combine(Path.GetTempPath(), $"nzua-test-{Guid.NewGuid():N}.json"));
        var client = new NzuaClient();
        var journalApi = new JournalApi(client);
        return new McpChatHost(store, client, journalApi, new MarksApi(client), new LessonsApi(client), new HomeTasksApi(client));
    }

    [Fact]
    public async Task AllTools_AreExposed()
    {
        await using var host = CreateHost();
        var tools = await host.ListToolsAsync();

        Assert.Contains(tools, t => t.Name == "nzua_list_journals");
        Assert.Contains(tools, t => t.Name == "nzua_set_marks");
    }

    [Fact]
    public async Task OnlyJournalWriteTools_RequireConfirmation()
    {
        await using var host = CreateHost();
        var tools = await host.ListToolsAsync();

        // Читання не повинно смикати вчителя діалогом підтвердження.
        Assert.False(PreparedTool.RequiresConfirmation(tools.First(t => t.Name == "nzua_list_journals")));
        Assert.False(PreparedTool.RequiresConfirmation(tools.First(t => t.Name == "nzua_get_journal")));
        Assert.False(PreparedTool.RequiresConfirmation(tools.First(t => t.Name == "nzua_get_form")));
        Assert.False(PreparedTool.RequiresConfirmation(tools.First(t => t.Name == "nzua_session")));

        foreach (var name in new[] { "nzua_set_marks", "nzua_add_lessons", "nzua_edit_lessons", "nzua_delete_lessons", "nzua_set_homework" })
            Assert.True(PreparedTool.RequiresConfirmation(tools.First(t => t.Name == name)), name);
    }

    [Fact]
    public async Task SanitizedSchemas_HaveNoKeywordsRejectedByGemini()
    {
        await using var host = CreateHost();
        var tools = await host.ListToolsAsync();

        foreach (var tool in tools)
        {
            var sanitized = new PreparedTool(tool, null).JsonSchema;
            AssertClean(sanitized, tool.Name);
        }
    }

    [Fact]
    public async Task BuildTools_SanitizesLocalToolsToo()
    {
        using var db = new TestDbFactory();
        await using var host = CreateHost();
        var chat = new ChatService(host, new LocalChatTools(new JournalStore(db), new OutboxService(db)));

        var tools = await chat.BuildToolsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(tools, t => t.Name == "local_pending_changes");
        foreach (var tool in tools.OfType<AIFunction>())
            AssertClean(tool.JsonSchema, tool.Name);
    }

    private static void AssertClean(JsonElement element, string toolName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    Assert.False(prop.NameEquals("$schema"), $"{toolName}: лишився $schema");
                    Assert.False(prop.NameEquals("additionalProperties"), $"{toolName}: лишився additionalProperties");
                    Assert.False(prop.NameEquals("default"), $"{toolName}: лишився default");
                    if (prop.NameEquals("type"))
                        Assert.True(prop.Value.ValueKind is JsonValueKind.String, $"{toolName}: type має бути рядком");
                    AssertClean(prop.Value, toolName);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AssertClean(item, toolName);
                break;
        }
    }
}
