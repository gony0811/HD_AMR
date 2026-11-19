using System.Globalization;

namespace HD.AMR.App.Service.Sequence.Steps;

/// <summary>
/// 점1 을 사람이 그은 비드선(수동 입력)으로 잡았을 때, 점2 에서 <b>자동으로</b> 찾은 비드와 대조해 점1 의 선이
/// 실제 비드였는지 확인한다.
///
/// 점2 는 점1 에서 진행축으로만 옮겨 간 자리라, 점1 이 비드 위에 있었다면 점2 의 비드 이격 d2 는
/// 용접선 방향 오차(교시 간격 × 각도)만큼만 생긴다. 점1 에서 비드가 아닌 다른 선(판 겹침 경계·반사 등)을
/// 그었으면 그 선과 비드 사이 거리가 d2 에 그대로 남는다.
///
/// 실기 근거(2026-11-11~19 로그, 점1 수동 + 점2 자동 66건): 맞게 그은 경우 |d2| ≤ 29.1mm(간격 706mm),
/// 틀리게 그은 경우 |d2| = 48.2~111.5mm — 간격(337~1437mm)과 무관하게 거의 일정했다(선 사이 거리라서).
/// 점1 자동 101건의 |d2| 는 최대 39.8mm(간격 704mm, 3.2°)·78.6mm 1건(PU).
/// 기본 허용 = max(35mm, 간격 × tan 1.5°).
/// </summary>
public static class ManualPoint1Check
{
    public const string EnabledKey = "Sequence.Bead.ManualPoint1Check";
    public const string MinMmKey = "Sequence.Bead.ManualPoint1CheckMm";
    public const string AngleDegKey = "Sequence.Bead.ManualPoint1CheckDeg";

    public const double DefaultMinMm = 35.0;
    public const double DefaultAngleDeg = 1.5;

    /// <summary>허용 이격 [mm] = max(<paramref name="minMm"/>, 간격 × tan(<paramref name="angleDeg"/>)).</summary>
    public static double AllowedMm(double spanMm, double minMm, double angleDeg)
        => Math.Max(minMm, Math.Abs(spanMm) * Math.Tan(angleDeg * Math.PI / 180.0));

    /// <summary>
    /// 문제가 있으면 사유, 없으면 null. <paramref name="d2Mm"/> = 점2 에서 자동으로 잰 비드 이격(cross, mm),
    /// <paramref name="spanMm"/> = 점1(비드 센터링 자세) → 점2 측정 자세 거리.
    /// </summary>
    public static string? Problem(double d2Mm, double spanMm, double minMm, double angleDeg, string? point1Note = null)
    {
        if (!double.IsFinite(d2Mm) || !double.IsFinite(spanMm) || spanMm <= 0) return null;
        var allowed = AllowedMm(spanMm, minMm, angleDeg);
        if (Math.Abs(d2Mm) <= allowed) return null;

        var tiltDeg = Math.Atan(Math.Abs(d2Mm) / spanMm) * 180.0 / Math.PI;
        var note = string.IsNullOrWhiteSpace(point1Note) ? "" : $" — 점1 입력:{point1Note}";
        return string.Create(CultureInfo.InvariantCulture,
            $"점1 수동 입력 선 의심 — 점2 에서 자동으로 찾은 비드가 점1 에서 그은 선에서 {Math.Abs(d2Mm):0.0}mm 벗어나 있습니다 " +
            $"(허용 {allowed:0.0}mm = max({minMm:0.#}mm, 간격 {spanMm:0}mm × tan {angleDeg:0.#}°)). " +
            $"점1 에서 비드가 아닌 다른 선(판 겹침 경계·반사 등)을 그었을 가능성이 큽니다 — 이대로면 작업물 좌표계가 " +
            $"{tiltDeg:0.00}° 기울어진 채 순회합니다. 점1 수동 입력 영상을 확인하고, 잘못 그은 선이면 그 학습 데이터를 지운 뒤 " +
            $"다시 실행하세요(점검 끄기: {EnabledKey}=false){note}");
    }

    /// <summary>파라미터 시드 행(ParameterService.SeedDefaultsAsync 가 없는 키만 추가).</summary>
    public static IEnumerable<(string Name, string Value, string Desc)> SeedDefaults() =>
    [
        (EnabledKey, "true",
            "Bead2 찾기 — 점1 을 수동 입력(사람이 그은 비드선)으로 잡았으면, 점2 에서 자동으로 찾은 비드 이격과 대조해 " +
            "점1 의 선이 실제 비드였는지 확인한다(true). 어긋나면 작업물 좌표계를 만들기 전에 그 용접선을 실패 처리. " +
            "점2 도 수동 입력이면 대조하지 않는다."),
        (MinMmKey, DefaultMinMm.ToString(CultureInfo.InvariantCulture),
            "점1 수동 입력 대조 — 점2 비드 이격 허용 최소값(mm). 허용 = max(이 값, 교시 간격 × tan(각도)). " +
            "실기: 맞게 그은 경우 ≤29mm, 다른 선을 그은 경우 48~112mm."),
        (AngleDegKey, DefaultAngleDeg.ToString(CultureInfo.InvariantCulture),
            "점1 수동 입력 대조 — 교시 간격에 비례해 늘리는 허용 각도(°). 용접선 가로 정렬 오차를 흡수한다."),
    ];
}
