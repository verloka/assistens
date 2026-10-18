# Implementation Backlog — Personal AI Desktop Agent

**125 задач · 15 последовательных delivery streams/PR · single source of truth: `tasks.json` + `progress/state.json`**

Этот файл — статическая навигационная карта. Актуальные состояния смотри через `scripts/taskctl.ps1 -Action report` или в `progress/state.json`.

Правило: агент выбирает только задачу со статусом `pending`, у которой все `depends_on` имеют статус `done`; если есть `in_progress`, сначала продолжает её. Изменять каталог задач при обычном выполнении запрещено.

## PR-01 — WPF Host, solution, initial architecture (8 задач)

### PA-001 · Создать solution и структуру modular monolith

Создать .NET 10 solution, WPF App, Core, Agent, Computer, MCP, Telegram, Persistence и три тестовых проекта.

**Dependencies:** none

**Acceptance:**
- [ ] dotnet build проходит на Windows; референсы модулей не цикличны.

**Verification:**
- dotnet build

**Reference:** 3.1, 3.2, 16 Phase 0

### PA-002 · Настроить Generic Host и жизненный цикл WPF

DI, app startup/shutdown, CancellationToken graceful shutdown, ILogger, ошибки загрузки.

**Dependencies:** PA-001

**Acceptance:**
- [ ] Приложение открывается и корректно закрывается; HostedService стартует и останавливается.

**Verification:**
- dotnet build; ручной smoke test

**Reference:** 2, 12.1

### PA-003 · Добавить минимальный экран health/status

Окно показывает статус host и зарегистрированных компонентов без реального LLM.

**Dependencies:** PA-002

**Acceptance:**
- [ ] Есть рабочий Windows UI с health indicator; не блокируется при длительной fake-операции.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 12.1, 16 Phase 0

### PA-004 · Зафиксировать NuGet-версии и начальные ADR

Проверить конкретные пакеты MAF, Microsoft.Extensions.AI, MCP SDK; закрепить версии и подготовить ADR.

**Dependencies:** PA-003

**Acceptance:**
- [ ] Файл versions / central package management и ADR содержат проверенные версии и причины выбора.

**Verification:**
- dotnet restore + dotnet build

**Reference:** 2, 16 Phase 0, 20

### PA-005 · Создать каркас тестов и общие команды проверки

Unit/Integration/E2E проекты, базовые mocks/fakes, minimal build+test README.

**Dependencies:** PA-004

**Acceptance:**
- [ ] Тесты запускаются одной командой и есть минимум один passing smoke/unit test.

**Verification:**
- dotnet test

**Reference:** 17, 20

### PA-006 · Проверить prototype MAF tool calling и approval/resume

Вынести отдельный диагностический spike: fake tool, агент, approval handshake, отметить ограничения SDK.

**Dependencies:** PA-005

**Acceptance:**
- [ ] Есть воспроизводимый smoke или документированный внешний блокер; не проектировать поверх вымышленного API.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7, 16 Phase 0 · **Needs external/manual validation**

### PA-007 · Проверить prototype MCP stdio и HTTP

Поднять test MCP server, проверить list tools и вызов; зафиксировать поддержку транспорта SDK.

**Dependencies:** PA-006

**Acceptance:**
- [ ] Подтверждено фактическое поведение stdio и Streamable HTTP либо указан независимый тестовый blocker.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 8, 16 Phase 0 · **Needs external/manual validation**

### PA-008 · Проверить prototype Windows capture, SendInput и hotkey

Простой Windows smoke без LLM: screenshot, ввод в безопасное тестовое окно, регистрация hotkey.

**Dependencies:** PA-007

**Acceptance:**
- [ ] Не нарушает пользовательский ввод; записаны ограничения прав, DPI, нескольких мониторов.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5, 6, 16 Phase 0 · **Needs external/manual validation**

## PR-02 — Domain, task runner, fake agent (8 задач)

### PA-009 · Определить доменные модели TaskDefinition/TaskRun/Conversation/RunEvent

IDs, timestamps, origin, status, summary, links без UI/SDK типов.

**Dependencies:** PA-005

**Acceptance:**
- [ ] Доменные типы изолированы в Core; сериализация не зависит от SDK.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 4, 9.1

### PA-010 · Реализовать state machine TaskRun

Валидные переходы Started/Queued/Running/Paused/Blocked/Cancelled/Failed/Completed; запрет назад из terminal.

**Dependencies:** PA-009

**Acceptance:**
- [ ] Unit tests проверяют допустимые и запрещённые переходы.

**Verification:**
- dotnet test --filter TaskRun

**Reference:** 9.2, 9.3

### PA-011 · Контракты IAgentRuntime, AgentRequest/Result/Events

Независимые от MAF интерфейсы и typed progress; cancellation.

**Dependencies:** PA-010

**Acceptance:**
- [ ] Fake и production adapters могут реализовать одинаковый контракт.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 4.1, 7.1

### PA-012 · Создать FakeAgentRuntime с контролируемыми шагами

Заданный deterministic script, progress, cancellation, pause points.

**Dependencies:** PA-011

