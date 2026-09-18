using System.Globalization;
using System.Text.Json;
using HD.AMR.App.Models;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service;

/// <summary>
/// 맵 정합 캘리브레이션 저장/계산 서비스. 값은 별도 테이블 없이 범용 key/value 저장소
/// (<see cref="ParameterService"/> = Parameters 테이블)에 보관해 재시작 후에도 복원한다.
///
/// 다루는 것: 코봇 장착 오프셋 <b>T_A_B</b>(AMR 차체→코봇 BASE, 상수)와 그 측정 표본,
/// 기준점 대응(도면 p_G ↔ 맵 p_W), 정합 결과 <b>T_W_G</b>(맵↔도면 2D 강체).
/// 계산은 <see cref="MapCalibration"/> 순수 함수에 위임한다.
/// </summary>
public class CalibrationService
{
    private readonly ParameterService _param;
    private readonly ILogger<CalibrationService> _logger;

    public CalibrationService(ParameterService param, ILogger<CalibrationService> logger)
    {
        _param = param;
        _logger = logger;
    }

    // 파라미터 키
    private const string MountKey = "Calib.Mount.Pose";          // JSON double[6] = [x,y,z,rx,ry,rz]
    private const string MountSamplesKey = "Calib.Mount.SamplesJson";
    private const string MountTargetZKey = "Calib.Mount.TargetZmm";   // double — AMR 원점 기준 타깃 높이
    private const string MountSolveKey = "Calib.Mount.SolveJson";     // JSON MountSolveSnapshot
    private const string RefPointsKey = "Calib.MapRef.PointsJson";
    private const string HandEyeKey = "Calib.HandEye.Pose";      // JSON double[6] = [x,y,z,rx,ry,rz]
    private const string QrMarkersKey = "Calib.Qr.MarkersJson";
    private const string QrStopReferenceKey = "Calib.Qr.StopReferenceJson";
    private const string RegThetaKey = "Calib.MapReg.ThetaDeg";
    private const string RegTxKey = "Calib.MapReg.Tx";
    private const string RegTyKey = "Calib.MapReg.Ty";
    private const string RegRmsKey = "Calib.MapReg.RmsMm";
    private const string RegCountKey = "Calib.MapReg.Count";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>표본 간 허용 스트로크 편차(mm). 1mm 편차 = 기울기 0.32° 오차라 사실상 "동일" 을 요구한다.</summary>
    private const double StrokeTolMm = 1.0;

