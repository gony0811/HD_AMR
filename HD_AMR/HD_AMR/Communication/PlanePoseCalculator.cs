using HD_AMR.Models;

namespace HD_AMR.Communication;

/// <summary>
/// 레이저 변위 센서 3점 측정 → 삼각형 평면 중심의 <b>툴 좌표계</b> pose 계산.
///
/// 3개 헤드가 툴(플랜지)에 강체 고정되어 헤드 XY·빔 방향이 모두 툴 좌표에서 고정이므로, 계산 결과는
/// 로봇 자세(FK) 없이 툴 좌표계에서 성립한다(툴 Z가 평면 법선에 정렬되면 rx=ry≈0). base 좌표가 필요하면
/// 호출측에서 현재 로봇 pose로 툴→base 변환을 곱한다.
///
/// 규약: 빔 = 툴 +Z 평행(헤드는 툴 Z=0 평면). 입력 거리는 출사부→표면 절대거리(mm)이며 표면점 Z = +거리
/// (<c>signForUp</c>=false면 반전). 툴 좌표축은 <b>X+ = 왼쪽(CH1측), Y+ = 아래</b>이며 실측 헤드 배치는
/// CH1=(+18,0), CH2=(0,−32), CH3=(−18,0). 자세는 법선 <c>n</c>(nz>0 정규화)에서 툴축 기울기로 분해한다:
/// <c>Rx=atan2(ny,nz)</c>(Y방향), <c>Ry=atan2(nx,nz)</c>(X방향), <c>Rz=0</c>(3점 거리로는 yaw 미결정).
///
/// 검산: Z=[357.244, 358.599, 357.028] → 중심(0, −10.667, 357.624), Rx≈+2.617°, Ry≈−0.344°, Rz=0.
/// </summary>
public static class PlanePoseCalculator
{
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>
    /// 세 헤드의 툴-XY(<paramref name="hx"/>/<paramref name="hy"/>, mm, 0=Ch1,1=Ch2,2=Ch3)와 거리
    /// <paramref name="d"/>(mm, 출사부→표면)로 삼각형 평면 중심의 <b>툴 좌표계 pose</b> [x,y,z,rx,ry,rz]를 계산한다.
    /// 빔 = 툴 +Z 평행, 헤드는 툴 Z=0 평면 가정. 중심 z = <paramref name="standoffMm"/>(출사부 평면의 툴 Z 오프셋,
    /// 보통 0) + 부호적용 거리평균. 자세는 법선(nz>0)에서 툴축 기울기로 분해: Rx=atan2(ny,nz), Ry=atan2(nx,nz), Rz=0.
    /// </summary>
    /// <param name="signForUp">거리 +가 툴 +Z 방향(표면점 Z 증가)에 대응하면 true, 반대면 false.</param>
    public static PlanePose ComputePose(
        double[] hx, double[] hy, double[] d, double standoffMm, bool signForUp)
    {
        if (hx.Length < 3 || hy.Length < 3 || d.Length < 3)
            return PlanePose.Invalid("헤드 좌표/측정값이 3개 미만입니다.");

        double zSign = signForUp ? 1.0 : -1.0;

        // 표면점(툴): 빔 ∥ 툴 +Z, 헤드 툴 Z=0 → Z = standoff + 부호적용 거리.
        var p1 = new[] { hx[0], hy[0], standoffMm + zSign * d[0] };
        var p2 = new[] { hx[1], hy[1], standoffMm + zSign * d[1] };
        var p3 = new[] { hx[2], hy[2], standoffMm + zSign * d[2] };

        // 중심 = 세 점의 무게중심. XY는 헤드 XY 평균(빔 평행이라 거리 무관).
        double cx = (hx[0] + hx[1] + hx[2]) / 3.0;
        double cy = (hy[0] + hy[1] + hy[2]) / 3.0;
        double cz = (p1[2] + p2[2] + p3[2]) / 3.0;

        // 법선: nz>0(일관 방향)으로 정규화. 이 헤드 배치에서 (P3−P1)×(P2−P1) 의 nz = 헤드 XY 외적 = 항상 양(+).
        var n = Cross(Sub(p3, p1), Sub(p2, p1));
        double nmag = Math.Sqrt(Dot(n, n));
        if (nmag < 1e-9)
            return PlanePose.Invalid("세 점이 일직선이거나 헤드 간격이 0입니다. 헤드 좌표 설정을 확인하세요.");
        n = new[] { n[0] / nmag, n[1] / nmag, n[2] / nmag };
        if (n[2] < 0) n = new[] { -n[0], -n[1], -n[2] };

        // 툴 좌표축(X+ 왼쪽, Y+ 아래) 기준 독립 기울기 분해. 3점 거리로는 yaw(rz) 미결정 → 0.
        // 평면 z=ax+by+c 의 법선 n ∝ (−a,−b,1) 이므로 atan2(ny,nz)=−atan(∂z/∂y), atan2(nx,nz)=−atan(∂z/∂x).
        double rx = Math.Atan2(n[1], n[2]) * Rad2Deg;   // 툴 X축 회전(Y방향 기울기). Y−(위)쪽이 멀수록 +
        double ry = Math.Atan2(n[0], n[2]) * Rad2Deg;   // 툴 Y축 회전(X방향 기울기). X+(왼쪽)쪽이 멀수록 −
        double rz = 0.0;

        return new PlanePose(cx, cy, cz, rx, ry, rz, n, true, null);
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

    private static double[] Cross(double[] a, double[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    };
}
