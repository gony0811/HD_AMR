namespace HD.AMR.App.Service;

public enum SettleResult
{
    /// <summary>허용 오차 밖 — 아직 도착 아님.</summary>
    OutOfTolerance,
    /// <summary>허용 오차 안이지만 아직 움직이는 중(또는 정차 유지 시간 미달).</summary>
    Moving,
    /// <summary>허용 오차 안에서 pose 가 정차 유지 시간 이상 변하지 않음 — 완전 정차.</summary>
    Settled,
}

/// <summary>
/// AMR 완전 정차 판정 — 허용 오차 진입만으로는 감속 중일 수 있어(현장: 진입 1초 뒤 126 mm 더 진행, θ 5.4° 변화)
/// pose 가 ε 이내로 <c>window</c> 이상 유지돼야 정차로 본다. 벤더 진행 신호(schedule·WorkStatus)는 의미가
/// 미확정이라 쓰지 않는다.
///
/// 샘플 시각이 같은 입력은 무시한다 — Modbus 상태는 1초 주기 캐시라 갱신 전 같은 값을 "멈춤"으로 오인하지 않기 위해.
/// </summary>
public sealed class ArrivalSettleDetector
{
    private readonly TimeSpan _window;
    private readonly double _epsXy;
    private readonly double _epsTheta;

    private DateTime? _lastSampleUtc;
    private SettleResult _last = SettleResult.OutOfTolerance;
    private DateTime _anchorUtc;
    private (double X, double Y, double Theta)? _anchor;

    /// <summary>이번 진입(허용 오차 안 연속 구간)의 첫 샘플 시각.</summary>
    public DateTime? FirstInToleranceUtc { get; private set; }

    /// <summary>이번 진입의 첫 pose — "진입 후 정지까지 이동량" 로그용.</summary>
    public (double X, double Y, double Theta)? FirstInTolerancePose { get; private set; }

    /// <summary>마지막으로 반영한 pose.</summary>
    public (double X, double Y, double Theta)? LastPose { get; private set; }

    public ArrivalSettleDetector(TimeSpan window, double epsXy, double epsTheta)
    {
        _window = window < TimeSpan.Zero ? TimeSpan.Zero : window;
        _epsXy = Math.Max(0, epsXy);
        _epsTheta = Math.Max(0, epsTheta);
    }

    public SettleResult Feed(DateTime sampleUtc, double x, double y, double theta, bool withinTolerance)
    {
        if (_lastSampleUtc == sampleUtc)
            return _last;
        _lastSampleUtc = sampleUtc;
        LastPose = (x, y, theta);

        if (!withinTolerance)
        {
            ClearEntry();
            return _last = SettleResult.OutOfTolerance;
        }

        if (FirstInToleranceUtc is null)
        {
            FirstInToleranceUtc = sampleUtc;
            FirstInTolerancePose = (x, y, theta);
        }

        if (_anchor is not { } a || Moved(a, x, y, theta))
        {
            _anchor = (x, y, theta);
            _anchorUtc = sampleUtc;
            return _last = _window == TimeSpan.Zero ? SettleResult.Settled : SettleResult.Moving;
        }

        return _last = sampleUtc - _anchorUtc >= _window ? SettleResult.Settled : SettleResult.Moving;
    }

    /// <summary>처음부터 다시 판정(일시정지 후 재주행 등).</summary>
    public void Reset()
    {
        _lastSampleUtc = null;
        LastPose = null;
        ClearEntry();
        _last = SettleResult.OutOfTolerance;
    }

    /// <summary>진입 시점 대비 마지막 pose 의 이동량(m, rad). 진입 전이면 null.</summary>
    public (double Dxy, double DTheta)? DriftSinceEntry()
    {
        if (FirstInTolerancePose is not { } f || LastPose is not { } l) return null;
        return (Math.Sqrt((l.X - f.X) * (l.X - f.X) + (l.Y - f.Y) * (l.Y - f.Y)), Math.Abs(AngleDiff(l.Theta, f.Theta)));
    }

    private void ClearEntry()
    {
        _anchor = null;
        FirstInToleranceUtc = null;
        FirstInTolerancePose = null;
    }

    private bool Moved((double X, double Y, double Theta) a, double x, double y, double theta)
    {
        var dxy = Math.Sqrt((x - a.X) * (x - a.X) + (y - a.Y) * (y - a.Y));
        return dxy > _epsXy || Math.Abs(AngleDiff(theta, a.Theta)) > _epsTheta;
    }

    private static double AngleDiff(double a, double b) => Math.IEEERemainder(a - b, 2 * Math.PI);
}
