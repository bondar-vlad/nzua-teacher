using Microsoft.EntityFrameworkCore;
using NzuaMcp.Nzua;
using NzuaTeacher.Core.Data;
using NzuaTeacher.Core.Models;
using NzuaTeacher.Core.Services;
using Xunit;

namespace NzuaTeacher.Tests;

public class SemesterFilteringTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly JournalStore _store;
    private readonly OutboxService _outbox;

    public SemesterFilteringTests()
    {
        _store = new JournalStore(_factory);
        _outbox = new OutboxService(_factory);
    }

    private static JournalListData ListFor(string semesterId, params (string Id, string Subject, string Class)[] journals) => new(
        journals.Select(j => new JournalListItem(j.Id, j.Subject, j.Class)).ToList(),
        [],
        [],
        [
            new SemesterInfo("100", "I семестр", semesterId == "100"),
            new SemesterInfo("200", "II семестр", semesterId == "200"),
        ],
        // NZ.UA повертає тут підпис семестру, а не ID — саме це й ламало фільтрацію.
        semesterId == "100" ? "I семестр" : "II семестр");

    [Fact]
    public async Task StoresSemesterId_NotLabel()
    {
        await _store.ApplyJournalListAsync(ListFor("100", ("1", "Алгебра", "7-А")));

        var (semesters, currentId) = await _store.GetSemestersAsync();
        Assert.Equal(2, semesters.Count);
        Assert.Equal("100", currentId);

        var journals = await _store.GetJournalsAsync("100");
        Assert.Single(journals);
        Assert.Equal("100", journals[0].SemesterId);
    }

    [Fact]
    public async Task SwitchingSemester_ShowsOnlySelectedSemesterJournals()
    {
        await _store.ApplyJournalListAsync(ListFor("100", ("1", "Алгебра", "7-А"), ("2", "Геометрія", "7-А")));
        await _store.ApplyJournalListAsync(ListFor("200", ("3", "Алгебра", "8-Б")), "200");

        var second = await _store.GetJournalsAsync("200");
        Assert.Single(second);
        Assert.Equal("3", second[0].JournalId);

        // Журнали попереднього семестру залишаються в кеші (для офлайн-перемикання назад), але не в цьому списку.
        Assert.DoesNotContain(second, j => j.SemesterId == "100");
        Assert.Equal(2, (await _store.GetJournalsAsync("100")).Count);

        var (_, currentId) = await _store.GetSemestersAsync();
        Assert.Equal("200", currentId);
    }

    [Fact]
    public async Task LegacyRowsWithLabelAsSemester_ArePurged()
    {
        // Запис зі старої версії, де в SemesterId зберігався підпис замість ID.
        await using (var db = _factory.CreateDbContext())
        {
            db.Journals.Add(new CachedJournal { JournalId = "999", Subject = "Старе", ClassName = "9-В", SemesterId = "I семестр" });
            await db.SaveChangesAsync();
        }

        await _store.ApplyJournalListAsync(ListFor("100", ("1", "Алгебра", "7-А")));

        await using var check = _factory.CreateDbContext();
        Assert.Null(await check.Journals.FindAsync("999"));
    }

    [Fact]
    public async Task StaleJournal_WithPendingChanges_IsKept()
    {
        await _store.ApplyJournalListAsync(ListFor("100", ("1", "Алгебра", "7-А"), ("2", "Геометрія", "7-А")));
        await _outbox.SetMarkLocallyAsync("1", "555", "9001", 15, null);

        // Журналу "1" більше немає на сервері, але в ньому є ненадіслана оцінка.
        await _store.ApplyJournalListAsync(ListFor("100", ("2", "Геометрія", "7-А")));

        await using var db = _factory.CreateDbContext();
        Assert.NotNull(await db.Journals.FindAsync("1"));
        Assert.Single(await db.PendingOps.ToListAsync());
    }

    [Fact]
    public async Task RemovingJournal_ClearsItsCachedData()
    {
        await _store.ApplyJournalListAsync(ListFor("100", ("1", "Алгебра", "7-А"), ("2", "Геометрія", "7-А")));
        await _store.ApplyPullAsync("1", new JournalPage(
            new Journal("1", "7-А", "Алгебра", "Вчитель"),
            [new Student("9001", "Учень", "", 1)],
            [new Lesson("555", 10, "09", "Урок", 1, [])],
            [new Mark("555", "9001", "15", "10")],
            [],
            new Pagination(1, 1, [1]),
            "100"), PullScope.All);

        // Журнал зник у тому самому семестрі — чистимо його дані.
        await _store.ApplyJournalListAsync(ListFor("100", ("2", "Геометрія", "7-А")));

        await using var db = _factory.CreateDbContext();
        Assert.Null(await db.Journals.FindAsync("1"));
        Assert.Empty(await db.Students.Where(s => s.JournalId == "1").ToListAsync());
        Assert.Empty(await db.Marks.Where(m => m.JournalId == "1").ToListAsync());
        Assert.Empty(await db.Lessons.Where(l => l.JournalId == "1").ToListAsync());
    }

    [Fact]
    public async Task GetJournals_WithoutSemester_ReturnsAll()
    {
        await _store.ApplyJournalListAsync(ListFor("100", ("1", "Алгебра", "7-А")));
        await _store.ApplyJournalListAsync(ListFor("200", ("3", "Алгебра", "8-Б")), "200");

        Assert.Equal(2, (await _store.GetJournalsAsync()).Count);
    }

    public void Dispose() => _factory.Dispose();
}
