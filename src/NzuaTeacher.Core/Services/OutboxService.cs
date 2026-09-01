using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzuaMcp.Nzua;
using NzuaTeacher.Core.Data;

namespace NzuaTeacher.Core.Services;

// Payload-и pending-операцій (серіалізуються в PendingOp.PayloadJson).
public sealed record SetMarkPayload(string ScheduleId, string StudentId, int MarkId, string? Comment);
public sealed record AddLessonPayload(
    string LocalScheduleId, int LessonTypeId, string LessonDate, string BuzzerId, string RoomId,
    bool? ForNus, int? NusLessonTypeId, string BuzzerLabel, string TypeLabel);
public sealed record EditLessonPayload(
    string ScheduleId, int LessonTypeId, string LessonDate, string BuzzerId, string RoomId,
    bool? ForNus, int? NusLessonTypeId);
public sealed record DeleteLessonPayload(string ScheduleId);
public sealed record HomeworkPayload(
    string ScheduleId, string? Topic, string? LessonNumber, string? Homework, string? HomeworkTo, bool ForNus);

/// <summary>
/// Outbox локальних змін: кожне редагування миттєво застосовується до кешу
/// та стає у чергу на синхронізацію. Повторна правка тієї ж цілі зливається в одну операцію.
/// </summary>
public sealed class OutboxService(IDbContextFactory<TeacherDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions Json = new();

    public static string MarkKey(string scheduleId, string studentId) => $"mark:{scheduleId}:{studentId}";
    public static string HomeworkKey(string scheduleId) => $"hw:{scheduleId}";
    public static string LessonAddKey(string localId) => $"lesson-add:{localId}";
    public static string LessonEditKey(string scheduleId) => $"lesson-edit:{scheduleId}";
    public static string LessonDeleteKey(string scheduleId) => $"lesson-del:{scheduleId}";

    public event Action? Changed;

    // ------------------------------------------------------------------ marks

    public async Task SetMarkLocallyAsync(string journalId, string scheduleId, string studentId, int markId, string? comment)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var existing = await db.Marks.FirstOrDefaultAsync(m =>
            m.JournalId == journalId && m.ScheduleId == scheduleId && m.StudentId == studentId);

        var key = MarkKey(scheduleId, studentId);
        var op = await FindOpAsync(db, journalId, key);

        // Base-знімок фіксуємо лише при першій правці клітинки.
        var baseSnapshot = op?.BaseSnapshotJson ?? JsonSerializer.Serialize(
            new MarkSnapshot(existing?.IsLocal == true ? null : existing?.MarkId, existing?.Value), Json);

        var display = MarkDisplay.Get(markId);
        var payload = new SetMarkPayload(scheduleId, studentId, markId, comment);

        if (markId == SpecialMarks.Delete)
        {
            if (existing is not null) db.Marks.Remove(existing);
        }
        else if (existing is null)
        {
            db.Marks.Add(new CachedMark
            {
                JournalId = journalId,
                ScheduleId = scheduleId,
                StudentId = studentId,
                MarkId = markId.ToString(),
                Value = display,
                Comment = comment,
                IsLocal = true,
            });
        }
        else
        {
            existing.MarkId = markId.ToString();
            existing.Value = display;
            existing.Comment = comment;
            existing.IsLocal = true;
        }

        UpsertOp(db, op, journalId, PendingOpType.SetMark, key,
            JsonSerializer.Serialize(payload, Json), baseSnapshot,
            $"Оцінка «{display}»");

        await db.SaveChangesAsync();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- lessons

    public async Task AddLessonLocallyAsync(
        string journalId, int lessonTypeId, string typeLabel, string lessonDate,
        string buzzerId, string buzzerLabel, string roomId, bool? forNus, int? nusLessonTypeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var localId = $"local-{Guid.NewGuid():N}";
        var maxColumn = await db.Lessons.Where(l => l.JournalId == journalId)
            .Select(l => (int?)l.ColumnIndex).MaxAsync() ?? 0;

        var date = DateTime.TryParse(lessonDate, out var d) ? d : DateTime.Today;
        db.Lessons.Add(new CachedLesson
        {
            JournalId = journalId,
            ScheduleId = localId,
            Day = date.Day,
            Month = date.ToString("MM"),
            LessonType = typeLabel,
            ColumnIndex = maxColumn + 1,
            IsLocal = true,
        });

        var payload = new AddLessonPayload(localId, lessonTypeId, lessonDate, buzzerId, roomId, forNus, nusLessonTypeId, buzzerLabel, typeLabel);
        UpsertOp(db, null, journalId, PendingOpType.AddLesson, LessonAddKey(localId),
            JsonSerializer.Serialize(payload, Json), null,
            $"Новий урок {lessonDate} ({typeLabel})");

        await db.SaveChangesAsync();
        Changed?.Invoke();
    }

    public async Task EditLessonLocallyAsync(
        string journalId, string scheduleId, int lessonTypeId, string typeLabel, string lessonDate,
        string buzzerId, string roomId, bool? forNus, int? nusLessonTypeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var lesson = await db.Lessons.FirstOrDefaultAsync(l => l.JournalId == journalId && l.ScheduleId == scheduleId);
        if (lesson is not null)
        {
            var date = DateTime.TryParse(lessonDate, out var d) ? d : DateTime.Today;
            lesson.Day = date.Day;
            lesson.Month = date.ToString("MM");
            lesson.LessonType = typeLabel;
        }

        // Редагування локально доданого уроку — правимо payload операції додавання.
        if (scheduleId.StartsWith("local-", StringComparison.Ordinal))
        {
            var addOp = await FindOpAsync(db, journalId, LessonAddKey(scheduleId));
            if (addOp is not null)
            {
                var prev = JsonSerializer.Deserialize<AddLessonPayload>(addOp.PayloadJson)!;
                var updated = prev with
                {
                    LessonTypeId = lessonTypeId,
                    LessonDate = lessonDate,
                    BuzzerId = buzzerId,
                    RoomId = roomId,
                    ForNus = forNus,
                    NusLessonTypeId = nusLessonTypeId,
                    TypeLabel = typeLabel,
                };
                addOp.PayloadJson = JsonSerializer.Serialize(updated, Json);
                addOp.Summary = $"Новий урок {lessonDate} ({typeLabel})";
                await db.SaveChangesAsync();
                Changed?.Invoke();
                return;
            }
        }

        var key = LessonEditKey(scheduleId);
        var op = await FindOpAsync(db, journalId, key);
        var payload = new EditLessonPayload(scheduleId, lessonTypeId, lessonDate, buzzerId, roomId, forNus, nusLessonTypeId);
        UpsertOp(db, op, journalId, PendingOpType.EditLesson, key,
            JsonSerializer.Serialize(payload, Json), null,
            $"Зміна уроку {lessonDate} ({typeLabel})");

        await db.SaveChangesAsync();
        Changed?.Invoke();
    }

    public async Task DeleteLessonLocallyAsync(string journalId, string scheduleId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var hasMarks = await db.Marks.AnyAsync(m => m.JournalId == journalId && m.ScheduleId == scheduleId);
        if (hasMarks)
            throw new InvalidOperationException("Не можна видалити урок, на якому вже є оцінки. Спочатку видаліть оцінки.");

        var lesson = await db.Lessons.FirstOrDefaultAsync(l => l.JournalId == journalId && l.ScheduleId == scheduleId);
        if (lesson is not null) db.Lessons.Remove(lesson);

        var hw = await db.Homework.FirstOrDefaultAsync(h => h.JournalId == journalId && h.ScheduleId == scheduleId);
        if (hw is not null) db.Homework.Remove(hw);

        if (scheduleId.StartsWith("local-", StringComparison.Ordinal))
        {
            // Видалення ще не надісланого уроку скасовує пов’язані операції.
            var related = await db.PendingOps.Where(p => p.JournalId == journalId &&
                (p.TargetKey == LessonAddKey(scheduleId) || p.TargetKey == HomeworkKey(scheduleId))).ToListAsync();
            db.PendingOps.RemoveRange(related);
        }
        else
        {
            var key = LessonDeleteKey(scheduleId);
            var op = await FindOpAsync(db, journalId, key);
            // Видалення уроку скасовує його pending-редагування/ДЗ.
            var stale = await db.PendingOps.Where(p => p.JournalId == journalId &&
                (p.TargetKey == LessonEditKey(scheduleId) || p.TargetKey == HomeworkKey(scheduleId))).ToListAsync();
            db.PendingOps.RemoveRange(stale);
            UpsertOp(db, op, journalId, PendingOpType.DeleteLesson, key,
                JsonSerializer.Serialize(new DeleteLessonPayload(scheduleId), Json), null,
                $"Видалення уроку #{scheduleId}");
        }

        await db.SaveChangesAsync();
        Changed?.Invoke();
    }

    // --------------------------------------------------------------- homework

    public async Task SetHomeworkLocallyAsync(
        string journalId, string scheduleId, string? topic, string? lessonNumber,
        string? homework, string? homeworkTo, bool forNus)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var existing = await db.Homework.FirstOrDefaultAsync(h => h.JournalId == journalId && h.ScheduleId == scheduleId);
        var key = HomeworkKey(scheduleId);
        var op = await FindOpAsync(db, journalId, key);

        var baseSnapshot = op?.BaseSnapshotJson ?? JsonSerializer.Serialize(
            new HomeworkSnapshot(
                existing?.IsLocal == true ? null : existing?.Topic,
                existing?.LessonNumber, existing?.Homework, existing?.HomeworkDate), Json);

        if (existing is null)
        {
            var lesson = await db.Lessons.FirstOrDefaultAsync(l => l.JournalId == journalId && l.ScheduleId == scheduleId);
            db.Homework.Add(new CachedHomework
            {
                JournalId = journalId,
                ScheduleId = scheduleId,
                Date = lesson is null ? "" : $"{lesson.Day:00}.{lesson.Month}",
                LessonNumber = lessonNumber ?? "",
                Topic = topic ?? "",
                HomeworkDate = homeworkTo ?? "",
                Homework = homework ?? "",
                Substitution = "",
                IsLocal = true,
            });
        }
        else
        {
            if (topic is not null) existing.Topic = topic;
            if (lessonNumber is not null) existing.LessonNumber = lessonNumber;
            if (homework is not null) existing.Homework = homework;
            if (homeworkTo is not null) existing.HomeworkDate = homeworkTo;
            existing.IsLocal = true;
        }

        // Злиття: нові non-null поля перекривають попередні, решта зберігається.
        HomeworkPayload payload;
        if (op is not null && JsonSerializer.Deserialize<HomeworkPayload>(op.PayloadJson) is { } prev)
        {
            payload = new HomeworkPayload(
                scheduleId,
                topic ?? prev.Topic,
                lessonNumber ?? prev.LessonNumber,
                homework ?? prev.Homework,
                homeworkTo ?? prev.HomeworkTo,
                forNus);
        }
        else
        {
            payload = new HomeworkPayload(scheduleId, topic, lessonNumber, homework, homeworkTo, forNus);
        }

        UpsertOp(db, op, journalId, PendingOpType.SetHomework, key,
            JsonSerializer.Serialize(payload, Json), baseSnapshot,
            $"Тема/ДЗ: {Truncate(topic ?? homework ?? "зміни", 40)}");

        await db.SaveChangesAsync();
        Changed?.Invoke();
    }

    // ----------------------------------------------------------------- запити

    public async Task<List<PendingOp>> GetPendingAsync(string? journalId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.PendingOps.AsNoTracking()
            .Where(p => p.Status == PendingOpStatus.Pending || p.Status == PendingOpStatus.Conflict || p.Status == PendingOpStatus.Failed);
        if (journalId is not null)
            query = query.Where(p => p.JournalId == journalId);
        return await query.OrderBy(p => p.CreatedAtUtc).ToListAsync();
    }

    public async Task<int> GetPendingCountAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PendingOps.CountAsync(p =>
            p.Status == PendingOpStatus.Pending || p.Status == PendingOpStatus.Conflict || p.Status == PendingOpStatus.Failed);
    }

    public async Task DiscardOpAsync(long opId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var op = await db.PendingOps.FindAsync(opId);
        if (op is null) return;

        // Відкат локальної правки: повертаємо base-значення в кеш.
        if (op.Type == PendingOpType.SetMark && op.BaseSnapshotJson is not null)
        {
            var payload = JsonSerializer.Deserialize<SetMarkPayload>(op.PayloadJson)!;
            var baseSnap = JsonSerializer.Deserialize<MarkSnapshot>(op.BaseSnapshotJson);
            var mark = await db.Marks.FirstOrDefaultAsync(m =>
                m.JournalId == op.JournalId && m.ScheduleId == payload.ScheduleId && m.StudentId == payload.StudentId);
            if (baseSnap?.MarkId is null)
            {
                if (mark is not null) db.Marks.Remove(mark);
            }
            else if (mark is not null)
            {
                mark.MarkId = baseSnap.MarkId;
                mark.Value = baseSnap.Value ?? baseSnap.MarkId;
                mark.IsLocal = false;
            }
        }
        else if (op.Type == PendingOpType.AddLesson)
        {
            var payload = JsonSerializer.Deserialize<AddLessonPayload>(op.PayloadJson)!;
            var lesson = await db.Lessons.FirstOrDefaultAsync(l =>
                l.JournalId == op.JournalId && l.ScheduleId == payload.LocalScheduleId);
            if (lesson is not null) db.Lessons.Remove(lesson);
        }

        db.PendingOps.Remove(op);
        await db.SaveChangesAsync();
        Changed?.Invoke();
    }

    /// <summary>Розвʼязання конфлікту «взяти моє»: операція знову стає Pending із новим base.</summary>
    public async Task RetakeMineAsync(long opId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var op = await db.PendingOps.FindAsync(opId);
        if (op is null) return;
        op.Status = PendingOpStatus.Pending;
        op.Error = null;
        op.BaseSnapshotJson = null; // конфлікт свідомо переписуємо
        await db.SaveChangesAsync();
        Changed?.Invoke();
    }

    private static async Task<PendingOp?> FindOpAsync(TeacherDbContext db, string journalId, string targetKey) =>
        await db.PendingOps.FirstOrDefaultAsync(p =>
            p.JournalId == journalId && p.TargetKey == targetKey &&
            (p.Status == PendingOpStatus.Pending || p.Status == PendingOpStatus.Conflict || p.Status == PendingOpStatus.Failed));

    private static void UpsertOp(
        TeacherDbContext db, PendingOp? existing, string journalId, PendingOpType type,
        string targetKey, string payloadJson, string? baseSnapshotJson, string summary)
    {
        if (existing is null)
        {
            db.PendingOps.Add(new PendingOp
            {
                JournalId = journalId,
                Type = type,
                TargetKey = targetKey,
                PayloadJson = payloadJson,
                BaseSnapshotJson = baseSnapshotJson,
                Status = PendingOpStatus.Pending,
                Summary = summary,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }
        else
        {
            existing.PayloadJson = payloadJson;
            existing.Summary = summary;
            existing.Status = PendingOpStatus.Pending;
            existing.Error = null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
