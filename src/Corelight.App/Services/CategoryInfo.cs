using Wpf.Ui.Controls;
using Corelight.Core.Tweaks;

namespace Corelight.App.Services;

/// <summary>Название, пояснение и иконка раздела. Category = null — раздел «Автозагрузка».</summary>
public sealed record CategoryInfo(TweakCategory? Category, string Title, string Subtitle, SymbolRegular Icon)
{
    public static readonly IReadOnlyList<CategoryInfo> All =
    [
        new(TweakCategory.Apps, "Приложения", "Предустановленные приложения Microsoft. Удалённое приложение можно вернуть из Microsoft Store.", SymbolRegular.Apps24),
        new(TweakCategory.Privacy, "Телеметрия и приватность", "Сбор данных, реклама и фоновые задачи диагностики.", SymbolRegular.ShieldLock24),
        new(TweakCategory.Interface, "Интерфейс и фон", "Виджеты, советы и фоновая активность. Внешний вид Windows не меняется.", SymbolRegular.Board24),
        new(TweakCategory.Gaming, "Игровые", "Штатные игровые настройки Windows. Без разгона и без вмешательства в игры.", SymbolRegular.Games24),
        new(TweakCategory.Services, "Службы", "Системные службы, которые на домашнем игровом ПК обычно не нужны.", SymbolRegular.Server24),
        new(TweakCategory.Network, "Сеть", "Системные параметры сети. DNS, драйверы и hosts-файл не меняются.", SymbolRegular.Globe24),
        new(null, "Автозагрузка", "Программы, которые запускаются при входе в Windows. Переключатель работает так же, как в Диспетчере задач.", SymbolRegular.Flash24),
    ];

    public static CategoryInfo For(TweakCategory category) => All.First(c => c.Category == category);
}
