namespace HD.AMR.App.Service;

/// <summary>
/// AMR REST status.error 항목의 경고(W…)/오류(E…) 구분과, 경고 뒤 정체 판정.
///
/// AMR 은 경고도 오류와 같은 error 배열에 싣는다(2026-11-25 현장: W07_001 드라이버 오버코스트,
/// W12_005 모터 전진 제한됨, W12_007 모터 회전 제한됨, W12_008 경로 막힘(라이다)). 종전에는 새 항목이면
/// 무엇이든 임무 실패로 끝내 하루 주행 실패 5건 중 4건이 경고였다. 경고는 주행을 끝내지 않고 기록만 하되,
/// 경고 뒤 AMR 이 그대로 서 버리면 주행 타임아웃(600s)까지 기다리지 않도록 정체를 따로 본다.
/// </summary>
public static class AmrDriveWarning
{
    /// <summary>경고 뒤 pose 가 이 시간 동안 그대로면 주행 실패로 본다(초).</summary>
    public const double StallSec = 30.0;

    /// <summary>정체 판정 — 이만큼 움직이면 "움직였다"(m).</summary>
    public const double StallMoveM = 0.005;

    /// <summary>정체 판정 — 이만큼 돌면 "움직였다"(rad, ≈0.3°).</summary>
    public const double StallTurnRad = 0.005;

    /// <summary>ACS 에 올리는 errorType(WARNING).</summary>
    public const string ErrorType = "amrDriveWarning";

    /// <summary>
    /// error 항목 키(code|timestamp) 또는 코드가 경고인가 — 코드가 'W' + 숫자로 시작하면 경고.
    /// 코드가 없거나(원문 키) 다른 글자로 시작하면 오류로 본다(모르는 것은 종전대로 멈춘다).
    /// </summary>
    public static bool IsWarning(string? keyOrCode)
    {
        if (string.IsNullOrWhiteSpace(keyOrCode)) return false;
        var code = keyOrCode.Trim();
        return code.Length >= 2 && (code[0] == 'W' || code[0] == 'w') && char.IsDigit(code[1]);
    }

    /// <summary>경고 뒤 정체 감시 — 경고가 한 번 뜬 뒤부터 pose 가 얼마나 오래 그대로인지 센다.</summary>
    public sealed class StallWatch
    {
        private (double X, double Y, double Theta)? _ref;
        private DateTime _refAt;

        /// <summary>감시 중인가(경고가 떴는가).</summary>
        public bool Armed { get; private set; }

        /// <summary>경고 발생 — 감시를 켠다(이미 켜져 있으면 기준을 유지한다).</summary>
        public void Arm(DateTime now)
        {
            if (Armed) return;
            Armed = true;
            _ref = null;
            _refAt = now;
        }

        /// <summary>재지령 등으로 처음부터 다시 볼 때.</summary>
        public void Reset()
        {
            Armed = false;
            _ref = null;
        }

        /// <summary>pose 1건 반영 — 감시 중이고 <see cref="StallSec"/> 넘게 그대로면 true.</summary>
        public bool Feed(DateTime now, double x, double y, double theta)
        {
            if (!Armed) return false;
            if (_ref is not { } r)
            {
                _ref = (x, y, theta);
                _refAt = now;
                return false;
            }
            var dxy = Math.Sqrt((x - r.X) * (x - r.X) + (y - r.Y) * (y - r.Y));
            var dTheta = Math.Abs(Math.Atan2(Math.Sin(theta - r.Theta), Math.Cos(theta - r.Theta)));
            if (dxy > StallMoveM || dTheta > StallTurnRad)
            {
                _ref = (x, y, theta);
                _refAt = now;
                return false;
            }
            return (now - _refAt).TotalSeconds >= StallSec;
        }

        /// <summary>정체 기준을 버린다(감시는 유지) — 허용 오차 안에서 정차 판정이 맡는 동안 호출해,
        /// 다시 밖으로 나왔을 때 묵은 기준으로 곧바로 정체 판정하지 않게 한다.</summary>
        public void Rebase() => _ref = null;
    }
}
