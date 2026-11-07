using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Service.Vision;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 비드 라벨링(DL 학습용). 라벨 종류를 골라 <b>면</b>(칠하기 → <c>_area.png</c>) 또는 <b>선</b>(중심선 → <c>_line.json</c>)을
/// 따로 편집·저장한다 — 한쪽을 저장해도 다른 쪽은 지워지지 않는다. 대상 폴더는 화면에서 고른다(Vision.Label.Dir).
/// 촬영본 목록은 IR/RGB 탭으로 나눠 보여 주며, 고른 탭(모달리티)은 촬영본을 바꿔도 유지된다.
/// 파일 I/O 는 LabelDataService(Scoped, 작업마다 scope) — 편집은 MaskCanvas 컨트롤이 담당하고
/// 뷰 코드비하인드가 <see cref="EditorOpenRequested"/> 등 이벤트로 연결한다.
/// </summary>
public sealed partial class LabelEditorViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>현재 탭(모달리티) 이미지가 있는 촬영본만.</summary>
    public ObservableCollection<CaptureEntryRow> Entries { get; } = new();
    private List<CaptureEntry> _all = new();
    private static int _lastTab;   // 화면을 나갔다 와도 마지막 탭 유지

    [ObservableProperty] private string? _dir;
    [ObservableProperty] private CaptureEntryRow? _selected;
    [ObservableProperty] private int _modalityTab = _lastTab;   // 0 IR, 1 RGB
    [ObservableProperty] private int _kindIndex = 1;     // 0 면(칠하기), 1 선
    [ObservableProperty] private int _brushModeIndex;    // 면 도구: 0 칠하기, 1 지우기
    [ObservableProperty] private int _radius = 12;
    [ObservableProperty] private int _lineThickness = WeldLineLabel.DefaultThickness;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    public bool HasDir => Dir is not null;
    public bool HasSelection => Selected is not null;
    public string Modality => ModalityTab == 0 ? "ir" : "rgb";
    public string IrTabText => $"IR ({_all.Count(e => e.HasIr)})";
    public string RgbTabText => $"RGB ({_all.Count(e => e.HasRgb)})";
    public string ListTitle => $"{Modality.ToUpperInvariant()} 촬영본 ({Entries.Count}) · 면 {Entries.Count(e => e.HasArea)} · 선 {Entries.Count(e => e.HasLine)}";
    public LabelKind Kind => KindIndex == 1 ? LabelKind.Line : LabelKind.Area;
    public bool IsLineMode => Kind == LabelKind.Line;
    public bool IsBrushMode => !IsLineMode;
    public string SaveButtonText => IsLineMode ? "💾 선 라벨 저장" : "💾 면 라벨 저장";
    public string SaveHint => Selected is null ? "" : IsLineMode
        ? $"용접선 중심을 따라 클릭(보통 양 끝 2점, 휘면 중간점 추가). Shift+클릭=끊고 새 선 시작(떨어진 비드), 점 드래그=이동, 우클릭=마지막 점 삭제. " +
          $"저장하면 {LabelFiles.LineName(Selected.Stem, Modality)} (점 + 두께 {LineThickness}px). 면 라벨은 그대로 둡니다. 점을 모두 지우고 저장하면 선 라벨 삭제."
        : $"비드를 빨갛게 칠하세요. 저장하면 {LabelFiles.AreaName(Selected.Stem, Modality)} (흑백 마스크). 선 라벨은 그대로 둡니다. 모두 지우고 저장하면 면 라벨 삭제.";

    /// <summary>(이미지 PNG, 마스크 초안 PNG 또는 null, 선 라벨 또는 null) — 뷰가 MaskCanvas.Open/SetLine 으로 연결.</summary>
    public event Action<byte[], byte[]?, WeldLineLabel?>? EditorOpenRequested;
    /// <summary>(lineMode, erase, radius, lineThickness) 도구 변경.</summary>
    public event Action<bool, bool, int, int>? BrushChanged;
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
                var stem = _pendingStem;
                var mod = _pendingMod; _pendingStem = _pendingMod = null;
                var e = _all.FirstOrDefault(x => x.Stem == stem);
                if (e is not null)
                {
                    // 요청 모달리티 이미지가 있으면 그 탭, 없으면 있는 쪽 탭.
                    int tab = (mod == "ir" && e.HasIr) ? 0 : (mod == "rgb" && e.HasRgb) ? 1 : e.HasIr ? 0 : 1;
                    if (tab != ModalityTab) ModalityTab = tab;   // 탭 변경이 목록을 다시 채운다
                    Selected = Entries.FirstOrDefault(x => x.Stem == stem);
                }
            }
        }
        catch (Exception ex) { Notify($"초기화 실패: {ex.Message}", true); }
    }

    partial void OnDirChanged(string? value) => OnPropertyChanged(nameof(HasDir));
    partial void OnRadiusChanged(int value) => RaiseTool();
    partial void OnLineThicknessChanged(int value) { RaiseTool(); OnPropertyChanged(nameof(SaveHint)); }
    partial void OnBrushModeIndexChanged(int value) => RaiseTool();
    async partial void OnKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Kind)); OnPropertyChanged(nameof(IsLineMode)); OnPropertyChanged(nameof(IsBrushMode));
        OnPropertyChanged(nameof(SaveHint)); OnPropertyChanged(nameof(SaveButtonText));
        if (Selected is not null) await OpenEditorAsync(); else RaiseTool();
    }
    private void RaiseTool() => BrushChanged?.Invoke(IsLineMode, IsBrushMode && BrushModeIndex == 1, Radius, LineThickness);

    async partial void OnSelectedChanged(CaptureEntryRow? value)
    {
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(SaveHint));
        if (value is not null) await OpenEditorAsync();
    }

    // 탭 전환 — 목록을 그 모달리티로 다시 채우고, 보던 촬영본이 있으면 그대로 선택(같은 촬영본의 IR↔RGB 비교).
    partial void OnModalityTabChanged(int value)
    {
        _lastTab = value;
        OnPropertyChanged(nameof(Modality)); OnPropertyChanged(nameof(SaveHint));
        Fill(Selected?.Stem);
    }

    private void Fill(string? keepStem)
    {
        var mod = Modality;
        Entries.Clear();
        foreach (var e in _all.Where(e => mod == "ir" ? e.HasIr : e.HasRgb)) Entries.Add(new CaptureEntryRow(e, mod));
        Selected = keepStem is null ? null : Entries.FirstOrDefault(e => e.Stem == keepStem);
        OnPropertyChanged(nameof(IrTabText)); OnPropertyChanged(nameof(RgbTabText)); OnPropertyChanged(nameof(ListTitle));
    }

    /// <summary>뷰의 폴더 선택기 결과. 빈값이면 캡처 저장 폴더로 되돌린다.</summary>
    public async Task SetDirAsync(string? dir)
    {
        try
        {
            await With(async l => { await l.SetDirAsync(dir); return 0; });
            Selected = null;
            Dir = await With(l => l.GetDirAsync());
            await RefreshListAsync();
            Notify($"라벨링 폴더: {Dir}", false);
        }
        catch (Exception ex) { Notify($"폴더 변경 실패: {ex.Message}", true); }
    }

    [RelayCommand] private Task UseCaptureDir() => SetDirAsync(null);

    [RelayCommand]
    private async Task RefreshListAsync()
    {
        try
        {
            var stem = Selected?.Stem;
            _all = await With(l => l.ListAsync());
            Fill(stem);
        }
        catch (Exception ex) { Notify($"목록 로드 실패: {ex.Message}", true); }
    }

    private async Task OpenEditorAsync()
    {
        if (Selected is null) return;
        try
        {
            var stem = Selected.Stem;
            var img = await With(l => l.ReadAsync(LabelFiles.ImageName(stem, Modality)));
            if (img is null) { Notify("이미지를 읽지 못했습니다.", true); return; }
            // 고른 라벨 종류만 불러온다 — 면: _area.png 마스크, 선: _line.json 점(마스크는 두께대로 다시 그림).
            byte[]? mask = null; WeldLineLabel? line = null;
            if (IsLineMode) line = await With(l => l.ReadLineAsync(stem, Modality));
            else mask = await With(l => l.ReadAsync(LabelFiles.AreaName(stem, Modality)));
            if (line is not null) LineThickness = line.Thickness;
            RaiseTool();
            EditorOpenRequested?.Invoke(img, mask, line);
        }
        catch (Exception ex) { Notify($"에디터 열기 실패: {ex.Message}", true); }
    }

    [RelayCommand] private Task ReloadDraft() => OpenEditorAsync();
    [RelayCommand] private void ClearMask() => ClearRequested?.Invoke();

    /// <summary>
    /// 뷰가 MaskCanvas 결과로 호출. 면 모드: 칠한 마스크(<paramref name="png"/>, 비었으면 null) → _area.png.
    /// 선 모드: 선 라벨(<paramref name="line"/>, 점 2개 미만이면 null) → _line.json. null 은 해당 라벨 삭제.
    /// </summary>
    public async Task SaveLabelAsync(byte[]? png, WeldLineLabel? line)
    {
        if (Busy || Selected is null) return;
        Busy = true; Message = null;
        try
        {
            var stem = Selected.Stem;
            string? name;
            if (IsLineMode) name = await With(l => l.SaveLineAsync(stem, Modality, line));
            else name = await With(l => l.SaveAreaAsync(stem, Modality, png));
            await RefreshListAsync();
            var what = IsLineMode ? "선" : "면";
            Notify(name is null ? $"{what} 라벨 삭제: {stem}_{Modality} (비어 있음)"
                : IsLineMode ? $"저장 완료: {name} (점 {line!.Points.Count}개" + (line.Breaks is { Count: > 0 } b ? $", 선 {b.Count + 1}개)" : ")") : $"저장 완료: {name}", false);
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

/// <summary>촬영본 목록 행 — 해당 탭 모달리티의 면/선 라벨 보유 표시(▦ 면, 📏 선).</summary>
public sealed record CaptureEntryRow(CaptureEntry Entry, string Modality)
{
    public string Stem => Entry.Stem;
    public bool HasArea => Modality == "ir" ? Entry.IrArea : Entry.RgbArea;
    public bool HasLine => Modality == "ir" ? Entry.IrLine : Entry.RgbLine;
    public string Label => $"{(HasArea ? "▦" : "·")}{(HasLine ? "📏" : "· ")} {Entry.Stem}";
}
