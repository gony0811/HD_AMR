using HD.AMR.App.Models;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// ⑤~⑫ Peak/Bead 시퀀스 공용 헬퍼. 파라미터 키·깊이 ROI 변환·mm 환산을 한곳에 모은다.
/// (③④는 각자 GetDepthRoiAsync 를 중복 구현하고 있으나, 신규 단계는 여기를 공유한다.)
/// public 인 이유: Sequence.razor(웹 어셈블리)가 파라미터 키 상수를 공유한다.
/// </summary>
public static class WeldSequenceSupport
{
    // ── 파라미터 키 ─────────────────────────────────────────────────
    // 평탄면(깊이) ROI — CameraView / CameraAlignStep / FlatSurfaceAlignStep 과 공유. ⑤~⑪의 폴백.
    public const string RoiEnabledKey = "Camera.Depth.Roi.Enabled";
    public const string RoiXKey = "Camera.Depth.Roi.X";
    public const string RoiYKey = "Camera.Depth.Roi.Y";
    public const string RoiWKey = "Camera.Depth.Roi.W";
    public const string RoiHKey = "Camera.Depth.Roi.H";

    // Peak(코로게이션) 탐색 ROI — 카메라 페이지 Depth 뷰에서 "Peak" 대상으로 저장. ⑤⑥⑨⑩ + ⑦⑪의 Peak ROI.
    public const string PeakRoiEnabledKey = "Camera.Peak.Roi.Enabled";
    public const string PeakRoiXKey = "Camera.Peak.Roi.X";
    public const string PeakRoiYKey = "Camera.Peak.Roi.Y";
    public const string PeakRoiWKey = "Camera.Peak.Roi.W";
    public const string PeakRoiHKey = "Camera.Peak.Roi.H";

    // Bead(용접비드) 검출 ROI — 비드는 IR 영상에서 보이므로 카메라 페이지 IR 뷰에서 저장. ⑦⑪의 Weld ROI.
    public const string BeadRoiEnabledKey = "Camera.Bead.Roi.Enabled";
    public const string BeadRoiXKey = "Camera.Bead.Roi.X";
    public const string BeadRoiYKey = "Camera.Bead.Roi.Y";
    public const string BeadRoiWKey = "Camera.Bead.Roi.W";
    public const string BeadRoiHKey = "Camera.Bead.Roi.H";

    // ── 시퀀스 실행 파라미터(시퀀스 페이지가 저장, ACS 실행 경로도 같은 값을 읽는다) ──────
    // ⚠ 진실 원천은 이 상수들이다. 페이지가 자기 상수로 저장하고 ACS 경로가 읽지 않으면,
    //   현장에서 튜닝한 값이 실제 검사에는 적용되지 않는 조용한 불일치가 생긴다.

    /// <summary>② 검사위치 이동 수평 오프셋 u [mm] — 툴 X(좌+/우−).</summary>
    public const string InspectionOffsetUKey = "Sequence.Inspection.OffsetU";

    /// <summary>② 검사위치 이동 수직 오프셋 v [mm] — 툴 Y(상+/하−).</summary>
    public const string InspectionOffsetVKey = "Sequence.Inspection.OffsetV";

    /// <summary>④ 평탄면 센터링 후 카메라 광축 → 레이저 중심 보정 횡이동 [mm, 툴 Y].</summary>
    public const string CameraToLaserShiftYKey = "Sequence.FlatSurface.CameraToLaserShiftYmm";

    /// <summary>도면 전역 z(선창 바닥 기준) → AMR 바닥 기준 보정 [mm] — 해당 층 바닥 높이(level_z).
    /// ACS 의 T_W_D 는 2D 변환이라 z 는 도면 전역값이 그대로 실려 온다(사양 §10 N17). 1층은 0.</summary>
    public const string ZDatumOffsetKey = "Sequence.Inspection.ZDatumOffsetMm";

    /// <summary>도착 판정 XY 허용 오차(m). Order allowedDeviationXY → 이 키 → AmrRestSettings → 0.1 순 폴백.</summary>
    public const string ArrivalDeviationXyKey = "arrival.deviationXy";

