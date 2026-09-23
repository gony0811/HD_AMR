using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Service;
using Microsoft.Extensions.DependencyInjection;

namespace HD.AMR.Desktop.ViewModels;

/// <summary>
/// ArUco 기반 설정 — 핸드아이 보정 후 도면의 T_A_B를 직접 입력한다.
///   ① <see cref="HandEyeStep"/> — <c>T_T_C</c>(Tool TCP → 카메라) 를 AX=XB 로 측정
///   ② <see cref="MountStep"/>  — AMR 기준 코봇 BASE 장착 자세 <c>T_A_B</c>를 직접 입력·저장
/// Tool·마커 설정은 ①에서만 사용한다. <c>T_T_C</c>가 저장돼야 ②로 넘어갈 수 있다.
/// </summary>
public sealed partial class ArucoCalibrationViewModel : ViewModelBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CameraService _camera;
    private readonly DispatcherTimer _previewTimer;
    private readonly CancellationTokenSource _cts = new();
    private bool _previewBusy;
    private bool _loaded;

    /// <summary>① 핸드아이 측정 단계.</summary>
    public HandEyeViewModel HandEyeStep { get; }

    /// <summary>② T_A_B 직접 입력 단계.</summary>
    public ArucoMountCalibrationViewModel MountStep { get; }

    /// <summary>핸드아이 보정 기준 tool.
    /// 기본값 0(플랜지): 뎁스 카메라는 컨트롤러 TOOL 이 없어 플랜지 기준으로 T_T_C 를 잡는다.
    /// 저장된 측정 공구(<c>Calib.HandEye.Tool</c>)가 있으면 <see cref="OnActivated"/> 에서 그 값으로 덮는다.</summary>
    [ObservableProperty] private int _tool;

    [ObservableProperty] private int _markerId;
    [ObservableProperty] private double _markerSizeMm = 120;
    [ObservableProperty] private int _selectedStep;

    // ── 실시간 카메라 뷰(마커 검출 오버레이 포함) ──
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private string _previewStatusText = "카메라 대기 중…";
    [ObservableProperty] private bool _targetMarkerFound;

    public ArucoCalibrationViewModel(IServiceScopeFactory scopeFactory, CameraService camera,
        HandEyeViewModel handEyeStep, ArucoMountCalibrationViewModel mountStep)
    {
        _scopeFactory = scopeFactory;
        _camera = camera;
        HandEyeStep = handEyeStep;
        MountStep = mountStep;
        // ①에서 T_T_C를 저장하는 즉시 ② 탭 게이트를 다시 평가한다.
        HandEyeStep.HandEyeSaved += async () => await RefreshAfterHandEyeSaveAsync();
        // CameraViewModel 과 동일한 ~10fps 폴링 — 프레임을 가져와 마커를 그려 넣은 JPEG 를 표시한다.
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _previewTimer.Tick += async (_, _) => await PollPreviewAsync();
    }

    public override async void OnActivated()
    {
        // ②의 저장값 로드는 await로 완료해 탭 게이트가 최신 T_T_C를 보도록 한다.
        HandEyeStep.OnActivated();
        await MountStep.ReloadAsync();

        if (!_loaded)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var calib = scope.ServiceProvider.GetRequiredService<CalibrationService>();
                var aruco = await calib.GetArucoSettingsAsync();
                MarkerSizeMm = aruco.SizeMm;
                MarkerId = aruco.MarkerId ?? 0;
                // 지난 측정에 쓴 공구를 이어 쓴다. 소비처(QR 측위)도 이 번호를 사용한다.
                if (await calib.GetHandEyeToolAsync() is { } t && t >= 0 && t <= 15) Tool = (int)t;
                _loaded = true;
            }
            catch { /* 기본값 유지 — 각 단계가 자체 메시지로 알린다. */ }
        }

        PushShared();

        _previewTimer.Start();
        await EnsureStreamingAsync();
    }

    // 내비게이션이 OnDeactivated → Dispose(→ OnDeactivated) 로 두 번 호출한다. 하위도 멱등이어야 한다.
    public override void OnDeactivated()
    {
        _previewTimer.Stop();
        PreviewImage = null;
        HandEyeStep.OnDeactivated();
        MountStep.OnDeactivated();
    }

    public override void Dispose()
    {
        base.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        HandEyeStep.Dispose();
        MountStep.Dispose();
    }

    /// <summary>화면 진입 시 스트림이 꺼져 있으면 자동 시작한다. 이탈 시에는 끄지 않는다(Camera 화면과 공유).</summary>
    private async Task EnsureStreamingAsync()
    {
        if (!_camera.IsConnected || _camera.IsStreaming) return;
        try { await _camera.StartStreamAsync(_cts.Token); }
        catch (Exception ex) { SetPreviewStatus($"카메라 스트림 시작 실패: {ex.Message}", false); }
    }

    private async Task PollPreviewAsync()
    {
        if (_previewBusy) return;
        _previewBusy = true;
        try
        {
            if (!_camera.IsConnected) { PreviewImage = null; SetPreviewStatus("카메라 미연결", false); return; }
            if (!_camera.IsStreaming) { PreviewImage = null; SetPreviewStatus("카메라 스트리밍 꺼짐", false); return; }

            var frame = _camera.LatestColor;
            if (frame is null || DateTime.UtcNow - _camera.LastFrameAt > TimeSpan.FromSeconds(1))
            {
                SetPreviewStatus("프레임 수신 없음", false);
                return;
            }

            byte[]? jpeg;
            if (OperatingSystem.IsWindows())
            {
                var target = MarkerId;
                var result = await Task.Run(() => ArucoPoseEstimator.DetectAndRender(frame, target));
                jpeg = result?.Jpeg;
                if (result is not null)
                {
                    SetPreviewStatus(result.DetectedIds.Length == 0
                            ? "마커 미검출"
                            : $"마커 검출: [{string.Join(", ", result.DetectedIds.OrderBy(i => i))}]" +
                              (result.TargetFound ? $" — ✔ 대상 ID {target} 검출됨" : $" — 대상 ID {target} 없음"),
                        result.TargetFound);
                }
                else
                {
                    SetPreviewStatus($"검출 불가한 프레임 포맷({frame.PixelFormat}) — 영상만 표시", false);
                    jpeg = await _camera.GetLatestColorJpegAsync(_camera.Settings.JpegQuality, _cts.Token);
                }
            }
            else
            {
                // OpenCV 네이티브는 Windows 전용 — 다른 플랫폼에서는 검출 없이 영상만 보여준다.
                SetPreviewStatus("이 플랫폼에서는 검출 미지원 — 영상만 표시", false);
                jpeg = await _camera.GetLatestColorJpegAsync(_camera.Settings.JpegQuality, _cts.Token);
            }

            if (jpeg is { Length: > 0 })
            {
                using var ms = new MemoryStream(jpeg);
                PreviewImage = new Bitmap(ms);
            }
        }
        catch { /* 프레임 처리 오류 무시 — 다음 틱 재시도 */ }
        finally { _previewBusy = false; }
    }

    private void SetPreviewStatus(string text, bool targetFound)
    {
        PreviewStatusText = text;
        TargetMarkerFound = targetFound;
    }

    // 핸드아이 설정이 바뀌면 ① 단계에 즉시 반영한다.
    partial void OnToolChanged(int value) => PushShared();
    partial void OnMarkerIdChanged(int value) => PushShared();
    partial void OnMarkerSizeMmChanged(double value) => PushShared();

    private void PushShared()
    {
        HandEyeStep.Tool = Tool;
        HandEyeStep.MarkerId = MarkerId;
        HandEyeStep.MarkerSizeMm = MarkerSizeMm;

        OnPropertyChanged(nameof(MountStepReady));
        OnPropertyChanged(nameof(StepGuideText));
    }

    /// <summary>②로 넘어갈 수 있는가 — <c>T_T_C</c> 가 저장돼 있어야 한다(전부 0 이면 미설정).</summary>
    public bool MountStepReady => !MountStep.HandEye.ToArray().All(v => v == 0);

    public string StepGuideText => MountStepReady
        ? $"T_T_C 설정됨 (tool {Tool} 기준) — ② T_A_B를 입력할 수 있습니다."
        : "T_T_C 가 미설정입니다(전부 0) — ① 핸드아이 측정을 먼저 완료하세요. " +
          "저장 후 ② T_A_B 직접 입력 탭이 활성화됩니다.";

    /// <summary>① 저장 직후 ②가 새 T_T_C 를 집도록 다시 읽는다 — 로드 완료 후 게이트를 재평가한다.</summary>
    public async Task RefreshAfterHandEyeSaveAsync()
    {
        await MountStep.ReloadAsync();
        PushShared();
    }
}
