using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace NzuaTeacher.Core.AI;

public sealed record ToolCallRequest(string ToolName, string HumanSummary, string ArgumentsJson);

/// <summary>Повертає true, якщо вчитель підтвердив виконання операції запису.</summary>
public delegate Task<bool> ToolConfirmationHandler(ToolCallRequest request);

/// <summary>
/// Обгортка MCP-тула: прибирає зі схеми ключові слова, яких не розуміють деякі провайдери,
/// і перед записом у журнал питає підтвердження вчителя.
/// </summary>
public sealed class PreparedTool : DelegatingAIFunction
{
    private readonly ToolConfirmationHandler? _confirm;
    private readonly JsonElement _schema;

    public PreparedTool(AIFunction inner, ToolConfirmationHandler? confirm) : base(inner)
    {
        _confirm = confirm;
        _schema = ToolSchemaSanitizer.Sanitize(inner.JsonSchema);
    }

    public override JsonElement JsonSchema => _schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (_confirm is not null)
        {
            var argsJson = JsonSerializer.Serialize(
                arguments.ToDictionary(kv => kv.Key, kv => kv.Value),
                new JsonSerializerOptions { WriteIndented = true });

            var approved = await _confirm(new ToolCallRequest(Name, ToolCallSummarizer.Summarize(Name, arguments), argsJson));
            if (!approved)
                return "⛔ Вчитель відхилив цю операцію. Не повторюй її без нового явного прохання користувача.";
        }

        return await base.InvokeCoreAsync(arguments, cancellationToken);
    }

    /// <summary>Інструменти, що справді змінюють журнал (лише вони вимагають підтвердження).</summary>
    private static readonly HashSet<string> JournalWriteTools =
    [
        "nzua_set_marks",
        "nzua_add_lessons",
        "nzua_edit_lessons",
        "nzua_delete_lessons",
        "nzua_set_homework",
    ];

    public static bool RequiresConfirmation(McpClientTool tool)
    {
        if (tool.ProtocolTool.Annotations?.ReadOnlyHint == true) return false;
        return JournalWriteTools.Contains(tool.Name) || tool.ProtocolTool.Annotations?.DestructiveHint == true;
    }
}

/// <summary>Людський опис виклику тула українською для діалогу підтвердження.</summary>
public static class ToolCallSummarizer
{
    public static string Summarize(string toolName, IDictionary<string, object?> args)
    {
        string? Str(string key) => args.TryGetValue(key, out var v) ? v?.ToString() : null;

        int CountEntries()
        {
            var json = Str("entriesJson");
            if (string.IsNullOrWhiteSpace(json)) return 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
            }
            catch
            {
                return 0;
            }
        }

        var batch = CountEntries();
        return toolName switch
        {
            "nzua_set_marks" => batch > 0
                ? $"Виставити оцінки: {batch} запис(ів) одним пакетом."
                : $"Виставити оцінку учню {Str("studentId")} на уроці {Str("scheduleId")}: {Str("grade") ?? Str("specialMark") ?? "?"}",
            "nzua_add_lessons" => batch > 0
                ? $"Додати {batch} урок(ів) у журнал {Str("journalId")}."
                : $"Додати урок {Str("lessonDate")} у журнал {Str("journalId")}.",
            "nzua_edit_lessons" => batch > 0
                ? $"Змінити {batch} урок(ів) у журналі {Str("journalId")}."
                : $"Змінити урок {Str("scheduleId")} у журналі {Str("journalId")}.",
            "nzua_delete_lessons" => $"Видалити урок(и) {Str("scheduleIds")} з журналу {Str("journalId")}.",
            "nzua_set_homework" => batch > 0
                ? $"Записати теми/ДЗ: {batch} урок(ів) у журналі {Str("journalId")}."
                : $"Записати тему/ДЗ для уроку {Str("scheduleId")} у журналі {Str("journalId")}.",
            _ => $"Виконати операцію {toolName}.",
        };
    }
}

public sealed record ChatAttachment(byte[] Data, string MediaType, string FileName);

public sealed record ToolCallInfo(string ToolName, string Summary);

public sealed record ChatTurnResult(string Text, List<ToolCallInfo> ToolCalls);

/// <summary>
/// Чат із AI-помічником: історія, MCP-тули з підтвердженням записів,
/// локальні офлайн-тули поверх SQLite-кешу.
/// </summary>
public sealed class ChatService(McpChatHost mcpHost, LocalChatTools localTools)
{
    private readonly List<ChatMessage> _history = [];

    public IReadOnlyList<ChatMessage> History => _history;

    public ToolConfirmationHandler? ConfirmationHandler { get; set; }