**Acceptance:**
- [ ] Ручная задача без API проходит путь Start→progress→Complete.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 16 Phase 1, 17.1

### PA-013 · Application commands: Submit, Cancel, Pause, Resume

Единые команды из Desktop и будущих каналов, валидные transitions, idempotency.

**Dependencies:** PA-012

**Acceptance:**
- [ ] Повторные команды безопасны; Cancel не может случайно стать Resume.

**Verification:**
- dotnet test --filter Commands

**Reference:** 4.4, 9.5

### PA-014 · Task queue и TaskRunner над fake agent

Один источник истины о Run; lifecycle/stop/timeouts; ручной запуск.

**Dependencies:** PA-013

**Acceptance:**
- [ ] Несколько быстрых Submit не дают двух исполнений одного Run.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 9.3, 9.4

### PA-015 · Подключить live progress к Dashboard

ViewModel вызывает application commands и отображает RunEvent без бизнес-логики.

**Dependencies:** PA-014

**Acceptance:**
- [ ] Статусы и шаги видны в UI; приложение отзывчиво.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 12.3, 16 Phase 1 · **Needs external/manual validation**

### PA-016 · Тесты state transitions, concurrent commands, fake flow

Покрыть duplicate submit, cancel during execution, pause/resume, failures.

**Dependencies:** PA-015

**Acceptance:**
- [ ] Зелёные unit tests и детерминированный E2E fake-flow.

**Verification:**
- dotnet test

**Reference:** 17.1, 16 Phase 1

## PR-03 — SQLite persistence and recovery (7 задач)

### PA-017 · Определить EF Core schema Run/Events/Conversations

DbContext + migrations, FK, indexes, UTC dates, schema version.

**Dependencies:** PA-014

**Acceptance:**
- [ ] На пустой БД миграции успешно создают таблицы; повторное применение безопасно.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 13.1, 13.3

### PA-018 · Реализовать repositories и atomic state updates

Сохранение runs и events с атомарными переходами; optimistic concurrency для Run.

**Dependencies:** PA-017

**Acceptance:**
- [ ] Повторные/конкурентные переходы не создают неконсистентное состояние.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 9.3, 13.1

### PA-019 · Persist progress и итоговые результаты

Записывать RunEvents, timestamps, errors, summary, ссылки и metadata.

**Dependencies:** PA-018

**Acceptance:**
- [ ] После restart видна полная история fake task.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 13.1, 15.1

### PA-020 · Создать session/conversation persistence scaffolding

Models для AgentSessions, messages, migration без зависимости от конкретной модели.

**Dependencies:** PA-019

**Acceptance:**
- [ ] Историю можно сохранить и восстановить; session metadata версионируется.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7.4, 13.1

### PA-021 · Реализовать recovery для interrupted Runs

При старте незаконченный Run получает Interrupted; внешние writes не переигрываются.

**Dependencies:** PA-020

**Acceptance:**
- [ ] Crash simulation: нет нового SendInput/MCP write, в UI понятный статус.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 9.6, 18 E2E-08

### PA-022 · Добавить защиту от duplicate command и occurrence IDs

Персистентные уникальные ключи идемпотентности будущих scheduler/Telegram triggers.

**Dependencies:** PA-021

**Acceptance:**
- [ ] Два вызова с одним external key создают не более одного Run.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 9.4, 10, 13

### PA-023 · Integration tests SQLite migrations, crash/restart, idempotency

Тестовая временная DB; migrate/close/reopen, competing updates и recovery.

**Dependencies:** PA-022

**Acceptance:**
- [ ] Детерминированный интеграционный набор без внешнего LLM.

**Verification:**
- dotnet test --filter Persistence

**Reference:** 17.2, 16 Phase 1

## PR-04 — Screenshots and coordinate handling (7 задач)

### PA-024 · Определить ScreenCapture, ScreenBounds, ScreenPoint, ScreenshotOptions

Raw physical coordinates, source capture geometry, DPI metadata, content type.

**Dependencies:** PA-005

**Acceptance:**
- [ ] Контракты не зависят от WPF ViewModels; negative X/Y допустимы.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 4.6, 5.1

### PA-025 · Реализовать Windows capture для основного монитора

Получение bitmap/image bytes + dimensions, lock-screen error.

**Dependencies:** PA-024

**Acceptance:**
- [ ] Кадр фактически соответствует разрешённому экрану; не пишет screenshot в обычный лог.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5.1

### PA-026 · Добавить virtual desktop и multi-monitor capture

Поддержка нескольких мониторов и отрицательных координат.

**Dependencies:** PA-025

**Acceptance:**
- [ ] Виртуальные bounds воспроизводятся корректно на двух дисплеях.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5.1, 17.3 · **Needs external/manual validation**

### PA-027 · Реализовать DPI/resize coordinate transforms

Сопоставление LLM image pixels и physical desktop coordinates.

**Dependencies:** PA-026

**Acceptance:**
- [ ] Unit tests на 100/125/150% scaling и отрицательные bounds.

**Verification:**
- dotnet test --filter Coordinates

**Reference:** 5.1, 17.3

### PA-028 · Оптимизировать image serialization для LLM

