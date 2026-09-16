using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>현재 표시 중인 페이지 뷰모델을 관리. 셸의 ContentControl 이 <see cref="CurrentViewModel"/> 을 바인딩한다.</summary>
public interface INavigationService
{
    ViewModelBase? CurrentViewModel { get; }
    void NavigateTo(Type viewModelType);
    void NavigateTo<TViewModel>() where TViewModel : ViewModelBase;
}

/// <summary>
/// DI 컨테이너에서 페이지 뷰모델을 해석해 전환한다. 직전 뷰모델은 <see cref="ViewModelBase.OnDeactivated"/>
/// + Dispose 로 정리하고, 새 뷰모델은 <see cref="ViewModelBase.OnActivated"/> 로 활성화한다.
/// (Blazor 의 서킷별 Scoped/StateHasChanged 를 대체하는 데스크톱 수명 관리 지점)
/// </summary>
public sealed partial class NavigationService : ObservableObject, INavigationService
{
    private readonly IServiceProvider _services;

    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    public NavigationService(IServiceProvider services) => _services = services;

    public void NavigateTo<TViewModel>() where TViewModel : ViewModelBase
        => NavigateTo(typeof(TViewModel));

    public void NavigateTo(Type viewModelType)
    {
        var next = (ViewModelBase)_services.GetRequiredService(viewModelType);
        if (CurrentViewModel is { } prev)
        {
            prev.OnDeactivated();
            prev.Dispose();
        }
        CurrentViewModel = next;
        next.OnActivated();
    }
}