    /// <summary>도착 판정 각도 허용 오차(rad). Order allowedDeviationTheta → 이 키 → AmrRestSettings → 0.1 순 폴백.</summary>
    public const string ArrivalDeviationThetaKey = "arrival.deviationTheta";

    /// <summary>비드 검출 YOLO 모델 경로(필수). 빈 문자열 = 모델 없음(비드 찾기 단계 실패).
    /// 카메라 페이지 드롭다운이 저장하고, ⑦⑪·⑦⁺ 가 측정 전 <see cref="ApplyDlModelAsync"/> 로 적용한다.</summary>
    public const string DlModelPathKey = WeldDetectionSettingsStore.DlModelPathKey;

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

    public const double DefaultPitchMm = 360.0;

    /// <summary>45° 챔퍼(SL/PL/SU/PU) 경사 방향 코로게이션 top 피치(mm) — KC-2B 도면 실측.
    /// 경사면 도면은 실제 경사 길이로 그려져 있어 환산 없이 쓴다(선체 길이 방향은 360 그대로).</summary>
    public const double ChamferSlopePitchMm = 254.6;

    /// <summary>Peak 찾기 결과를 담는 Bag 키. id 는 1 또는 2.</summary>
    public static string PeakFindBagKey(int id) => $"peak{id}.find";

    /// <summary>Peak 이격거리(mm)를 담는 Bag 키. id 는 1 또는 2.</summary>
    public static string OffsetMmBagKey(int id) => $"peak{id}.offsetMm";

    /// <summary>비드 위치오차 d(mm, cross 축)를 담는 Bag 키. id 는 1 또는 2. ⑦⑪이 쓰고 비드 센터링이 소비.</summary>
    public static string BeadDMmBagKey(int id) => $"bead{id}.dMm";

    /// <summary>⑦/⑪ 수동 입력을 팝업 조그 뒤에 했을 때, 그 자리에서 Peak 가 진행축 센터에서 벗어난 거리 [mm](double).
    /// ⑦⁺/⑪⁺ 비드 센터링이 cross 이동과 함께 진행축으로도 맞춘다. 조그가 없었으면 넣지 않는다.</summary>
    public static string BeadPeakOffsetMmBagKey(int id) => $"bead{id}.peakOffsetMm";

    /// <summary>⑦⁺⑪⁺가 검사캠 오프셋 시프트를 적용한 상태임을 알리는 Bag 키(bool).
    /// ⑧이 이 플래그가 있을 때만 시프트 역보정을 합성하고 플래그를 지운다 — 세미오토 순서 이탈 가드.</summary>
    public const string InspectShiftedBagKey = "inspectCam.shifted";

    /// <summary>② 접근 완료 TCP 포즈(double[6], BASE 기준). ④ 재실행 시 덮어쓰지 않는다.
    /// ② 없이 ④만 실행하면 최초 ④ 시작점으로 초기화한다. 새 ② 접근 또는 UI 초기화 시 교체/삭제.
    /// ④⁺는 WD 조정 후 이 점을 현재 면 평행 평면에 투영해 복귀한다(광축 거리·자세 유지).</summary>
    public const string InspectAnchorPoseBagKey = "inspect.anchorPose";

    /// <summary>② 가 환산한 용접선 점 S(면 위, BASE [x,y,z]). ④⁺ 복귀 기준 — 있으면 <see cref="InspectAnchorPoseBagKey"/>보다
    /// 우선한다(목표 = S + n·dot(C−S, n), 틸트 보정 후에도 광축이 S 를 지난다). 새 ② 접근 또는 UI 초기화 시 교체/삭제.</summary>
    public const string InspectAnchorSeamPointBagKey = "inspect.anchorSeamPoint";

    /// <summary>⑦⁺ Bead1 센터링 완료 직후(검사캠 시프트 <b>전</b>) TCP 포즈(double[6])를 담는 Bag 키.
    /// ⑧이 이 포즈를 앵커로 pitch 이동해, 검사캠 시프트·작업물 좌표계 교시용 이동을 자동 원복한다.</summary>
    public const string Bead1CenteredPoseBagKey = "bead1.centeredPose";