PNG/JPEG/resize, неизменная мета-геометрия, конфиг limits без потери координат.

**Dependencies:** PA-027

**Acceptance:**
- [ ] LLM adapter может получить корректный image payload; размер ограничен.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5.1, 7

### PA-029 · Создать FakeComputerController и тестовый screenshot fixture

Базовые подмены и артефакты для отказоустойчивых тестов.

**Dependencies:** PA-028

**Acceptance:**
- [ ] Тесты GUI tool results не требуют экрана Windows.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 17.1, 16 Phase 2

### PA-030 · Visual capture smoke test на Windows

Проверить DPI, различные мониторы и перемещение окна; оформить чеклист.

**Dependencies:** PA-029

**Acceptance:**
- [ ] Описаны результаты; реальные ошибки с DPI устранены.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 17.3, 16 Phase 2 · **Needs external/manual validation**

## PR-05 — Win32 input tools and cleanup (8 задач)

### PA-031 · Реализовать низкоуровневый SendInput wrapper

P/Invoke thin adapter, structured errors, input serialization, no elevation.

**Dependencies:** PA-029

**Acceptance:**
- [ ] Input посылается только в unlocked authorized session, ошибки безопасны.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5.2, 5.3, 5.4, 5.5

### PA-032 · Click tool: coordinate validation и mouse buttons

Position, left/right/middle press/release; bounds проверяются.

**Dependencies:** PA-031

**Acceptance:**
- [ ] Click в тестовом окне корректен; out-of-bounds отвергается до SendInput.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5.2

### PA-033 · Scroll tool: anchor point, direction и amount

Поддержка направления, step count, upper bounds.

**Dependencies:** PA-032

**Acceptance:**
- [ ] Прокрутка в тестовом окне и граничные аргументы покрыты тестами.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5.3

### PA-034 · Type tool: Unicode, multiline и batched input

Печатать без clipboard по умолчанию, разделять длинный ввод на короткие chunks.

**Dependencies:** PA-033

**Acceptance:**
- [ ] В тестовом окне вводятся Unicode/спецсимволы; возможна остановка между chunks.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5.4, 6.12

### PA-035 · PressKey tool: chords and modifier lifetime

Ctrl/Alt/Shift, release all held modifiers in finally.

**Dependencies:** PA-034

**Acceptance:**
- [ ] Ошибки/Cancel не оставляют зажатыми Ctrl, Alt или Shift.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5.5, 6.12

### PA-036 · Input safety: cleanup, atomic sections, cancellation boundaries

Короткие critical sections, mouse up on interruption, unified fail-safe cleanup.

**Dependencies:** PA-035

**Acceptance:**
- [ ] После injected failure нет удержанных кнопок и новых операций.

**Verification:**
- dotnet test --filter InputSafety

**Reference:** 6.3, 6.4

### PA-037 · Комплексный fake/real input diagnostic harness

Тестовое окно с кнопками и текстом; не кликать по рабочим приложениям при тестах.

**Dependencies:** PA-036

**Acceptance:**
- [ ] Есть повторяемый сценарий Click/Scroll/Type/PressKey.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 17.2, 17.3 · **Needs external/manual validation**

### PA-038 · Win32 input unit/integration regression tests

Bounds, special keys, cleanup on exception, cancel midway, privilege error.

**Dependencies:** PA-037

**Acceptance:**
- [ ] Зелёный набор на Windows test session.

**Verification:**
- dotnet test --filter Computer

**Reference:** 17.1, 17.2

## PR-06 — Desktop lease, pause and user takeover (10 задач)

### PA-039 · DesktopAccessCoordinator: lease ownership and generation

One GUI owner, monotonic generation, Acquire/Release API, no stale grants.

**Dependencies:** PA-036, PA-014

**Acceptance:**
- [ ] Второй Run не получает GUI lease одновременно; stale generation отвергается.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 4.7, 6.8

### PA-040 · Gate перед каждым Computer tool и atomic input

Проверка generation непосредственно перед действием, sync with PauseRequested.

**Dependencies:** PA-039

**Acceptance:**
- [ ] GUI tool с устаревшим токеном доступа не исполняется.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.4, 9.4

### PA-041 · Разделить Pause GUI / Pause Task / Cancel / Skip

Конкретная pause scope semantics и отдельное desktop state.

**Dependencies:** PA-040

**Acceptance:**
- [ ] GUI-only может выполнять разрешённые MCP-read шаги; Entire Run ничего нового не запускает.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.1, 6.8

### PA-042 · Глобальная горячая клавиша Take Control

RegisterHotKey, конфликт/ошибка регистрации, отзывчивость даже при свернутом окне.

**Dependencies:** PA-041

**Acceptance:**
- [ ] Горячая клавиша работает вне фокуса WPF и запрещает новые GUI actions без LLM.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.2, 6.3 · **Needs external/manual validation**

### PA-043 · Tray и маленькое окно Pause/Resume/Cancel

UI command dispatcher без прямого Win32 управления из ViewModel.

**Dependencies:** PA-042

**Acceptance:**
- [ ] Take Control доступен при закрытом главном окне; статус Paused видим.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.2, 12.2 · **Needs external/manual validation**

