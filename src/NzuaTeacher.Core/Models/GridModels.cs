using Microsoft.EntityFrameworkCore;
using NzuaMcp.Nzua;
using NzuaTeacher.Core.Data;

namespace NzuaTeacher.Core.Models;

public sealed record GridCell(
    string ScheduleId,
    string StudentId,
    string MarkId,
    string Value,
    string? Comment,
    bool IsPending,
    bool IsConflict);

public sealed class JournalGrid
{
    public required CachedJournal Journal { get; init; }
    public required List<CachedStudent> Students { get; init; }
    public required List<CachedLesson> Lessons { get; init; }
    public required Dictionary<(string ScheduleId, string StudentId), GridCell> Cells { get; init; }
    public required Dictionary<string, CachedHomework> HomeworkBySchedule { get; init; }
    public required int PendingCount { get; init; }
    public required int ConflictCount { get; init; }
}

[Flags]
public enum PullScope
{
    None = 0,
    Lessons = 1,
    Marks = 2,
    Homework = 4,
    All = Lessons | Marks | Homework,
}

public sealed record PullResult(
    int NewOrChangedLessons,
    int NewOrChangedMarks,
    int NewOrChangedHomework,
    int ConflictsDetected);
