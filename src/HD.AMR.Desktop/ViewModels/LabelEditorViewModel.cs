using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Service.Vision;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 비드 마스크 라벨링(DL 학습용 초안 수정/생성). 기존 LabelEditor.razor 이식.
/// 이미지/마스크 파일 I/O 는 LabelDataService(Scoped, 작업마다 scope) — 브러시 편집은 MaskCanvas 컨트롤이 담당하고
/// 뷰 코드비하인드가 <see cref="EditorOpenRequested"/> 등 이벤트로 연결한다.
/// </summary>
public sealed partial class LabelEditorViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ObservableCollection<CaptureEntryRow> Entries { get; } = new();

    [ObservableProperty] private string? _dir;
    [ObservableProperty] private CaptureEntryRow? _selected;
    [ObservableProperty] private int _modalityIndex;    // 0 rgb, 1 ir
    [ObservableProperty] private int _brushModeIndex;   // 0 칠하기, 1 지우기
    [ObservableProperty] private int _radius = 12;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    public bool HasDir => Dir is not null;
    public bool HasSelection => Selected is not null;
    public string Modality => ModalityIndex == 1 ? "ir" : "rgb";
    public bool CanRgb => Selected?.HasRgb ?? false;
    public bool CanIr => Selected?.HasIr ?? false;
    public string SaveHint => Selected is null ? "" : $"비드를 빨갛게 칠하세요. 저장하면 {Selected.Stem}_{Modality}_maskdraft.png 로 덮어씁니다(흑백 이진 마스크).";

    /// <summary>(이미지 PNG, 마스크 초안 PNG 또는 null) — 뷰가 MaskCanvas.Open 으로 연결.</summary>
    public event Action<byte[], byte[]?>? EditorOpenRequested;
    /// <summary>(erase, radius) 브러시 변경.</summary>
    public event Action<bool, int>? BrushChanged;
    public event Action? ClearRequested;

    // ④ 추론 탭 "학습셋에 추가 + 라벨링" 이동 시 대상 촬영본/모달리티(원본 쿼리 파라미터 sel/mod 대응).
    private static string? _pendingStem, _pendingMod;
    public static void Preselect(string stem, string modality) { _pendingStem = stem; _pendingMod = modality; }

    public LabelEditorViewModel(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public override async void OnActivated()
    {
        try
        {
            Dir = await With(l => l.GetDirAsync());
            OnPropertyChanged(nameof(HasDir));
            await RefreshListAsync();
            if (_pendingStem is not null)
            {
                var match = Entries.FirstOrDefault(e => e.Stem == _pendingStem);
                var mod = _pendingMod; _pendingStem = _pendingMod = null;
                if (match is not null)
                {
                    ModalityIndex = (mod == "ir" && match.HasIr) ? 1 : (mod == "rgb" && match.HasRgb) ? 0 : match.HasIr ? 1 : 0;
                    Selected = match;
                }
            }
        }
        catch (Exception ex) { Notify($"초기화 실패: {ex.Message}", true); }
    }

    partial void OnDirChanged(string? value) => OnPropertyChanged(nameof(HasDir));
    partial void OnRadiusChanged(int value) => BrushChanged?.Invoke(BrushModeIndex == 1, value);
    partial void OnBrushModeIndexChanged(int value) => BrushChanged?.Invoke(value == 1, Radius);

    async partial void OnSelectedChanged(CaptureEntryRow? value)
    {
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanRgb)); OnPropertyChanged(nameof(CanIr));
        if (value is null) return;
        // 사용 가능한 모달리티로 기본 선택 후 에디터 열기.
        var idx = value.HasRgb ? 0 : value.HasIr ? 1 : 0;
        if (idx == ModalityIndex) await OpenEditorAsync(); else ModalityIndex = idx;
    }

    async partial void OnModalityIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Modality)); OnPropertyChanged(nameof(SaveHint));
        if (Selected is not null) await OpenEditorAsync();
    }

    [RelayCommand]
    private async Task RefreshListAsync()
    {
        try
        {
            var stem = Selected?.Stem;
            var list = await With(l => l.ListAsync());
            Entries.Clear();
            foreach (var e in list) Entries.Add(new CaptureEntryRow(e));
            if (stem is not null) Selected = Entries.FirstOrDefault(e => e.Stem == stem);
        }
        catch (Exception ex) { Notify($"목록 로드 실패: {ex.Message}", true); }
    }

    private async Task OpenEditorAsync()
    {
        if (Selected is null) return;
        try
        {
            var img = await With(l => l.ReadAsync($"{Selected.Stem}_{Modality}.png"));
            if (img is null) { Notify("이미지를 읽지 못했습니다.", true); return; }
            var mask = await With(l => l.ReadAsync($"{Selected.Stem}_{Modality}_maskdraft.png"));
            EditorOpenRequested?.Invoke(img, mask);
            BrushChanged?.Invoke(BrushModeIndex == 1, Radius);
        }
        catch (Exception ex) { Notify($"에디터 열기 실패: {ex.Message}", true); }
    }

    [RelayCommand] private Task ReloadDraft() => OpenEditorAsync();
    [RelayCommand] private void ClearMask() => ClearRequested?.Invoke();

    /// <summary>뷰가 MaskCanvas.ExportPng 결과로 호출.</summary>
    public async Task SaveMaskAsync(byte[]? png)
    {
        if (Busy || Selected is null) return;
        if (png is null) { Notify("마스크를 읽지 못했습니다.", true); return; }
        Busy = true; Message = null;
        try
        {
            var stem = Selected.Stem;
            var name = await With(l => l.SaveMaskAsync(stem, Modality, png));
            await RefreshListAsync();
            Notify($"저장 완료: {name}", false);
        }
        catch (Exception ex) { Notify($"저장 실패: {ex.Message}", true); }
        finally { Busy = false; }
    }

    private async Task<T> With<T>(Func<LabelDataService, Task<T>> op)
    {
        using var scope = _scopeFactory.CreateScope();
        return await op(scope.ServiceProvider.GetRequiredService<LabelDataService>());
    }

    private void Notify(string m, bool err) { Message = m; IsError = err; }
}

/// <summary>촬영본 목록 행(✓ = 마스크 있음).</summary>
public sealed record CaptureEntryRow(CaptureEntry Entry)
{
    public string Stem => Entry.Stem;
    public bool HasRgb => Entry.HasRgb;
    public bool HasIr => Entry.HasIr;
    public string Label => $"{((Entry.RgbMask || Entry.IrMask) ? "✓ " : "· ")}{Entry.Stem}";
}
