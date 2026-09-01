using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzuaTeacher.Core.Data;

namespace NzuaTeacher.Tests;

/// <summary>In-memory SQLite фабрика: спільне відкрите зʼєднання на час тесту.</summary>
public sealed class TestDbFactory : IDbContextFactory<TeacherDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TeacherDbContext> _options;

    public TestDbFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TeacherDbContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = new TeacherDbContext(_options);
        db.Database.EnsureCreated();
    }

    public TeacherDbContext CreateDbContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}
