using Microsoft.EntityFrameworkCore;

namespace NzuaTeacher.Core.Data;

public class TeacherDbContext(DbContextOptions<TeacherDbContext> options) : DbContext(options)
{
    public DbSet<CachedJournal> Journals => Set<CachedJournal>();
    public DbSet<CachedStudent> Students => Set<CachedStudent>();
    public DbSet<CachedLesson> Lessons => Set<CachedLesson>();
    public DbSet<CachedMark> Marks => Set<CachedMark>();
    public DbSet<CachedHomework> Homework => Set<CachedHomework>();
    public DbSet<CachedLessonForm> LessonForms => Set<CachedLessonForm>();
    public DbSet<PendingOp> PendingOps => Set<PendingOp>();
    public DbSet<SyncLogEntry> SyncLog => Set<SyncLogEntry>();
    public DbSet<ChatSessionEntity> ChatSessions => Set<ChatSessionEntity>();
    public DbSet<ChatMessageEntity> ChatMessages => Set<ChatMessageEntity>();
    public DbSet<GeneratedAssetEntity> Assets => Set<GeneratedAssetEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<CachedJournal>().HasKey(x => x.JournalId);

        b.Entity<CachedStudent>().HasIndex(x => new { x.JournalId, x.StudentId }).IsUnique();
        b.Entity<CachedLesson>().HasIndex(x => new { x.JournalId, x.ScheduleId }).IsUnique();
        b.Entity<CachedMark>().HasIndex(x => new { x.JournalId, x.ScheduleId, x.StudentId }).IsUnique();
        b.Entity<CachedHomework>().HasIndex(x => new { x.JournalId, x.ScheduleId }).IsUnique();

        b.Entity<CachedLessonForm>().HasKey(x => x.JournalId);
        b.Entity<PendingOp>().HasIndex(x => new { x.JournalId, x.TargetKey });
        b.Entity<ChatMessageEntity>().HasIndex(x => x.SessionId);
        b.Entity<SettingEntity>().HasKey(x => x.Key);
    }
}
