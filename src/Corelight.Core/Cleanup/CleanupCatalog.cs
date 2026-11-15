namespace Corelight.Core.Cleanup;

internal static class CleanupCatalog
{
    internal static readonly IReadOnlyList<CleanupCategory> Categories = Array.AsReadOnly(new CleanupCategory[]
    {
        new("user-temp", "Временные файлы пользователя", "Старше 7 дней. Закройте программы; нужные файлы из временной папки заранее сохраните.", true),
        new("windows-temp", "Временные файлы Windows", "Старше 7 дней. Занятые и защищённые файлы будут пропущены.", true),
        new("thumbnails", "Кэш эскизов", "Миниатюры изображений в Проводнике. После очистки создадутся заново.", false),
        new("shaders", "Кэш шейдеров DirectX", "После очистки первые запуски игр могут быть медленнее из-за пересоздания кэша.", false),
        new("error-reports", "Отчёты об ошибках Windows", "Старше 7 дней. Удалятся сохранённые отчёты, которые могут понадобиться для диагностики.", false),
        new("crash-dumps", "Дампы сбоев", "Старше 7 дней. Эти файлы помогают выяснять причины сбоев программ и Windows.", false),
        new("shortcuts", "Неработающие ярлыки", "Рабочий стол и меню «Пуск». Только ссылки на отсутствующие файлы локальных дисков; проверьте список.", false),
        new("recycle-bin", "Корзина", "Полное удаление содержимого корзины текущей учётной записи на всех дисках, включая новые элементы.", false),
    });

    internal static IReadOnlyList<CleanupRule> Rules()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var week = TimeSpan.FromDays(7); var day = TimeSpan.FromDays(1);
        var rules = new List<CleanupRule>
        {
            new("user-temp", Path.Combine(local, "Temp"), week),
            new("windows-temp", Path.Combine(windows, "Temp"), week),
            new("thumbnails", Path.Combine(local, "Microsoft", "Windows", "Explorer"), day, CleanupFilter.Thumbnails),
            new("shaders", Path.Combine(local, "D3DSCache"), day),
            new("crash-dumps", Path.Combine(local, "CrashDumps"), week, CleanupFilter.Dumps),
            new("crash-dumps", Path.Combine(windows, "Minidump"), week, CleanupFilter.Dumps),
        };
        foreach (var basePath in new[] { local, common })
            foreach (var folder in new[] { "ReportArchive", "ReportQueue" })
                rules.Add(new("error-reports", Path.Combine(basePath, "Microsoft", "Windows", "WER", folder), week));
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path)) rules.Add(new("shortcuts", path, day, CleanupFilter.BrokenShortcuts));
        }
        return rules.AsReadOnly();
    }
}
