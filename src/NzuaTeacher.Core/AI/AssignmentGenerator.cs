using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using NzuaMcp.Nzua;
using NzuaTeacher.Core.Data;

namespace NzuaTeacher.Core.AI;

// ---------------------------------------------------------------- специфікація

public sealed record AssignmentSpec(
    string JournalId,
    List<string> ScheduleIds,
    string WorkType,           // самостійна | контрольна | діагностична
    bool Differentiate,
    int VariantsPerGroup,
    string? ExtraInstructions);

// ------------------------------------------------------------------- документ

public sealed class AssignmentDoc
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";
    [JsonPropertyName("className")] public string ClassName { get; set; } = "";
    [JsonPropertyName("workType")] public string WorkType { get; set; } = "";
    [JsonPropertyName("durationMinutes")] public int DurationMinutes { get; set; }
    [JsonPropertyName("groups")] public List<GroupPlan> Groups { get; set; } = [];
    [JsonPropertyName("evaluationCriteria")] public string EvaluationCriteria { get; set; } = "";
}

public sealed class GroupPlan
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("levelNote")] public string LevelNote { get; set; } = "";
    [JsonPropertyName("studentPseudonyms")] public List<string> StudentPseudonyms { get; set; } = [];
    [JsonPropertyName("variants")] public List<VariantDoc> Variants { get; set; } = [];
}

public sealed class VariantDoc
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("tasks")] public List<TaskDoc> Tasks { get; set; } = [];
}

public sealed class TaskDoc
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("points")] public double Points { get; set; }
    [JsonPropertyName("answer")] public string Answer { get; set; } = "";
}

// ------------------------------------------------------------------ аналітика

public sealed record StudentPerformance(
    string Pseudonym,
    string StudentId,
    string RealName,
    double? AverageGrade,
    int MarksCount,
    int Absences,
    string Group);

public sealed record ClassAnalytics(
    string Subject,
    string ClassName,
    List<StudentPerformance> Students,
    List<string> Topics,
    List<string> WeakTopics);

/// <summary>
/// Генератор диференційованих робіт: локальна аналітика успішності з кешу
/// (до LLM ідуть лише псевдоніми і статистика) → структурований JSON → HTML-рендери.
/// </summary>
public sealed class AssignmentGenerator(IDbContextFactory<TeacherDbContext> dbFactory)
{
    private static bool ShowRealNames =>
        string.Equals(Environment.GetEnvironmentVariable("NZUA_SHOW_REAL_NAMES"), "true", StringComparison.OrdinalIgnoreCase);

    public async Task<ClassAnalytics> BuildAnalyticsAsync(string journalId, IReadOnlyCollection<string> scheduleIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var journal = await db.Journals.AsNoTracking().FirstOrDefaultAsync(j => j.JournalId == journalId)
            ?? throw new InvalidOperationException("Журнал відсутній у збережених даних. Спочатку оновіть його з NZ.UA.");
        var students = await db.Students.AsNoTracking()
            .Where(s => s.JournalId == journalId).OrderBy(s => s.OrderIndex).ToListAsync();
        var marks = await db.Marks.AsNoTracking()
            .Where(m => m.JournalId == journalId).ToListAsync();
        var homework = await db.Homework.AsNoTracking()
            .Where(h => h.JournalId == journalId).ToListAsync();

        var scopedMarks = scheduleIds.Count == 0
            ? marks
            : marks.Where(m => scheduleIds.Contains(m.ScheduleId)).ToList();

        var perStudent = new List<StudentPerformance>();
        foreach (var s in students)
        {
            var own = scopedMarks.Where(m => m.StudentId == s.StudentId).ToList();
            var grades = own
                .Select(m => int.TryParse(m.MarkId, out var id) ? MarkMappings.MarkIdToGrade(id) : null)
                .Where(g => g.HasValue)
                .Select(g => (double)g!.Value)
                .ToList();
            var absences = own.Count(m => m.MarkId is "1" or "2" or "23");
            var avg = grades.Count > 0 ? Math.Round(grades.Average(), 2) : (double?)null;

            var group = avg switch
            {
                null => "Б",
                >= 9 => "А",
                >= 6 => "Б",
                _ => "В",
            };

            var pseudonym = NzuaPrivacy.StudentLabel(new Student(s.StudentId, s.Name, "", s.OrderIndex), false);
            perStudent.Add(new StudentPerformance(pseudonym, s.StudentId, s.Name, avg, grades.Count, absences, group));
        }

        var topics = homework
            .Where(h => scheduleIds.Count == 0 || scheduleIds.Contains(h.ScheduleId))
            .Select(h => h.Topic)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct()
            .ToList();

        // Слабкі теми: найнижчий середній бал класу по уроках теми.
        var topicAvg = new List<(string Topic, double Avg)>();
        foreach (var hw in homework.Where(h => !string.IsNullOrWhiteSpace(h.Topic)))
        {
            var lessonGrades = marks.Where(m => m.ScheduleId == hw.ScheduleId)
                .Select(m => int.TryParse(m.MarkId, out var id) ? MarkMappings.MarkIdToGrade(id) : null)
                .Where(g => g.HasValue)
                .Select(g => (double)g!.Value)
                .ToList();
            if (lessonGrades.Count > 0)
                topicAvg.Add((hw.Topic, lessonGrades.Average()));
        }
        var weakTopics = topicAvg
            .GroupBy(t => t.Topic)
            .Select(g => (Topic: g.Key, Avg: g.Average(x => x.Avg)))
            .OrderBy(t => t.Avg)
            .Take(3)
            .Select(t => t.Topic)
            .ToList();

        return new ClassAnalytics(journal.Subject, journal.ClassName, perStudent, topics, weakTopics);
    }

