using HD_AMR.Models;

namespace HD_AMR.Service.Sequence.Steps;

/// <summary>
/// ⑤~⑫ Peak/Bead 시퀀스 공용 헬퍼. 파라미터 키·깊이 ROI 변환·mm 환산을 한곳에 모은다.
/// (③④는 각자 GetDepthRoiAsync 를 중복 구현하고 있으나, 신규 단계는 여기를 공유한다.)
/// </summary>
internal static class WeldSequenceSupport
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

    /// <summary>영상 +X 픽셀이 BASE +X 인지 −X 인지(+1/−1). 카메라 장착 방향으로 결정된다.</summary>
    public const string XSignKey = "Camera.Axis.XSign";

    public const double DefaultPitchMm = 370.0;

    /// <summary>Peak 찾기 결과를 담는 Bag 키. id 는 1 또는 2.</summary>
    public static string PeakFindBagKey(int id) => $"peak{id}.find";

    /// <summary>Peak 이격거리(mm)를 담는 Bag 키. id 는 1 또는 2.</summary>
    public static string OffsetMmBagKey(int id) => $"peak{id}.offsetMm";

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
        if (f is not null && f.Width > 0 && r.DepthMm > 0)
        {
            var mmPerPx = camera.PixelDeltaToMm(1.0 / f.Width, 0, r.DepthMm).DxMm;
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

    /// <summary>pitch(mm)를 현재 거리 기준 픽셀로 환산. 산출 불가면 0.</summary>
    public static double PitchToPixels(double pitchMm, int depthMm, CameraService camera)
    {
        var f = camera.LatestIr;
        if (f is null || f.Width <= 0 || depthMm <= 0) return 0;
        var mmPerPx = camera.PixelDeltaToMm(1.0 / f.Width, 0, depthMm).DxMm;
        return mmPerPx > 0 ? pitchMm / mmPerPx : 0;
    }

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
