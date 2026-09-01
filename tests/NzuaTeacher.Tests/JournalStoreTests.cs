using Microsoft.EntityFrameworkCore;
using NzuaMcp.Nzua;
using NzuaTeacher.Core.Data;
using NzuaTeacher.Core.Models;
using NzuaTeacher.Core.Services;
using Xunit;

namespace NzuaTeacher.Tests;

public class JournalStoreTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly JournalStore _store;
    private readonly OutboxService _outbox;

    public JournalStoreTests()
    {
        _store = new JournalStore(_factory);
        _outbox = new OutboxService(_factory);
    }

    private static JournalPage MakePage(params Mark[] marks) => new(
        new Journal("101", "7-А", "Алгебра", "Вчитель"),
        [new Student("9001", "Учень Тестовий", "", 1)],
        [new Lesson("555", 10, "09", "Урок", 1, [])],
        marks.ToList(),
        [new HomeworkEntry("555", "10.09", "1", "Тема з сервера", "", "Впр. 1", "")],
        new Pagination(1, 1, [1]),
        "12345");

    [Fact]
    public async Task ApplyPull_PopulatesCache()
    {
        var result = await _store.ApplyPullAsync("101", MakePage(new Mark("555", "9001", "15", "10")), PullScope.All);

        Assert.Equal(1, result.NewOrChangedMarks);
        var grid = await _store.GetGridAsync("101");
        Assert.NotNull(grid);
        Assert.Single(grid!.Students);
        Assert.Single(grid.Lessons);
        Assert.Equal("10", grid.Cells[("555", "9001")].Value);
        Assert.Equal("Тема з сервера", grid.HomeworkBySchedule["555"].Topic);
    }

    [Fact]
    public async Task ApplyPull_DoesNotOverwritePendingLocalMark()
    {
        await _store.ApplyPullAsync("101", MakePage(new Mark("555", "9001", "12", "7")), PullScope.All);
        await _outbox.SetMarkLocallyAsync("101", "555", "9001", 15, null); // локально: 10

        // Сервер досі каже 7 → локальне значення зберігається, конфлікту немає (base = 12).
        await _store.ApplyPullAsync("101", MakePage(new Mark("555", "9001", "12", "7")), PullScope.All);

        var grid = await _store.GetGridAsync("101");
        Assert.Equal("15", grid!.Cells[("555", "9001")].MarkId);
        Assert.True(grid.Cells[("555", "9001")].IsPending);
        Assert.Equal(0, grid.ConflictCount);
    }

    [Fact]
    public async Task ApplyPull_FlagsConflict_WhenServerChangedSinceBase()
    {
        await _store.ApplyPullAsync("101", MakePage(new Mark("555", "9001", "12", "7")), PullScope.All);
        await _outbox.SetMarkLocallyAsync("101", "555", "9001", 15, null);

        // Хтось на сервері тим часом поставив 9 (mark_value_id 14).
        var result = await _store.ApplyPullAsync("101", MakePage(new Mark("555", "9001", "14", "9")), PullScope.All);

        Assert.Equal(1, result.ConflictsDetected);
        var grid = await _store.GetGridAsync("101");
        Assert.True(grid!.Cells[("555", "9001")].IsConflict);
        // Локальне значення не перетерто.
        Assert.Equal("15", grid.Cells[("555", "9001")].MarkId);
    }

    [Fact]
    public async Task ApplyPull_ScopeMarksOnly_LeavesHomeworkUntouched()
    {
        await _store.ApplyPullAsync("101", MakePage(new Mark("555", "9001", "15", "10")), PullScope.All);

        var page2 = new JournalPage(
            new Journal("101", "7-А", "Алгебра", "Вчитель"),
            [new Student("9001", "Учень Тестовий", "", 1)],
            [new Lesson("555", 10, "09", "Урок", 1, [])],
            [new Mark("555", "9001", "16", "11")],
            [new HomeworkEntry("555", "10.09", "1", "ІНША ТЕМА", "", "", "")],
            new Pagination(1, 1, [1]),
            "12345");

        await _store.ApplyPullAsync("101", page2, PullScope.Marks);

        var grid = await _store.GetGridAsync("101");
        Assert.Equal("11", grid!.Cells[("555", "9001")].Value);
        Assert.Equal("Тема з сервера", grid.HomeworkBySchedule["555"].Topic); // не чіпали
    }

    [Fact]
    public async Task LocalLesson_SurvivesPull()
    {
        await _store.ApplyPullAsync("101", MakePage(), PullScope.All);
        await _outbox.AddLessonLocallyAsync("101", 1, "Урок", "2026-09-20", "2", "2-й", "3", null, null);

        await _store.ApplyPullAsync("101", MakePage(), PullScope.All);

        var grid = await _store.GetGridAsync("101");
        Assert.Equal(2, grid!.Lessons.Count);
        Assert.Contains(grid.Lessons, l => l.IsLocal);
    }

    public void Dispose() => _factory.Dispose();
}
