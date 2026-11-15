using Wpf.Ui.Controls;
using Corelight.Core.Tweaks;

namespace Corelight.App.Services;

/// <summary>Название, пояснение и иконка раздела. Category = null — раздел «Автозагрузка».</summary>
public sealed record CategoryInfo(TweakCategory? Category, string Title, string Subtitle, SymbolRegular Icon)
{
    public static readonly IReadOnlyList<CategoryInfo> All =
    [
        new(TweakCategory.Apps, "Приложения", "AppX удаляются для всех пользователей, возвращаются текущему, если доступны в Store. OneDrive — из штатного установщика.", SymbolRegular.Apps24),
        new(TweakCategory.Privacy, "Телеметрия и приватность", "Сбор данных, реклама и фоновые задачи диагностики.", SymbolRegular.ShieldLock24),
        new(TweakCategory.Interface, "Интерфейс и фон", "Виджеты, советы и фоновая активность. Внешний вид Windows не меняется.", SymbolRegular.Board24),
        new(TweakCategory.Gaming, "Игровые", "Штатные игровые настройки Windows. Без разгона и без вмешательства в игры.", SymbolRegular.Games24),
        new(TweakCategory.Services, "Службы", "Необязательные службы для домашнего игрового ПК.", SymbolRegular.Server24),
        new(TweakCategory.Network, "Сеть", "Системные параметры сети. DNS, драйверы и hosts-файл не меняются.", SymbolRegular.Globe24),
        new(null, "Автозагрузка", "Запуск программ при входе в Windows, как в Диспетчере задач.", SymbolRegular.Flash24),
    ];

    public static CategoryInfo For(TweakCategory category) => All.First(c => c.Category == category);
}
