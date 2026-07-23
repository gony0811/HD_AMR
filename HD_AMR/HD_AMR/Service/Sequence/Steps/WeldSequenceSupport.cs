using HD_AMR.Models;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑤~⑫ Peak/Bead 시퀀스 공용 헬퍼. 파라미터 키·깊이 ROI 변환·mm 환산을 한곳에 모은다.
/// (③④는 각자 GetDepthRoiAsync 를 중복 구현하고 있으나, 신규 단계는 여기를 공유한다.)
/// public 인 이유: Sequence.razor(웹 어셈블리)가 파라미터 키 상수를 공유한다.
/// </summary>
public static class WeldSequenceSupport
{
    // ── 파라미터 키 ─────────────────────────────────────────────────
    // 깊이 ROI — CameraView / CameraAlignStep / FlatSurfaceAlignStep 과 공유.
    public const string RoiEnabledKey = "Camera.Depth.Roi.Enabled";
    public const string RoiXKey = "Camera.Depth.Roi.X";
    public const string RoiYKey = "Camera.Depth.Roi.Y";
    public const string RoiWKey = "Camera.Depth.Roi.W";
    public const string RoiHKey = "Camera.Depth.Roi.H";

    /// <summary>Peak 간 pitch(mm). ⑧ 이동량이자 ⑫ 각도식의 분모.</summary>
    public const string PitchMmKey = "Weld.Peak.PitchMm";

    /// <summary>Peak2 가 Peak1 의 어느 쪽인지(+1/−1). 설비 배치·측정 방향의 선택.</summary>
    public const string PitchDirKey = "Weld.Peak.PitchDir";

    /// <summary>영상 +X(화면 오른쪽) 이동량을 실을 툴축 — ④ FlatSurfaceAlignStep 과 공유.
    /// 카메라 페이지에서 실측 확인 후 저장한 매핑(부호 포함)이므로 별도 부호 파라미터를 두지 않는다.</summary>
    public const string ImageXAxisKey = "Camera.Align.ImageXAxis";

    /// <summary>영상 +Y(화면 아래) 이동량을 실을 툴축 — ④ FlatSurfaceAlignStep 과 공유.</summary>
    public const string ImageYAxisKey = "Camera.Align.ImageYAxis";

    /// <summary>광축(전방 = 대상을 향하는 방향) 툴축 — ③ CameraAlignStep 과 공유.
    /// 카메라 광축과 레이저 3점 빔 모두 툴 +Z 와 평행하므로 하나의 키를 쓴다.</summary>
    public const string DepthAxisKey = "Camera.Align.DepthAxis";

    /// <summary>검사 카메라 시야 중심이 depth 영상에서 보이는 위치 − 영상 중심 (mm, 영상 +X=화면 오른쪽).
    /// 센터링(⑥⑩⑦⁺⑪⁺)의 목표를 영상 중심이 아니라 이 위치로 옮겨, 타깃이 검사 카메라 아래에 오게 한다.</summary>
    public const string InspectCamOffsetXKey = "Sequence.InspectCam.OffsetXMm";

    /// <summary>검사 카메라 오프셋 영상 +Y(화면 아래) 성분 (mm). <see cref="InspectCamOffsetXKey"/> 참조.</summary>
    public const string InspectCamOffsetYKey = "Sequence.InspectCam.OffsetYMm";

    public const double DefaultPitchMm = 370.0;

    /// <summary>Peak 찾기 결과를 담는 Bag 키. id 는 1 또는 2.</summary>
    public static string PeakFindBagKey(int id) => $"peak{id}.find";

    /// <summary>Peak 이격거리(mm)를 담는 Bag 키. id 는 1 또는 2.</summary>
    public static string OffsetMmBagKey(int id) => $"peak{id}.offsetMm";

    /// <summary>비드 위치오차 d(mm, cross 축)를 담는 Bag 키. id 는 1 또는 2. ⑦⑪이 쓰고 비드 센터링이 소비.</summary>
    public static string BeadDMmBagKey(int id) => $"bead{id}.dMm";