### PA-044 · Safe Pause: release input, revoke lease and persist checkpoint

Порядок PauseRequested → gate → release input → revoke lease → state persisted.

**Dependencies:** PA-043

**Acceptance:**
- [ ] Нет активной мыши/клавиатуры, когда UI показывает «Управление твоё».

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.4, 6.5

### PA-045 · Safe Resume: fresh screenshot before new coordinate action

Re-acquire lease, invalidate screenshot/tool args, confirm current UI before action.

**Dependencies:** PA-044

**Acceptance:**
- [ ] После пользовательского переключения окон агент не делает старый клик.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.6

### PA-046 · Поведение при GUI-запросе из другого Run

Очередь ожидающих GUI, graceful cancellation while awaiting; no focus stealing.

**Dependencies:** PA-045

**Acceptance:**
- [ ] Во время Pause первого Run второй не получит GUI без разрешённой политики.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.8, 9.4

### PA-047 · Unit tests pause/resume/race and lease invalidation

Рассинхронизация потоков, stale calls, repeated pause/cancel, no auto-resume.

**Dependencies:** PA-046

**Acceptance:**
- [ ] Детерминированный конкурентный тест; не нужен реальный экран.

**Verification:**
- dotnet test --filter DesktopAccess

**Reference:** 6.12, 17.1

### PA-048 · Windows takeover acceptance: click/type/hotkey/resume

Прогнать сценарии 20 кликов, прерывание Unicode Type, сочетания клавиш.

**Dependencies:** PA-047

**Acceptance:**
- [ ] E2E-03: новые GUI-операции остановлены, модификаторы отпущены, свежий снимок при Resume.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.12, 18 E2E-03 · **Needs external/manual validation**

## PR-07 — OpenAI agent and first GUI flow (11 задач)

### PA-049 · MAF production adapter: agent lifecycle and session run

Инициализировать Agent Framework, run/stream events, map errors/partial statuses.

**Dependencies:** PA-006, PA-047, PA-022

**Acceptance:**
- [ ] Fake/real runtime соответствуют общему IAgentRuntime; DI integration работает.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7.1, 4.1

### PA-050 · OpenAI IModelProvider и secured profile

Создание клиента, ключ через secret reference, fail-fast при недоступном provider.

**Dependencies:** PA-049

**Acceptance:**
- [ ] Test connection работает; ключ не попадает в logs или DB plain text.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 4.2, 7.2

### PA-051 · Model capability validation: vision and function calling

Проверка возможностей выбранной модели до GUI-run; MCP-only path отдельно.

**Dependencies:** PA-050

**Acceptance:**
- [ ] Несовместимая модель блокирует GUI-run с ясным объяснением.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7.2, 19

### PA-052 · ToolRegistry и adapter IAgentTool → MAF function tools

Stable schema, result mapping text+image, validation, source identity.

**Dependencies:** PA-051

**Acceptance:**
- [ ] Модель вызывает fake tool по схеме, результат проходит единый adapter.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 4.3, 7.1

### PA-053 · Подключить пять Computer Use tools к LLM

Screenshot, Click, Scroll, Type, PressKey со строгими input schemas и desktop gate.

**Dependencies:** PA-052

**Acceptance:**
- [ ] Fake invocation и реальный ComputerController исполняют одинаковый tool contract.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5, 7.1

### PA-054 · Image content round-trip и обновление screenshots

Передача изображения с metadata, последующий screenshot после GUI-step.

**Dependencies:** PA-053

**Acceptance:**
- [ ] Модель получает визуальные данные после действия; нет stale coordinates.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5.6, 7

### PA-055 · System prompts и tool-selection policy

Версионированные инструкции; MCP preference, untrusted content, safe stop.

**Dependencies:** PA-054

**Acceptance:**
- [ ] Prompt files отслеживаются git; агент не принимает сообщения Teams как новые system instructions.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7.3, 14.3

### PA-056 · Agent limits: steps, duration, retries, cancellation

Max tool calls, timeouts, repeated action detection; graceful error result.

**Dependencies:** PA-055

**Acceptance:**
- [ ] Loop прекращается при лимите или Cancel без дальнейших GUI-команд.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 5.7, 7.1, 14.4

### PA-057 · Structured final result and token/cost usage

Status, Summary, Sources, Warnings, Usage; display in UI + persist.

**Dependencies:** PA-056

**Acceptance:**
- [ ] TaskRun завершает статусом с источниками/предупреждениями и статистикой.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 4.1, 7.1, 15.2

### PA-058 · Contract tests: fake model with deterministic tool sequence

Имитация MAF provider: correct tool calls, malformed args, timeout, pause.

**Dependencies:** PA-057

**Acceptance:**
- [ ] Automated integration tests без платного API.

**Verification:**
- dotnet test --filter Agent

**Reference:** 17.2, 17.4

### PA-059 · Live OpenAI GUI E2E read-only flow

Модель читает заголовок тестового окна и возвращает подтверждённый итог.

**Dependencies:** PA-058

**Acceptance:**
- [ ] E2E-01: полный поток LLM→computer tools→result→history, с hotkey takeover.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 18 E2E-01, 16 Phase 3 · **Needs external/manual validation**

