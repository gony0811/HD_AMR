using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class SeamLocalizationTestView : UserControl
{
    private Grid _host = null!;
    private Rectangle _roi = null!;
    private Ellipse _target = null!;
    private SeamLocalizationTestViewModel? _vm;

    public SeamLocalizationTestView()
    {
        InitializeComponent();
        _host = this.FindControl<Grid>("ImageHost")!;
        _roi = this.FindControl<Rectangle>("RoiRect")!;
        _target = this.FindControl<Ellipse>("TargetMark")!;
        _host.SizeChanged += (_, _) => DrawOverlay();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as SeamLocalizationTestViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            DrawOverlay();
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SeamLocalizationTestViewModel.HasProjection)
            or nameof(SeamLocalizationTestViewModel.RoiX) or nameof(SeamLocalizationTestViewModel.RoiY)
            or nameof(SeamLocalizationTestViewModel.RoiW) or nameof(SeamLocalizationTestViewModel.RoiH)
            or nameof(SeamLocalizationTestViewModel.TargetU) or nameof(SeamLocalizationTestViewModel.TargetV))
            DrawOverlay();
    }

    private void DrawOverlay()
    {
        if (_vm is not { HasProjection: true }) { _roi.IsVisible = _target.IsVisible = false; return; }
        var size = _host.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        _roi.IsVisible = _target.IsVisible = true;
        Canvas.SetLeft(_roi, _vm.RoiX * size.Width); Canvas.SetTop(_roi, _vm.RoiY * size.Height);
        _roi.Width = _vm.RoiW * size.Width; _roi.Height = _vm.RoiH * size.Height;
        Canvas.SetLeft(_target, _vm.TargetU * size.Width - _target.Width / 2);
        Canvas.SetTop(_target, _vm.TargetV * size.Height - _target.Height / 2);
    }
}
