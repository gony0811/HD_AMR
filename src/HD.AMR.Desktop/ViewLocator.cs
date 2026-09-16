using Avalonia.Controls;
using Avalonia.Controls.Templates;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop;

/// <summary>
/// 뷰모델 → 뷰 규약 매핑. "HD.AMR.Desktop.ViewModels.XxxViewModel" 을
/// "HD.AMR.Desktop.Views.XxxView" 로 치환해 인스턴스화한다. ContentControl 이
/// 현재 뷰모델을 이 템플릿으로 렌더한다.
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? param)
    {
        if (param is null)
            return new TextBlock { Text = "(null)" };

        var name = param.GetType().FullName!
            .Replace(".ViewModels.", ".Views.")
            .Replace("ViewModel", "View");
        var type = Type.GetType(name);

        return type is not null
            ? (Control)Activator.CreateInstance(type)!
            : new TextBlock { Text = $"뷰를 찾을 수 없음: {name}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
