using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using HD.AMR.App.Models;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class WeldTrackingView : UserControl
{
    private Grid _host = null!;
    private Rectangle _weldRect = null!, _peakRect = null!, _dragRect = null!;
    private WeldTrackingViewModel? _vm;
    private bool _dragging;
    private Point _dragStart;

    public WeldTrackingView()
    {
        InitializeComponent();
        _host = this.FindControl<Grid>("EditorHost")!;
        _weldRect = this.FindControl<Rectangle>("WeldRect")!;
        _peakRect = this.FindControl<Rectangle>("PeakRect")!;
        _dragRect = this.FindControl<Rectangle>("DragRect")!;

        _host.PointerPressed += OnPressed;
        _host.PointerMoved += OnMoved;
        _host.PointerReleased += OnReleased;
        _host.SizeChanged += (_, _) => DrawRois();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as WeldTrackingViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        DrawRois();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WeldTrackingViewModel.WeldRoi) or nameof(WeldTrackingViewModel.PeakRoi))
            DrawRois();
    }

    private (double u, double v) Norm(Point p)
    {
        var b = _host.Bounds.Size;
        if (b.Width <= 0 || b.Height <= 0) return (0, 0);
        return (Math.Clamp(p.X / b.Width, 0, 1), Math.Clamp(p.Y / b.Height, 0, 1));
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(_host);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_host);
        _dragRect.IsVisible = true;
        Canvas.SetLeft(_dragRect, Math.Min(_dragStart.X, p.X));
        Canvas.SetTop(_dragRect, Math.Min(_dragStart.Y, p.Y));
        _dragRect.Width = Math.Abs(p.X - _dragStart.X);
        _dragRect.Height = Math.Abs(p.Y - _dragStart.Y);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _dragRect.IsVisible = false;
        var end = e.GetPosition(_host);
        var (u0, v0) = Norm(new Point(Math.Min(_dragStart.X, end.X), Math.Min(_dragStart.Y, end.Y)));
        var (u1, v1) = Norm(new Point(Math.Max(_dragStart.X, end.X), Math.Max(_dragStart.Y, end.Y)));
        _vm?.SetRoiFromDrag(u0, v0, u1 - u0, v1 - v0);
        DrawRois();
    }

    // 픽셀 ROI(프레임 기준) → 표시 좌표.
    private void DrawRois()
    {
        if (_vm is null) return;
        Place(_weldRect, _vm.WeldRoi);
        Place(_peakRect, _vm.PeakRoi);
    }

    private void Place(Rectangle rect, RoiRect? roi)
    {
        var b = _host.Bounds.Size;
        if (_vm is null || roi is null || b.Width <= 0 || b.Height <= 0 || _vm.FrameW <= 0 || _vm.FrameH <= 0)
        {
            rect.IsVisible = false;
            return;
        }
        rect.IsVisible = true;
        Canvas.SetLeft(rect, (double)roi.X / _vm.FrameW * b.Width);
        Canvas.SetTop(rect, (double)roi.Y / _vm.FrameH * b.Height);
        rect.Width = (double)roi.Width / _vm.FrameW * b.Width;
        rect.Height = (double)roi.Height / _vm.FrameH * b.Height;
    }
}
