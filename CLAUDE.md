# Corelight — контекст проекта

Общение с пользователем и все тексты в программе — **на русском**.

- Репозиторий: `github.com/Shtarkerr/Corelight`, лицензия GPL-3.0. Сторонние лицензии лежат в `THIRD-PARTY-NOTICES.txt`; при добавлении NuGet-пакета, который попадает в exe, допишите туда его лицензию.
- Рабочая папка на диске по историческим причинам называется `C:\Windows Optimizer`. До 2026-11-01 проект назывался WinOptimizer.
- Git и GitHub CLI (`gh`) стоят через winget. Автор коммитов: `Shtarkerr <125823204+Shtarkerr@users.noreply.github.com>`.

## Что это

GUI-оптимизатор Windows 11 для игр. Цель — снизить фоновое потребление ресурсов системой, чтобы вырос минимальный FPS (1% low) и стабильность кадров.

Как работает:
- **Главная.** Сводка по железу и кнопка «Сканировать систему»; сканирование только читает систему.
- **Результаты сканирования.** Рекомендации с галочками: `Safe` отмечены сразу, `Caution` пользователь отмечает сам.
- **Расширенное.** Тумблеры по разделам, у каждого пункта короткое описание. Тумблер включён = оптимизация применена. У приложений вместо тумблера кнопки «Удалить» / «Вернуть».
- **История и откат.**

Обещания об эффекте в интерфейсе — только честные: «+50% FPS» не пишем.

## Жёсткие правила пользователя (не нарушать)

1. **Никакого разгона.** Частоты, напряжения, лимиты мощности, параметры питания CPU, BIOS и драйверы не трогаем. Разрешены только штатные схемы питания Windows.
2. **Ничего, что может дать бан в Steam/FACEIT/Vanguard/EAC/BattlEye.**
   - Не трогаем Secure Boot, TPM, VBS/HVCI, DSE/test-signing, bcdedit, Defender, hosts-файл и службы античитов.
   - Никаких драйверов, инъекций, изменения файлов игр, резидентных «бустеров».
3. **Визуальную часть системы и игр не урезаем.** Анимации, прозрачность, DWM и `Control Panel\Desktop` не трогаем.
4. **Windows Update не отключаем.** Отключаем только P2P-раздачу (Delivery Optimization).

Эти правила проверяет код: `src/Corelight.Core/Catalog/GuardrailValidator.cs`. Твик, который их нарушает, не загружается. Тесты лежат в `CatalogTests`. Любой новый твик проверять на соответствие этим правилам.

## Стек и структура

C# / .NET 10 (`net10.0-windows10.0.22621.0`), WPF, WPF-UI 4.3.0 (тёмная Fluent-тема), CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection, Serilog. Тесты на xUnit.

```
src/Corelight.Core     движок (без UI)
  Catalog/Data/*.json       каталог твиков, по файлу на категорию (Apps, Privacy, Interface, Gaming, Services, Network)
  Catalog/                  TweakCatalog (загрузка вшитых JSON), GuardrailValidator
  Tweaks/                   TweakDefinition, TweakAction (полиморфный JSON по полю "type"), TweakEngine
  Actions/                  registry, registryListEntry, service, task, appx, powerPlan, tcpNoDelay
  Platform/                 адаптеры Windows за интерфейсами (реестр, sc.exe, планировщик через COM, PackageManager WinRT, powercfg, ProcessRunner)
  Backup/                   JsonBackupStore → %ProgramData%\Corelight\backup.json
  SystemInfo/               HardwareProbe (WMI), HagsProbe (D3DKMT), RestorePointService (WMI SystemRestore)
  Scanning/Scanner.cs, Startup/StartupManager.cs (StartupApproved, как в Диспетчере задач)
src/Corelight.App      WPF: Services (AppState, ApplyCoordinator, DialogService, NavigationService), ViewModels, Views, DebugTour.cs
tests/Corelight.Tests  модульные тесты на фейках (Fakes.cs) + WindowsIntegrationTests (Category=Integration)
```

## Ключевые решения

- **Бэкап.** У каждого действия есть `Detect` / `Capture` / `Apply` / `Revert` / `RevertToDefault`. Перед применением исходное состояние сохраняется в бэкап. Повторное применение первый бэкап не перезаписывает. Если какое-то действие падает, уже выполненные откатываются.
- **`default` в JSON** — значение Windows по умолчанию. Нужно, чтобы откатить твик, применённый не нами (бэкапа нет). Для реестра `null` означает «удалить значение».
- **`optional: true`** — ошибка действия не отменяет весь твик. Используется для задач планировщика, которые Windows может не дать отключить или которых нет в новой версии.
- **`exclusiveGroup`** — взаимоисключающие твики (схемы питания). `ApplyCoordinator` перед применением вызывает `TweakEngine.RevertConflictsAsync`.
- **Точка восстановления** создаётся один раз за сеанс, перед первым изменением (`ApplyCoordinator`).
- **Приложения.** «Применено» = приложения нет в системе. Удаление идёт для всех пользователей и снимает подготовку пакета (deprovision). Возврат — через `winget --source msstore` по `storeId`. В «Расширенном» показываются только установленные приложения или удалённые нами.
- **Удалять нельзя:** Store, App Installer/winget, Edge/WebView2, VCLibs/UI.Xaml, Xbox Identity Provider, GamingServices.

