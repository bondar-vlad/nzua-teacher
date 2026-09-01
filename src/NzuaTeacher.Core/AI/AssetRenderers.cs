using System.Net;
using System.Text;
using System.Text.Json;

namespace NzuaTeacher.Core.AI;

/// <summary>HTML-рендери згенерованих робіт: друкований А4 та інтерактивний (для дошки/онлайн-уроку).</summary>
public static class AssetRenderers
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>Самодостатній HTML для друку (А4): варіанти посторінково + ключ відповідей для вчителя.</summary>
    public static string RenderPrintable(AssignmentDoc doc, bool includeAnswerKey = true)
    {
        var sb = new StringBuilder();
        sb.Append($$"""
<!DOCTYPE html>
<html lang="uk">
<head>
<meta charset="utf-8">
<title>{{E(doc.Title)}}</title>
<style>
  @page { size: A4; margin: 18mm 15mm; }
  * { box-sizing: border-box; }
  body { font-family: "Times New Roman", Georgia, serif; font-size: 13pt; line-height: 1.45; color: #111; margin: 0; }
  .variant { page-break-after: always; }
  .variant:last-child { page-break-after: auto; }
  h1 { font-size: 16pt; margin: 0 0 2mm; text-align: center; }
  .meta { text-align: center; font-size: 11pt; color: #333; margin-bottom: 6mm; }
  .group-note { font-size: 10.5pt; color: #555; text-align: center; margin-bottom: 4mm; }
  .fio { margin: 4mm 0 6mm; font-size: 12pt; }
  .fio span { display: inline-block; border-bottom: 1px solid #111; min-width: 70mm; }
  ol.tasks { padding-left: 6mm; }
  ol.tasks li { margin-bottom: 5mm; }
  .points { color: #444; font-size: 10.5pt; white-space: nowrap; }
  .answers h2 { font-size: 14pt; }
  .answers table { width: 100%; border-collapse: collapse; font-size: 11pt; }
  .answers th, .answers td { border: 1px solid #999; padding: 2mm 3mm; text-align: left; vertical-align: top; }
  .criteria { margin-top: 5mm; font-size: 11pt; }
  @media screen {
    body { background: #e5e5e5; padding: 10mm; }
    .variant, .answers { background: #fff; max-width: 210mm; margin: 0 auto 8mm; padding: 18mm 15mm; box-shadow: 0 2px 8px rgba(0,0,0,.2); }
  }
</style>
</head>
<body>
""");

        foreach (var group in doc.Groups)
        {
            foreach (var variant in group.Variants)
            {
                sb.Append($"""
<section class="variant">
  <h1>{E(doc.Title)}</h1>
  <div class="meta">{E(doc.Subject)} · {E(doc.ClassName)} · {E(doc.WorkType)}{(doc.DurationMinutes > 0 ? $" · {doc.DurationMinutes} хв" : "")}</div>
  <div class="group-note">{E(group.Name)}{(string.IsNullOrWhiteSpace(group.LevelNote) ? "" : $" — {E(group.LevelNote)}")} · Варіант {E(variant.Label)}</div>
  <div class="fio">Прізвище, ім’я: <span>&nbsp;</span>&nbsp;&nbsp;Дата: <span style="min-width:30mm">&nbsp;</span></div>
  <ol class="tasks">
""");
                foreach (var task in variant.Tasks.OrderBy(t => t.Number))
                    sb.Append($"    <li>{E(task.Text)} <span class=\"points\">({task.Points:0.##} б.)</span></li>\n");
                sb.Append("  </ol>\n</section>\n");
            }
        }

        if (includeAnswerKey)
        {
            sb.Append("""
<section class="answers">
  <h2>Ключ відповідей (для вчителя)</h2>
  <table>
    <tr><th>Група</th><th>Варіант</th><th>№</th><th>Відповідь</th><th>Бали</th></tr>
""");
            foreach (var group in doc.Groups)
                foreach (var variant in group.Variants)
                    foreach (var task in variant.Tasks.OrderBy(t => t.Number))
                        sb.Append($"    <tr><td>{E(group.Name)}</td><td>{E(variant.Label)}</td><td>{task.Number}</td><td>{E(task.Answer)}</td><td>{task.Points:0.##}</td></tr>\n");
            sb.Append($"""
  </table>
  <div class="criteria"><b>Критерії оцінювання:</b> {E(doc.EvaluationCriteria)}</div>
""");

            var assignmentsByGroup = doc.Groups
                .Where(g => g.StudentPseudonyms.Count > 0)
                .ToList();
            if (assignmentsByGroup.Count > 0)
            {
                sb.Append("  <div class=\"criteria\"><b>Розподіл за групами:</b><ul>");
                foreach (var g in assignmentsByGroup)
                    sb.Append($"<li>{E(g.Name)}: {E(string.Join(", ", g.StudentPseudonyms))}</li>");
                sb.Append("</ul></div>\n");
            }
            sb.Append("</section>\n");
        }

        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Самодостатній інтерактивний HTML: великі шрифти, повний екран, навігація завданнями,
    /// таймер і показ відповіді — для інтерактивної дошки або демонстрації на онлайн-уроці.
    /// </summary>
    public static string RenderInteractive(AssignmentDoc doc)
    {
        var docJson = JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        return Template
            .Replace("__TITLE__", E(doc.Title))
            .Replace("__SUBTITLE__", E($"{doc.Subject} · {doc.ClassName} · {doc.WorkType}"))
            .Replace("__DOC_JSON__", docJson);
    }

    private const string Template = """
<!DOCTYPE html>
<html lang="uk">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__</title>
<style>
  * { box-sizing: border-box; margin: 0; }
  body { font-family: "Segoe UI", system-ui, sans-serif; background: #0f172a; color: #f1f5f9; min-height: 100vh; display: flex; flex-direction: column; }
  header { padding: 14px 22px; display: flex; align-items: center; gap: 16px; background: #1e293b; }
  header h1 { font-size: 1.15rem; font-weight: 600; flex: 1; }
  header .sub { color: #94a3b8; font-size: .85rem; }
  select, button { font: inherit; border-radius: 10px; border: 1px solid #334155; background: #1e293b; color: #f1f5f9; padding: 8px 14px; cursor: pointer; }
  button.primary { background: #2563eb; border-color: #2563eb; }
  button:hover { filter: brightness(1.15); }
  main { flex: 1; display: flex; align-items: center; justify-content: center; padding: 4vh 6vw; }
  .card { max-width: 1100px; width: 100%; }
  .task-num { color: #38bdf8; font-size: 1.2rem; margin-bottom: 18px; }
  .task-text { font-size: clamp(1.6rem, 3.4vw, 2.6rem); line-height: 1.35; white-space: pre-wrap; }
  .points { margin-top: 22px; color: #94a3b8; font-size: 1.05rem; }
  .answer { margin-top: 26px; padding: 18px 22px; border-radius: 14px; background: #14532d; font-size: 1.35rem; display: none; white-space: pre-wrap; }
  .answer.show { display: block; }
  footer { display: flex; gap: 12px; align-items: center; justify-content: center; padding: 16px; background: #1e293b; }
  .timer { font-variant-numeric: tabular-nums; font-size: 1.25rem; padding: 8px 16px; border-radius: 10px; background: #0f172a; }
  .timer.warn { color: #f87171; }
  .start-screen { text-align: center; }
  .start-screen h2 { font-size: clamp(1.8rem, 4vw, 3rem); margin-bottom: 8px; }
  .start-screen p { color: #94a3b8; margin-bottom: 28px; font-size: 1.15rem; }
  .variant-grid { display: flex; flex-wrap: wrap; gap: 14px; justify-content: center; }
  .variant-grid button { font-size: 1.2rem; padding: 18px 30px; }
  .progress { color: #94a3b8; font-size: 1rem; min-width: 90px; text-align: center; }
</style>
</head>
<body>
<header>
  <h1>__TITLE__ <span class="sub">__SUBTITLE__</span></h1>
  <button id="btnFs" title="Повний екран">⛶ Повний екран</button>
</header>
<main><div class="card" id="stage"></div></main>
<footer id="controls" style="display:none">
  <button id="btnPrev">← Назад</button>
  <div class="progress" id="progress"></div>
  <button id="btnNext" class="primary">Далі →</button>
  <button id="btnAnswer">Показати відповідь</button>
  <div class="timer" id="timer">00:00</div>
  <button id="btnExit">Завершити</button>
</footer>
<script>
const DOC = __DOC_JSON__;
const stage = document.getElementById('stage');
const controls = document.getElementById('controls');
let variant = null, idx = 0, seconds = 0, timerId = null;

function fmt(s) { return String(Math.floor(s / 60)).padStart(2, '0') + ':' + String(s % 60).padStart(2, '0'); }

function startScreen() {
  controls.style.display = 'none';
  if (timerId) { clearInterval(timerId); timerId = null; }
  let html = '<div class="start-screen"><h2>__TITLE__</h2><p>Оберіть варіант для показу на екрані</p><div class="variant-grid">';
  DOC.groups.forEach((g, gi) => g.variants.forEach((v, vi) => {
    html += `<button class="primary" onclick="startVariant(${gi},${vi})">${g.name} · Варіант ${v.label}</button>`;
  }));
  html += '</div></div>';
  stage.innerHTML = html;
}

function startVariant(gi, vi) {
  variant = DOC.groups[gi].variants[vi];
  idx = 0; seconds = 0;
  controls.style.display = 'flex';
  if (timerId) clearInterval(timerId);
  timerId = setInterval(() => {
    seconds++;
    const t = document.getElementById('timer');
    t.textContent = fmt(seconds);
    if (DOC.durationMinutes > 0 && seconds >= DOC.durationMinutes * 60) t.classList.add('warn');
  }, 1000);
  render();
}

function render() {
  const t = variant.tasks[idx];
  stage.innerHTML = `
    <div class="task-num">Завдання ${t.number} з ${variant.tasks.length}</div>
    <div class="task-text">${escapeHtml(t.text)}</div>
    <div class="points">${t.points} бал(ів)</div>
    <div class="answer" id="answer">✅ ${escapeHtml(t.answer)}</div>`;
  document.getElementById('progress').textContent = (idx + 1) + ' / ' + variant.tasks.length;
}

function escapeHtml(s) { const d = document.createElement('div'); d.textContent = s ?? ''; return d.innerHTML; }

document.getElementById('btnPrev').onclick = () => { if (idx > 0) { idx--; render(); } };
document.getElementById('btnNext').onclick = () => { if (idx < variant.tasks.length - 1) { idx++; render(); } };
document.getElementById('btnAnswer').onclick = () => document.getElementById('answer').classList.toggle('show');
document.getElementById('btnExit').onclick = startScreen;
document.getElementById('btnFs').onclick = () =>
  document.fullscreenElement ? document.exitFullscreen() : document.documentElement.requestFullscreen();
document.addEventListener('keydown', e => {
  if (!variant) return;
  if (e.key === 'ArrowRight') document.getElementById('btnNext').click();
  if (e.key === 'ArrowLeft') document.getElementById('btnPrev').click();
  if (e.key === ' ') { e.preventDefault(); document.getElementById('btnAnswer').click(); }
});
startScreen();
</script>
</body>
</html>
""";
}