## PR-08 — Anthropic and model provider switching (6 задач)

### PA-060 · Anthropic provider: runtime and model creation

Использовать SDK совместимый с зафиксированным MAF; учесть отличия tool/vision.

**Dependencies:** PA-058

**Acceptance:**
- [ ] Claude-profile запускается через тот же IAgentRuntime контракт.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7.2, 16 Phase 3

### PA-061 · Vision/tool capability mapping для Claude

Images, tools, usage, failure semantics, model IDs.

**Dependencies:** PA-060

**Acceptance:**
- [ ] Wrong capability блокирует запуск; real image/tool flow поддержан.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7.2

### PA-062 · UI профилей моделей и переключения provider

Create/update/select profile, test connection, protected keys.

**Dependencies:** PA-061

**Acceptance:**
- [ ] Пользователь выбирает OpenAI или Anthropic для следующего Run.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 12, 7.2

### PA-063 · Neutral conversation handoff on provider switch

Не переносить чужой serialized session; summary + messages, session recreation.

**Dependencies:** PA-062

**Acceptance:**
- [ ] Новый Run на другой модели не использует provider-specific session internals.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7.4, 18 E2E-10

### PA-064 · Cross-provider adapter contract tests

Общая матрица tool call, image input, cancellation, limits, metadata mapping.

**Dependencies:** PA-063

**Acceptance:**
- [ ] Тесты OpenAI и Claude используют одинаковые assertions.

**Verification:**
- dotnet test --filter Providers

**Reference:** 17.4, 18 E2E-10

### PA-065 · Live Claude read-only E2E and provider switch

Проверить на доступной модели Claude, затем switch to OpenAI с сохранённым контекстом.

**Dependencies:** PA-064

**Acceptance:**
- [ ] E2E-10 проведён или статус Blocked с конкретной причиной доступа.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 18 E2E-10 · **Needs external/manual validation**

## PR-09 — MCP lifecycle, discovery and hybrid tools (10 задач)

### PA-066 · MCP connection config and SDK provider abstraction

McpConnectionId, transport, metadata, allowed tools, scoped credentials.

**Dependencies:** PA-058

**Acceptance:**
- [ ] Новый сервер добавляется настройкой без изменения Core.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 8.1, 8.2

### PA-067 · Подключение stdio MCP с безопасным процессом

Command/args allowlisted user config; process lifecycle, stderr logs redacted.

**Dependencies:** PA-066

**Acceptance:**
- [ ] Тестовый local MCP server отвечает и корректно закрывается.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 8.1, 8.2

### PA-068 · Подключение Streamable HTTP MCP

HTTP transport, abort, reconnect, structured errors и timeout.

**Dependencies:** PA-067

**Acceptance:**
- [ ] Тестовый remote MCP server обслуживает list tools и call tools.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 8.1, 8.2

### PA-069 · Dynamic tool discovery и input schema mapping

List tools, stable IDs namespace per connection, version/new tool detection.

**Dependencies:** PA-068

**Acceptance:**
- [ ] MCP tools доступны через ToolRegistry; новые не получают auto approval.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 8.4, 14.1

### PA-070 · MCP tool execution through common permissions

Tool adapters + risk metadata + cancellation; consistent errors and result mapping.

**Dependencies:** PA-069

**Acceptance:**
- [ ] MCP tools исполняются через те же политики, что и GUI tools.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 4.3, 8.4

### PA-071 · Connection lifecycle, timeout, reconnect, disable/remove

Не оставлять orphan processes; auto reconnect bounded; disconnect invalidates tool availability.

**Dependencies:** PA-070

**Acceptance:**
- [ ] Временно недоступный сервер не падает всё приложение.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 8.2, 16 Phase 4

### PA-072 · MCP connections UI, test button, tool allowlist

Добавление stdio/HTTP, тест соединения, список discovered tools.

**Dependencies:** PA-071

**Acceptance:**
- [ ] Можно добавить новый совместимый сервер без пересборки приложения.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 12.1, 16 Phase 4

### PA-073 · MCP integration tests with local test servers

Auth errors, malformed tool responses, cancellation, reconnect, new unsafe tool.

**Dependencies:** PA-072

**Acceptance:**
- [ ] Детерминированная suite без Jira/ADO account.

**Verification:**
- dotnet test --filter Mcp

**Reference:** 17.2, 17.4

### PA-074 · Hybrid MCP + GUI deterministic E2E

Fake Jira + GUI fixture: один Run вызывает оба источника и проверяет desktop lease.

**Dependencies:** PA-073

**Acceptance:**
- [ ] E2E-02, E2E-05 проходят без рабочих аккаунтов.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 18 E2E-02, 18 E2E-05

### PA-075 · Live Jira/Azure DevOps connection smoke

При разрешённых корпоративных правах проверить чтение тикета/задачи, без write.

**Dependencies:** PA-074

**Acceptance:**
- [ ] Реальные ссылки на source появляются в результате; если доступа нет — Blocked с инструкцией.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 8.3, 16 Phase 4 · **Needs external/manual validation**

## PR-10 — Security policies and human approvals (8 задач)