    /// <summary>② 가 환산한 면 바깥 단위 법선(BASE [x,y,z], 면에서 멀어지는 쪽). ⑧ Peak2 이동이 면 거리를 유지하는 기준.
    /// 계산 법선 자세가 없으면(티칭 폴백) 두지 않는다.</summary>
    public const string WallOutNormalBagKey = "seam.wallOutNormal";

    /// <summary>② 가 환산한 용접선 방향(접근한 점 → 반대쪽 점) 단위벡터(BASE [x,y,z]). ⑧ Peak2 이동 방향의 기준.
    /// 끝점이 없으면 두지 않는다.</summary>
    public const string SeamDirBaseBagKey = "seam.dirBase";

    /// <summary>
    /// 피치 이동(BASE 벡터)이 용접선 방향과 반대인가. <paramref name="pitchDir"/> = +1 은 용접선 끝점 쪽, −1 은 그 반대다.
    /// 용접선 가로 정렬은 툴을 광축 둘레로 0° 또는 180° 중 J6 안전 범위에 드는 쪽으로 돌리므로, 툴 진행축 + 방향이
    /// 끝점 쪽일 수도 반대쪽일 수도 있다(실기 2026-11-13 F L4-R01C04 — 점2 를 끝점 반대쪽 한 피치에 잡아 옆 구간을 검사).
    /// 용접선과 거의 수직인 이동(코사인 0.2 미만)은 판단하지 않는다.
    /// </summary>
    internal static bool PitchMoveOpposesSeam(double[] baseMove, double[] seamDir, int pitchDir)
    {
        var len = Math.Sqrt(baseMove[0] * baseMove[0] + baseMove[1] * baseMove[1] + baseMove[2] * baseMove[2]);
        var sLen = Math.Sqrt(seamDir[0] * seamDir[0] + seamDir[1] * seamDir[1] + seamDir[2] * seamDir[2]);
        if (len < 1e-9 || sLen < 1e-9) return false;
        var cos = (baseMove[0] * seamDir[0] + baseMove[1] * seamDir[1] + baseMove[2] * seamDir[2]) / (len * sLen);
        if (Math.Abs(cos) < 0.2) return false;
        return cos * (pitchDir >= 0 ? 1 : -1) < 0;
    }

    /// <summary>툴 좌표 이동량을 <paramref name="anchorPose"/> 자세 기준 BASE 벡터로 바꾼다(회전만).</summary>
    internal static double[] ToolOffsetInBase(double[] anchorPose, double[] toolOffset)
    {
        var m = HD.AMR.App.Communication.FrameMath.PoseToMatrix(anchorPose);
        var v = new double[3];
        for (var i = 0; i < 3; i++)
            v[i] = m[i, 0] * toolOffset[0] + m[i, 1] * toolOffset[1] + m[i, 2] * toolOffset[2];
        return v;
    }

    /// <summary>
    /// 툴 좌표 이동량 <paramref name="toolOffset"/>(x,y,z) 을 <paramref name="anchorPose"/> 자세 기준 BASE 벡터로 바꾸고,
    /// 면 법선 <paramref name="wallOut"/> 성분을 빼 면과 평행한 이동으로 만든다(길이는 원래 이동량 유지).
    /// ④ 평탄면 정렬이 툴을 국부 면에 맞춰 기울이면(실기 2026-11-09 ry 0.76°) 툴 X 로 360mm 갈 때 면 쪽으로 4.7mm 가 섞여,
    /// 점2 의 면 거리가 점1 과 달라지고 작업물 좌표계 X 축이 면 쪽으로 기울었다. 면 평행 성분이 거의 없으면 null.
    /// </summary>
    internal static double[]? ToolOffsetParallelToWall(double[] anchorPose, double[] toolOffset, double[] wallOut)
    {
        var m = HD.AMR.App.Communication.FrameMath.PoseToMatrix(anchorPose);
        var v = new double[3];
        for (var i = 0; i < 3; i++)
            v[i] = m[i, 0] * toolOffset[0] + m[i, 1] * toolOffset[1] + m[i, 2] * toolOffset[2];
        var len = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

        var nLen = Math.Sqrt(wallOut[0] * wallOut[0] + wallOut[1] * wallOut[1] + wallOut[2] * wallOut[2]);
        if (len < 1e-9 || nLen < 1e-9) return null;
        var n = new[] { wallOut[0] / nLen, wallOut[1] / nLen, wallOut[2] / nLen };
        var dot = v[0] * n[0] + v[1] * n[1] + v[2] * n[2];
        var p = new[] { v[0] - dot * n[0], v[1] - dot * n[1], v[2] - dot * n[2] };
        var pLen = Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
        if (pLen < 1e-3 * len) return null;
        var s = len / pLen;
        return new[] { p[0] * s, p[1] * s, p[2] * s };
    }