    /// <summary>⑦⁺⑪⁺가 검사캠 오프셋 시프트를 적용한 상태임을 알리는 Bag 키(bool).
    /// ⑧이 이 플래그가 있을 때만 시프트 역보정을 합성하고 플래그를 지운다 — 세미오토 순서 이탈 가드.</summary>
    public const string InspectShiftedBagKey = "inspectCam.shifted";

    /// <summary>④ 시작 시점 TCP 포즈(double[6], BASE 기준 = ② 목표 ⊕ ③ 거리 정렬 지점)를 담는 Bag 키.
    /// ④⁺가 WD 조정 후 이 위치로 툴 X/Y 횡복귀할 때 기준 앵커로 쓴다(초점거리·자세는 유지).</summary>
    public const string InspectAnchorPoseBagKey = "inspect.anchorPose";

    /// <summary>
    /// 정규화 깊이 ROI(0~1)를 읽어 IR 프레임 픽셀 ROI 로 변환한다.
    /// IR 해상도 = Depth 해상도(848×480)라 IR 모드에서는 좌표 변환이 이 스케일링뿐이다.
    /// </summary>
    public static async Task<(RoiRect? Roi, string Src)> GetRoiAsync(
        ParameterService param, CameraService camera)
    {
        var f = camera.LatestIr;
        if (f is null) return (null, "IR 프레임 없음");

        double x = 0.35, y = 0.35, w = 0.30, h = 0.30;
        var src = "중앙 기본 ROI";
        try
        {
            if (await param.GetBoolAsync(RoiEnabledKey) == true)
            {
                var px = await param.GetDoubleAsync(RoiXKey) ?? 0;
                var py = await param.GetDoubleAsync(RoiYKey) ?? 0;
                var pw = await param.GetDoubleAsync(RoiWKey) ?? 0;
                var ph = await param.GetDoubleAsync(RoiHKey) ?? 0;
                if (pw > 0 && ph > 0 && px + pw <= 1.0001 && py + ph <= 1.0001)
                {
                    x = px; y = py; w = pw; h = ph;
                    src = "저장 ROI";
                }
            }
        }
        catch { /* DB 미준비 등 — 기본값 폴백 */ }

        var roi = new RoiRect(
            (int)Math.Round(x * f.Width),
            (int)Math.Round(y * f.Height),
            (int)Math.Round(w * f.Width),
            (int)Math.Round(h * f.Height)).ClampTo(f.Width, f.Height);

        return (roi, src);
    }

    /// <summary>부호 파라미터(+1/−1) 읽기. 값이 없거나 0이면 +1.</summary>
    public static async Task<int> GetSignAsync(ParameterService param, string key)
    {
        try
        {
            var v = await param.GetDoubleAsync(key);
            if (v is null || v.Value == 0) return 1;
            return Math.Sign(v.Value);
        }
        catch { return 1; }
    }

    /// <summary>pitch(mm) 읽기. 없으면 기본 370.</summary>
    public static async Task<double> GetPitchMmAsync(ParameterService param)
    {
        try { return await param.GetDoubleAsync(PitchMmKey) ?? DefaultPitchMm; }
        catch { return DefaultPitchMm; }
    }

    /// <summary>
    /// 영상 +X → 툴축 매핑 읽기 (④ FlatSurfaceAlignStep.GetAxisMapAsync 와 동일 규약).
    /// 없거나 범위 밖이면 기본 +X 폴백 — <c>FromParam=false</c> 로 알려 호출부가 경고를 남기게 한다.
    /// </summary>
    public static Task<(ToolAxisDir Axis, bool FromParam)> GetImageXAxisAsync(ParameterService param)
        => GetToolAxisAsync(param, ImageXAxisKey, ToolAxisDir.PlusX);

    /// <summary>영상 +Y → 툴축 매핑 읽기. 없으면 기본 +Y 폴백.</summary>
    public static Task<(ToolAxisDir Axis, bool FromParam)> GetImageYAxisAsync(ParameterService param)
        => GetToolAxisAsync(param, ImageYAxisKey, ToolAxisDir.PlusY);

