using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service;
using HD.AMR.App.Service.Sequence;
using HD.AMR.App.Service.Sequence.Steps;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// 시퀀스 실행(풀오토/세미오토) + 단계 상태. 기존 Sequence.razor 이식.
/// SequenceService(스텝 그래프·상태 보유)는 페이지 수명 scope 에서 해석한다. 단계별 파라미터는
/// 원본의 인라인 편집 대신 상단 "실행 파라미터" 카드로 묶되, 동일한 Parameter 키에 저장한다.
/// </summary>
public sealed partial class SequenceViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CobotService _cobot;
    private readonly DispatcherTimer _timer;

    private IServiceScope? _scope;
    private SequenceService? _seq;
    private ParameterService? _param;
    private TeachingService? _teaching;
    private DrawingService? _drawing;
    private readonly SequenceContext _context = new();
    private bool _loading;

    // Parameter 키 — 스텝(WeldSequenceSupport 등)이 실행 시 같은 키를 읽으므로 일치해야 한다.
    private const string OffsetUKey = "Sequence.Inspection.OffsetU";
    private const string OffsetVKey = "Sequence.Inspection.OffsetV";
    private const string DirectionKey = "Sequence.Inspection.Direction";
    private const string CameraDistKey = "Sequence.Camera.TargetDistance";
    private const string CameraToLaserShiftKey = "Sequence.FlatSurface.CameraToLaserShiftYmm";
    private const string InspectionDrawingKey = "Sequence.Inspection.DrawingId";
    private const string InspectionProfileKey = "Sequence.Inspection.ProfileId";
    private const string InspectionSurfaceKey = "Sequence.Inspection.SurfaceId";
    private const string XSignKey = "Camera.Axis.XSign";
    private const string PitchMmKey = "Weld.Peak.PitchMm";
    private const string PitchDirKey = "Weld.Peak.PitchDir";
    private const string InspectCamOffsetXKey = "Sequence.InspectCam.OffsetXMm";
    private const string InspectCamOffsetYKey = "Sequence.InspectCam.OffsetYMm";

    /// <summary>실행 시작 시 발생 — 뷰가 모니터 창을 열도록(원본 window.open 대응).</summary>
    public event Action? MonitorRequested;

    public ObservableCollection<SeqStepVm> Steps { get; } = new();
    public ObservableCollection<SurfaceOption> SurfaceOptions { get; } = new();
    public ObservableCollection<Drawing> Drawings { get; } = new();
    public ObservableCollection<InspectionProfile> Profiles { get; } = new();

    [ObservableProperty] private int _tool = 1;
    [ObservableProperty] private int _velocity = 20;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _cobotConnected;
    [ObservableProperty] private string? _faultText;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;

    // 파라미터 카드
    [ObservableProperty] private int _inspectionSurfaceId = 0x01;
    [ObservableProperty] private int _directionIndex;         // 0 수평, 1 수직
    [ObservableProperty] private double _offsetU;
    [ObservableProperty] private double _offsetV;
    [ObservableProperty] private double _cameraTargetDistanceMm = 400;
    [ObservableProperty] private double _cameraToLaserShiftYmm = -65;
    [ObservableProperty] private int _xSignIndex;            // 0 → +1, 1 → −1
    [ObservableProperty] private double _inspectCamOffsetX;
    [ObservableProperty] private double _inspectCamOffsetY;
    [ObservableProperty] private int _wobjId = 1;
    [ObservableProperty] private double _pitchMm = 370;
    [ObservableProperty] private int _pitchDirIndex;         // 0 → +1, 1 → −1
    [ObservableProperty] private int _inspectionDrawingId;
    [ObservableProperty] private int _inspectionProfileId;

    public SequenceViewModel(IServiceScopeFactory scopeFactory, CobotService cobot)
    {
        _scopeFactory = scopeFactory;
        _cobot = cobot;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshConnection();
    }

    public override async void OnActivated()
    {
        _scope = _scopeFactory.CreateScope();
        var sp = _scope.ServiceProvider;
        _seq = sp.GetRequiredService<SequenceService>();
        _param = sp.GetRequiredService<ParameterService>();
        _teaching = sp.GetRequiredService<TeachingService>();
        _drawing = sp.GetRequiredService<DrawingService>();
        _seq.StateChanged += OnSeqStateChanged;

        BuildSteps();
        _timer.Start();
        RefreshConnection();
        await LoadAsync();
    }

    public override void OnDeactivated()
    {
        _timer.Stop();
        if (_seq is not null) _seq.StateChanged -= OnSeqStateChanged;
        _scope?.Dispose();
        _scope = null; _seq = null; _param = null; _teaching = null; _drawing = null;
    }

    private void BuildSteps()
    {
        Steps.Clear();
        if (_seq is null) return;
        var i = 0;
        foreach (var step in _seq.Steps)
            Steps.Add(new SeqStepVm(step.Key, StepCircle(++i), step.DisplayName));
        RefreshSteps();
    }

    private async Task LoadAsync()
    {
        if (_param is null || _teaching is null || _drawing is null) return;
        _loading = true;
        try
        {
            var positions = await _teaching.ListAsync();
            _context.Positions = positions.ToDictionary(p => p.Key, p => p);

            OffsetU = await _param.GetDoubleAsync(OffsetUKey) ?? 0;
            OffsetV = await _param.GetDoubleAsync(OffsetVKey) ?? 0;
            DirectionIndex = (int)(await _param.GetDoubleAsync(DirectionKey) ?? 0) == 1 ? 1 : 0;
            CameraTargetDistanceMm = await _param.GetDoubleAsync(CameraDistKey) ?? 400;
            CameraToLaserShiftYmm = await _param.GetDoubleAsync(CameraToLaserShiftKey) ?? -65;
            XSignIndex = Math.Sign(await _param.GetDoubleAsync(XSignKey) ?? 1) == -1 ? 1 : 0;
            PitchMm = await _param.GetDoubleAsync(PitchMmKey) ?? 370;
            PitchDirIndex = Math.Sign(await _param.GetDoubleAsync(PitchDirKey) ?? 1) == -1 ? 1 : 0;
            InspectCamOffsetX = await _param.GetDoubleAsync(InspectCamOffsetXKey) ?? 0;
            InspectCamOffsetY = await _param.GetDoubleAsync(InspectCamOffsetYKey) ?? 0;
            WobjId = (int)(await _param.GetDoubleAsync(WObjPointStep.WObjIdKey) ?? 1);

            var drawings = await _drawing.ListAsync();
            Drawings.Clear();
            foreach (var d in drawings) Drawings.Add(d);

            InspectionDrawingId = (int)(await _param.GetDoubleAsync(InspectionDrawingKey) ?? 0);
            InspectionProfileId = (int)(await _param.GetDoubleAsync(InspectionProfileKey) ?? 0);
            InspectionSurfaceId = (int)(await _param.GetDoubleAsync(InspectionSurfaceKey) ?? 0x01);

            RebuildSurfaceOptions();
            await LoadProfilesAsync();
            ApplyContext();
        }
        finally { _loading = false; }
        RefreshSteps();
    }

    private void RebuildSurfaceOptions()
    {
        SurfaceOptions.Clear();
        var rows = _context.Positions.Values
            .Where(p => p.SurfaceId > 0)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id);
        foreach (var p in rows)
            SurfaceOptions.Add(new SurfaceOption(p.SurfaceId, $"0x{p.SurfaceId:X2} {p.Name}{(IsTaught(p) ? "" : " (미티칭)")}"));
        if (InspectionSurfaceId > 0 && SurfaceOptions.All(o => o.SurfaceId != InspectionSurfaceId))
            SurfaceOptions.Insert(0, new SurfaceOption(InspectionSurfaceId, $"0x{InspectionSurfaceId:X2} (티칭 위치 없음)"));
    }

    private static bool IsTaught(TeachingPosition p) => p is { X: not null, Y: not null, Z: not null };

    private async Task LoadProfilesAsync()
    {
        Profiles.Clear();
        if (_drawing is not null && InspectionDrawingId > 0)
            foreach (var p in await _drawing.ListProfilesAsync(InspectionDrawingId))
                Profiles.Add(p);
        if (Profiles.All(p => p.Id != InspectionProfileId))
            InspectionProfileId = 0;
    }

    // 뷰모델 → context 반영(실행에 사용).
    private void ApplyContext()
    {
        _context.Tool = Tool;
        _context.Velocity = Velocity;
        _context.InspectionSurfaceId = InspectionSurfaceId;
        _context.InspectionDirection = DirectionIndex == 1 ? InspectionMoveDirection.Vertical : InspectionMoveDirection.Horizontal;
        _context.InspectionOffsetU = OffsetU;
        _context.InspectionOffsetV = OffsetV;
        _context.CameraTargetDistanceMm = CameraTargetDistanceMm;
        _context.CameraToLaserShiftYmm = CameraToLaserShiftYmm;
        _context.InspectionDrawingId = InspectionDrawingId;
        _context.InspectionProfileId = InspectionProfileId;
    }

    // ── 파라미터 저장(변경 시) ──
    partial void OnToolChanged(int value) => _context.Tool = value;
    partial void OnVelocityChanged(int value) => _context.Velocity = value;
    partial void OnOffsetUChanged(double value) => SaveOffsets();
    partial void OnOffsetVChanged(double value) => SaveOffsets();
    partial void OnDirectionIndexChanged(int value) => SaveOffsets();
    partial void OnCameraTargetDistanceMmChanged(double value) => SaveOffsets();
    partial void OnCameraToLaserShiftYmmChanged(double value) => SaveOffsets();
    partial void OnXSignIndexChanged(int value) => SaveWeld();
    partial void OnInspectCamOffsetXChanged(double value) => SaveWeld();
    partial void OnInspectCamOffsetYChanged(double value) => SaveWeld();
    partial void OnPitchMmChanged(double value) => SaveWeld();
    partial void OnPitchDirIndexChanged(int value) => SaveWeld();
    partial void OnWobjIdChanged(int value) => Save(WObjPointStep.WObjIdKey, value);
    partial void OnInspectionSurfaceIdChanged(int value) { _context.InspectionSurfaceId = value; SaveInspection(); RefreshSteps(); }
    partial void OnInspectionProfileIdChanged(int value) { _context.InspectionProfileId = value; SaveInspection(); }
    async partial void OnInspectionDrawingIdChanged(int value)
    {
        _context.InspectionDrawingId = value;
        await LoadProfilesAsync();
        SaveInspection();
    }

    private async void SaveOffsets()
    {
        if (_loading || _param is null) return;
        ApplyContext();
        await _param.SetDoubleAsync(OffsetUKey, OffsetU);
        await _param.SetDoubleAsync(OffsetVKey, OffsetV);
        await _param.SetDoubleAsync(DirectionKey, DirectionIndex);
        await _param.SetDoubleAsync(CameraDistKey, CameraTargetDistanceMm);
        await _param.SetDoubleAsync(CameraToLaserShiftKey, CameraToLaserShiftYmm);
    }

    private async void SaveWeld()
    {
        if (_loading || _param is null) return;
        await _param.SetDoubleAsync(XSignKey, XSignIndex == 1 ? -1 : 1);
        await _param.SetDoubleAsync(PitchMmKey, PitchMm);
        await _param.SetDoubleAsync(PitchDirKey, PitchDirIndex == 1 ? -1 : 1);
        await _param.SetDoubleAsync(InspectCamOffsetXKey, InspectCamOffsetX);
        await _param.SetDoubleAsync(InspectCamOffsetYKey, InspectCamOffsetY);
    }

    private async void SaveInspection()
    {
        if (_loading || _param is null) return;
        await _param.SetDoubleAsync(InspectionDrawingKey, InspectionDrawingId);
        await _param.SetDoubleAsync(InspectionProfileKey, InspectionProfileId);
        await _param.SetDoubleAsync(InspectionSurfaceKey, InspectionSurfaceId);
    }

    private async void Save(string key, double v)
    {
        if (_loading || _param is null) return;
        await _param.SetDoubleAsync(key, v);
    }

    private void OnSeqStateChanged() => Dispatcher.UIThread.Post(() => { RefreshSteps(); RefreshConnection(); });

    private void RefreshConnection()
    {
        CobotConnected = _cobot.IsConnected;
        FaultText = _cobot.State is { ErrorCode: not 0 } st ? $"오류 코드 {st.ErrorCode} — '오류 해제' 필요" : null;
        Busy = _seq?.IsBusy ?? false;
        RunAllCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void RefreshSteps()
    {
        if (_seq is null) return;
        foreach (var vm in Steps)
        {
            var step = _seq.Steps.FirstOrDefault(s => s.Key == vm.Key);
            if (step is null) continue;
            var status = _seq.StepStatuses.GetValueOrDefault(vm.Key);
            var current = _seq.CurrentStepKey == vm.Key;
            var validation = step.Validate(_context);

            (vm.StatusKind, vm.StatusText) = status?.State switch
            {
                StepState.Running => (1, "실행 중…"),
                StepState.Completed => (2, "완료" + (string.IsNullOrEmpty(status.Message) ? "" : $" · {status.Message}")),
                StepState.Failed => (3, "실패" + (string.IsNullOrEmpty(status.Message) ? "" : $" · {status.Message}")),
                _ => validation.IsValid ? (0, "대기") : (4, validation.Message ?? "검증 필요"),
            };
            vm.IsCurrent = current;
            vm.CanRun = _cobot.IsConnected && !(_seq.IsBusy) && validation.IsValid;
        }
    }

    // ── 실행 제어 ──
    private bool CanRunAll => _cobot.IsConnected && !(_seq?.IsBusy ?? false);
    private bool CanStop => _seq?.IsBusy ?? false;

    [RelayCommand(CanExecute = nameof(CanRunAll))]
    private async Task RunAll()
    {
        if (_seq is null) return;
        Message = null; ApplyContext();
        MonitorRequested?.Invoke();
        var ok = await _seq.RunAllAsync(_context);
        IsError = !ok;
        Message = ok ? "풀오토 시퀀스 완료." : "시퀀스 중단 — 위 상태를 확인하세요.";
    }

    [RelayCommand]
    private async Task RunStep(string stepKey)
    {
        if (_seq is null) return;
        Message = null; ApplyContext();
        MonitorRequested?.Invoke();
        var r = await _seq.RunStepAsync(stepKey, _context);
        IsError = !r.Success;
        Message = r.Message;
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop() { if (_seq is not null) await _seq.StopAsync(); }

    [RelayCommand]
    private void ResetStatuses()
    {
        _seq?.Reset();
        _context.Bag.Clear();
        IsError = false; Message = "시퀀스 상태 초기화 완료.";
        RefreshSteps();
    }

    [RelayCommand]
    private async Task ResetFault()
    {
        Message = null;
        try
        {
            var rc = await _cobot.ResetErrorAsync();
            IsError = rc != 0;
            Message = rc == 0 ? "오류 해제 완료." : $"오류 해제 실패 (rc={rc}).";
        }
        catch (Exception ex) { IsError = true; Message = $"오류 해제 실패: {ex.Message}"; }
    }

    private static string StepCircle(int n) => n switch
    {
        >= 1 and <= 20 => char.ConvertFromUtf32(0x2460 + n - 1),
        >= 21 and <= 35 => char.ConvertFromUtf32(0x3251 + n - 21),
        >= 36 and <= 50 => char.ConvertFromUtf32(0x32B1 + n - 36),
        _ => $"({n})",
    };
}

/// <summary>단계 행(표시용). 상태는 실행 중 갱신.</summary>
public sealed partial class SeqStepVm : ObservableObject
{
    public string Key { get; }
    public string No { get; }
    public string DisplayName { get; }
    [ObservableProperty] private string _statusText = "대기";
    [ObservableProperty] private int _statusKind;   // 0 대기,1 실행,2 완료,3 실패,4 경고
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _canRun;

    public SeqStepVm(string key, string no, string displayName)
    {
        Key = key; No = no; DisplayName = displayName;
    }
}

public sealed record SurfaceOption(int SurfaceId, string Label);
