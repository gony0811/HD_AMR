using CommunityToolkit.Mvvm.ComponentModel;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// ArUco 기반 장착 보정 — <b>한 페이지 2단계</b>. 바닥 마커 하나로 순서대로 진행한다.
///   ① <see cref="HandEyeStep"/> — <c>T_T_C</c>(Tool TCP → 카메라) 를 AX=XB 로 측정
///   ② <see cref="MountStep"/>  — <c>T_A_B</c>(AMR 차체 → 코봇 BASE) 를 동시 추정
///
/// <b>합친 이유는 Tool 번호 정합이다.</b> <c>T_T_C</c> 는 특정 tool 의 TCP 기준이고 ②가 같은 기준의
/// <c>T_B_T</c> 와 합성한다 — 두 값의 tool 이 어긋나면 결과가 tool 오프셋만큼 틀리는데
/// <b>잔차로는 전혀 드러나지 않는다.</b> 화면을 나누면 각 화면에서 따로 입력해 어긋날 수 있으므로,
/// tool·마커 설정을 이 컨테이너가 <b>단일 원천</b>으로 들고 두 단계에 밀어넣는다.
///
/// 순서도 강제한다 — <c>T_T_C</c> 가 미설정이면 ②로 넘어갈 수 없다(<see cref="MountStepReady"/>).
/// </summary>
public sealed partial class ArucoCalibrationViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private bool _loaded;

    /// <summary>① 핸드아이 측정 단계.</summary>
    public HandEyeViewModel HandEyeStep { get; }

    /// <summary>② 장착 보정 단계(기존 ArUco 동시 추정).</summary>
    public ArucoMountCalibrationViewModel MountStep { get; }

    /// <summary>두 단계가 공유하는 기준 tool — 어긋날 수 없게 여기 하나만 둔다.</summary>
    [ObservableProperty] private int _tool = 2;

    [ObservableProperty] private int _markerId;
    [ObservableProperty] private double _markerSizeMm = 100;
    [ObservableProperty] private int _selectedStep;

    public ArucoCalibrationViewModel(IServiceScopeFactory scopeFactory,
        HandEyeViewModel handEyeStep, ArucoMountCalibrationViewModel mountStep)
    {
        _scopeFactory = scopeFactory;
        HandEyeStep = handEyeStep;
        MountStep = mountStep;
    }

    public override async void OnActivated()
    {
        // 두 단계 모두 활성화 — 각자 폴링 타이머와 저장값 로드를 시작한다.
        HandEyeStep.OnActivated();
        MountStep.OnActivated();

        if (!_loaded)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
                var aruco = await calib.GetArucoSettingsAsync();
                MarkerSizeMm = aruco.SizeMm;
                MarkerId = aruco.MarkerId ?? 0;
                _loaded = true;
            }
            catch { /* 기본값 유지 — 각 단계가 자체 메시지로 알린다. */ }
        }

        PushShared();
    }

    // 내비게이션이 OnDeactivated → Dispose(→ OnDeactivated) 로 두 번 호출한다. 하위도 멱등이어야 한다.
    public override void OnDeactivated()
    {
        HandEyeStep.OnDeactivated();
        MountStep.OnDeactivated();
    }

    public override void Dispose()
    {
        base.Dispose();
        HandEyeStep.Dispose();
        MountStep.Dispose();
    }

    // 공유 값이 바뀌면 즉시 두 단계에 반영 — 한쪽만 바뀌는 상태를 만들지 않는다.
    partial void OnToolChanged(int value) => PushShared();
    partial void OnMarkerIdChanged(int value) => PushShared();
    partial void OnMarkerSizeMmChanged(double value) => PushShared();

    private void PushShared()
    {
        HandEyeStep.Tool = Tool;
        HandEyeStep.MarkerId = MarkerId;
        HandEyeStep.MarkerSizeMm = MarkerSizeMm;

        MountStep.Tool = Tool;
        MountStep.MarkerId = MarkerId;
        MountStep.MarkerSizeMm = MarkerSizeMm;

        OnPropertyChanged(nameof(MountStepReady));
        OnPropertyChanged(nameof(StepGuideText));
    }

    /// <summary>②로 넘어갈 수 있는가 — <c>T_T_C</c> 가 저장돼 있어야 한다(전부 0 이면 미설정).</summary>
    public bool MountStepReady => !MountStep.HandEye.ToArray().All(v => v == 0);

    public string StepGuideText => MountStepReady
        ? $"T_T_C 설정됨 (tool {Tool} 기준) — ② 장착 보정을 진행할 수 있습니다."
        : "T_T_C 가 미설정입니다(전부 0) — ① 핸드아이 측정을 먼저 완료하세요. " +
          "미설정 상태로 ②를 돌리면 오차가 T_A_B 로 흡수되어 잔차로는 드러나지 않습니다.";

    /// <summary>① 저장 직후 ②가 새 T_T_C 를 집도록 다시 읽힌다.</summary>
    public void RefreshAfterHandEyeSave()
    {
        MountStep.OnActivated();
        PushShared();
    }
}