    /// <summary>
    /// Peak(코로게이션) 탐색용 ROI — 우선순위: Peak ROI(Depth 뷰에서 저장) → 평탄면 ROI 폴백 → 중앙 30%.
    /// ⑤⑥⑨⑩ 측정과 ⑦⑪의 Peak ROI 인자로 쓴다. 어느 것을 썼는지 Src 로 반환.
    /// IR 해상도 = Depth 해상도(848×480)라 IR 모드에서는 좌표 변환이 정규화→픽셀 스케일링뿐이다.
    /// </summary>
    public static async Task<(RoiRect? Roi, string Src)> GetPeakRoiAsync(
        ParameterService param, CameraService camera)
    {
        var f = camera.LatestIr;
        if (f is null) return (null, "IR 프레임 없음");
        var (roi, src) = await GetPeakRoiAsync(param, f.Width, f.Height);
        return (roi, src);
    }

    /// <summary><see cref="GetPeakRoiAsync(ParameterService, CameraService)"/> 의 영상 크기 지정판 — 저장된 촬영본용.</summary>
    public static async Task<(RoiRect? Roi, string Src)> GetPeakRoiAsync(ParameterService param, int width, int height)
    {
        var roi = DefaultCenterRoi;
        var src = "중앙 기본 ROI";

        if (await CameraRoiParameters.ReadAsync(param, CameraRoiKeys.Peak) is { } peak)
        {
            roi = peak;
            src = "Peak ROI";
        }
        else if (await CameraRoiParameters.ReadAsync(param, CameraRoiKeys.Depth) is { } depth)
        {
            roi = depth;
            src = "평탄면 ROI 폴백";
        }

        return (roi.ToPixel(width, height), src);
    }

    /// <summary>
    /// Bead(용접비드) 검출용 ROI — 우선순위: Bead ROI(IR 뷰에서 저장) → Peak ROI 폴백 → 평탄면 ROI → 중앙 30%.
    /// ⑦⑪의 Weld ROI 인자로 쓴다.
    /// </summary>
    public static async Task<(RoiRect? Roi, string Src)> GetBeadRoiAsync(
        ParameterService param, CameraService camera)
    {
        var f = camera.LatestIr;
        if (f is null) return (null, "IR 프레임 없음");
        var (roi, src) = await GetBeadRoiAsync(param, f.Width, f.Height);
        return (roi, src);
    }

    /// <summary><see cref="GetBeadRoiAsync(ParameterService, CameraService)"/> 의 영상 크기 지정판 — 저장된 촬영본용.</summary>
    public static async Task<(RoiRect? Roi, string Src)> GetBeadRoiAsync(ParameterService param, int width, int height)
    {
        var roi = DefaultCenterRoi;
        var src = "중앙 기본 ROI";

        if (await CameraRoiParameters.ReadAsync(param, CameraRoiKeys.Bead) is { } bead)
        {
            roi = bead;
            src = "Bead ROI";
        }
        else if (await CameraRoiParameters.ReadAsync(param, CameraRoiKeys.Peak) is { } peak)
        {
            roi = peak;
            src = "Peak ROI 폴백";
        }
        else if (await CameraRoiParameters.ReadAsync(param, CameraRoiKeys.Depth) is { } depth)
        {
            roi = depth;
            src = "평탄면 ROI 폴백";
        }

        return (roi.ToPixel(width, height), src);
    }

