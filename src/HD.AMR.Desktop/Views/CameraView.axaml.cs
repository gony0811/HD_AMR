using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class CameraView : UserControl
{
    private Grid _depthHost = null!;
    private Rectangle _roiRect = null!;
    private Border _probeLabel = null!;
    private CameraViewModel? _vm;

    private bool _dragging;
    private Point _dragStart;

    public CameraView()
    {
        InitializeComponent();
        _depthHost = this.FindControl<Grid>("DepthHost")!;
        _roiRect = this.FindControl<Rectangle>("RoiRect")!;
        _probeLabel = this.FindControl<Border>("ProbeLabel")!;

        _depthHost.PointerMoved += OnDepthPointerMoved;
        _depthHost.PointerPressed += OnDepthPointerPressed;
        _depthHost.PointerReleased += OnDepthPointerReleased;
        _depthHost.PointerExited += (_, _) => _probeLabel.IsVisible = false;
        _depthHost.SizeChanged += (_, _) => DrawRoiFromVm();

        this.FindControl<Button>("BrowseButton")!.Click += OnBrowseClick;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as CameraViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        DrawRoiFromVm();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CameraViewModel.RoiX) or nameof(CameraViewModel.RoiY)
            or nameof(CameraViewModel.RoiW) or nameof(CameraViewModel.RoiH))
            DrawRoiFromVm();
    }

    private (double u, double v) Norm(Point p)
    {
        var b = _depthHost.Bounds.Size;
        if (b.Width <= 0 || b.Height <= 0) return (0, 0);
        return (Math.Clamp(p.X / b.Width, 0, 1), Math.Clamp(p.Y / b.Height, 0, 1));
    }

    private void OnDepthPointerMoved(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(_depthHost);
        var (u, v) = Norm(p);
        _vm?.ProbeAt(u, v);

        _probeLabel.IsVisible = true;
        Canvas.SetLeft(_probeLabel, p.X + 12);
        Canvas.SetTop(_probeLabel, p.Y + 12);

        if (_dragging)
        {
            var x = Math.Min(_dragStart.X, p.X);
            var y = Math.Min(_dragStart.Y, p.Y);
            _roiRect.IsVisible = true;
            Canvas.SetLeft(_roiRect, x);
            Canvas.SetTop(_roiRect, y);
            _roiRect.Width = Math.Abs(p.X - _dragStart.X);
            _roiRect.Height = Math.Abs(p.Y - _dragStart.Y);
        }
    }

    private void OnDepthPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = true;
        _dragStart = e.GetPosition(_depthHost);
    }

    private void OnDepthPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        var end = e.GetPosition(_depthHost);
        var (u0, v0) = Norm(new Point(Math.Min(_dragStart.X, end.X), Math.Min(_dragStart.Y, end.Y)));
        var (u1, v1) = Norm(new Point(Math.Max(_dragStart.X, end.X), Math.Max(_dragStart.Y, end.Y)));
        _vm?.SetRoiFromDrag(u0, v0, u1 - u0, v1 - v0);
        DrawRoiFromVm();
    }

    // VM 의 정규화 ROI → 오버레이 사각형(픽셀).
    private void DrawRoiFromVm()
    {
        if (_vm is null) return;
        var b = _depthHost.Bounds.Size;
        if (b.Width <= 0 || b.Height <= 0 || _vm.RoiW <= 0 || _vm.RoiH <= 0)
        {
            _roiRect.IsVisible = false;
            return;
        }
        _roiRect.IsVisible = true;
        Canvas.SetLeft(_roiRect, _vm.RoiX * b.Width);
        Canvas.SetTop(_roiRect, _vm.RoiY * b.Height);
        _roiRect.Width = _vm.RoiW * b.Width;
        _roiRect.Height = _vm.RoiH * b.Height;
    }

    private async void OnBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || _vm is null) return;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "캡처 저장 폴더 선택",
            AllowMultiple = false,
        });
        var path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (!string.IsNullOrWhiteSpace(path))
            await _vm.SetCaptureDirAsync(path);
    }
}