    /// <summary>광축(전방) 툴축 읽기 (③ CameraAlignStep 과 동일 규약). 없으면 기본 +Z 폴백.</summary>
    public static Task<(ToolAxisDir Axis, bool FromParam)> GetDepthAxisAsync(ParameterService param)
        => GetToolAxisAsync(param, DepthAxisKey, ToolAxisDir.PlusZ);

    /// <summary>
    /// ⑤~⑫ 진행축(코로게이션 Peak 배열 방향의 영상 축)을 ② 검사방향에서 결정한다.
    /// 수직 용접라인(Vertical)은 툴 RZ −90° 합성으로 영상이 90° 회전해 진행축이 영상 가로가 되고
    /// (실기 검증된 기준 케이스), 수평 용접라인(Horizontal, RZ 0)은 진행축이 영상 세로가 된다.
    /// </summary>
    public static WeldProgressAxis GetProgressAxis(SequenceContext context)
        => context.InspectionDirection == InspectionMoveDirection.Vertical
            ? WeldProgressAxis.Horizontal
            : WeldProgressAxis.Vertical;

    /// <summary>
    /// 진행축을 ② 검사방향에서 결정해 <see cref="WeldTrackingService.Params"/> 에 반영하고 반환한다.
    /// 측정(⑤⑥⑦⑨⑩⑪) 전에 호출해 측정 축과 이동 축의 정합을 보장한다 — Weld 패널 수동 설정과
    /// 무관하게 시퀀스가 축을 소유한다.
    /// </summary>
    public static WeldProgressAxis ApplyProgressAxis(WeldTrackingService weld, SequenceContext context)
    {
        var axis = GetProgressAxis(context);
        weld.Params.ProgressAxis = axis;
        return axis;
    }

    /// <summary>진행축 이동량을 실을 툴축 매핑 — 진행축이 영상 가로면 ImageX, 세로면 ImageY.</summary>
    public static Task<(ToolAxisDir Axis, bool FromParam)> GetProgressToolAxisAsync(
        ParameterService param, WeldProgressAxis axis)
        => axis == WeldProgressAxis.Horizontal ? GetImageXAxisAsync(param) : GetImageYAxisAsync(param);

    /// <summary>진행축과 수직(cross, 비드 d 방향) 이동량을 실을 툴축 매핑 — 진행축의 반대 영상 축.</summary>
    public static Task<(ToolAxisDir Axis, bool FromParam)> GetCrossToolAxisAsync(
        ParameterService param, WeldProgressAxis axis)
        => axis == WeldProgressAxis.Horizontal ? GetImageYAxisAsync(param) : GetImageXAxisAsync(param);

    /// <summary>진행축/cross 축 매핑의 파라미터 키 이름 — 오류 메시지 안내용.</summary>
    public static string ProgressToolAxisKey(WeldProgressAxis axis)
        => axis == WeldProgressAxis.Horizontal ? ImageXAxisKey : ImageYAxisKey;

    public static string CrossToolAxisKey(WeldProgressAxis axis)
        => axis == WeldProgressAxis.Horizontal ? ImageYAxisKey : ImageXAxisKey;

    /// <summary>검사 카메라 설치 오프셋(영상 X/Y 성분, mm) 읽기. 없으면 0/0.</summary>
    public static async Task<(double XMm, double YMm)> GetInspectCamOffsetAsync(ParameterService param)
    {
        try
        {
            var x = await param.GetDoubleAsync(InspectCamOffsetXKey) ?? 0;
            var y = await param.GetDoubleAsync(InspectCamOffsetYKey) ?? 0;
            return (x, y);
        }
        catch { return (0, 0); }
    }


    private static async Task<(ToolAxisDir Axis, bool FromParam)> GetToolAxisAsync(
        ParameterService param, string key, ToolAxisDir fallback)
    {
        try
        {
            var v = await param.GetDoubleAsync(key);
            if (v is >= 0 and <= 5)
                return ((ToolAxisDir)(int)v.Value, true);
        }
        catch { /* DB 미준비 등 — 기본값 폴백 */ }
        return (fallback, false);
    }

