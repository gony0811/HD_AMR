using CommunityToolkit.Mvvm.ComponentModel;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>모든 페이지 뷰모델의 기반. INotifyPropertyChanged(ObservableObject) + 정리 훅.</summary>
public abstract class ViewModelBase : ObservableObject, IDisposable
{
    /// <summary>뷰가 화면에 나타날 때 호출(폴링 타이머 시작 등).</summary>
    public virtual void OnActivated() { }

    /// <summary>뷰가 화면에서 사라질 때 호출(타이머 중지, 이벤트 해제 등).</summary>
    public virtual void OnDeactivated() { }

    public virtual void Dispose() => OnDeactivated();
}
