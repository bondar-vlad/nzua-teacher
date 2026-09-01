using NzuaTeacher.Core.AI;
using Xunit;

namespace NzuaTeacher.Tests;

public class AssignmentToolsTests
{
    [Fact]
    public void ExtractJson_HandlesPlainJson()
    {
        var json = AssignmentGenerator.ExtractJson("""{"title":"Тест"}""");
        Assert.Equal("""{"title":"Тест"}""", json);
    }

    [Fact]
    public void ExtractJson_StripsMarkdownFences()
    {
        var input = "```json\n{\"title\":\"Тест\"}\n```";
        Assert.Equal("{\"title\":\"Тест\"}", AssignmentGenerator.ExtractJson(input));
    }

    [Fact]
    public void ExtractJson_FindsObjectInsideProse()
    {
        var input = "Ось результат: {\"title\":\"X\"} — готово.";
        Assert.Equal("{\"title\":\"X\"}", AssignmentGenerator.ExtractJson(input));
    }

    [Fact]
    public void ExtractJson_Throws_WhenNoJson()
    {
        Assert.Throws<InvalidOperationException>(() => AssignmentGenerator.ExtractJson("немає жодного обʼєкта"));
    }

    private static AssignmentDoc SampleDoc() => new()
    {
        Title = "Самостійна робота: Квадратні рівняння",
        Subject = "Алгебра",
        ClassName = "8-А",
        WorkType = "самостійна робота",
        DurationMinutes = 20,
        EvaluationCriteria = "12 балів за все",
        Groups =
        [
            new GroupPlan
            {
                Name = "Група А",
                LevelNote = "високий рівень",
                StudentPseudonyms = ["Учень-AAAAA"],
                Variants =
                [
                    new VariantDoc
                    {
                        Label = "1",
                        Tasks = [new TaskDoc { Number = 1, Text = "Розв'яжіть рівняння: 2x - 6 = 0", Points = 12, Answer = "x = 3" }],
                    },
                ],
            },
        ],
    };

    [Fact]
    public void RenderPrintable_ContainsTasksAndAnswerKey()
    {
        var html = AssetRenderers.RenderPrintable(SampleDoc());
        Assert.Contains("Квадратні рівняння", html);
        Assert.Contains("2x - 6 = 0", html);
        Assert.Contains("Ключ відповідей", html);
        Assert.Contains("x = 3", html);
        Assert.Contains("page-break-after", html);
    }

    [Fact]
    public void RenderPrintable_WithoutAnswers_HasNoKey()
    {
        var html = AssetRenderers.RenderPrintable(SampleDoc(), includeAnswerKey: false);
        Assert.DoesNotContain("Ключ відповідей", html);
        Assert.DoesNotContain("x = 3", html);
    }

    [Fact]
    public void RenderInteractive_IsSelfContainedWithDocJson()
    {
        var html = AssetRenderers.RenderInteractive(SampleDoc());
        Assert.Contains("const DOC =", html);
        Assert.Contains("Квадратні рівняння", html);
        Assert.Contains("requestFullscreen", html);
        Assert.DoesNotContain("__DOC_JSON__", html);
        Assert.DoesNotContain("src=\"http", html); // без зовнішніх залежностей
    }
}
