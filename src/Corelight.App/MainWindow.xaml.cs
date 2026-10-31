using System.ComponentModel;
using Corelight.App.ViewModels;
using Wpf.Ui.Controls;

namespace Corelight.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.SelectedItem ??= viewModel.Items[0];
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Закрытие посреди применения оставило бы часть изменений без отчёта; бэкап при этом уже сохранён.
        if (_viewModel.State.IsBusy)
        {
            e.Cancel = true;
            System.Windows.MessageBox.Show(this, "Дождитесь окончания текущей операции.", "Corelight",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        base.OnClosing(e);
    }
}