    /// <summary>Обробник текстових дельт стрімінгу для UI.</summary>
    public Action<string>? OnDelta { get; set; }

    private const string SystemPrompt =
        "Ти — помічник українського вчителя в застосунку «НЗ Вчитель» для роботи з електронними журналами NZ.UA. " +
        "Відповідай українською, стисло і по суті. Доступні MCP-інструменти журналу: " +
        "спочатку nzua_list_journals для journal_id; перед будь-яким записом читай актуальний стан через nzua_get_journal; " +
        "масові зміни роби ОДНИМ викликом з entriesJson; ID типів уроків/часу/кабінетів бери лише з nzua_get_form. " +
        "Семестрові й річні оцінки не виставляй автоматично — лише готуй дані, рішення ухвалює вчитель. " +
        "ПІБ учнів можуть бути замінені стабільними псевдонімами (Учень-XXXXX) — це навмисне налаштування приватності, не намагайся їх розкрити. " +
        "Якщо немає з'єднання з NZ.UA — використовуй локальні тули local_* з кешу застосунку.";

    public void ResetConversation()
    {
        _history.Clear();
    }

    public async Task<ChatTurnResult> SendAsync(
        IChatClient chatClient,
        string userText,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationToken ct = default)
    {
        if (_history.Count == 0)
            _history.Add(new ChatMessage(ChatRole.System, SystemPrompt));

        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(userText))
            contents.Add(new TextContent(userText));
        foreach (var att in attachments)
        {
            if (att.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                contents.Add(new TextContent($"Вкладення «{att.FileName}»:\n{Encoding.UTF8.GetString(att.Data)}"));
            else
                contents.Add(new DataContent(att.Data, att.MediaType));
        }
        _history.Add(new ChatMessage(ChatRole.User, contents));

        var options = new ChatOptions
        {
            MaxOutputTokens = 4096,
            Tools = await BuildToolsAsync(ct),
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync(_history, options, ct))
        {
            updates.Add(update);
            var delta = update.Text;
            if (!string.IsNullOrEmpty(delta))
                OnDelta?.Invoke(delta);
        }

        var response = updates.ToChatResponse();
        _history.AddRange(response.Messages);

        var toolCalls = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(fc => new ToolCallInfo(
                fc.Name,
                ToolCallSummarizer.Summarize(fc.Name, fc.Arguments ?? new Dictionary<string, object?>())))
            .ToList();

        return new ChatTurnResult(response.Text, toolCalls);
    }

    /// <summary>Вставляє повідомлення MCP-промпта (сценарію) в історію і виконує хід.</summary>
    public async Task<ChatTurnResult> RunPromptScenarioAsync(
        IChatClient chatClient,
        string promptName,
        IReadOnlyDictionary<string, object?>? args,
        CancellationToken ct = default)
    {
        if (_history.Count == 0)
            _history.Add(new ChatMessage(ChatRole.System, SystemPrompt));

        var prompt = await mcpHost.GetPromptAsync(promptName, args, ct);
        foreach (var m in prompt.Messages)
        {
            var role = m.Role == ModelContextProtocol.Protocol.Role.Assistant ? ChatRole.Assistant : ChatRole.User;
            var text = m.Content is ModelContextProtocol.Protocol.TextContentBlock tb ? tb.Text : m.Content.ToString() ?? "";
            _history.Add(new ChatMessage(role, text));
        }

        var options = new ChatOptions
        {
            MaxOutputTokens = 4096,
            Tools = await BuildToolsAsync(ct),
        };

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chatClient.GetStreamingResponseAsync(_history, options, ct))
        {
            updates.Add(update);
            var delta = update.Text;
            if (!string.IsNullOrEmpty(delta))
                OnDelta?.Invoke(delta);
        }

        var response = updates.ToChatResponse();
        _history.AddRange(response.Messages);

        var toolCalls = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(fc => new ToolCallInfo(
                fc.Name,
                ToolCallSummarizer.Summarize(fc.Name, fc.Arguments ?? new Dictionary<string, object?>())))
            .ToList();

        return new ChatTurnResult(response.Text, toolCalls);
    }

    private async Task<List<AITool>> BuildToolsAsync(CancellationToken ct)
    {
        var tools = new List<AITool>();

        try
        {
            var mcpTools = await mcpHost.ListToolsAsync(ct);
            foreach (var tool in mcpTools)
            {
                var needsConfirm = PreparedTool.RequiresConfirmation(tool) && ConfirmationHandler is not null;
                tools.Add(new PreparedTool(tool, needsConfirm ? ConfirmationHandler : null));
            }
        }
        catch
        {
            // MCP-сервер недоступний — лишаються локальні тули.
        }

        tools.AddRange(localTools.GetTools());
        return tools;
    }
}