### PA-076 · Policy model: read-only, write, destructive, external data

Risk levels and access contexts, default deny for unknown write tool.

**Dependencies:** PA-071, PA-074, PA-058

**Acceptance:**
- [ ] Непривилегированные actions не могут использовать более высокий уровень разрешений.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 14.1, 14.4

### PA-077 · Tool permission rules per run and per connection

Allow/deny policy, no newly discovered tool default rights; inheritance safe.

**Dependencies:** PA-076

**Acceptance:**
- [ ] Неизвестный MCP write отказывается исполняться без разрешений.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 8.4, 14.1

### PA-078 · Approval state and durable pending actions

ApprovalRequests tied to exact ToolCallId/payload/run/user/expiry.

**Dependencies:** PA-077

**Acceptance:**
- [ ] Повторный Approve другого tool или stale request не подходит.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 14.2, 13.1

### PA-079 · Human-in-the-loop pause and explicit resume

Approval requested before side effect, desktop lease released while waiting.

**Dependencies:** PA-078

**Acceptance:**
- [ ] No write before approval; Reject returns controlled tool denial.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7.1, 14.2

### PA-080 · App WPF approval dialog and UI controls

Show what action will do, target, data destination; Approve/Deny.

**Dependencies:** PA-079

**Acceptance:**
- [ ] User clearly sees action and can decline; no modal UI blocking entire app.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 12, 14.2

### PA-081 · Non-idempotent calls: ToolCall ID and OutcomeUnknown

Track dispatched writes and ack boundaries; no blind replay after crash.

**Dependencies:** PA-080

**Acceptance:**
- [ ] E2E-08: unknown result surfaced, not executed twice.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 9.6, 18 E2E-08

### PA-082 · Prompt injection data boundaries / destination restrictions

Data from emails, tools and screenshots does not override system or allowlists.

**Dependencies:** PA-081

**Acceptance:**
- [ ] Adversarial tool result cannot raise permissions or send secrets via a new channel.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 14.3

### PA-083 · Policy tests with both Computer Use and MCP

Risk classification, approve/reject/expire, unknown write, cancellation race.

**Dependencies:** PA-082

**Acceptance:**
- [ ] Permission policy tested identically across tool sources.

**Verification:**
- dotnet test --filter Policy

**Reference:** 17.1, 18 E2E-07

## PR-11 — Scheduling and idle-aware desktop access (9 задач)

### PA-084 · Schedule entity and recurrence parser

Once/interval/daily/weekdays, chosen timezone; display NextRunAtUtc.

**Dependencies:** PA-083, PA-047

**Acceptance:**
- [ ] Невалидные расписания не сохраняются; следующий запуск детерминирован.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 10.1

### PA-085 · Background scheduler and persistent occurrence identity

Hosted service; unique ScheduleId+ScheduledOccurrenceUtc, atomic create.

**Dependencies:** PA-084

**Acceptance:**
- [ ] Двойной wake/restart создаёт один TaskRun на occurrence.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 10, 9.4

### PA-086 · Timezone/DST and misfire policies

Skip/RunOnce/CatchUp; UTC storage + local scheduling.

**Dependencies:** PA-085

**Acceptance:**
- [ ] Тесты на Europe/Kyiv DST и многократные пропущенные интервалы.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 10.2, 17.1

### PA-087 · UI create/edit/enable/disable scheduled tasks

Schedule editor; show future triggers and run history.

**Dependencies:** PA-086

**Acceptance:**
- [ ] Может быть создана 10-минутная проверка и отключена без удаления.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 12.1, 10

### PA-088 · 15-second countdown before scheduled GUI use

Start Now/Skip Once/Defer/Disable controls; warning displays task.

**Dependencies:** PA-087

**Acceptance:**
- [ ] No GUI action before selected policy permits.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.9, 10.3

### PA-089 · GetLastInputInfo pre-run idle check

StartWhenIdle default; keep system idle separate from agent activity.

**Dependencies:** PA-088

**Acceptance:**
- [ ] При пользовательской активности scheduled GUI Run не забирает фокус.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.9, 6.7

### PA-090 · First GUI call gate during initially MCP-only run

Scheduled MCP run calls GUI later: acquire lease and warning/idle policy.

**Dependencies:** PA-089

**Acceptance:**
- [ ] E2E-05: нет скрытого click after MCP reads.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.10, 18 E2E-05

### PA-091 · Defer TTL and occurrence coalescing

Pending GUI with max wait; merge repeated checks and prevent notification spam.

**Dependencies:** PA-090

**Acceptance:**
- [ ] Пропущенные 10-минутные интервалы не копятся в десятки runs.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.11, 10.2

### PA-092 · Schedule tests: restarts, concurrency, DST, idle

Fake clock tests and integration tests with DB; one real 10-min smoke optional.

**Dependencies:** PA-091

**Acceptance:**
- [ ] Идемпотентные runs и E2E-04 verified.

**Verification:**
- dotnet test --filter Scheduler

**Reference:** 17, 18 E2E-04

## PR-12 — Channels, Telegram and reliable notifications (10 задач)

### PA-093 · Introduce channel-agnostic inbound/outbound contracts