    // ── 코봇 장착 오프셋 T_A_B ──────────────────────────────────────
    /// <summary>저장된 장착 오프셋 [x,y,z,rx,ry,rz](mm/도). 없으면 0 배열.</summary>
    public async Task<double[]> GetMountAsync()
    {
        var raw = await _param.GetAsync(MountKey);
        if (raw is not null)
        {
            try
            {
                var arr = JsonSerializer.Deserialize<double[]>(raw, JsonOpts);
                if (arr is { Length: 6 }) return arr;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "장착 오프셋 역직렬화 실패 — 0으로 폴백"); }
        }
        return new double[6];
    }

    public Task SaveMountAsync(double[] pose)
        => _param.SetAsync(MountKey, JsonSerializer.Serialize(pose),
            "코봇 장착 오프셋 T_A_B [x,y,z,rx,ry,rz] (mm/도, AMR 차체→코봇 BASE)");

    // ── 장착 측정 표본 ──────────────────────────────────────────────
    public async Task<List<MountSample>> GetMountSamplesAsync()
    {
        var raw = await _param.GetAsync(MountSamplesKey);
        if (raw is not null)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<MountSample>>(raw, JsonOpts);
                if (list is not null) return list;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "장착 표본 역직렬화 실패 — 빈 목록"); }
        }
        return new List<MountSample>();
    }

    public Task SaveMountSamplesAsync(List<MountSample> samples)
        => _param.SetAsync(MountSamplesKey, JsonSerializer.Serialize(samples),
            "장착 캘리브레이션 표본(AMR 맵 pose + 코봇 BASE 터치점)");

    /// <summary>
    /// 표본으로 장착 오프셋의 평면 성분(rz, tx, ty)을 추정. 표본 3개 미만이면 null.
    /// <b>레거시 평면 전용 경로(rx=ry=0 가정)</b> — 신규 화면은 <see cref="SolveMount3D"/> 를 쓰세요.
    /// 표본에 Bz 가 없던 구버전 데이터의 폴백 경로로 남겨 둔다.
    /// </summary>
    public MountSolveResult? SolveMount(IEnumerable<MountSample> samples)
    {
        var list = samples.ToList();
        if (list.Count < 3) return null;
        var s = list.Select(m => (m.AmrXmm, m.AmrYmm, m.AmrYawDeg, m.Bx, m.By)).ToList();
        var (phi, tx, ty, rms, n) = MapCalibration.SolveMount2D(s);
        return new MountSolveResult(phi, tx, ty, rms, n);
    }

    /// <summary>
    /// 표본으로 6-DoF 장착 오프셋(T_A_B)을 산출. 예외를 던지지 않고 결과에 성공/실패를 담는다.
    /// 표본 메타데이터 경고(<see cref="MountSampleInspector"/>)를 수치 경고 앞에 덧붙인다.
    /// </summary>
    /// <param name="samples">터치 표본.</param>
    /// <param name="targetZmm">타깃 높이 q_z(AMR 차체 원점 기준, mm). null 이면 tz 미관측으로 보고.</param>
    /// <param name="currentMount">현재 저장된 T_A_B — 주면 변화량을 함께 보고.</param>
    public MountCalibrationResult SolveMount3D(
        IEnumerable<MountSample> samples, double? targetZmm, double[]? currentMount = null)
    {
        var list = samples.ToList();

        // 전제조건: 모든 표본이 같은 텔레스코픽 스트로크여야 한다. 순수 수학 계층은 "한 평면"을
        // 가정하므로 여기서 막지 않으면 스트로크 편차가 조용히 가짜 기울기가 된다.
        var strokes = list.Where(m => m.TelescopicStrokeMm.HasValue)
                          .Select(m => m.TelescopicStrokeMm!.Value).ToList();
        if (strokes.Count > 0)
        {
            double spread = strokes.Max() - strokes.Min();
            if (spread > StrokeTolMm)
                return MountCalibrationResult.Fail(
                    $"표본 간 텔레스코픽 스트로크가 {spread:0.0}mm 다릅니다 — " +
                    "이 편차는 전부 가짜 기울기(rx/ry)로 흡수됩니다. " +
                    "같은 스트로크(완전 하강 권장)에서 다시 표본하세요.");
        }

        var tuples = list.Select(m => (m.AmrXmm, m.AmrYmm, m.AmrYawDeg, m.Bx, m.By, m.Bz)).ToList();
        var result = MapCalibration.SolveMount3D(tuples, targetZmm, currentMount);

        var provenance = MountSampleInspector.Inspect(list);
        if (provenance.Count == 0 || !result.Success) return result;
        return result with { Warnings = provenance.Concat(result.Warnings).ToList() };
    }

    // ── 장착 타깃 높이 q_z ──────────────────────────────────────────
    /// <summary>저장된 타깃 높이(mm). 한 번도 입력하지 않았으면 null — 0 과 구별해야 한다
    /// (AMR 원점이 바닥에 있으면 0 도 정당한 값이다).</summary>
    public Task<double?> GetMountTargetZmmAsync() => _param.GetDoubleAsync(MountTargetZKey);

    public Task SaveMountTargetZmmAsync(double zmm)
        => _param.SetDoubleAsync(MountTargetZKey, zmm,
            "장착 캘리브 타깃 높이 q_z (AMR 차체 원점 기준, mm — 바닥 타깃이면 음수)");

    // ── 마지막 산출 스냅샷 ──────────────────────────────────────────
    /// <summary>마지막 산출 결과. 화면의 "마지막 산출" 표시와 높이 후입력 시 tz 재계산에 쓴다.</summary>
    public async Task<MountSolveSnapshot?> GetMountSolveAsync()
    {
        var raw = await _param.GetAsync(MountSolveKey);
        if (raw is null) return null;
        try { return JsonSerializer.Deserialize<MountSolveSnapshot>(raw, JsonOpts); }
        catch (Exception ex) { _logger.LogWarning(ex, "장착 산출 스냅샷 역직렬화 실패 — 무시"); return null; }
    }

    /// <summary>성공한 산출 결과만 스냅샷으로 저장한다(실패는 남기지 않는다).</summary>
    public Task SaveMountSolveAsync(MountCalibrationResult r)
    {
        if (!r.Success) return Task.CompletedTask;
        var snap = new MountSolveSnapshot(
            DateTime.UtcNow, r.MountPose, r.TzObserved, r.TargetZmm, r.PlaneOffsetDmm,
            r.RmsMm, r.MaxAbsMm, r.PlaneRmsMm, r.PlanarRmsMm, r.PlaneSpanMm,
            r.TiltSigmaDeg, r.YawSpanDeg, r.N, r.Warnings.ToArray());
        return _param.SetAsync(MountSolveKey, JsonSerializer.Serialize(snap),
            "장착 캘리브 마지막 산출 결과(측정값 — 적용값 Calib.Mount.Pose 와 별개)");
    }

    // ── 핸드아이 오프셋 T_T_C (툴 TCP→카메라 광학 프레임) ───────────
    /// <summary>저장된 핸드아이 오프셋 [x,y,z,rx,ry,rz](mm/도). 없으면 0 배열.</summary>
    public async Task<double[]> GetHandEyeAsync()
    {
        var raw = await _param.GetAsync(HandEyeKey);
        if (raw is not null)
        {
            try
            {
                var arr = JsonSerializer.Deserialize<double[]>(raw, JsonOpts);
                if (arr is { Length: 6 }) return arr;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "핸드아이 오프셋 역직렬화 실패 — 0으로 폴백"); }
        }
        return new double[6];
    }

    public Task SaveHandEyeAsync(double[] pose)
        => _param.SetAsync(HandEyeKey, JsonSerializer.Serialize(pose),
            "핸드아이 오프셋 T_T_C [x,y,z,rx,ry,rz] (mm/도, 코봇 툴 TCP→컬러카메라 광학 프레임)");

    // ── QR 마커 등록 ────────────────────────────────────────────────
    public async Task<List<QrMarkerReg>> GetQrMarkersAsync()
    {
        var raw = await _param.GetAsync(QrMarkersKey);
        if (raw is not null)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<QrMarkerReg>>(raw, JsonOpts);
                if (list is not null) return list;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "QR 마커 역직렬화 실패 — 빈 목록"); }
        }
        return new List<QrMarkerReg>();
    }

    public Task SaveQrMarkersAsync(List<QrMarkerReg> markers)
        => _param.SetAsync(QrMarkersKey, JsonSerializer.Serialize(markers),
            "QR 맵 정합 기준 마커(디코딩 텍스트, 도면 좌표, 바닥/벽, 방위각, 크기)");

    public async Task<QrStopReference> GetQrStopReferenceAsync()
    {
        var raw = await _param.GetAsync(QrStopReferenceKey);
        if (raw is not null)
        {
            try
            {
                var value = JsonSerializer.Deserialize<QrStopReference>(raw, JsonOpts);
                if (value is not null) return value;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "QR 정차 기준 역직렬화 실패 — 기본값 사용"); }
        }
        return new QrStopReference();
    }

    public Task SaveQrStopReferenceAsync(QrStopReference reference)
        => _param.SetAsync(QrStopReferenceKey, JsonSerializer.Serialize(reference),
            "QR 기준 AMR 정차 pose 계산 설정(ID, 크기, 목표 T_A_Q, 허용오차)");

    // ── 기준점 대응 ─────────────────────────────────────────────────
    /// <summary>저장된 기준점 대응 목록. 없으면 빈 값 3점.</summary>
    public async Task<List<MapRefPoint>> GetRefPointsAsync()
    {
        var raw = await _param.GetAsync(RefPointsKey);
        if (raw is not null)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<MapRefPoint>>(raw, JsonOpts);
                if (list is { Count: > 0 }) return list;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "기준점 역직렬화 실패 — 기본 3점으로 폴백"); }
        }
        return new List<MapRefPoint> { new(1), new(2), new(3) };
    }

    public Task SaveRefPointsAsync(List<MapRefPoint> points)
        => _param.SetAsync(RefPointsKey, JsonSerializer.Serialize(points),
            "맵 정합 기준점 대응(도면 p_G ↔ 맵 p_W)");

    // ── 정합 결과 T_W_G ─────────────────────────────────────────────
    public async Task<MapRegistration?> GetRegistrationAsync()
    {
        var th = await _param.GetDoubleAsync(RegThetaKey);
        var tx = await _param.GetDoubleAsync(RegTxKey);
        var ty = await _param.GetDoubleAsync(RegTyKey);
        if (th is null || tx is null || ty is null) return null;
        var rms = await _param.GetDoubleAsync(RegRmsKey) ?? 0;
        var cnt = await _param.GetIntAsync(RegCountKey) ?? 0;
        return new MapRegistration(th.Value, tx.Value, ty.Value, rms, cnt);
    }

    public async Task SaveRegistrationAsync(MapRegistration reg)
    {
        await _param.SetDoubleAsync(RegThetaKey, reg.ThetaDeg, "맵 정합 T_W_G 회전(도)");
        await _param.SetDoubleAsync(RegTxKey, reg.Tx, "맵 정합 T_W_G 평행이동 X(mm)");
        await _param.SetDoubleAsync(RegTyKey, reg.Ty, "맵 정합 T_W_G 평행이동 Y(mm)");
        await _param.SetDoubleAsync(RegRmsKey, reg.RmsMm, "맵 정합 잔차 RMS(mm)");
        await _param.SetAsync(RegCountKey, reg.PointCount.ToString(CultureInfo.InvariantCulture),
            "맵 정합에 사용한 점 수");
    }

    /// <summary>맵 좌표가 채워진(HasW) 점들로 정합 계산. 유효점 2개 미만이면 null.</summary>
    public MapRegistration? Compute(IEnumerable<MapRefPoint> points)
    {
        var used = points.Where(p => p.HasW).ToList();
        if (used.Count < 2) return null;
        var g = used.Select(p => (p.Gx, p.Gy)).ToList();
        var w = used.Select(p => (p.Wx, p.Wy)).ToList();
        var (theta, tx, ty, rms) = MapCalibration.SolveRigid2D(g, w);
        return new MapRegistration(theta, tx, ty, rms, used.Count);
    }
}

