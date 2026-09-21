namespace HD.AMR.App.Service;

/// <summary>
/// 장착 캘리브레이션 표본의 <b>메타데이터</b> 점검 — 수치 산출(<see cref="MapCalibration.SolveMount3D"/>)과
/// 무관한, 사후 수치로는 검출할 수 없는 오염만 본다. DB 의존이 없어 단위 테스트가 쉽다.
///
/// 잔차로 잡히지 않는 것들이 요점이다: 공구 번호를 섞어 기록하면 일부 터치점에만 수백 mm
/// 바이어스가 들어가고, 그 결과는 잔차가 작은 "그럴듯하지만 틀린" 기울기로 위장된다.
/// </summary>
public static class MountSampleInspector
{
    private const double DuplicatePosMm = 50.0;
    private const double DuplicateYawDeg = 3.0;
    private const int StaleDays = 30;
    private const int SpanDays = 7;

    /// <summary>표본 메타데이터 점검 결과(한국어 경고). 문제가 없으면 빈 목록.</summary>
    public static IReadOnlyList<string> Inspect(IReadOnlyList<MountSample> samples)
    {
        var warnings = new List<string>();
        if (samples.Count == 0) return warnings;

        // ① 공구 번호 혼용 — 가장 위험하고, 수치로는 사후 검출 불가.
        var tools = samples.Where(s => s.Tool.HasValue).Select(s => s.Tool!.Value).Distinct().OrderBy(v => v).ToList();
        if (tools.Count > 1)
            warnings.Add($"표본 간 코봇 공구 번호가 다릅니다({string.Join(", ", tools)}) — " +
                         "BASE 기준 터치점이 서로 다른 TCP 로 기록되어 있습니다. 전체 삭제 후 재기록을 권장합니다.");

        // ② 구버전 표본(Bz 미기록) — 비트 정확히 0 이면 기록 누락으로 본다.
        if (samples.All(s => s.Bz == 0.0))
            warnings.Add("모든 표본의 BASE Z 가 0 입니다 — Bz 를 기록하지 않던 구버전 표본일 수 있습니다. " +
                         "rx/ry 는 0 으로 산출되니 재기록을 권장합니다.");

        // ③ 사실상 같은 AMR 자세 — 정보 중복.
        for (var i = 0; i < samples.Count; i++)
        {
            for (var j = i + 1; j < samples.Count; j++)
            {
                double dx = samples[i].AmrXmm - samples[j].AmrXmm;
                double dy = samples[i].AmrYmm - samples[j].AmrYmm;
                double dyaw = Math.Abs(MapCalibration.NormalizeDeg(samples[i].AmrYawDeg - samples[j].AmrYawDeg));
                if (Math.Sqrt(dx * dx + dy * dy) < DuplicatePosMm && dyaw < DuplicateYawDeg)
                    warnings.Add($"표본 #{i + 1}/#{j + 1} 가 거의 같은 AMR 자세입니다 — " +
                                 "정보가 중복되어 정밀도에 기여하지 않습니다.");
            }
        }

        // ④ 텔레스코픽 스트로크 미기록 — 산출은 전 표본이 같은 스트로크라고 가정한다.
        if (samples.All(s => !s.TelescopicStrokeMm.HasValue))
            warnings.Add("텔레스코픽 스트로크가 기록되지 않은 표본입니다 — 전 표본이 같은 높이(완전 하강)에서 " +
                         "기록됐는지 확인하세요. 스트로크 1mm 편차가 기울기 0.32° 오차가 됩니다.");
        else if (samples.Any(s => !s.TelescopicStrokeMm.HasValue))
            warnings.Add("일부 표본에만 텔레스코픽 스트로크가 기록돼 있습니다 — 미기록 표본이 다른 높이에서 " +
                         "잡혔다면 기울기가 조용히 틀어집니다. 전체 삭제 후 재기록을 권장합니다.");

        // ⑤ 표적 높이 미기록 — 같은 표적이었는지 확인할 근거가 없다.
        if (samples.All(s => !s.TargetZmm.HasValue))
            warnings.Add("표적 높이가 기록되지 않은 표본입니다 — 전 표본이 같은 점을 터치했는지 확인할 수 없습니다. " +
                         "표적을 옮긴 적이 있으면 전체 삭제 후 재기록하세요.");
        else if (samples.Any(s => !s.TargetZmm.HasValue))
            warnings.Add("일부 표본에만 표적 높이가 기록돼 있습니다 — 미기록 표본이 다른 표적일 수 있습니다. " +
                         "전체 삭제 후 재기록을 권장합니다.");

        // ⑥ 표본 노후·기간 분산 — 그 사이 재장착이 있었다면 섞어 쓰면 안 된다.
        var stamped = samples.Where(s => s.CapturedAtUtc.HasValue).Select(s => s.CapturedAtUtc!.Value).ToList();
        if (stamped.Count > 0)
        {
            var newest = stamped.Max();
            var oldest = stamped.Min();
            double ageDays = (DateTime.UtcNow - newest).TotalDays;
            double spanDays = (newest - oldest).TotalDays;
            if (ageDays > StaleDays)
                warnings.Add($"가장 최근 표본이 {ageDays:0}일 전입니다 — 그 사이 코봇을 재장착했다면 " +
                             "전체 삭제 후 재기록하세요.");
            else if (spanDays > SpanDays)
                warnings.Add($"표본 기록 시점이 {spanDays:0}일에 걸쳐 있습니다 — 그 사이 코봇을 재장착했다면 " +
                             "전체 삭제 후 재기록하세요.");
        }

        return warnings;
    }
}
