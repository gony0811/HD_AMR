using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class JogRibbonView : UserControl
{
    public JogRibbonView()
    {
        InitializeComponent();
        // 누름 연속(hold-to-jog): 조그 버튼을 누르는 동안 StartJOG, 떼면 StopJOG.
        // 증분 모드는 Button.Click(Command)로 처리되며, VM 이 모드로 분기한다.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is JogRibbonViewModel vm &&
            FindJogButton(e.Source as Visual) is { } b &&
            TryParseTag(b.Tag, out var axis, out var sign))
        {
            vm.HoldStart(axis, sign);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is JogRibbonViewModel vm)
            vm.HoldStop();
    }

    private static Button? FindJogButton(Visual? from)
    {
        var v = from;
        while (v is not null)
        {
            if (v is Button b && b.Classes.Contains("jog"))
                return b;
            v = v.GetVisualParent();
        }
        return null;
    }

    private static bool TryParseTag(object? tag, out int axis, out int sign)
    {
        axis = 0; sign = 0;
        if (tag is string s)
        {
            var parts = s.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out axis) && int.TryParse(parts[1], out sign))
                return true;
        }
        return false;
    }
}