IInboundChannel / IOutboundChannel, messages/actions, routes.

**Dependencies:** PA-083, PA-022

**Acceptance:**
- [ ] Fake Telegram and fake second channel use same application commands.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 4.5, 11.2

### PA-094 · Single-user identity and channel ownership bindings

Allowlist allowed external sender/chat IDs, pending pairing.

**Dependencies:** PA-093

**Acceptance:**
- [ ] Unauthorized senders cannot start/cancel/approve run.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 11.1, 14.4

### PA-095 · Telegram long-polling receiver and update dedup

getUpdates, external update ids, safe offset updates, bounded retries.

**Dependencies:** PA-094

**Acceptance:**
- [ ] Repeated Telegram update accepted once; no public webhook needed.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 11.1

### PA-096 · Telegram natural-language task routing + system commands

/status,/tasks,/cancel,/pause,/resume routed without LLM for control.

**Dependencies:** PA-095

**Acceptance:**
- [ ] Commands from authorized user work independently of AI.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 11.1, 4.4

### PA-097 · Telegram outgoing summaries, errors, statuses, buttons

Formatting, message chunks, inline callbacks with unguessable approved scope.

**Dependencies:** PA-096

**Acceptance:**
- [ ] Bot returns run summary and can request per-action confirmation.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 11.1, 11.2

### PA-098 · Notification outbox, bounded retries and independent delivery state

Persist attempt status, schedule retries, terminal run not failed by Telegram outage.

**Dependencies:** PA-097

**Acceptance:**
- [ ] Network outage: complete Run saved, notification delivered once after recovery.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 11.2, 13.1

### PA-099 · Telegram approvals/cancel/pause authorization and dedup

Callbacks scoped to actor/run/action/expiry; duplicates safe.

**Dependencies:** PA-098

**Acceptance:**
- [ ] Unauthorized callback rejected; approved action fired once.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 11.1, 14.2

### PA-100 · Desktop outbound notifications via same abstraction

Toast/tray and outgoing messages share same contract with Telegram.

**Dependencies:** PA-099

**Acceptance:**
- [ ] One Run can route notifications to configured multiple destinations.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 11.2, 12.2

### PA-101 · Channel configuration WPF and Test Connection

Pair bot, configure allowed identity and outgoing events; protect tokens.

**Dependencies:** PA-100

**Acceptance:**
- [ ] Wizard saves settings securely, Test Connection reports useful error.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 11, 12.1

### PA-102 · Telegram HTTP fake integration and second-channel compatibility tests

Verify polling offset, retries, allowlist, buttons, fake Discord adapter.

**Dependencies:** PA-101

**Acceptance:**
- [ ] E2E-09: second channel added without Core, Computer or Agent modifications.

**Verification:**
- dotnet test --filter Channels

**Reference:** 17.4, 18 E2E-09

## PR-13 — Conversations and follow-up context (6 задач)

### PA-103 · Conversation lifecycle and origin-independent correlation

Route reply-to message to Conversation; persist external identifiers.

**Dependencies:** PA-102, PA-064

**Acceptance:**
- [ ] Desktop and Telegram messages can share a conversation safely.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7.4, 11.1

### PA-104 · Persist conversation messages with source references

Roles, link to RunId, sources, timestamps, errors; secrets filtered.

**Dependencies:** PA-103

**Acceptance:**
- [ ] Restart does not discard previous summaries and follow-up context.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 13.1, 7.4

### PA-105 · Agent sessions persistence with provider-specific serialization

Store session version and provider; restore only compatible session.

**Dependencies:** PA-104

**Acceptance:**
- [ ] Old session loads on same provider; incompatible provider uses fallback.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7.4, 13.3

### PA-106 · Neutral handoff and compact context summary

Avoid infinite history; preserve source links and decision context.

**Dependencies:** PA-105

**Acceptance:**
- [ ] Subsequent follow-up uses correct issue without replaying old tool writes.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 7.4, 19

### PA-107 · Telegram follow-up routing and fresh source lookup

Reply to prior bot message or follow-up in active thread; ask for specifics if unclear.

**Dependencies:** PA-106

**Acceptance:**
- [ ] E2E-06: user gets details of previous Teams/Jira finding.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 11.1, 18 E2E-06 · **Needs external/manual validation**

### PA-108 · Session/restart and cross-channel conversation regression tests

Fake LLM responds across two runs, restart, provider switch.

**Dependencies:** PA-107

**Acceptance:**
- [ ] Follow-up is stable after restart; no duplicate writes.

**Verification:**
- dotnet test --filter Conversation

**Reference:** 17, 18 E2E-06

## PR-14 — Automatic activity pause and resilience (7 задач)

### PA-109 · Prototype physical input observation (Windows)

GetLastInputInfo + relevant low-level hooks; document limitations distinguishing injected events.

**Dependencies:** PA-092, PA-047

**Acceptance:**
- [ ] Known limitations documented; no excessive resource use idle.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.7, 16 Phase 7

### PA-110 · Attribute own injected input vs physical user input

Tag SendInput injection when possible; robust heuristics and fail-safe uncertain case.

**Dependencies:** PA-109