/// <summary>맵 정합 기준점 한 개. 도면 좌표(Gx,Gy)와 맵 좌표(Wx,Wy)는 모두 mm.
/// <see cref="HasW"/>=false 면 아직 맵 좌표 미캡처.</summary>
public class MapRefPoint
{
    public MapRefPoint() { }
    public MapRefPoint(int index) { Index = index; }

    public int Index { get; set; }
    public double Gx { get; set; }
    public double Gy { get; set; }
    public double Wx { get; set; }
    public double Wy { get; set; }
    public bool HasW { get; set; }
}

/// <summary>
/// 장착 캘리브레이션 표본 한 개. AMR 맵 pose(x,y[mm], yaw[도])와 코봇 BASE 기준 터치점(mm).
/// JSON(<c>Calib.Mount.SamplesJson</c>)으로 보관하므로 nullable 필드 추가는 하위호환이다.
/// </summary>
public class MountSample
{
    public int Index { get; set; }
    public double AmrXmm { get; set; }
    public double AmrYmm { get; set; }
    public double AmrYawDeg { get; set; }
    public double Bx { get; set; }
    public double By { get; set; }
    public double Bz { get; set; }

    /// <summary>기록 시각(UTC). 표본 노후·재장착 판정용. 구버전 표본은 null.</summary>
    public DateTime? CapturedAtUtc { get; set; }

