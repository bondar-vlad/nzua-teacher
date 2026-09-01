using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzuaMcp.Nzua;
using NzuaTeacher.Core.Data;
using NzuaTeacher.Core.Models;

namespace NzuaTeacher.Core.Services;

/// <summary>
/// Локальний кеш журналів у SQLite: читання для UI та застосування pull-знімків з NZ.UA.
/// Клітинки з незасинхронізованими локальними правками ніколи не перетираються сервером.
/// </summary>
public sealed class JournalStore(IDbContextFactory<TeacherDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public async Task<List<CachedJournal>> GetJournalsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Journals.AsNoTracking()
            .OrderBy(j => j.ClassName).ThenBy(j => j.Subject)
            .ToListAsync();
    }

    public async Task ApplyJournalListAsync(JournalListData data)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.Journals.ToDictionaryAsync(j => j.JournalId);
        var incoming = new HashSet<string>();

        foreach (var item in data.Journals)
        {
            incoming.Add(item.JournalId);
            if (existing.TryGetValue(item.JournalId, out var row))
            {
                row.Subject = item.Subject;
                row.ClassName = item.ClassName;
                row.SemesterId = data.CurrentSemester;
            }
            else
            {
                db.Journals.Add(new CachedJournal
                {
                    JournalId = item.JournalId,
                    Subject = item.Subject,
                    ClassName = item.ClassName,
                    SemesterId = data.CurrentSemester,
                });
            }
        }

        // Журнали, що зникли зі списку (інший семестр), лишаємо в кеші — вони можуть мати pending-операції.
        await SetSettingAsync(db, "semesters", JsonSerializer.Serialize(data.Semesters, Json));
        await SetSettingAsync(db, "currentSemester", data.CurrentSemester);
        await db.SaveChangesAsync();
    }

    public async Task<(List<SemesterInfo> Semesters, string? Current)> GetSemestersAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var semJson = await GetSettingAsync(db, "semesters");
        var current = await GetSettingAsync(db, "currentSemester");
        var semesters = semJson is null
            ? []
            : JsonSerializer.Deserialize<List<SemesterInfo>>(semJson) ?? [];
        return (semesters, current);
    }

    public async Task<JournalGrid?> GetGridAsync(string journalId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var journal = await db.Journals.AsNoTracking().FirstOrDefaultAsync(j => j.JournalId == journalId);
        if (journal is null) return null;

        var students = await db.Students.AsNoTracking()
            .Where(s => s.JournalId == journalId).OrderBy(s => s.OrderIndex).ToListAsync();
        var lessons = await db.Lessons.AsNoTracking()
            .Where(l => l.JournalId == journalId).OrderBy(l => l.ColumnIndex).ToListAsync();
        var marks = await db.Marks.AsNoTracking()
            .Where(m => m.JournalId == journalId).ToListAsync();
        var homework = await db.Homework.AsNoTracking()
            .Where(h => h.JournalId == journalId).ToListAsync();
        var pending = await db.PendingOps.AsNoTracking()
            .Where(p => p.JournalId == journalId && (p.Status == PendingOpStatus.Pending || p.Status == PendingOpStatus.Conflict))
            .ToListAsync();

        var pendingKeys = pending.Where(p => p.Status == PendingOpStatus.Pending).Select(p => p.TargetKey).ToHashSet();
        var conflictKeys = pending.Where(p => p.Status == PendingOpStatus.Conflict).Select(p => p.TargetKey).ToHashSet();

        var cells = new Dictionary<(string, string), GridCell>();
        foreach (var m in marks)
        {
            var key = OutboxService.MarkKey(m.ScheduleId, m.StudentId);
            cells[(m.ScheduleId, m.StudentId)] = new GridCell(
                m.ScheduleId, m.StudentId, m.MarkId, m.Value, m.Comment,
                IsPending: pendingKeys.Contains(key),
                IsConflict: conflictKeys.Contains(key));
        }

        return new JournalGrid
        {
            Journal = journal,
            Students = students,
            Lessons = lessons,
            Cells = cells,
            HomeworkBySchedule = homework.ToDictionary(h => h.ScheduleId),
            PendingCount = pendingKeys.Count,
            ConflictCount = conflictKeys.Count,
        };
    }

    /// <summary>
    /// Застосовує свіжий знімок журналу з NZ.UA до кешу в межах вибраного обсягу.
    /// Рядки з pending-операціями не перетираються; розбіжність із base-знімком позначається як конфлікт.
    /// </summary>
    public async Task<PullResult> ApplyPullAsync(string journalId, JournalPage page, PullScope scope)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var journal = await db.Journals.FirstOrDefaultAsync(j => j.JournalId == journalId);
        if (journal is null)
        {
            journal = new CachedJournal { JournalId = journalId };
            db.Journals.Add(journal);
        }
        journal.Subject = page.Journal.Subject;
        journal.ClassName = page.Journal.ClassName;
        journal.TeacherName = page.Journal.TeacherName;
        journal.SemesterId = page.SemesterId;
        journal.LastPulledAt = DateTime.UtcNow;

        // Учні оновлюються завжди.
        var oldStudents = await db.Students.Where(s => s.JournalId == journalId).ToListAsync();
        db.Students.RemoveRange(oldStudents);
        db.Students.AddRange(page.Students.Select(s => new CachedStudent
        {
            JournalId = journalId,
            StudentId = s.StudentId,
            Name = s.Name,
            OrderIndex = s.Index,
        }));

        var pendingOps = await db.PendingOps
            .Where(p => p.JournalId == journalId && (p.Status == PendingOpStatus.Pending || p.Status == PendingOpStatus.Conflict))
            .ToListAsync();

        int changedLessons = 0, changedMarks = 0, changedHomework = 0, conflicts = 0;

        if (scope.HasFlag(PullScope.Lessons))
            changedLessons = await ApplyLessons(db, journalId, page, pendingOps);

        if (scope.HasFlag(PullScope.Marks))
            (changedMarks, conflicts) = await ApplyMarks(db, journalId, page, pendingOps, conflicts);

        if (scope.HasFlag(PullScope.Homework))
            (changedHomework, conflicts) = await ApplyHomework(db, journalId, page, pendingOps, conflicts);

        await db.SaveChangesAsync();
        return new PullResult(changedLessons, changedMarks, changedHomework, conflicts);
    }

    private static async Task<int> ApplyLessons(TeacherDbContext db, string journalId, JournalPage page, List<PendingOp> pendingOps)
    {
        var old = await db.Lessons.Where(l => l.JournalId == journalId).ToListAsync();
        var oldById = old.ToDictionary(l => l.ScheduleId);
        var changed = 0;

        // Локально додані уроки (ще без серверного scheduleId) зберігаємо.
        var localOnly = old.Where(l => l.IsLocal).ToList();
        db.Lessons.RemoveRange(old.Where(l => !l.IsLocal));

        foreach (var l in page.Lessons)
        {
            if (!oldById.TryGetValue(l.ScheduleId, out var prev) ||
                prev.Day != l.Day || prev.Month != l.Month || prev.LessonType != l.LessonType)
                changed++;

            db.Lessons.Add(new CachedLesson
            {
                JournalId = journalId,
                ScheduleId = l.ScheduleId,
                Day = l.Day,
                Month = l.Month,
                LessonType = l.LessonType,
                ColumnIndex = l.ColumnIndex,
                IsLocal = false,
            });
        }

        // Локальні уроки — в кінець сітки.
        var maxColumn = page.Lessons.Count == 0 ? 0 : page.Lessons.Max(l => l.ColumnIndex);
        foreach (var (local, i) in localOnly.Select((l, i) => (l, i)))
            local.ColumnIndex = maxColumn + 1 + i;

        return changed;
    }

    private static async Task<(int Changed, int Conflicts)> ApplyMarks(
        TeacherDbContext db, string journalId, JournalPage page, List<PendingOp> pendingOps, int conflicts)
    {
        var old = await db.Marks.Where(m => m.JournalId == journalId).ToListAsync();
        var oldByKey = old.ToDictionary(m => (m.ScheduleId, m.StudentId));
        var markOps = pendingOps
            .Where(p => p.Type == PendingOpType.SetMark)
            .ToDictionary(p => p.TargetKey);
        var changed = 0;

        db.Marks.RemoveRange(old.Where(m => !markOps.ContainsKey(OutboxService.MarkKey(m.ScheduleId, m.StudentId))));

        var serverKeys = new HashSet<string>();
        foreach (var m in page.Marks)
        {
            var key = OutboxService.MarkKey(m.ScheduleId, m.StudentId);
            serverKeys.Add(key);

            if (markOps.TryGetValue(key, out var op))
            {
                // Локальна правка існує: сервер не перетирає її, але перевіряємо конфлікт.
                var baseValue = op.BaseSnapshotJson is null
                    ? null
                    : JsonSerializer.Deserialize<MarkSnapshot>(op.BaseSnapshotJson);
                if (baseValue?.MarkId != m.MarkId && op.Status == PendingOpStatus.Pending)
                {
                    op.Status = PendingOpStatus.Conflict;
                    op.Error = $"На сервері вже інше значення: «{m.Value}»";
                    conflicts++;
                }
                continue;
            }

            if (!oldByKey.TryGetValue((m.ScheduleId, m.StudentId), out var prev) || prev.MarkId != m.MarkId)
                changed++;

            db.Marks.Add(new CachedMark
            {
                JournalId = journalId,
                ScheduleId = m.ScheduleId,
                StudentId = m.StudentId,
                MarkId = m.MarkId,
                Value = m.Value,
                Comment = m.Comment,
                IsLocal = false,
            });
        }

        // Оцінка зникла на сервері, а в нас на неї pending-правка з base-значенням → теж конфлікт не потрібен:
        // якщо base був null (ми додавали нову) — все ок; якщо base був, а сервер видалив — конфлікт.
        foreach (var (key, op) in markOps)
        {
            if (op.Status != PendingOpStatus.Pending || serverKeys.Contains(key)) continue;
            if (op.BaseSnapshotJson is not null &&
                JsonSerializer.Deserialize<MarkSnapshot>(op.BaseSnapshotJson)?.MarkId is not null)
            {
                op.Status = PendingOpStatus.Conflict;
                op.Error = "Оцінку видалено на сервері";
                conflicts++;
            }
        }

        return (changed, conflicts);
    }

    private static async Task<(int Changed, int Conflicts)> ApplyHomework(
        TeacherDbContext db, string journalId, JournalPage page, List<PendingOp> pendingOps, int conflicts)
    {
        var old = await db.Homework.Where(h => h.JournalId == journalId).ToListAsync();
        var oldByKey = old.ToDictionary(h => h.ScheduleId);
        var hwOps = pendingOps
            .Where(p => p.Type == PendingOpType.SetHomework)
            .ToDictionary(p => p.TargetKey);
        var changed = 0;

        db.Homework.RemoveRange(old.Where(h => !hwOps.ContainsKey(OutboxService.HomeworkKey(h.ScheduleId))));

        foreach (var h in page.Homework)
        {
            var key = OutboxService.HomeworkKey(h.ScheduleId);
            if (hwOps.TryGetValue(key, out var op))
            {
                var baseHw = op.BaseSnapshotJson is null
                    ? null
                    : JsonSerializer.Deserialize<HomeworkSnapshot>(op.BaseSnapshotJson);
                if (op.Status == PendingOpStatus.Pending &&
                    baseHw is not null && (baseHw.Topic != h.Topic || baseHw.Homework != h.Homework))
                {
                    op.Status = PendingOpStatus.Conflict;
                    op.Error = "Тему/ДЗ уже змінено на сервері";
                    conflicts++;
                }
                continue;
            }

            if (!oldByKey.TryGetValue(h.ScheduleId, out var prev) || prev.Topic != h.Topic || prev.Homework != h.Homework)
                changed++;

            db.Homework.Add(new CachedHomework
            {
                JournalId = journalId,
                ScheduleId = h.ScheduleId,
                Date = h.Date,
                LessonNumber = h.LessonNumber,
                Topic = h.Topic,
                HomeworkDate = h.HomeworkDate,
                Homework = h.Homework,
                Substitution = h.Substitution,
                IsLocal = false,
            });
        }

        return (changed, conflicts);
    }

    public async Task SaveLessonFormAsync(string journalId, LessonFormData form)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.LessonForms.FindAsync(journalId);
        var json = JsonSerializer.Serialize(form, Json);
        if (row is null)
            db.LessonForms.Add(new CachedLessonForm { JournalId = journalId, FormJson = json, CachedAtUtc = DateTime.UtcNow });
        else
        {
            row.FormJson = json;
            row.CachedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    public async Task<LessonFormData?> GetLessonFormAsync(string journalId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.LessonForms.FindAsync(journalId);
        return row is null ? null : JsonSerializer.Deserialize<LessonFormData>(row.FormJson);
    }

    private static async Task<string?> GetSettingAsync(TeacherDbContext db, string key) =>
        (await db.Settings.FindAsync(key))?.Value;

    private static async Task SetSettingAsync(TeacherDbContext db, string key, string value)
    {
        var row = await db.Settings.FindAsync(key);
        if (row is null)
            db.Settings.Add(new SettingEntity { Key = key, Value = value });
        else
            row.Value = value;
    }
}

public sealed record MarkSnapshot(string? MarkId, string? Value);
public sealed record HomeworkSnapshot(string? Topic, string? LessonNumber, string? Homework, string? HomeworkTo);