**Acceptance:**
- [ ] Own input does not self-trigger auto-pause; human input can trigger.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.7

### PA-111 · Auto-pause on human activity in active GUI Run

Gate closes without LLM, no automatic GUI resume; transition reason AutoPause.

**Dependencies:** PA-110

**Acceptance:**
- [ ] User gets mouse promptly; no other injected steps until Resume.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.7, 6.12

### PA-112 · Pause during in-flight LLM and MCP request

No new GUI action after user pause regardless of queued tool result; side effects controlled.

**Dependencies:** PA-111

**Acceptance:**
- [ ] Race test: delayed model tool call cannot run after lease revoke.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.3, 6.4, 7.5

### PA-113 · Handle lock/unlock, sleep/wake, monitor layout changes

Graceful Blocked/Paused state, invalidate coordinates, manual safe resume.

**Dependencies:** PA-112

**Acceptance:**
- [ ] No blind click on unlock, resolution or window change.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 6.6, 16 Phase 7

### PA-114 · Stress & race test suite for GUI takeover

Many click/type, cancelled midway, overlapping runs, forced exceptions.

**Dependencies:** PA-113

**Acceptance:**
- [ ] Repeated stress runs preserve invariants: no stale lease and no held modifiers.

**Verification:**
- dotnet test --filter Takeover

**Reference:** 6.12, 17.3

### PA-115 · Real Windows user-takeover regression checklist

Hotkey, tray, window switching, long type, scheduled run while active.

**Dependencies:** PA-114

**Acceptance:**
- [ ] E2E-03/04/05 under real desktop passed or concrete blocker noted.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 18 E2E-03, 18 E2E-04, 18 E2E-05 · **Needs external/manual validation**

## PR-15 — Security hardening, packaging and release verification (10 задач)

### PA-116 · Protect secrets using Windows credential storage / DPAPI

Local protected storage for model keys, Telegram and MCP credentials; no plain json keys.

**Dependencies:** PA-114, PA-108, PA-074

**Acceptance:**
- [ ] Keys survive restart for current owner; are not exported in settings backups.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 13.2, 14.4

### PA-117 · Redact sensitive logs and screenshots/data retention

No screenshot bytes/private text by default; configurable expiry and cleanup.

**Dependencies:** PA-116

**Acceptance:**
- [ ] Standard logs contain no credentials, message bodies or screenshots.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 14.3, 15.3

### PA-118 · Structured logging, metrics, Run diagnostics screen

RunId, ToolCallId, lease gen, usage, failures; UI displays actionable problems.

**Dependencies:** PA-117

**Acceptance:**
- [ ] Failures and pause causes diagnosed without secret exposure.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 15.1, 15.2

### PA-119 · First-run wizard and connectivity diagnostics

Choose model, validate vision/tools, optional MCP and Telegram onboarding.

**Dependencies:** PA-118

**Acceptance:**
- [ ] Fresh Windows profile can run read-only first task from wizard.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 12, 16 Phase 8

### PA-120 · Settings export/import, migration/backup plan

Export only non-secret configuration; validate schema version, backup DB before migrations.

**Dependencies:** PA-119

**Acceptance:**
- [ ] Upgrade does not lose history; export contains no secrets.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 13.3, 16 Phase 8

### PA-121 · Self-contained Windows publish and optional autostart

Build release artifact, tray exit behavior, optional login startup with user consent.

**Dependencies:** PA-120

**Acceptance:**
- [ ] Runs on clean Windows without preinstalled .NET; autostart opt-in.

**Verification:**
- dotnet publish -c Release

**Reference:** 2, 16 Phase 8

### PA-122 · Provider, MCP, channel and tools contract regression matrix

Shared behavior for cancellation, errors, image/text, policy, DI extension without changing Core.

**Dependencies:** PA-121

**Acceptance:**
- [ ] Fake second channel & fake provider pass; no contract regression.

**Verification:**
- dotnet test

**Reference:** 17.4, 18 E2E-09, 18 E2E-10

### PA-123 · Run all ten E2E acceptance scenarios

Document test results E2E-01..E2E-10 incl. approval and unknown outcomes.

**Dependencies:** PA-122

**Acceptance:**
- [ ] All ten scenarios documented with passed evidence; external access gating explicit.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 18, 22 · **Needs external/manual validation**

### PA-124 · Final privacy/security/operational review

Check prompt injection, corporate data egress policy, no lock/UAC bypass, cost caps, no blind retries.

**Dependencies:** PA-123, PA-059, PA-065, PA-075, PA-115

**Acceptance:**
- [ ] Documented review checklist passes; risky defaults disabled.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 14, 19, 21, 22

### PA-125 · Release documentation, known limitations, versioned MVP gate

README setup, auth expectations, incident recovery, limitations, changelog, release tag candidate.

**Dependencies:** PA-124

**Acceptance:**
- [ ] All 22 MVP DoD checks pass and release candidate is reproducible.

**Verification:**
- Проверить критерии вручную либо через подходящий автоматический тест; зафиксировать вывод и команду/свидетельство.

**Reference:** 20, 22, 23, 24 · **Needs external/manual validation**

