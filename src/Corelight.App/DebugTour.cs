#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Corelight.App.Services;
using Corelight.App.ViewModels;

namespace Corelight.App;

/// <summary>
/// Только для разработки: Corelight.exe --tour=&lt;папка&gt; проходит по всем страницам
/// (сканирование только читает систему), сохраняет скриншоты и закрывает приложение.
/// </summary>
internal static class DebugTour
{
    public static async Task RunAsync(IServiceProvider services, Window window, string dir)
    {
        Directory.CreateDirectory(dir);
        var main = services.GetRequiredService<MainViewModel>();
        var state = services.GetRequiredService<AppState>();

        await SettleAsync(state);
        Save(window, dir, "1-home");

        await services.GetRequiredService<HomeViewModel>().ScanCommand.ExecuteAsync(null);
        await SettleAsync(state);
        Save(window, dir, "2-results");

        var advanced = services.GetRequiredService<AdvancedViewModel>();
        main.SelectedItem = main.Items[2];
        await SettleAsync(state);
        for (var i = 0; i < advanced.Sections.Count; i++)
        {
            advanced.SelectedSection = advanced.Sections[i];
            await SettleAsync(state);
            Save(window, dir, $"3-advanced-{i}");
        }

        main.SelectedItem = main.Items[3];
        await SettleAsync(state);
        Save(window, dir, "4-history");

        main.SelectedItem = main.Items.First(i => i.Page is CleanupViewModel);
        await SettleAsync(state);
        Save(window, dir, "4-cleanup-before");
        await services.GetRequiredService<CleanupViewModel>().AnalyzeCommand.ExecuteAsync(null);
        await SettleAsync(state);
        Save(window, dir, "4-cleanup-analysis");

        // Диалоги только показываются и закрываются без выбора, поэтому ничего не применяется.
        var dialogs = services.GetRequiredService<DialogService>();
        await CaptureDialogAsync(dir, "5-dialog-confirm", () => dialogs.ConfirmAsync(
            "Применить изменения?", "Будет применено изменений: 34.\n\nПеред этим программа создаст точку восстановления Windows.", "Применить"));
        await CaptureDialogAsync(dir, "6-dialog-three-buttons", () => dialogs.ShowAsync(
            "Защита системы выключена", "Чтобы создать точку восстановления, нужно включить защиту системы.", "Включить и создать", "Продолжить без точки"));
        await CaptureDialogAsync(dir, "7-dialog-info", () => dialogs.ShowInfoAsync(
            "Готово", "Применено изменений: 34.\n\nЧасть изменений вступит в силу после перезагрузки."));

        window.Close();
    }

    private static async Task CaptureDialogAsync(string dir, string name, Func<Task> show)
    {
        // ShowDialog модальный и может не вернуть управление до закрытия, поэтому снимок планируем заранее.
        _ = Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(1200);
            var box = Application.Current.Windows.OfType<Wpf.Ui.Controls.MessageBox>().FirstOrDefault();
            if (box is null)
            {
                File.WriteAllText(Path.Combine(dir, name + ".error.txt"), "Окно диалога не найдено");
                return;
            }
            Save(box, dir, name);
            box.TemplateButtonCommand.Execute(Wpf.Ui.Controls.MessageBoxButton.Close);
        });
        try
        {
            await show();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, name + ".error.txt"), ex.ToString());
        }
    }

    private static async Task SettleAsync(AppState state)
    {
        await Task.Delay(700);
        while (state.IsBusy)
            await Task.Delay(200);
        await Task.Delay(500);
    }

    private static void Save(Window window, string dir, string name)
    {
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var size = new Size(root.ActualWidth, root.ActualHeight);
        var dpi = VisualTreeHelper.GetDpi(window);

        // У окна фон Mica, которого нет в RenderTargetBitmap, поэтому подкладываем тёмную заливку.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 32, 32)), null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(size));
        }

        var bitmap = new RenderTargetBitmap(
            (int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, name + ".png"));
        encoder.Save(file);
    }
}
#endif
