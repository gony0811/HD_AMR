using System.ComponentModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HD.AMR.Desktop.ViewModels;

namespace HD.AMR.Desktop.Views;

public partial class InspectionPointsView : UserControl
{
    private XyPlot _plot = null!;
    private InspectionPointsViewModel? _vm;

    public InspectionPointsView()
    {
        InitializeComponent();
        _plot = this.FindControl<XyPlot>("Plot")!;
        this.FindControl<Button>("ExportBtn")!.Click += OnExport;
        this.FindControl<Button>("TemplateBtn")!.Click += OnTemplate;
        this.FindControl<Button>("ImportBtn")!.Click += OnImport;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as InspectionPointsViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        Redraw();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InspectionPointsViewModel.PlotVersion) or nameof(InspectionPointsViewModel.Selected))
            Redraw();
    }

    private void Redraw()
    {
        if (_vm is null) return;
        var pts = new XyPlot.PlotPoint[_vm.Points.Count];
        for (var i = 0; i < pts.Length; i++)
        {
            var p = _vm.Points[i];
            pts[i] = new XyPlot.PlotPoint(p.X, p.Y, p.Z, i == _vm.Selected);
        }
        _plot.Update(pts, _vm.FovMm, _vm.VbMinX, _vm.VbMinY, _vm.VbW, _vm.VbH);
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.Points.Count == 0) { _vm.NotifyCsv("내보낼 경유점이 없습니다.", true); return; }
        var file = await SaveAsync(_vm.SuggestedCsvName());
        if (file is null) return;
        await WriteCsvAsync(file, _vm.BuildCsv(includeRows: true));
        _vm.NotifyCsv($"내보내기 완료: {file.Name} ({_vm.Points.Count}점)", false);
    }

    private async void OnTemplate(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var file = await SaveAsync("waypoints_template.csv");
        if (file is null) return;
        await WriteCsvAsync(file, _vm.TemplateCsv());
        _vm.NotifyCsv("템플릿 다운로드 완료 — 예시 행을 지우고 자세를 채워 다시 불러오세요.", false);
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "경유점 CSV 불러오기",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
        if (picked.Count == 0) return;
        await using var stream = await picked[0].OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync();
        _vm.ImportCsv(text);
    }

    private async Task<IStorageFile?> SaveAsync(string suggested)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;
        return await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "경유점 CSV 저장",
            SuggestedFileName = suggested,
            DefaultExtension = "csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } } },
        });
    }

    // 엑셀 한글 헤더 호환을 위해 UTF-8 BOM 을 붙여 쓴다(원본 inspection.js.download 와 동일).
    private static async Task WriteCsvAsync(IStorageFile file, string text)
    {
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteAsync(text);
    }
}