    /// <summary>ROI 파라미터가 하나도 없을 때의 폴백 — 화면 중앙 30%.</summary>
    private static readonly NormalizedRoi DefaultCenterRoi = new(0.35, 0.35, 0.30, 0.30);

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

    /// <summary>pitch(mm) 읽기. 파라미터(없으면 기본 360)를 <see cref="ResolvePitchMm"/> 로 면·방향에 맞춘다.</summary>
    public static async Task<double> GetPitchMmAsync(ParameterService param, SequenceContext context)
    {
        double nominal;
        try { nominal = await param.GetDoubleAsync(PitchMmKey) ?? DefaultPitchMm; }
        catch { nominal = DefaultPitchMm; }
        return ResolvePitchMm(nominal, context);
    }

    /// <summary>
    /// 이번 검사에 쓸 pitch(mm). 챔퍼 면(SL/PL/SU/PU)에서 수직(경사 방향) 용접선을 따라 진행하면
    /// 코로게이션 간격이 <see cref="ChamferSlopePitchMm"/> 이므로 그 값을, 그 외에는 파라미터 pitch 를 쓴다.
    /// 진행축은 용접선 방향이고 ② 검사방향 Vertical = 챔퍼에서 경사 방향(SeamDirectionResolver).
    /// </summary>
    public static double ResolvePitchMm(double nominalMm, SequenceContext context)
        => IsChamferSlopeProgress(context) ? ChamferSlopePitchMm : nominalMm;

    /// <summary>챔퍼 면에서 경사 방향으로 진행하는지 — 면은 <see cref="SequenceContext.InspectionSurfaceId"/> 로 판정.</summary>
    public static bool IsChamferSlopeProgress(SequenceContext context)
        => context.InspectionDirection == InspectionMoveDirection.Vertical
           && WallCodes.FindBySurfaceId(context.InspectionSurfaceId)?.Orientation
               is SurfaceOrientation.ChamferLower or SurfaceOrientation.ChamferUpper;

    /// <summary>
    /// 학습 모델을 쓰는 검출 방식(YOLO·U-Net)인데 모델이 지정되지 않았으면 경고 문구, 아니면 null.
    /// 비드 찾기 단계가 <see cref="ApplyDlModelAsync"/> 직후 확인해 로봇을 움직이기 전에 실패로 멈춘다.
    /// (파일 존재는 검출기가 경로를 해석해 확인한다 — 파일명만 저장된 경우 models 폴더 기준.)
    /// </summary>
    public static string? MissingModel(WeldTrackingService weld) => weld.Method switch
    {
        WeldDetectionMethod.Dl when string.IsNullOrWhiteSpace(weld.Params.DlModelPath)
            => "모델 없음 — 비드 검출 YOLO 모델이 지정되지 않았습니다. 용접 추적 화면에서 모델을 선택하거나 파라미터 Weld.Yolo.ModelPath 를 지정하세요.",
        WeldDetectionMethod.Unet when string.IsNullOrWhiteSpace(weld.Params.UnetModelPath)
            => "모델 없음 — 비드 검출 U-Net 모델이 지정되지 않았습니다. 용접 추적 화면에서 모델을 선택하거나 파라미터 Weld.Unet.ModelPath 를 지정하세요.",
        _ => null,
    };

