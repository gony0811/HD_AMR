using Avalonia.Controls;
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
        this.FindControl<Button>("SaveBtn")!.Click += async (_, _) => { if (_vm is not null) await _vm.SaveMaskAsync(_mask.ExportPng()); };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) { _vm.EditorOpenRequested -= OnOpen; _vm.BrushChanged -= OnBrush; _vm.ClearRequested -= OnClear; }
            _vm = DataContext as LabelEditorViewModel;
            if (_vm is not null) { _vm.EditorOpenRequested += OnOpen; _vm.BrushChanged += OnBrush; _vm.ClearRequested += OnClear; }
        };
    }

    private void OnOpen(byte[] img, byte[]? mask) => _mask.Open(img, mask);
    private void OnBrush(bool erase, int radius) { _mask.Erase = erase; _mask.Radius = radius; }
    private void OnClear() => _mask.Clear();
}
