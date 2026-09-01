namespace NzuaTeacher.Core.Data;

/// <summary>Журнал у локальному кеші (заголовок зі списку журналів).</summary>
public class CachedJournal
{
    public string JournalId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string? TeacherName { get; set; }
    public string? SemesterId { get; set; }
    /// <summary>Час останнього успішного pull з NZ.UA (UTC). null — ще не тягнули.</summary>
    public DateTime? LastPulledAt { get; set; }
}

public class CachedStudent
{
    public long Id { get; set; }
    public string JournalId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string Name { get; set; } = "";
    public int OrderIndex { get; set; }
}

public class CachedLesson
{
    public long Id { get; set; }
    public string JournalId { get; set; } = "";
    public string ScheduleId { get; set; } = "";
    public int Day { get; set; }
    public string Month { get; set; } = "";
    public string? LessonType { get; set; }
    public int ColumnIndex { get; set; }
    /// <summary>Урок створено локально й ще не надіслано на сервер.</summary>
    public bool IsLocal { get; set; }
}

public class CachedMark
{
    public long Id { get; set; }
    public string JournalId { get; set; } = "";
    public string ScheduleId { get; set; } = "";
    public string StudentId { get; set; } = "";
    /// <summary>mark_value_id як рядок (сумісно з Nzua Mark.MarkId).</summary>
    public string MarkId { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Comment { get; set; }
    public bool IsLocal { get; set; }
}

public class CachedHomework
{
    public long Id { get; set; }
    public string JournalId { get; set; } = "";
    public string ScheduleId { get; set; } = "";
    public string Date { get; set; } = "";
    public string LessonNumber { get; set; } = "";
    public string Topic { get; set; } = "";
    public string HomeworkDate { get; set; } = "";
    public string Homework { get; set; } = "";
    public string Substitution { get; set; } = "";
    public bool IsLocal { get; set; }
}

/// <summary>Кешована форма уроку (buzzer/room/типи) для офлайн-діалогів.</summary>
public class CachedLessonForm
{
    public string JournalId { get; set; } = "";
    public string FormJson { get; set; } = "";
    public DateTime CachedAtUtc { get; set; }
}

public enum PendingOpType
{
    SetMark = 0,
    AddLesson = 1,
    EditLesson = 2,
    DeleteLesson = 3,
    SetHomework = 4,
}

public enum PendingOpStatus
{
    Pending = 0,
    Synced = 1,
    Failed = 2,
    Conflict = 3,
}

/// <summary>Outbox: локальна зміна, що чекає на надсилання в NZ.UA.</summary>
public class PendingOp
{
    public long Id { get; set; }
    public string JournalId { get; set; } = "";
    public PendingOpType Type { get; set; }
    /// <summary>Ключ злиття: повторне редагування тієї ж клітинки оновлює наявну операцію.</summary>
    public string TargetKey { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    /// <summary>Значення на сервері на момент першого локального редагування (для виявлення конфліктів).</summary>
    public string? BaseSnapshotJson { get; set; }
    public PendingOpStatus Status { get; set; }
    public string? Error { get; set; }
    public string Summary { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public class SyncLogEntry
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string JournalId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
    public bool Success { get; set; }
}

public class ChatSessionEntity
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public class ChatMessageEntity
{
    public long Id { get; set; }
    public long SessionId { get; set; }
    /// <summary>user / assistant / tool</summary>
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime AtUtc { get; set; }
}

public class GeneratedAssetEntity
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    /// <summary>printable | interactive</summary>
    public string Kind { get; set; } = "";
    public string Html { get; set; } = "";
    public string? ParamsJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Довільні пари ключ-значення (семестри, останній вибір тощо).</summary>
public class SettingEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
