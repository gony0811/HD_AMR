using Avalonia.Controls;
using HD.AMR.App.Service.Vision;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class LabelEditorView : UserControl
{
    private MaskCanvas _mask = null!;
    private LabelEditorViewModel? _vm;

    public LabelEditorView()
    {
        InitializeComponent();
        _mask = this.FindControl<MaskCanvas>("Mask")!;
        this.FindControl<Button>("SaveBtn")!.Click += async (_, _) =>
        {
            if (_vm is not null) await _vm.SaveLabelAsync(_mask.HasPaint ? _mask.ExportPng() : null, _mask.GetLineLabel());
        };
        foreach (var name in new[] { "PickDirBtn", "PickDirBtn2" })
            this.FindControl<Button>(name)!.Click += async (_, _) =>
            {
                if (_vm is null) return;
                var path = await FolderPicker.PickAsync(this, "라벨링 폴더 선택", _vm.Dir);
                if (path is not null) await _vm.SetDirAsync(path);
            };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) { _vm.EditorOpenRequested -= OnOpen; _vm.BrushChanged -= OnBrush; _vm.ClearRequested -= OnClear; _vm.BaseImageRequested -= OnBase; }
            _vm = DataContext as LabelEditorViewModel;
            if (_vm is not null) { _vm.EditorOpenRequested += OnOpen; _vm.BrushChanged += OnBrush; _vm.ClearRequested += OnClear; _vm.BaseImageRequested += OnBase; }
        };
    }

    private void OnOpen(byte[] img, byte[]? mask, WeldLineLabel? line) { _mask.Open(img, mask); _mask.SetLine(line); }
    private void OnBrush(bool lineMode, bool erase, int radius, int thickness)
    {
        _mask.LineMode = lineMode; _mask.Erase = erase; _mask.Radius = radius; _mask.LineThickness = thickness;
    }
    private void OnClear() => _mask.Clear();
    private void OnBase(byte[] img) => _mask.SetBase(img);
}
