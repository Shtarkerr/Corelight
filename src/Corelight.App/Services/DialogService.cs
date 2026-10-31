using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Corelight.App.Services;

public enum DialogChoice
{
    Primary,
    Secondary,
    Cancel,
}

public sealed class DialogService
{
    /// <param name="primary">Текст основной кнопки; null — только кнопка закрытия.</param>
    public async Task<DialogChoice> ShowAsync(string title, string message, string? primary, string? secondary = null, string close = "Отмена")
    {
        var box = new MessageBox
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 580, HorizontalAlignment = HorizontalAlignment.Left },
            PrimaryButtonAppearance = ControlAppearance.Primary,
            CloseButtonText = close,
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (primary is not null)
            box.PrimaryButtonText = primary;
        else
            box.IsPrimaryButtonEnabled = false;

        if (secondary is not null)
        {
            box.SecondaryButtonText = secondary;
            // Три кнопки с длинными подписями не помещаются в стандартную ширину.
            box.MinWidth = 640;
        }
        else
        {
            box.IsSecondaryButtonEnabled = false;
        }

        return await box.ShowDialogAsync() switch
        {
            MessageBoxResult.Primary => DialogChoice.Primary,
            MessageBoxResult.Secondary => DialogChoice.Secondary,
            _ => DialogChoice.Cancel,
        };
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primary) =>
        await ShowAsync(title, message, primary) == DialogChoice.Primary;

    public Task ShowInfoAsync(string title, string message) => ShowAsync(title, message, primary: null, close: "OK");
}
