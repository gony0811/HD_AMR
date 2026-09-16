using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>셸 뷰모델. 좌측 NavMenu 항목 + 현재 페이지(<see cref="Navigation"/>)를 노출.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public INavigationService Navigation { get; }

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty]
    private NavItem? _selectedItem;

    public MainWindowViewModel(INavigationService navigation)
    {
        Navigation = navigation;

        // 기존 HD.AMR.Web/Components/Layout/NavMenu.razor 의 18개 항목 이식.
        // 아직 포팅되지 않은 페이지는 Target = null (클릭 시 "준비 중" 플레이스홀더로 이동).
        NavItems = new ObservableCollection<NavItem>
        {
            new("Dashboard",     "🏠", typeof(HomeViewModel)),
            new("AMR",           "🚚", typeof(AmrViewModel)),
            new("Teaching",      "✏️", null),
            new("Inspection",    "📋", null),
            new("검사 레시피",   "📗", typeof(InspectionRecipesViewModel)),
            new("검사 포인트(X-Y)", "📍", typeof(InspectionPointsViewModel)),
            new("검사 매핑 요약", "🗂", null),
            new("Cobot",         "🤖", typeof(CobotViewModel)),
            new("Sequence",      "🔀", typeof(SequenceViewModel)),
            new("Camera",        "🎥", typeof(CameraViewModel)),
            new("용접 추적",     "📈", typeof(WeldTrackingViewModel)),
            new("Vision Interface", "🖧", typeof(VisionInterfaceViewModel)),
            new("비드 라벨링",   "🏷", null),
            new("비전 학습",     "🧠", null),
            new("QR Pose Teaching", "🧭", typeof(CalibrationViewModel)),
            new("Parameter",     "🎛", typeof(ParametersViewModel)),
            new("Laser Sensor",  "📏", null),
            new("IO Module",     "🔌", typeof(IoModuleViewModel)),
        };

        SelectedItem = NavItems[0];
        Navigation.NavigateTo(typeof(HomeViewModel));
    }

    partial void OnSelectedItemChanged(NavItem? value)
    {
        if (value is not null)
            Navigate(value);
    }

    [RelayCommand]
    private void Navigate(NavItem item)
    {
        if (item.Target is { } vmType)
            Navigation.NavigateTo(vmType);
        else
            Navigation.NavigateTo(typeof(ComingSoonViewModel));
    }
}

/// <summary>NavMenu 한 항목. <paramref name="Target"/> 이 null 이면 아직 포팅 전 페이지.</summary>
public sealed record NavItem(string Label, string Icon, Type? Target);