## Команды

```powershell
dotnet build Corelight.slnx
dotnet test tests/Corelight.Tests                     # 49 тестов; только модульные: --filter Category!=Integration
dotnet publish src/Corelight.App -c Release -o publish # один Corelight.exe (~71 МБ, self-contained)
```

- Если `dotnet` не находится в терминале VS Code (SDK ставился после запуска VS Code), обновите PATH:
  `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`
- Программа требует прав администратора (манифест `requireAdministrator`). Журнал: `%ProgramData%\Corelight\logs`.
- **Проверка интерфейса без UAC.** Debug-сборка: `Corelight.exe --tour=<папка>`. Тур работает без прав администратора и только читает систему. Он проходит по всем страницам и диалогам, сохраняет PNG-скриншоты и закрывается.
- **Ограничение UIPI.** Окно, запущенное с правами администратора, из обычной оболочки нельзя ни закрыть, ни кликнуть. Если оно висит, сборка не перезапишет exe; попросите пользователя закрыть окно.

## Статус (на 2026-11-01)

**Готово:**
- Полный функционал по плану, 64 твика в каталоге.
- Проведено ревью, исправлено:
  - конфликт схем питания (добавлен `exclusiveGroup`);
  - обход правил через реестровый путь `\Services\<защищённая служба>`;
  - OEM-кодировка вывода `sc` / `powercfg`;
  - падение «Расширенного» на нестандартной записи `StartupApproved`;
  - ложное «применено» у твиков только из `optional`-действий;
  - необработанные исключения WMI при создании точки восстановления;
  - удалён `Microsoft.Extensions.Hosting`;
  - мелкие недочёты интерфейса.
- Проверено на ПК пользователя без изменений системы: сканирование, все страницы, диалоги, однофайловый exe, интеграционные тесты.
- Проект переименован WinOptimizer → Corelight. Добавлены LICENSE (GPL-3.0), THIRD-PARTY-NOTICES.txt и .gitattributes. Опубликован на GitHub (первый коммит `c4c52e7`).

**Не проверено:** реальное применение и откат твиков ни разу не запускались.

### План дальше (согласован с пользователем, по порядку)

1. **Claude: утилита сверки состояния.** Предложено, пользователь ещё не подтвердил.
   - Отладочная команда `Corelight.exe --snapshot <файл.json>`: пишет в JSON состояние всех целей каталога (значения реестра, тип запуска служб, состояние задач, активная схема питания, установленные приложения, `StartupApproved`).
   - Сценарий: снимок до → применить всё → откатить всё → снимок после → сравнить файлы.
2. **Claude: GitHub Actions.** Предложено, ещё не подтверждено.
   - На `windows-latest` при каждом push: build + `dotnet test --filter Category!=Integration`.
   - Значок «build passing» в README.
3. **Пользователь: виртуалка в Hyper-V.**
   - Включить компонент Hyper-V и перезагрузиться.
   - ISO Windows 11 24H2/25H2 с сайта Microsoft.
   - VM поколения 2 с vTPM: 4 ядра, 8 ГБ, 64 ГБ. Активация не нужна.
   - Сделать контрольную точку. Пользователь просил подробно провести по шагам, когда дойдёт.
4. **Тест в VM по чек-листу:**
   1. Создание точки восстановления, в том числе при выключенной защите системы.
   2. Применить все рекомендации → перезагрузка → проверить «Пуск», поиск, Store, winget, Steam.
   3. Службы через `sc.exe`, задачи планировщика, переключение схем «Высокая» ↔ «Максимальная».
   4. Удаление приложения и «Вернуть» через winget (заодно проверить `storeId`: они указаны по памяти).
   5. «Откатить всё» → снимки состояния до и после совпадают.
   6. Пользователь присылает результаты, снимки и журнал (`%ProgramData%\Corelight\logs`).
5. **Первый релиз `v0.1.0`:** `gh release create` с `publish\Corelight.exe`, LICENSE и THIRD-PARTY-NOTICES.txt.
6. **Замер FPS на реальном ПК** (CapFrameX/PresentMon, одна сцена до и после).

Мелочь: на ПК пользователя осталась `C:\ProgramData\WinOptimizer` с журналами тестовых запусков. Её можно удалить, нужны права администратора.

**Известные ограничения:**
- Нет `storeId` у Tips, Teams, Dev Home, Cortana, Skype, Mixed Reality; их можно вернуть только вручную через Store.
- В автозагрузке не показываются задачи планировщика, запускаемые при входе.
- HKCU относится к учётной записи, от имени которой выданы права администратора.
- Exe не подписан, SmartScreen покажет предупреждение.
- Константа `KMTQAITYPE_WDDM_2_7_CAPS = 70` взята по памяти. На ПК пользователя HAGS определился корректно, а при ошибке запроса твик просто не рекомендуется.