    public async Task<AssignmentDoc> GenerateAsync(
        IChatClient chatClient,
        AssignmentSpec spec,
        ClassAnalytics analytics,
        IReadOnlyList<ChatAttachment>? materials = null,
        CancellationToken ct = default)
    {
        var groups = spec.Differentiate
            ? analytics.Students.GroupBy(s => s.Group).OrderBy(g => g.Key)
                .Select(g => new
                {
                    name = $"Група {g.Key}",
                    level = g.Key switch { "А" => "високий рівень (середній бал ≥ 9)", "Б" => "середній рівень (6–8)", _ => "потребують підтримки (< 6)" },
                    students = g.Select(s => new { pseudonym = s.Pseudonym, avg = s.AverageGrade, absences = s.Absences }).ToList(),
                })
                .Cast<object>().ToList()
            : [new { name = "Весь клас", level = "спільний рівень", students = analytics.Students.Select(s => new { pseudonym = s.Pseudonym, avg = s.AverageGrade, absences = s.Absences }).Cast<object>().ToList() }];

        var context = new
        {
            subject = analytics.Subject,
            className = analytics.ClassName,
            workType = spec.WorkType,
            variantsPerGroup = Math.Clamp(spec.VariantsPerGroup, 1, 4),
            topics = analytics.Topics,
            weakTopics = analytics.WeakTopics,
            groups,
            extraInstructions = spec.ExtraInstructions,
        };

        var systemPrompt =
            "Ти — досвідчений український методист НУШ. Створюєш диференційовані роботи для учнів " +
            "за 12-бальною шкалою оцінювання (наказ МОН №1427). Завдання формулюй українською, чітко, відповідно до вікових норм. " +
            "Якщо вчитель додав матеріали (скріншоти сторінок підручника, PDF, текст) — бери їх за основу: " +
            "тримайся того самого типу, стилю й рівня складності завдань, але змінюй числа/умови, щоб варіанти відрізнялись. " +
            "Поверни СТРОГО один JSON-об'єкт без markdown-огорток за схемою: " +
            """{"title":str,"subject":str,"className":str,"workType":str,"durationMinutes":int,"groups":[{"name":str,"levelNote":str,"studentPseudonyms":[str],"variants":[{"label":str,"tasks":[{"number":int,"text":str,"points":num,"answer":str}]}]}],"evaluationCriteria":str}""" +
            " Сума балів варіанта — 12. Для слабших груп — простіші завдання з опорою, для сильніших — творчі та проблемні. " +
            "answer — стислий правильний розвʼязок/відповідь для вчителя.";

        var userContent = new List<AIContent>
        {
            new TextContent(
                $"Дані класу (JSON):\n{JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}\n\n" +
                $"Склади {spec.WorkType} роботу з урахуванням слабких тем і рівнів груп."),
        };

        if (materials is { Count: > 0 })
        {
            userContent.Add(new TextContent($"Додано матеріалів від вчителя: {materials.Count}. Використай їх як основу для завдань."));
            foreach (var material in materials)
            {
                if (material.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                    userContent.Add(new TextContent($"Матеріал «{material.FileName}»:\n{System.Text.Encoding.UTF8.GetString(material.Data)}"));
                else
                    userContent.Add(new DataContent(material.Data, material.MediaType));
            }
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userContent),
        ];

        var response = await chatClient.GetResponseAsync(messages, new ChatOptions { MaxOutputTokens = 8192 }, ct);
        var json = ExtractJson(response.Text);
        var doc = JsonSerializer.Deserialize<AssignmentDoc>(json)
            ?? throw new InvalidOperationException("Модель повернула порожній результат.");

        // Підміняємо псевдоніми реальними іменами для друку, якщо дозволено.
        if (ShowRealNames)
        {
            var map = analytics.Students.ToDictionary(s => s.Pseudonym, s => s.RealName);
            foreach (var g in doc.Groups)
                g.StudentPseudonyms = g.StudentPseudonyms
                    .Select(p => map.TryGetValue(p, out var real) ? real : p)
                    .ToList();
        }

        return doc;
    }

    internal static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException("У відповіді моделі немає JSON-обʼєкта.");
        return trimmed[start..(end + 1)];
    }
}
