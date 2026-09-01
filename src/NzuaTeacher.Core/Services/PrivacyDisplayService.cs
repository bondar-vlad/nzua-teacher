using Microsoft.EntityFrameworkCore;
using NzuaMcp.Nzua;
using NzuaTeacher.Core.Data;

namespace NzuaTeacher.Core.Services;

/// <summary>
/// Локальна підстановка псевдонімів реальними іменами для відображення в UI чату.
/// LLM бачить лише «Учень-XXXXX», вчитель — справжні імена.
/// </summary>
public sealed class PrivacyDisplayService(IDbContextFactory<TeacherDbContext> dbFactory)
{
    private Dictionary<string, string>? _map;

    public async Task RefreshAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var students = await db.Students.AsNoTracking().ToListAsync();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in students)
        {
            var pseudonym = NzuaPrivacy.StudentLabel(new Student(s.StudentId, s.Name, "", s.OrderIndex), false);
            map[pseudonym] = s.Name;
        }
        _map = map;
    }

    /// <summary>Замінює псевдоніми на «Ім’я (Учень-XXXXX)» у тексті для показу вчителю.</summary>
    public string Humanize(string text)
    {
        var map = _map;
        if (map is null || map.Count == 0 || string.IsNullOrEmpty(text)) return text;

        foreach (var (pseudonym, name) in map)
        {
            if (text.Contains(pseudonym, StringComparison.Ordinal))
                text = text.Replace(pseudonym, $"{name} ({pseudonym})", StringComparison.Ordinal);
        }
        return text;
    }
}
