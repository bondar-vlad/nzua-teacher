using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using NzuaMcp.Nzua;
using NzuaTeacher.Core.Data;
using NzuaTeacher.Core.Services;

namespace NzuaTeacher.Core.AI;

/// <summary>
/// Локальні AI-тули поверх SQLite-кешу — працюють без з'єднання з NZ.UA.
/// Імена учнів у виводі — псевдоніми (як і в MCP-тулах), якщо не ввімкнено реальні імена.
/// </summary>
public sealed class LocalChatTools(JournalStore journalStore, OutboxService outbox)
{
    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(GetCachedJournals, "local_cached_journals",
            "Локальний кеш: список журналів вчителя з часом останнього оновлення. Працює офлайн."),
        AIFunctionFactory.Create(GetCachedJournal, "local_cached_journal",
            "Локальний кеш: знімок журналу (учні, уроки, оцінки, теми/ДЗ) без мережі. Працює офлайн."),
        AIFunctionFactory.Create(GetPendingChanges, "local_pending_changes",
            "Локальні незасинхронізовані зміни (outbox): що буде надіслано в NZ.UA під час синхронізації."),
    ];

    private static bool ShowRealNames =>
        string.Equals(Environment.GetEnvironmentVariable("NZUA_SHOW_REAL_NAMES"), "true", StringComparison.OrdinalIgnoreCase);

    private static string StudentLabel(CachedStudent s) =>
        NzuaPrivacy.StudentLabel(new Student(s.StudentId, s.Name, "", s.OrderIndex), ShowRealNames);

    [Description("Список журналів із локального кешу")]
    private async Task<string> GetCachedJournals()
    {
        var journals = await journalStore.GetJournalsAsync();
        if (journals.Count == 0) return "Кеш порожній. Потрібно виконати синхронізацію (pull) хоча б раз.";

        var sb = new StringBuilder("| journal_id | Клас | Предмет | Оновлено (UTC) |\n|---|---|---|---|\n");
        foreach (var j in journals)
            sb.AppendLine($"| {j.JournalId} | {j.ClassName} | {j.Subject} | {j.LastPulledAt:yyyy-MM-dd HH:mm} |");
        return sb.ToString();
    }

    [Description("Знімок журналу з локального кешу")]
    private async Task<string> GetCachedJournal(
        [Description("Ідентифікатор журналу (journal_id)")] string journalId)
    {
        var grid = await journalStore.GetGridAsync(journalId);
        if (grid is null) return $"Журнал {journalId} відсутній у кеші.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {grid.Journal.ClassName} — {grid.Journal.Subject} (кеш від {grid.Journal.LastPulledAt:yyyy-MM-dd HH:mm} UTC)");
        sb.AppendLine($"Учнів: {grid.Students.Count}, уроків: {grid.Lessons.Count}, незасинхронізованих змін: {grid.PendingCount}.");

        sb.AppendLine("\n## Уроки");
        sb.AppendLine("| schedule_id | Дата | Тип | Тема | ДЗ |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var l in grid.Lessons)
        {
            grid.HomeworkBySchedule.TryGetValue(l.ScheduleId, out var hw);
            sb.AppendLine($"| {l.ScheduleId} | {l.Day:00}.{l.Month} | {l.LessonType} | {hw?.Topic} | {hw?.Homework} |");
        }

        sb.AppendLine("\n## Оцінки (учень → урок: значення)");
        foreach (var s in grid.Students)
        {
            var marks = grid.Lessons
                .Select(l => grid.Cells.TryGetValue((l.ScheduleId, s.StudentId), out var c) ? $"{l.Day:00}.{l.Month}:{c.Value}{(c.IsPending ? "*" : "")}" : null)
                .Where(v => v is not null)
                .ToList();
            sb.AppendLine($"- {StudentLabel(s)} (id {s.StudentId}): {(marks.Count == 0 ? "—" : string.Join(", ", marks))}");
        }
        sb.AppendLine("\n(* — локальна зміна, ще не надіслана в NZ.UA)");
        return sb.ToString();
    }

    [Description("Незасинхронізовані локальні зміни")]
    private async Task<string> GetPendingChanges(
        [Description("journal_id або порожньо для всіх журналів")] string? journalId = null)
    {
        var ops = await outbox.GetPendingAsync(string.IsNullOrWhiteSpace(journalId) ? null : journalId);
        if (ops.Count == 0) return "Черга синхронізації порожня — всі зміни надіслані.";

        var sb = new StringBuilder("| Журнал | Тип | Опис | Статус |\n|---|---|---|---|\n");
        foreach (var op in ops)
            sb.AppendLine($"| {op.JournalId} | {TypeLabel(op.Type)} | {op.Summary} | {StatusLabel(op.Status)}{(op.Error is null ? "" : $": {op.Error}")} |");
        return sb.ToString();
    }

    internal static string TypeLabel(PendingOpType t) => t switch
    {
        PendingOpType.SetMark => "Оцінка",
        PendingOpType.AddLesson => "Новий урок",
        PendingOpType.EditLesson => "Зміна уроку",
        PendingOpType.DeleteLesson => "Видалення уроку",
        PendingOpType.SetHomework => "Тема/ДЗ",
        _ => t.ToString(),
    };

    internal static string StatusLabel(PendingOpStatus s) => s switch
    {
        PendingOpStatus.Pending => "очікує",
        PendingOpStatus.Synced => "надіслано",
        PendingOpStatus.Failed => "помилка",
        PendingOpStatus.Conflict => "конфлікт",
        _ => s.ToString(),
    };
}
