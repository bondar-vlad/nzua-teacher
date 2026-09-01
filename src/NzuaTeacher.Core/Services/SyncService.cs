using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzuaMcp.Nzua;
using NzuaMcp.Nzua.Api;
using NzuaTeacher.Core.Data;
using NzuaTeacher.Core.Models;

namespace NzuaTeacher.Core.Services;

public sealed record PushOutcome(long OpId, string Summary, PendingOpStatus Status, string? Error);

/// <summary>
/// Синхронізація з NZ.UA: pull знімка журналу в кеш і push вибраних pending-операцій
/// (свіжий pull → перевірка конфліктів → батч-запис → verify-after-write → фінальний pull).
/// </summary>
public sealed class SyncService(
    IDbContextFactory<TeacherDbContext> dbFactory,
    JournalStore journalStore,
    JournalApi journalApi,
    MarksApi marksApi,
    LessonsApi lessonsApi,
    HomeTasksApi homeTasksApi)
{
    private static readonly JsonSerializerOptions Json = new();

    public event Action<string>? Progress;

    public async Task RefreshJournalListAsync(string? semesterId = null)
    {
        Report("Отримуємо список журналів…");
        var list = semesterId is null
            ? await journalApi.GetJournalList()
            : await journalApi.ChangeSemester(semesterId);
        await journalStore.ApplyJournalListAsync(list, semesterId);
        Report("Список журналів оновлено.");
    }

    public async Task<PullResult> PullJournalAsync(string journalId, PullScope scope)
    {
        Report("Завантажуємо журнал із NZ.UA…");
        var page = await journalApi.GetAll(journalId, (done, total) => Report($"Сторінка {done}/{total}…"));
        var result = await journalStore.ApplyPullAsync(journalId, page, scope);

        // Форму уроку кешуємо для офлайн-діалогів (не критично, якщо не вдасться).
        try
        {
            var form = await journalApi.GetLessonForm(journalId);
            await journalStore.SaveLessonFormAsync(journalId, form);
        }
        catch
        {
            // ігноруємо: форма — допоміжний кеш
        }

        await LogAsync(journalId, "pull", $"Уроки: {result.NewOrChangedLessons}, оцінки: {result.NewOrChangedMarks}, теми/ДЗ: {result.NewOrChangedHomework}, конфлікти: {result.ConflictsDetected}", true);
        return result;
    }

    /// <summary>Надсилає вибрані операції. Повертає результат по кожній.</summary>
    public async Task<List<PushOutcome>> PushAsync(IReadOnlyCollection<long> opIds)
    {
        var outcomes = new List<PushOutcome>();

        await using var db = await dbFactory.CreateDbContextAsync();
        var ops = await db.PendingOps
            .Where(p => opIds.Contains(p.Id) && p.Status == PendingOpStatus.Pending)
            .OrderBy(p => p.CreatedAtUtc)
            .ToListAsync();

        foreach (var journalGroup in ops.GroupBy(o => o.JournalId))
        {
            var journalId = journalGroup.Key;

            // 1. Свіжий стан із сервера + виявлення конфліктів для оцінок/ДЗ.
            Report($"Журнал {journalId}: перевіряємо актуальний стан…");
            JournalPage fresh;
            try
            {
                fresh = await journalApi.GetAll(journalId);
            }
            catch (Exception ex)
            {
                foreach (var op in journalGroup)
                    outcomes.Add(new PushOutcome(op.Id, op.Summary, PendingOpStatus.Failed, $"Не вдалося отримати журнал: {ex.Message}"));
                continue;
            }

            var serverMarks = fresh.Marks.ToDictionary(m => OutboxService.MarkKey(m.ScheduleId, m.StudentId));
            var serverHw = fresh.Homework.ToDictionary(h => h.ScheduleId);
            var toPush = new List<PendingOp>();

            foreach (var op in journalGroup)
            {
                var conflict = DetectConflict(op, serverMarks, serverHw);
                if (conflict is not null)
                {
                    op.Status = PendingOpStatus.Conflict;
                    op.Error = conflict;
                    outcomes.Add(new PushOutcome(op.Id, op.Summary, PendingOpStatus.Conflict, conflict));
                }
                else
                {
                    toPush.Add(op);
                }
            }

            // 2. Батч-запис за типами (порядок: уроки → теми/ДЗ → оцінки → видалення).
            await PushLessonAdds(journalId, toPush, outcomes);
            await PushLessonEdits(journalId, toPush, outcomes);
            await PushHomework(journalId, toPush, outcomes);
            await PushMarks(journalId, toPush, outcomes);
            await PushLessonDeletes(journalId, toPush, outcomes);

            // 3. Verify-after-write: фінальний pull і звірка očікуваних значень.
            Report($"Журнал {journalId}: перевіряємо результат запису…");
            try
            {
                var verified = await journalApi.GetAll(journalId);
                VerifyMarks(toPush, verified, outcomes);
                await journalStore.ApplyPullAsync(journalId, verified, PullScope.All);
            }
            catch (Exception ex)
            {
                Report($"Не вдалося перевірити результат: {ex.Message}");
            }

            await db.SaveChangesAsync();
        }

        // Статуси, виставлені під час батчів, зберігаємо.
        foreach (var outcome in outcomes)
        {
            var op = await db.PendingOps.FindAsync(outcome.OpId);
            if (op is null) continue;
            op.Status = outcome.Status;
            op.Error = outcome.Error;
            if (outcome.Status == PendingOpStatus.Synced)
            {
                var mark = await db.Marks.FirstOrDefaultAsync(m => m.JournalId == op.JournalId && op.TargetKey == "mark:" + m.ScheduleId + ":" + m.StudentId);
                if (mark is not null) mark.IsLocal = false;
            }
            await LogAsync(op.JournalId, "push", $"{op.Summary} → {outcome.Status}{(outcome.Error is null ? "" : $" ({outcome.Error})")}", outcome.Status == PendingOpStatus.Synced);
        }
        await db.SaveChangesAsync();

        // Успішні операції прибираємо з черги.
        var syncedIds = outcomes.Where(o => o.Status == PendingOpStatus.Synced).Select(o => o.OpId).ToList();
        if (syncedIds.Count > 0)
        {
            var synced = await db.PendingOps.Where(p => syncedIds.Contains(p.Id)).ToListAsync();
            db.PendingOps.RemoveRange(synced);
            await db.SaveChangesAsync();
        }

        Report("Синхронізацію завершено.");
        return outcomes;
    }

    private static string? DetectConflict(
        PendingOp op,
        Dictionary<string, Mark> serverMarks,
        Dictionary<string, HomeworkEntry> serverHw)
    {
        if (op.BaseSnapshotJson is null) return null;

        switch (op.Type)
        {
            case PendingOpType.SetMark:
            {
                var baseSnap = JsonSerializer.Deserialize<MarkSnapshot>(op.BaseSnapshotJson);
                serverMarks.TryGetValue(op.TargetKey, out var server);
                var serverMarkId = server?.MarkId;
                if (!string.Equals(baseSnap?.MarkId, serverMarkId, StringComparison.Ordinal))
                    return $"На сервері значення змінилося: було «{baseSnap?.Value ?? "порожньо"}», зараз «{server?.Value ?? "порожньо"}»";
                return null;
            }
            case PendingOpType.SetHomework:
            {
                var baseSnap = JsonSerializer.Deserialize<HomeworkSnapshot>(op.BaseSnapshotJson);
                var scheduleId = JsonSerializer.Deserialize<HomeworkPayload>(op.PayloadJson)!.ScheduleId;
                serverHw.TryGetValue(scheduleId, out var server);
                if (baseSnap?.Topic is not null && server is not null &&
                    baseSnap.Topic != server.Topic && !string.IsNullOrEmpty(server.Topic))
                    return $"Тему на сервері вже змінено: «{server.Topic}»";
                return null;
            }
            default:
                return null;
        }
    }

    private async Task PushMarks(string journalId, List<PendingOp> ops, List<PushOutcome> outcomes)
    {
        var markOps = ops.Where(o => o.Type == PendingOpType.SetMark).ToList();
        if (markOps.Count == 0) return;

        Report($"Виставляємо оцінки ({markOps.Count})…");
        var entries = new List<FlatMarkEntry>();
        var mapping = new List<PendingOp>();
        foreach (var op in markOps)
        {
            var p = JsonSerializer.Deserialize<SetMarkPayload>(op.PayloadJson)!;
            if (p.ScheduleId.StartsWith("local-", StringComparison.Ordinal))
            {
                outcomes.Add(new PushOutcome(op.Id, op.Summary, PendingOpStatus.Failed,
                    "Оцінка стоїть на локальному уроці — спочатку синхронізуйте додавання уроку."));
                continue;
            }
            entries.Add(new FlatMarkEntry(p.ScheduleId, p.StudentId, p.MarkId, p.Comment));
            mapping.Add(op);
        }
        if (entries.Count == 0) return;

        var results = await marksApi.BulkSetMarksFlat(entries, (done, total) => Report($"Оцінки: {done}/{total}"));
        for (var i = 0; i < mapping.Count && i < results.Count; i++)
        {
            var r = results[i];
            outcomes.Add(new PushOutcome(mapping[i].Id, mapping[i].Summary,
                r.Success ? PendingOpStatus.Synced : PendingOpStatus.Failed, r.Error));
        }
    }

    private async Task PushLessonAdds(string journalId, List<PendingOp> ops, List<PushOutcome> outcomes)
    {
        var addOps = ops.Where(o => o.Type == PendingOpType.AddLesson).ToList();
        if (addOps.Count == 0) return;

        Report($"Додаємо уроки ({addOps.Count})…");
        var paramsList = addOps.Select(op =>
        {
            var p = JsonSerializer.Deserialize<AddLessonPayload>(op.PayloadJson)!;
            return new AddLessonParams(journalId, p.LessonTypeId, p.LessonDate, p.BuzzerId, p.RoomId, "not", p.ForNus, p.NusLessonTypeId);
        }).ToList();

        var results = await lessonsApi.BatchAddLessons(paramsList, (done, total) => Report($"Уроки: {done}/{total}"));
        for (var i = 0; i < addOps.Count && i < results.Count; i++)
        {
            var r = results[i];
            outcomes.Add(new PushOutcome(addOps[i].Id, addOps[i].Summary,
                r.Success ? PendingOpStatus.Synced : PendingOpStatus.Failed, r.Error));

            if (r.Success)
            {
                // Локальний тимчасовий урок буде замінено серверним при фінальному pull.
                var payload = JsonSerializer.Deserialize<AddLessonPayload>(addOps[i].PayloadJson)!;
                await using var db = await dbFactory.CreateDbContextAsync();
                var localLesson = await db.Lessons.FirstOrDefaultAsync(l =>
                    l.JournalId == journalId && l.ScheduleId == payload.LocalScheduleId);
                if (localLesson is not null)
                {
                    db.Lessons.Remove(localLesson);
                    await db.SaveChangesAsync();
                }
            }
        }
    }

    private async Task PushLessonEdits(string journalId, List<PendingOp> ops, List<PushOutcome> outcomes)
    {
        var editOps = ops.Where(o => o.Type == PendingOpType.EditLesson).ToList();
        if (editOps.Count == 0) return;

        Report($"Оновлюємо уроки ({editOps.Count})…");
        var paramsList = editOps.Select(op =>
        {
            var p = JsonSerializer.Deserialize<EditLessonPayload>(op.PayloadJson)!;
            return new EditLessonParams(p.ScheduleId, journalId, p.LessonTypeId, p.LessonDate, p.BuzzerId, p.RoomId, "not", p.ForNus, p.NusLessonTypeId);
        }).ToList();

        var results = await lessonsApi.BatchEditLessons(paramsList, (done, total) => Report($"Уроки: {done}/{total}"));
        for (var i = 0; i < editOps.Count && i < results.Count; i++)
        {
            var r = results[i];
            outcomes.Add(new PushOutcome(editOps[i].Id, editOps[i].Summary,
                r.Success ? PendingOpStatus.Synced : PendingOpStatus.Failed, r.Error));
        }
    }

    private async Task PushLessonDeletes(string journalId, List<PendingOp> ops, List<PushOutcome> outcomes)
    {
        var delOps = ops.Where(o => o.Type == PendingOpType.DeleteLesson).ToList();
        if (delOps.Count == 0) return;

        Report($"Видаляємо уроки ({delOps.Count})…");
        var ids = delOps.Select(op => JsonSerializer.Deserialize<DeleteLessonPayload>(op.PayloadJson)!.ScheduleId).ToList();
        var results = await lessonsApi.BatchDeleteLessons(ids, (done, total) => Report($"Видалення: {done}/{total}"));
        for (var i = 0; i < delOps.Count && i < results.Count; i++)
        {
            var r = results[i];
            outcomes.Add(new PushOutcome(delOps[i].Id, delOps[i].Summary,
                r.Success ? PendingOpStatus.Synced : PendingOpStatus.Failed, r.Error));
        }
    }

    private async Task PushHomework(string journalId, List<PendingOp> ops, List<PushOutcome> outcomes)
    {
        var hwOps = ops.Where(o => o.Type == PendingOpType.SetHomework).ToList();
        if (hwOps.Count == 0) return;

        Report($"Записуємо теми/ДЗ ({hwOps.Count})…");
        var entries = new List<SetHomeworkEntry>();
        var mapping = new List<PendingOp>();
        foreach (var op in hwOps)
        {
            var p = JsonSerializer.Deserialize<HomeworkPayload>(op.PayloadJson)!;
            if (p.ScheduleId.StartsWith("local-", StringComparison.Ordinal))
            {
                outcomes.Add(new PushOutcome(op.Id, op.Summary, PendingOpStatus.Failed,
                    "Тема стоїть на локальному уроці — спочатку синхронізуйте додавання уроку."));
                continue;
            }
            entries.Add(new SetHomeworkEntry(p.ScheduleId, p.Topic, p.LessonNumber, p.Homework, p.HomeworkTo, null, null, p.ForNus));
            mapping.Add(op);
        }
        if (entries.Count == 0) return;

        var results = await homeTasksApi.BatchSetHomework(journalId, entries, (done, total) => Report($"Теми/ДЗ: {done}/{total}"));
        for (var i = 0; i < mapping.Count && i < results.Count; i++)
        {
            var r = results[i];
            outcomes.Add(new PushOutcome(mapping[i].Id, mapping[i].Summary,
                r.Success ? PendingOpStatus.Synced : PendingOpStatus.Failed, r.Error));
        }
    }

    private static void VerifyMarks(List<PendingOp> pushed, JournalPage verified, List<PushOutcome> outcomes)
    {
        var serverMarks = verified.Marks.ToDictionary(m => OutboxService.MarkKey(m.ScheduleId, m.StudentId));
        for (var i = 0; i < outcomes.Count; i++)
        {
            var outcome = outcomes[i];
            if (outcome.Status != PendingOpStatus.Synced) continue;
            var op = pushed.FirstOrDefault(p => p.Id == outcome.OpId);
            if (op is null || op.Type != PendingOpType.SetMark) continue;

            var payload = JsonSerializer.Deserialize<SetMarkPayload>(op.PayloadJson)!;
            serverMarks.TryGetValue(op.TargetKey, out var server);

            if (payload.MarkId == SpecialMarks.Delete)
            {
                if (server is not null)
                    outcomes[i] = outcome with { Status = PendingOpStatus.Failed, Error = "Перевірка: оцінка не видалилася на сервері." };
            }
            else if (server?.MarkId != payload.MarkId.ToString())
            {
                outcomes[i] = outcome with { Status = PendingOpStatus.Failed, Error = $"Перевірка: на сервері «{server?.Value ?? "порожньо"}», очікували «{MarkDisplay.Get(payload.MarkId)}»." };
            }
        }
    }

    private async Task LogAsync(string journalId, string action, string details, bool success)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.SyncLog.Add(new SyncLogEntry
        {
            AtUtc = DateTime.UtcNow,
            JournalId = journalId,
            Action = action,
            Details = details,
            Success = success,
        });
        await db.SaveChangesAsync();
    }

    public async Task<List<SyncLogEntry>> GetLogAsync(int take = 50)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SyncLog.AsNoTracking().OrderByDescending(l => l.AtUtc).Take(take).ToListAsync();
    }

    private void Report(string message) => Progress?.Invoke(message);
}