    /// <summary>
    /// <c>GetTcpPoseInBaseAsync</c> 에 넘긴 공구 번호. <b>표본 간 혼용이 가장 위험한 조용한 오염</b> —
    /// tool 0(플랜지)과 tool 1(프로브)을 섞으면 일부 터치점에만 수백 mm 바이어스가 들어가
    /// 그럴듯하지만 틀린 기울기로 위장된다. 사후 수치로는 검출 불가라 기록이 유일한 방어다.
    /// </summary>
    public int? Tool { get; set; }

    /// <summary>기록 시 BASE 기준 TCP 자세(도) — 현재 해에는 미사용, 재분석·감사용.</summary>
    public double? Brx { get; set; }
    public double? Bry { get; set; }
    public double? Brz { get; set; }

    /// <summary>
    /// 기록 시 Z축 텔레스코픽 스트로크(mm, <b>완전 하강 = 0</b>). 구버전 표본은 null.
    ///
    /// 코봇 BASE 가 약 1000mm 행정의 텔레스코픽 위에 있어 <b>T_A_B 의 tz 는 상수가 아니다</b>
    /// (tz(s) = tz0 + s). 산출은 모든 표본이 <b>같은 스트로크</b>에 있다고 가정하므로 —
    /// 다르면 터치점이 한 평면에 놓이지 않고 그 편차가 <b>전부 가짜 기울기로 흡수</b>된다.
    /// 면내 펼침 180mm 기준 스트로크 1mm 편차 = 기울기 0.32° 오차(터치 잡음 1mm 와 같은 크기).
    /// 그래서 <see cref="CalibrationService.SolveMount3D"/> 가 불일치를 계산 전에 거부한다.
    /// </summary>
    public double? TelescopicStrokeMm { get; set; }
}

/// <summary>장착 오프셋 평면 측정 결과 (rz=φ, tx, ty, 잔차, 표본수).</summary>
public record MountSolveResult(double PhiDeg, double Tx, double Ty, double RmsMm, int N);

/// <summary>
/// 장착 산출 결과 스냅샷 — "마지막 산출" 표시 + 타깃 높이 후입력 시 tz 재계산용.
/// <b>측정값</b>이므로 운영자가 손으로 보정할 수 있는 <b>적용값</b>(<c>Calib.Mount.Pose</c>)과 별개로 보관한다.
/// 두 값의 차이는 물리적 재장착 후 가장 유용한 진단 지표다.
/// </summary>
public record MountSolveSnapshot(
    DateTime SolvedAtUtc, double[] MountPose, bool TzObserved, double TargetZmm,
    double PlaneOffsetDmm, double RmsMm, double MaxAbsMm, double PlaneRmsMm, double PlanarRmsMm,
    double PlaneSpanMm, double TiltSigmaDeg, double YawSpanDeg, int N, string[] Warnings);

/// <summary>맵↔도면 2D 강체 정합 결과 T_W_G. w = R(θ)·g + t.</summary>
public record MapRegistration(double ThetaDeg, double Tx, double Ty, double RmsMm, int PointCount);
