namespace Corelight.App.Services;

/// <summary>Страница, которой нужно обновиться при каждом переходе на неё.</summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync();
}

public sealed class NavigationService
{
    public event Action<Type>? NavigationRequested;

    public void NavigateTo<TPage>() => NavigationRequested?.Invoke(typeof(TPage));
}