    /// <summary>
    /// Peak 이격거리를 mm 로 환산한다. 두 개의 mm/px 경로가 공존하므로 신뢰도 순으로 고른다.
    /// ① WeldTrackingService — 간이 FOV intrinsic 이지만 <b>2점 실측 보정</b>이 적용됨(우선).
    /// ② CameraService — 실제 D2C intrinsics 지만 실측 보정 없음(폴백).
    /// 두 값이 15% 넘게 벌어지면 캘리브레이션 이상 신호이므로 note 에 경고를 담는다.
    /// </summary>
    public static (double Mm, string Note) ResolveOffsetMm(
        PeakFindResult r, WeldTrackingService weld, CameraService camera)
    {
        var weldMm = r.OffsetMm;
        var camMm = 0.0;

        var f = camera.LatestIr;
        if (f is not null && f.Width > 0 && f.Height > 0 && r.DepthMm > 0)
        {
            // OffsetPx 는 진행축 방향 픽셀 — 진행축이 세로면 세로 스케일(fy/Height)로 환산해야 한다.
            var mmPerPx = MmPerPixel(camera, f, weld.Params.ProgressAxis, r.DepthMm);
            camMm = r.OffsetPx * mmPerPx;
        }

        if (weld.ScaleCorrectionEnabled && r.ScaleAvailable)
        {
            var note = "";
            if (camMm != 0 && Math.Abs(weldMm) > 1e-6)
            {
                var diffPct = Math.Abs(camMm - weldMm) / Math.Abs(weldMm) * 100;
                if (diffPct > 15) note = $" ⚠스케일 경로 차이 {diffPct:0}%";
            }
            return (weldMm, $"2점보정(×{weld.ScaleCorrection:0.000}){note}");
        }

        return camMm != 0
            ? (camMm, "intrinsics ⚠2점보정 미적용")
            : (weldMm, "FOV근사 ⚠2점보정 미적용");
    }

    /// <summary>pitch(mm)를 현재 거리 기준 진행축 픽셀로 환산. 산출 불가면 0.</summary>
    public static double PitchToPixels(double pitchMm, int depthMm, CameraService camera, WeldProgressAxis axis)
    {
        var f = camera.LatestIr;
        if (f is null || f.Width <= 0 || f.Height <= 0 || depthMm <= 0) return 0;
        var mmPerPx = MmPerPixel(camera, f, axis, depthMm);
        return mmPerPx > 0 ? pitchMm / mmPerPx : 0;
    }

    /// <summary>진행축 1픽셀의 mm 환산 — 가로는 fx/Width, 세로는 fy/Height 기반.</summary>
    private static double MmPerPixel(CameraService camera, CameraFrame frame, WeldProgressAxis axis, int depthMm)
        => axis == WeldProgressAxis.Horizontal
            ? camera.PixelDeltaToMm(1.0 / frame.Width, 0, depthMm).DxMm
            : camera.PixelDeltaToMm(0, 1.0 / frame.Height, depthMm).DyMm;

    /// <summary>
    /// ⑤~⑫ 공통 선행조건 — 코봇/카메라 연결, IR 프레임, IR 모드, 검출기 가용성.
    /// ParameterService 가 비동기라 pitch 검사는 여기 넣지 않고 ExecuteAsync 앞부분에서 한다.
    /// </summary>
    public static StepValidation ValidateCommon(
        CobotService cobot, CameraService camera, WeldTrackingService weld, bool requireDetector)
    {
        if (!cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");
        if (!camera.IsConnected)
            return StepValidation.Fail("카메라 미연결");
        if (camera.LatestIr is null)
            return StepValidation.Fail("IR 프레임 없음 — IR 스트림 활성 상태를 확인하세요.");
        if (weld.Params.Mode != WeldImageMode.Ir)
            return StepValidation.Fail("IR 모드가 아닙니다 — Weld 페이지에서 IR 모드로 전환하세요.");
        if (requireDetector && !weld.DetectorAvailable)
            return StepValidation.Fail("비드 검출기 비활성 (OpenCV 네이티브 없음 — Windows 전용)");

        return StepValidation.Ok();
    }
}
