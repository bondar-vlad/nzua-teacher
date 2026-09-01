using Microsoft.EntityFrameworkCore;
using NzuaMcp.Nzua;
using NzuaTeacher.Core.Data;
using NzuaTeacher.Core.Services;
using Xunit;

namespace NzuaTeacher.Tests;

public class OutboxServiceTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly OutboxService _outbox;

    public OutboxServiceTests() => _outbox = new OutboxService(_factory);

    [Fact]
    public async Task SetMark_CreatesCacheRowAndPendingOp()
    {
        await _outbox.SetMarkLocallyAsync("101", "555", "9001", MarkValueResolver.Resolve(grade: 10), null);

        await using var db = _factory.CreateDbContext();
        var mark = await db.Marks.SingleAsync();
        Assert.Equal("15", mark.MarkId); // 10 балів → mark_value_id 15
        Assert.Equal("10", mark.Value);
        Assert.True(mark.IsLocal);

        var op = await db.PendingOps.SingleAsync();
        Assert.Equal(PendingOpType.SetMark, op.Type);
        Assert.Equal(PendingOpStatus.Pending, op.Status);
        Assert.Equal("mark:555:9001", op.TargetKey);
    }

    [Fact]
    public async Task SetMark_Twice_MergesIntoSingleOp_PreservingBase()
    {
        // Початкове серверне значення в кеші.
        await using (var db = _factory.CreateDbContext())
        {
            db.Marks.Add(new CachedMark { JournalId = "101", ScheduleId = "555", StudentId = "9001", MarkId = "12", Value = "7" });
            await db.SaveChangesAsync();
        }

        await _outbox.SetMarkLocallyAsync("101", "555", "9001", 15, null);
        await _outbox.SetMarkLocallyAsync("101", "555", "9001", 17, null);

        await using var check = _factory.CreateDbContext();
        var op = await check.PendingOps.SingleAsync();
        Assert.Contains("\"MarkId\":17", op.PayloadJson);
        Assert.Contains("\"MarkId\":\"12\"", op.BaseSnapshotJson); // base — перше серверне значення

        var mark = await check.Marks.SingleAsync();
        Assert.Equal("12", MarkIdOfBase(op.BaseSnapshotJson!));
        Assert.Equal("17", mark.MarkId);
    }

    [Fact]
    public async Task DiscardOp_RestoresBaseValue()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Marks.Add(new CachedMark { JournalId = "101", ScheduleId = "555", StudentId = "9001", MarkId = "12", Value = "7" });
            await db.SaveChangesAsync();
        }

        await _outbox.SetMarkLocallyAsync("101", "555", "9001", 15, null);
        var op = (await _outbox.GetPendingAsync()).Single();
        await _outbox.DiscardOpAsync(op.Id);

        await using var check = _factory.CreateDbContext();
        var mark = await check.Marks.SingleAsync();
        Assert.Equal("12", mark.MarkId);
        Assert.False(mark.IsLocal);
        Assert.Empty(await check.PendingOps.ToListAsync());
    }

    [Fact]
    public async Task DeleteLocalLesson_CancelsRelatedOps()
    {
        await _outbox.AddLessonLocallyAsync("101", 1, "Урок", "2026-09-10", "2", "1-й урок", "3", null, null);

        await using var db = _factory.CreateDbContext();
        var lesson = await db.Lessons.SingleAsync();
        Assert.True(lesson.IsLocal);
        Assert.StartsWith("local-", lesson.ScheduleId);

        await _outbox.SetHomeworkLocallyAsync("101", lesson.ScheduleId, "Тема", null, null, null, false);
        await _outbox.DeleteLessonLocallyAsync("101", lesson.ScheduleId);

        await using var check = _factory.CreateDbContext();
        Assert.Empty(await check.PendingOps.ToListAsync());
        Assert.Empty(await check.Lessons.ToListAsync());
    }

    [Fact]
    public async Task DeleteLesson_WithMarks_Throws()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Lessons.Add(new CachedLesson { JournalId = "101", ScheduleId = "555", Day = 1, Month = "09", ColumnIndex = 1 });
            db.Marks.Add(new CachedMark { JournalId = "101", ScheduleId = "555", StudentId = "9001", MarkId = "15", Value = "10" });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _outbox.DeleteLessonLocallyAsync("101", "555"));
    }

    [Fact]
    public async Task SetHomework_MergesFields()
    {
        await _outbox.SetHomeworkLocallyAsync("101", "555", "Тема 1", null, null, null, false);
        await _outbox.SetHomeworkLocallyAsync("101", "555", null, null, "Впр. 25", null, false);

        await using var db = _factory.CreateDbContext();
        var op = await db.PendingOps.SingleAsync();
        var payload = System.Text.Json.JsonSerializer.Deserialize<HomeworkPayload>(op.PayloadJson)!;
        Assert.Equal("Тема 1", payload.Topic);
        Assert.Equal("Впр. 25", payload.Homework);

        var hw = await db.Homework.SingleAsync();
        Assert.Equal("Тема 1", hw.Topic);
        Assert.Equal("Впр. 25", hw.Homework);
    }

    private static string MarkIdOfBase(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<MarkSnapshot>(json)!.MarkId!;

    public void Dispose() => _factory.Dispose();
}
