# НЗ Вчитель (nzua-teacher)

Неофіційний десктопний застосунок Windows для вчителів, що працюють з електронними журналами NZ.UA.
Побудований поверх [nzua-mcp](https://github.com/bondar-vlad/nzua-mcp) (git submodule) на стеку **.NET MAUI Blazor Hybrid + SQLite + WiX**.

> ⚠️ Це неофіційний інструмент. NZ.UA не має публічного API — застосунок працює з тими самими сторінками, що й браузер вчителя, і може зламатися після оновлень NZ.UA.

## Можливості

- **Журнали як таблиця** — учні × уроки, клавіатурне виставлення оцінок (1–12, Н, хв, зв, НУШ-рівні П/С/Д/В), коментарі.
- **Теми та ДЗ** — редагування в таблиці + масова вставка тем із КТП (по рядку на тему).
- **Офлайн-перший підхід** — всі дані кешуються в SQLite; редагуйте без інтернету, зміни стають у чергу.
- **Керована синхронізація** — ви явно обираєте, що тягнути (уроки / оцінки / теми) і що надсилати. Перед записом звіряється актуальний стан (конфлікти — «взяти моє / лишити серверне»), після — перевіряється результат.
- **AI-помічник (чат)** — через вбудований MCP-сервер nzua-mcp (ті самі тули/промпти). Будь-який запис у журнал від AI вимагає вашого підтвердження в діалозі. Вкладення (зображення/PDF/текст) і голосовий ввід.
- **Генератор робіт** — диференційовані самостійні/контрольні на основі успішності учнів (групи А/Б/В, слабкі теми). Експорт: друк A4 (HTML → Ctrl+P → PDF) та **інтерактивний режим** для дошки чи онлайн-уроку (повний екран, таймер, показ відповідей).
- **Приватність за замовчуванням** — AI-провайдери бачать стабільні псевдоніми «Учень-XXXXX», а не ПІБ; реальні імена показуються лише локально. Перемикач — у Налаштуваннях.

## AI-провайдери

| Провайдер | Ключ | Вартість |
|---|---|---|
| **Google Gemini** | [aistudio.google.com](https://aistudio.google.com/apikey) | Є безоплатний тариф (Flash-моделі; дані можуть використовуватися Google для навчання — тому псевдоніми) |
| OpenAI | platform.openai.com | Платно |
| Anthropic Claude | console.anthropic.com | Платно |

Ключі зберігаються у захищеному сховищі Windows (DPAPI), не в конфігураційних файлах.

## Встановлення

Завантажте `NzuaTeacher-Setup-vX.Y.Z-x64.exe` з [Releases](../../releases) і запустіть:

- прав адміністратора **не потрібно** (встановлення для поточного користувача);
- .NET і Windows App SDK вбудовані; WebView2 доставиться автоматично, якщо відсутній;
- SmartScreen може попередити про невідомого видавця (збірка не підписана) — «Додатково → Виконати все одно»;
- Chromium для входу в NZ.UA (~170 МБ) завантажиться при першому вході.

## Збірка з коду

```powershell
git clone --recurse-submodules https://github.com/bondar-vlad/nzua-teacher
cd nzua-teacher
dotnet workload install maui-windows
dotnet build NzuaTeacher.slnx
dotnet test tests/NzuaTeacher.Tests/NzuaTeacher.Tests.csproj

# запуск
dotnet build src/NzuaTeacher/NzuaTeacher.csproj -t:Run -f net10.0-windows10.0.19041.0
```

Інсталер локально:

```powershell
dotnet publish src/NzuaTeacher/NzuaTeacher.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true -o publish
dotnet build installer/NzuaTeacher.Bundle/NzuaTeacher.Bundle.wixproj -c Release
# → installer/NzuaTeacher.Bundle/bin/Release/NzuaTeacher-Setup.exe
```

Реліз: пуш тегу `vX.Y.Z` → GitHub Actions збере та прикріпить інсталер автоматично.

## Архітектура

```
external/nzua-mcp      — submodule: Nzua-клієнт + MCP тули/промпти/ресурси (єдине джерело правди)
src/NzuaTeacher.Core   — SQLite-кеш, outbox, синхронізація, AI-сервіси, вбудований MCP-хост
src/NzuaTeacher        — MAUI Blazor Hybrid UI (лише Windows)
installer/             — WiX v6: per-user MSI + setup.exe (WebView2 bootstrapper)
```

- Сесія NZ.UA спільна з nzua-mcp (`%APPDATA%\.nzua-session.json`, міжпроцесний лок): застосунок і Claude Desktop працюють одночасно, вікно входу відкривається лише в одному процесі.
- Дані застосунку: `%APPDATA%\nzua-teacher\` (кеш `teacher.db`, згенеровані матеріали в `assets\`).
- Локальні правки застосовуються миттєво і потрапляють в outbox; синхронізація — лише за явною командою вчителя.

## Безпека і дані

- Пароль NZ.UA не зберігається — вхід відбувається у справжньому вікні браузера (Playwright).
- ПІБ учнів за замовчуванням не залишають компʼютер (псевдонімізація з nzua-mcp).
- Не комітьте реальні дані журналів у цей репозиторій.

## Ліцензія

MIT (як і nzua-mcp).
