using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HD_AMR.Service;

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
    private const string RefPointsKey = "Calib.MapRef.PointsJson";
    private const string RegThetaKey = "Calib.MapReg.ThetaDeg";
    private const string RegTxKey = "Calib.MapReg.Tx";
    private const string RegTyKey = "Calib.MapReg.Ty";
    private const string RegRmsKey = "Calib.MapReg.RmsMm";
    private const string RegCountKey = "Calib.MapReg.Count";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

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

    /// <summary>표본으로 장착 오프셋의 평면 성분(rz, tx, ty)을 추정. 표본 3개 미만이면 null.</summary>
    public MountSolveResult? SolveMount(IEnumerable<MountSample> samples)
    {
        var list = samples.ToList();
        if (list.Count < 3) return null;
        var s = list.Select(m => (m.AmrXmm, m.AmrYmm, m.AmrYawDeg, m.Bx, m.By)).ToList();
        var (phi, tx, ty, rms, n) = MapCalibration.SolveMount2D(s);
        return new MountSolveResult(phi, tx, ty, rms, n);
    }

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

/// <summary>장착 캘리브레이션 표본 한 개. AMR 맵 pose(x,y[mm], yaw[도])와 코봇 BASE 기준 터치점(mm).</summary>
public class MountSample
{
    public int Index { get; set; }
    public double AmrXmm { get; set; }
    public double AmrYmm { get; set; }
    public double AmrYawDeg { get; set; }
    public double Bx { get; set; }
    public double By { get; set; }
    public double Bz { get; set; }
}

/// <summary>장착 오프셋 평면 측정 결과 (rz=φ, tx, ty, 잔차, 표본수).</summary>
public record MountSolveResult(double PhiDeg, double Tx, double Ty, double RmsMm, int N);

/// <summary>맵↔도면 2D 강체 정합 결과 T_W_G. w = R(θ)·g + t.</summary>
public record MapRegistration(double ThetaDeg, double Tx, double Ty, double RmsMm, int PointCount);
