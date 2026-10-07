using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Collections;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class CameraView : UserControl
{
    private Grid _depthHost = null!;
    private Rectangle _roiRect = null!;
    private Border _probeLabel = null!;
    private Grid _flatHost = null!;
    private Canvas _flatOverlay = null!;
    private CameraViewModel? _vm;

    private bool _dragging;
    private Point _dragStart;

    public CameraView()
    {
        InitializeComponent();
        _depthHost = this.FindControl<Grid>("DepthHost")!;
        _roiRect = this.FindControl<Rectangle>("RoiRect")!;
        _probeLabel = this.FindControl<Border>("ProbeLabel")!;
        _flatHost = this.FindControl<Grid>("FlatHost")!;
        _flatOverlay = this.FindControl<Canvas>("FlatOverlay")!;
        _flatHost.SizeChanged += (_, _) => DrawFlatOverlay();

        _depthHost.PointerMoved += OnDepthPointerMoved;
        _depthHost.PointerPressed += OnDepthPointerPressed;
        _depthHost.PointerReleased += OnDepthPointerReleased;
        _depthHost.PointerExited += (_, _) => _probeLabel.IsVisible = false;
        _depthHost.SizeChanged += (_, _) => DrawRoiFromVm();

        this.FindControl<Button>("BrowseButton")!.Click += OnBrowseClick;
        this.FindControl<Button>("JogPopupBtn")!.Click += (_, _) => JogWindow.Open();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as CameraViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        DrawRoiFromVm();
        DrawFlatOverlay();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CameraViewModel.RoiX) or nameof(CameraViewModel.RoiY)
            or nameof(CameraViewModel.RoiW) or nameof(CameraViewModel.RoiH))
            DrawRoiFromVm();
        else if (e.PropertyName is nameof(CameraViewModel.FlatSnapshot))
            DrawFlatOverlay();
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

    // ── 평탄면 검출 정지 화면 오버레이 ──────────────────────────────────
    private const double CrossArm = 8;
    private static readonly IBrush RoiBrush = new SolidColorBrush(Color.Parse("#FFD000"));
    private static readonly IBrush PickBrush = new SolidColorBrush(Color.Parse("#5DCAA5"));
    private static readonly IBrush GridLineBrush = new SolidColorBrush(Color.Parse("#59FFFFFF"));
    private static readonly IBrush InvalidBrush = new SolidColorBrush(Color.Parse("#99888780"));
    // σ 상대 순위 4단계(평탄 → 거침). 반투명이라 아래 깊이 컬러맵이 비친다.
    private static readonly IBrush[] SigmaBrushes =
    {
        new SolidColorBrush(Color.Parse("#803B6D11")),
        new SolidColorBrush(Color.Parse("#7397C459")),
        new SolidColorBrush(Color.Parse("#73EF9F27")),
        new SolidColorBrush(Color.Parse("#73E24B4A")),
    };

    /// <summary>
    /// 검출 스냅샷을 정지 화면 위에 그린다: 셀 σ 히트맵(유효 셀 안에서 상대 비교), 검출 영역,
    /// ROI·중심 십자선, 2순위 셀(얇은 점선), 선택 셀(굵은 테두리), ROI 중심→선택 셀 이동 벡터.
    /// </summary>
    private void DrawFlatOverlay()
    {
        _flatOverlay.Children.Clear();
        var snap = _vm?.FlatSnapshot;
        var size = _flatHost.Bounds.Size;
        if (snap is null || size.Width <= 0 || size.Height <= 0) return;
        double w = size.Width, h = size.Height;
        var a = snap.Analysis;

        double minSigma = double.MaxValue, maxSigma = 0;
        foreach (var c in a.Cells)
        {
            if (!c.Valid) continue;
            minSigma = Math.Min(minSigma, c.SigmaMm);
            maxSigma = Math.Max(maxSigma, c.SigmaMm);
        }
        double span = maxSigma - minSigma;

        foreach (var c in a.Cells)
        {
            IBrush fill = InvalidBrush;
            if (c.Valid)
            {
                var t = span > 1e-6 ? (c.SigmaMm - minSigma) / span : 0;
                fill = SigmaBrushes[Math.Min(SigmaBrushes.Length - 1, (int)(t * SigmaBrushes.Length))];
            }
            AddRect(c.U0 * w, c.V0 * h, (c.U1 - c.U0) * w, (c.V1 - c.V0) * h, fill, GridLineBrush, 0.5);
        }

        // 검출 영역이 전체 ROI 와 다르면(폐루프 2회차 이후 중앙 축소 재검출) 점선으로 구분.
        bool refined = Math.Abs(a.RoiW - snap.FullRoiW) > 1e-6 || Math.Abs(a.RoiH - snap.FullRoiH) > 1e-6;
        if (refined)
            AddRect(a.RoiX * w, a.RoiY * h, a.RoiW * w, a.RoiH * h, null, RoiBrush, 1, dashed: true);
        AddRect(snap.FullRoiX * w, snap.FullRoiY * h, snap.FullRoiW * w, snap.FullRoiH * h, null, RoiBrush, 1.5);

        double cx = (snap.FullRoiX + snap.FullRoiW / 2) * w;
        double cy = (snap.FullRoiY + snap.FullRoiH / 2) * h;
        AddLine(cx - CrossArm, cy, cx + CrossArm, cy, RoiBrush, 1.5);
        AddLine(cx, cy - CrossArm, cx, cy + CrossArm, RoiBrush, 1.5);

        if (a.SecondIndex >= 0)
        {
            var c2 = a.Cells[a.SecondIndex];
            AddRect(c2.U0 * w, c2.V0 * h, (c2.U1 - c2.U0) * w, (c2.V1 - c2.V0) * h, null, PickBrush, 1, dashed: true);
        }
        var best = a.Cells[a.BestIndex];
        AddRect(best.U0 * w, best.V0 * h, (best.U1 - best.U0) * w, (best.V1 - best.V0) * h, null, PickBrush, 3);

        // 이동 벡터: ROI 중심 → 선택 셀 중심 (코봇이 이 방향으로 옮겨 셀을 중심에 맞춘다).
        double bx = a.Best.U * w, by = a.Best.V * h;
        double dx = bx - cx, dy = by - cy, len = Math.Sqrt(dx * dx + dy * dy);
        if (len > 6)
        {
            AddLine(cx, cy, bx, by, PickBrush, 2);
            double ux = dx / len, uy = dy / len, head = 7;
            _flatOverlay.Children.Add(new Polygon
            {
                Fill = PickBrush,
                Points = new AvaloniaList<Point>
                {
                    new(bx, by),
                    new(bx - ux * head - uy * head * 0.5, by - uy * head + ux * head * 0.5),
                    new(bx - ux * head + uy * head * 0.5, by - uy * head - ux * head * 0.5),
                },
            });
        }
        var dot = new Ellipse { Width = 5, Height = 5, Fill = PickBrush };
        Canvas.SetLeft(dot, bx - 2.5);
        Canvas.SetTop(dot, by - 2.5);
        _flatOverlay.Children.Add(dot);
    }

    private void AddRect(double x, double y, double w, double h, IBrush? fill, IBrush stroke, double thickness, bool dashed = false)
    {
        var r = new Rectangle
        {
            Width = Math.Max(0, w), Height = Math.Max(0, h),
            Fill = fill, Stroke = stroke, StrokeThickness = thickness,
        };
        if (dashed) r.StrokeDashArray = new AvaloniaList<double> { 4, 3 };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        _flatOverlay.Children.Add(r);
    }

    private void AddLine(double x0, double y0, double x1, double y1, IBrush stroke, double thickness)
        => _flatOverlay.Children.Add(new Line
        {
            StartPoint = new Point(x0, y0), EndPoint = new Point(x1, y1),
            Stroke = stroke, StrokeThickness = thickness,
        });

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
