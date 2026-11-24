namespace HD.AMR.App.Service.Motion;

/// <summary>
/// 코봇 자기 간섭 모델(<see cref="CobotEnvelope"/>)의 설치 치수 — <c>appsettings.json</c> 의 <c>"CobotEnvelope"</c> 섹션.
/// 단위는 모두 mm, 좌표는 차체 기준(+X 전방·+Y 좌측·z 바닥 위)이다. 기본값은 현 장비(2026-11-19):
/// 리프트 제거, 차체 767 위에 높이 160 고정 기둥, 그 위에 코봇 BASE.
/// </summary>
public class CobotEnvelopeSettings
{
    /// <summary>true 면 BASE 가 리프트 위에 있어 스트로크만큼 오르내린다. 리프트를 뗀 지금은 false.</summary>
    public bool LiftInstalled { get; set; }

    /// <summary>차체 윗면 높이(바닥 기준).</summary>
    public double BodyHeightMm { get; set; } = 767;

    /// <summary>차체 중심 → 전단 거리.</summary>
    public double BodyFrontMm { get; set; } = 395;

    /// <summary>차체 중심 → 후단 거리.</summary>
    public double BodyRearMm { get; set; } = 365;

    /// <summary>차체 전체 폭(좌우 대칭).</summary>
    public double BodyWidthMm { get; set; } = 700;

    /// <summary>받침 기둥 높이 — 차체 윗면 → 코봇 BASE 면. 리프트가 있으면 스트로크 0 일 때 높이.</summary>
    public double ColumnHeightMm { get; set; } = 160;

    /// <summary>받침 기둥 정사각 단면 한 변(차체 중심).</summary>
    public double ColumnWidthMm { get; set; } = 200;

    /// <summary>
    /// 바닥 → BASE 면 높이를 직접 줄 때만 쓴다(null 이면 <see cref="BodyHeightMm"/> + <see cref="ColumnHeightMm"/>).
    /// 장착 보정(T_A_B) z 와 맞춰야 할 때 쓴다.
    /// </summary>
    public double? BaseHeightMm { get; set; }

    /// <summary>신호탑 — 차체 중심 뒤로 From~To, 폭, 차체 윗면 위 높이.</summary>
    public double TowerRearFromMm { get; set; } = 77;
    public double TowerRearToMm { get; set; } = 225;
    public double TowerWidthMm { get; set; } = 216;
    public double TowerHeightMm { get; set; } = 46;

    /// <summary>검사 툴 몸체 원통(자기 간섭용) — 플랜지 면에서 광축 방향 길이·반경.</summary>
    public double ToolBodyLengthMm { get; set; } = 293;
    public double ToolBodyRadiusMm { get; set; } = 169;

    /// <summary>검사 툴 전체 외곽 반경(면 여유·바닥 투영용).</summary>
    public double ToolOuterRadiusMm { get; set; } = 245;

    public double EffectiveBaseHeightMm => BaseHeightMm ?? BodyHeightMm + ColumnHeightMm;

    /// <summary>값이 말이 되는지 — 아니면 <see cref="InvalidOperationException"/>.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        void Positive(string name, double v) { if (!(v > 0)) errors.Add($"{name}={v} (0보다 커야 함)"); }
        Positive(nameof(BodyHeightMm), BodyHeightMm);
        Positive(nameof(BodyFrontMm), BodyFrontMm);
        Positive(nameof(BodyRearMm), BodyRearMm);
        Positive(nameof(BodyWidthMm), BodyWidthMm);
        Positive(nameof(ColumnWidthMm), ColumnWidthMm);
        Positive(nameof(ToolBodyLengthMm), ToolBodyLengthMm);
        Positive(nameof(ToolBodyRadiusMm), ToolBodyRadiusMm);
        Positive(nameof(ToolOuterRadiusMm), ToolOuterRadiusMm);
        if (ColumnHeightMm < 0) errors.Add($"{nameof(ColumnHeightMm)}={ColumnHeightMm} (음수 불가)");
        if (TowerHeightMm < 0) errors.Add($"{nameof(TowerHeightMm)}={TowerHeightMm} (음수 불가)");
        if (TowerRearToMm < TowerRearFromMm) errors.Add($"TowerRearToMm({TowerRearToMm}) < TowerRearFromMm({TowerRearFromMm})");
        if (EffectiveBaseHeightMm < BodyHeightMm)
            errors.Add($"BASE 높이({EffectiveBaseHeightMm})가 차체 윗면({BodyHeightMm})보다 낮음");
        if (ToolOuterRadiusMm < ToolBodyRadiusMm)
            errors.Add($"ToolOuterRadiusMm({ToolOuterRadiusMm}) < ToolBodyRadiusMm({ToolBodyRadiusMm})");
        if (errors.Count > 0)
            throw new InvalidOperationException("appsettings \"CobotEnvelope\" 값이 잘못되었습니다 — " + string.Join(", ", errors));
    }
}
