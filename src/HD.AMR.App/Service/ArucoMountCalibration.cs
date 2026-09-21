using HD.AMR.App.Communication;
using HD.AMR.App.Models;

namespace HD.AMR.App.Service;

/// <summary>W_A·A_B·B_T·T_C·C_Q = W_Q 제약으로 장착 변환과 미지의 바닥 마커 pose를 동시 추정한다.</summary>
public static class ArucoMountCalibration
{
    private const double RotScaleMmPerDeg = 8.0;

    public static ArucoMountCalibrationResult Solve(IReadOnlyList<ArucoMountSample> samples,
        double[] toolToCamera, double markerQzMm, double[] initialMount)
    {
        if (samples.Count < 8) return ArucoMountCalibrationResult.Fail("최소 8개 표본이 필요합니다(권장 15개 이상).");
        double yawSpan = CircularSpan(samples.Select(s => s.AmrPoseWA[5]));
        double posSpan = PositionSpan(samples);
        if (yawSpan < 20) return ArucoMountCalibrationResult.Fail("AMR yaw 범위가 20° 미만입니다. 방향을 바꿔 표본을 추가하세요.");
        if (posSpan < 150) return ArucoMountCalibrationResult.Fail("AMR 위치 범위가 150 mm 미만입니다. 마커 주변의 다른 위치에서 촬영하세요.");

        var tc = FrameMath.PoseToMatrix(toolToCamera);
        var predicted = samples.Select(s => ComposeMarker(s, initialMount, tc)).ToList();
        double mx = predicted.Average(m => m[0, 3]), my = predicted.Average(m => m[1, 3]);
        double sy = predicted.Sum(m => m[1, 0]), cy = predicted.Sum(m => m[0, 0]);
        var x = initialMount.Concat(new[] { mx, my, Math.Atan2(sy, cy) * 180 / Math.PI }).ToArray();

        double lambda = 1e-3;
        double cost = Cost(samples, tc, markerQzMm, x);
        for (int iter = 0; iter < 60; iter++)
        {
            var r = Residuals(samples, tc, markerQzMm, x);
            var j = NumericJacobian(samples, tc, markerQzMm, x, r);
            var a = new double[9, 9]; var b = new double[9];
            for (int row = 0; row < r.Length; row++)
            {
                double w = HuberWeight(r[row], row % 6 < 3 ? 30 : 3 * RotScaleMmPerDeg);
                for (int p = 0; p < 9; p++)
                {
                    b[p] -= w * j[row, p] * r[row];
                    for (int q = 0; q < 9; q++) a[p, q] += w * j[row, p] * j[row, q];
                }
            }
            for (int p = 0; p < 9; p++) a[p, p] += lambda;
            var dx = SolveLinear(a, b);
            if (dx is null) return ArucoMountCalibrationResult.Fail("정규방정식이 특이합니다. AMR 위치와 yaw를 더 다양하게 바꾸세요.");
            var trial = x.Zip(dx, (v, d) => v + d).ToArray();
            for (int p = 3; p < 6; p++) trial[p] = Normalize(trial[p]);
            trial[8] = Normalize(trial[8]);
            double next = Cost(samples, tc, markerQzMm, trial);
            if (next < cost) { x = trial; if (Math.Abs(cost - next) < 1e-7) break; cost = next; lambda = Math.Max(1e-8, lambda / 3); }
            else lambda = Math.Min(1e8, lambda * 10);
        }

        var raw = Residuals(samples, tc, markerQzMm, x, scaleRotation: false);
        var trans = new List<double>(); var rot = new List<double>();
        for (int i = 0; i < raw.Length; i += 6)
        {
            trans.Add(Math.Sqrt(raw[i] * raw[i] + raw[i + 1] * raw[i + 1] + raw[i + 2] * raw[i + 2]));
            rot.Add(Math.Sqrt(raw[i + 3] * raw[i + 3] + raw[i + 4] * raw[i + 4] + raw[i + 5] * raw[i + 5]));
        }
        var warnings = new List<string>();
        if (samples.Count < 15) warnings.Add("표본 15개 이상을 권장합니다.");
        if (yawSpan < 60) warnings.Add("AMR yaw 범위를 60° 이상 확보하면 안정성이 좋아집니다.");
        if (samples.Average(s => s.ReprojectionErrorPx) > 1.5) warnings.Add("ArUco 평균 재투영 오차가 1.5 px를 초과합니다.");
        var mount = x.Take(6).ToArray();
        var marker = new[] { x[6], x[7], markerQzMm, 0.0, 0.0, x[8] };
        return new(true, null, mount, marker, Rms(trans), Rms(rot), trans.Max(), yawSpan, posSpan, samples.Count, warnings);
    }