    /// <summary>저장된 비드 검출 모델 선택(DL=YOLO, U-Net)을 <see cref="WeldTrackingService.Params"/> 에 적용한다.
    /// 키 미존재/DB 미준비면 현재 인메모리 선택을 유지한다. 현재 검출 방식의 모델 표시를 반환(예: "YOLO 모델: weld_yolo_ir_1106.onnx").</summary>
    public static async Task<string> ApplyDlModelAsync(ParameterService param, WeldTrackingService weld)
    {
        try
        {
            if (await param.GetAsync(DlModelPathKey) is { } v)
                weld.Params.DlModelPath = v;
            if (await param.GetAsync(WeldDetectionSettingsStore.UnetModelPathKey) is { } u)
                weld.Params.UnetModelPath = u;
        }
        catch { /* DB 미준비 — 인메모리 선택 유지 */ }

        // 지정된 모델 파일명(종류·ir/rgb)에 맞춰 검출 방식·입력 모드를 맞춘다(모델이 기준 — 불일치 방지).
        var aligned = weld.AlignToActiveModel();

        static string Name(string? path) => string.IsNullOrWhiteSpace(path) ? "미지정" : Path.GetFileName(path.Trim().Trim('"'));
        var input = weld.Params.Mode == WeldImageMode.Ir ? "IR" : "RGB";
        var label = weld.Method switch
        {
            WeldDetectionMethod.Dl => $"YOLO 모델: {Name(weld.Params.DlModelPath)}, 입력 {input}",
            WeldDetectionMethod.Unet => $"U-Net 모델: {Name(weld.Params.UnetModelPath)}, 입력 {input}",
            var m => $"검출 방식: {m}, 입력 {input}",
        };
        return aligned is null ? label : $"{label} ({aligned})";
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

    /// <summary>광축 파라미터가 저장돼 있으면 그 축, 없으면 null 을 <see cref="SequenceContext.OpticalAxis"/> 에 채운다.</summary>
    public static async Task LoadOpticalAxisAsync(ParameterService param, SequenceContext context)
    {
        var (axis, fromParam) = await GetDepthAxisAsync(param);
        context.OpticalAxis = fromParam ? axis : null;
    }

    /// <summary>광축 파라미터 미설정 시 광축 방향 이동 단계가 내는 실패 메시지.</summary>
    public static string OpticalAxisMissingMessage(string stage)
        => $"[{stage}] '{DepthAxisKey}' 광축 파라미터가 저장돼 있지 않습니다 — 기본 +Z 로 움직이면 현장(−Z 장착)에서 " +
           "벽 쪽으로 반대로 이동할 수 있어 정지합니다. 카메라 페이지에서 광축을 설정·저장하세요.";

    /// <summary>
    /// ⑤~⑫ 진행축(코로게이션 Peak 배열 방향의 영상 축)을 ② 검사방향에서 결정한다.
    /// 수직 용접라인(Vertical, J6 180°)은 진행축이 영상 가로가 되고(실기 검증된 기준 케이스),
    /// 수평 용접라인(Horizontal, J6 90° — 광축 둘레 90° 회전)은 진행축이 영상 세로가 된다.
    /// ② 가 용접선을 영상 가로선으로 맞췄으면(<see cref="SequenceContext.SeamLeveledInImage"/>) 방향과 무관하게 영상 가로다.
    /// </summary>
    public static WeldProgressAxis GetProgressAxis(SequenceContext context)
        => ProgressAxisFor(context, ProgressAxisAlwaysHorizontal);

    /// <summary>
    /// 진행축을 항상 영상 가로로 둘지 — 파라미터(미설정 = 켬). ② 가 툴을 돌려 용접선을 늘 영상 가로선으로 맞추므로
    /// Peak·Bead 찾기와 센터링은 가로 용접선만 보면 된다. 종전에는 "② 가로 정렬" 표시·용접선 기록이 없으면(앱 재시작 뒤,
    /// 단계 단독 실행) 검사방향으로 되돌아가 수평 용접선을 영상 세로로 잘못 봤다(실기 2026-11-14 15:33 A: 진행축=Vertical →
    /// Peak 센터링이 엉뚱한 축으로 움직여 잔차 +82 mm, 가로로 그린 수동 비드선은 방향 검증에서 거부).
    /// false 면 종전 규칙(가로 정렬 표시·용접선 기록·검사방향 순).
    /// </summary>
    public const string ProgressAxisAlwaysHorizontalKey = "Sequence.Weld.ProgressAxisAlwaysHorizontal";

    /// <summary><see cref="ProgressAxisAlwaysHorizontalKey"/> 의 마지막으로 읽은 값 — 파라미터를 읽을 수 없는 동기 경로용.</summary>
    public static bool ProgressAxisAlwaysHorizontal { get; set; } = true;

    /// <summary>진행축 결정 규칙(순수 함수). <paramref name="alwaysHorizontal"/> 이면 항상 영상 가로.</summary>
    internal static WeldProgressAxis ProgressAxisFor(SequenceContext context, bool alwaysHorizontal)
        => alwaysHorizontal || context.SeamLeveledInImage || context.InspectionDirection == InspectionMoveDirection.Vertical
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

    /// <summary>현재 광축이 ② 기록 광축과 이 각도 이상 어긋나면 다른 면을 보고 있다고 보고 기록을 쓰지 않는다 [도].</summary>
    internal const double SeamMemoryMaxOpticalDeviationDeg = 25.0;

    /// <summary>
    /// 현재 툴 자세 <paramref name="tcpPose"/> 에서 용접선(<paramref name="rec"/>)이 영상에 어느 축으로 보이는지.
    /// 영상 속 기울기(0~90°, 0=가로)가 45° 이하면 Horizontal. 광축이 기록과 크게 어긋나거나(다른 면) 축 매핑이 겹치거나
    /// 용접선이 광축과 거의 나란하면 null(→ 호출측 폴백).
    /// </summary>
    internal static WeldProgressAxis? ProgressAxisFromSeam(double[] tcpPose, SeamOrientationRecord rec,
        ToolAxisDir opticalAxis, ToolAxisDir imageX, ToolAxisDir imageY, out double tiltDeg, out string? reject)
    {
        tiltDeg = double.NaN;
        var u = CobotInspectionMoveStep.ToolAxisInBase(tcpPose, opticalAxis);
        var o = rec.OpticalAxisBase;
        var cos = Math.Clamp(u[0] * o[0] + u[1] * o[1] + u[2] * o[2], -1.0, 1.0);
        var dev = Math.Acos(cos) * 180.0 / Math.PI;
        if (dev > SeamMemoryMaxOpticalDeviationDeg)
        {
            reject = $"광축이 ② 기록과 {dev:0}° 어긋남(다른 면)";
            return null;
        }

        if (CobotInspectionMoveStep.SeamLevelTwistDeg(tcpPose, rec.SeamDirBase, opticalAxis, imageX, imageY) is not { } phi)
        {
            reject = "영상 축 매핑 겹침 또는 용접선이 광축과 나란함";
            return null;
        }

        var t = Math.Abs(phi);
        tiltDeg = t > 90.0 ? 180.0 - t : t;
        reject = null;
        return tiltDeg <= 45.0 ? WeldProgressAxis.Horizontal : WeldProgressAxis.Vertical;
    }

    /// <summary>
    /// ⑤~⑫ 진행축을 정한다. ② 가 기록한 용접선 방향(<see cref="SeamOrientationMemory"/>)이 있으면 현재 툴 자세에서
    /// 용접선이 영상에 가로로 보이는지 세로로 보이는지로 정한다 — 수직 용접선이면 툴도 용접선(수직)을 따라 움직인다.
    /// 기록이 없거나 쓸 수 없으면 종전대로 컨텍스트 검사방향(<see cref="GetProgressAxis"/>)으로 정한다.
    /// </summary>
    public static async Task<(WeldProgressAxis Axis, string Source)> ResolveProgressAxisAsync(
        SequenceContext context, CobotService cobot, ParameterService param, CancellationToken ct)
    {
        try { ProgressAxisAlwaysHorizontal = await param.GetBoolAsync(ProgressAxisAlwaysHorizontalKey) ?? true; }
        catch (OperationCanceledException) { throw; }
        catch { /* 파라미터를 못 읽으면 마지막 값 유지 */ }
        if (ProgressAxisAlwaysHorizontal)
            return (WeldProgressAxis.Horizontal, "가로 고정 (② 가 용접선을 영상 가로로 정렬)");

        var fallback = ProgressAxisFor(context, alwaysHorizontal: false);
        var fallbackSrc = context.SeamLeveledInImage ? "② 가로 정렬" :
            $"검사방향 {(context.InspectionDirection == InspectionMoveDirection.Vertical ? "수직" : "수평")}";
        if (SeamOrientationMemory.Last is not { } rec)
            return (fallback, $"{fallbackSrc} (② 용접선 기록 없음)");

        double[] tcp;
        try { tcp = await cobot.Rpc.GetTcpPoseInBaseAsync(context.Tool, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (fallback, $"{fallbackSrc} (TCP 조회 실패: {ex.Message})"); }

        var (optical, _) = await GetDepthAxisAsync(param);
        var (imageX, _) = await GetImageXAxisAsync(param);
        var (imageY, _) = await GetImageYAxisAsync(param);
        if (ProgressAxisFromSeam(tcp, rec, optical, imageX, imageY, out var tilt, out var reject) is not { } axis)
            return (fallback, $"{fallbackSrc} ({reject})");

        return (axis, $"② 용접선({rec.WallCode ?? "면 미상"}) 영상 기울기 {tilt:0.0}°");
    }

    /// <summary><see cref="ResolveProgressAxisAsync"/> 결과를 <see cref="WeldTrackingService.Params"/> 에 반영하고 반환한다.</summary>
    public static async Task<(WeldProgressAxis Axis, string Source)> ApplyProgressAxisAsync(
        WeldTrackingService weld, SequenceContext context, CobotService cobot, ParameterService param, CancellationToken ct)
    {
        var r = await ResolveProgressAxisAsync(context, cobot, param, ct);
        weld.Params.ProgressAxis = r.Axis;
        return r;
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

    /// <summary>
    /// 영상 평면 이동량(mm, 영상 +X=화면 오른쪽 / +Y=화면 아래)을 툴 프레임 오프셋[6]으로 바꾼다 —
    /// 카메라가 영상 (+X, +Y) 방향으로 움직인다. 검사캠 시프트(⑦⁺⑪⁺, 영상 −오프셋)와
    /// depth 카메라 센터 평행이동(④⁺⁺, 영상 +오프셋)이 공유해 두 이동이 정확히 서로를 상쇄한다.
    /// </summary>
    public static double[] ImageShiftToToolOffset(ToolAxisDir imageXAxis, ToolAxisDir imageYAxis, double xMm, double yMm)
    {
        var offset = new double[6];
        FlatSurfaceCenteringService.ApplyAxis(offset, imageXAxis, xMm);
        FlatSurfaceCenteringService.ApplyAxis(offset, imageYAxis, yMm);
        return offset;
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
    /// ⑤~⑫ 공통 선행조건 — 코봇/카메라 연결, IR 프레임, (RGB 입력이면) 컬러 프레임·Depth↔컬러 캘리브레이션, 검출기 가용성.
    /// ParameterService 가 비동기라 pitch 검사는 여기 넣지 않고 ExecuteAsync 앞부분에서 한다.
    /// </summary>
    public static StepValidation ValidateCommon(
        CobotService cobot, CameraService camera, WeldTrackingService weld, bool requireDetector)
    {
        if (!cobot.IsConnected)
            return StepValidation.Fail("코봇 RPC 미연결");
        if (!camera.IsConnected)
            return StepValidation.Fail("카메라 미연결");
        // IR 프레임은 입력 모드와 무관하게 필요하다 — ROI·센터링·mm 환산의 위치 기준이 IR(=Depth) 좌표다.
        if (camera.LatestIr is null)
            return StepValidation.Fail("IR 프레임 없음 — IR 스트림 활성 상태를 확인하세요.");
        // RGB 입력: 비드는 컬러 영상에서 찾고, IR 기준 ROI·중심선을 공장 캘리브레이션으로 컬러에 투영한다.
        if (weld.Params.Mode != WeldImageMode.Ir)
        {
            if (camera.LatestColor is null)
                return StepValidation.Fail("컬러 프레임 없음 — RGB 입력 모드는 컬러 스트림이 필요합니다.");
            if (camera.GetD2CParams() is not { IsValid: true })
                return StepValidation.Fail("Depth↔컬러 캘리브레이션 없음 — RGB 입력 모드를 쓸 수 없습니다(IR 모드로 전환하거나 카메라 연결 확인).");
        }
        if (requireDetector && !weld.DetectorAvailable)
            return StepValidation.Fail("비드 검출기 비활성 (OpenCV 네이티브 없음 — Windows 전용)");

        return StepValidation.Ok();
    }
}
