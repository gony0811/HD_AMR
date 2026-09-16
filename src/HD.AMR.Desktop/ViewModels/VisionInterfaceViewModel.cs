using HD.AMR.App.Service;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>비전 인터페이스 페이지 — VisionPanel(자동화 Client)을 감싼다. 기존 VisionInterface.razor 이식.</summary>
public sealed class VisionInterfaceViewModel : ViewModelBase
{
    public VisionPanelViewModel Panel { get; }

    public VisionInterfaceViewModel(VisionInterfaceService svc)
    {
        Panel = new VisionPanelViewModel(svc.Client, svc.Settings.ServerHost, svc.Settings.Port, autoReconnectDefault: false);
    }

    public override void OnActivated() => Panel.Start();
    public override void OnDeactivated() => Panel.Stop();
}