    private static double[,] ComposeMarker(ArucoMountSample s, double[] mount, double[,] tc) =>
        FrameMath.Multiply(FrameMath.Multiply(FrameMath.Multiply(FrameMath.Multiply(
            FrameMath.PoseToMatrix(s.AmrPoseWA), FrameMath.PoseToMatrix(mount)),
            FrameMath.PoseToMatrix(s.ToolPoseBT)), tc), FrameMath.PoseToMatrix(s.CameraPoseCQ));

    private static double[] Residuals(IReadOnlyList<ArucoMountSample> ss, double[,] tc, double qz, double[] x, bool scaleRotation = true)
    {
        var target = FrameMath.PoseToMatrix(new[] { x[6], x[7], qz, 0.0, 0.0, x[8] });
        var inv = FrameMath.Invert(target); var r = new double[ss.Count * 6];
        for (int n = 0; n < ss.Count; n++)
        {
            var e = FrameMath.Multiply(inv, ComposeMarker(ss[n], x, tc));
            r[n * 6] = e[0, 3]; r[n * 6 + 1] = e[1, 3]; r[n * 6 + 2] = e[2, 3];
            var rv = RotationVectorDeg(e);
            for (int k = 0; k < 3; k++) r[n * 6 + 3 + k] = rv[k] * (scaleRotation ? RotScaleMmPerDeg : 1);
        }
        return r;
    }

    private static double[,] NumericJacobian(IReadOnlyList<ArucoMountSample> ss, double[,] tc, double qz, double[] x, double[] r)
    {
        var j = new double[r.Length, 9];
        for (int p = 0; p < 9; p++)
        {
            double h = p is 3 or 4 or 5 or 8 ? 0.001 : 0.01;
            var y = (double[])x.Clone(); y[p] += h;
            var rr = Residuals(ss, tc, qz, y);
            for (int i = 0; i < r.Length; i++) j[i, p] = (rr[i] - r[i]) / h;
        }
        return j;
    }

    private static double Cost(IReadOnlyList<ArucoMountSample> s, double[,] tc, double qz, double[] x)
        => Residuals(s, tc, qz, x).Sum(v => v * v);
    private static double HuberWeight(double v, double d) => Math.Abs(v) <= d ? 1 : d / Math.Abs(v);
    private static double Rms(IEnumerable<double> v) { var a = v.ToArray(); return Math.Sqrt(a.Sum(x => x * x) / a.Length); }
    private static double Normalize(double d) { while (d > 180) d -= 360; while (d <= -180) d += 360; return d; }
    private static double PositionSpan(IReadOnlyList<ArucoMountSample> s)
    { double max = 0; for (int i = 0; i < s.Count; i++) for (int j = i + 1; j < s.Count; j++) { double dx = s[i].AmrPoseWA[0] - s[j].AmrPoseWA[0], dy = s[i].AmrPoseWA[1] - s[j].AmrPoseWA[1]; max = Math.Max(max, Math.Sqrt(dx * dx + dy * dy)); } return max; }
    private static double CircularSpan(IEnumerable<double> values)
    { var a = values.Select(v => (Normalize(v) + 360) % 360).Order().ToArray(); if (a.Length < 2) return 0; double gap = a[0] + 360 - a[^1]; for (int i = 1; i < a.Length; i++) gap = Math.Max(gap, a[i] - a[i - 1]); return 360 - gap; }
    private static double[] RotationVectorDeg(double[,] m)
    {
        double angle = Math.Acos(Math.Clamp((m[0, 0] + m[1, 1] + m[2, 2] - 1) / 2, -1, 1));
        if (angle < 1e-10) return new double[3];
        double d = 2 * Math.Sin(angle), f = angle * 180 / Math.PI / d;
        return new[] { (m[2, 1] - m[1, 2]) * f, (m[0, 2] - m[2, 0]) * f, (m[1, 0] - m[0, 1]) * f };
    }
    private static double[]? SolveLinear(double[,] a, double[] b)
    {
        int n = b.Length; var m = (double[,])a.Clone(); var x = (double[])b.Clone();
        for (int k = 0; k < n; k++)
        { int p = k; for (int i = k + 1; i < n; i++) if (Math.Abs(m[i, k]) > Math.Abs(m[p, k])) p = i; if (Math.Abs(m[p, k]) < 1e-12) return null;
          if (p != k) { for (int j = k; j < n; j++) (m[k, j], m[p, j]) = (m[p, j], m[k, j]); (x[k], x[p]) = (x[p], x[k]); }
          for (int i = k + 1; i < n; i++) { double f = m[i, k] / m[k, k]; for (int j = k; j < n; j++) m[i, j] -= f * m[k, j]; x[i] -= f * x[k]; } }
        var z = new double[n]; for (int i = n - 1; i >= 0; i--) { double s = x[i]; for (int j = i + 1; j < n; j++) s -= m[i, j] * z[j]; z[i] = s / m[i, i]; } return z;
    }
}
