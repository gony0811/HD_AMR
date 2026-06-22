using HD_AMR.Models;

namespace HD_AMR.Communication.Weld;

/// <summary>
/// 공장 캘리브레이션(<see cref="CameraD2CParams"/>)으로 Depth 픽셀 ↔ Color 픽셀을 변환한다(방법 ②).
/// <list type="bullet">
/// <item><see cref="DepthToColor"/>: Depth 픽셀 + 깊이 Z(mm) → Color 픽셀. 외부 파라미터(R,t)로
/// 시차(parallax)까지 정확히 반영한다. Peak 점을 RGB 화면에 올바르게 찍는 데 사용.</item>
/// <item><see cref="MapColorRoiToDepth"/>: Color ROI → Depth ROI. 깊이를 모르는 역방향이라
/// 시차를 무시한 내부 파라미터 비례 근사(분석 영역 지정용이라 약간의 오차 허용).</item>
/// </list>
/// 생성 시 파라미터 기준 해상도와 실제 프레임 해상도가 다르면 내부 파라미터를 스케일한다.
/// </summary>
public sealed class DepthColorMapper
{
    private readonly double _dfx, _dfy, _dcx, _dcy;
    private readonly double _cfx, _cfy, _ccx, _ccy;
    private readonly double[] _r;   // 3×3 row-major (Depth→Color)
    private readonly double[] _t;   // mm

    public DepthColorMapper(CameraD2CParams p, int depthW, int depthH, int colorW, int colorH)
    {
        double sdx = p.DepthW > 0 ? (double)depthW / p.DepthW : 1.0;
        double sdy = p.DepthH > 0 ? (double)depthH / p.DepthH : 1.0;
        double scx = p.ColorW > 0 ? (double)colorW / p.ColorW : 1.0;
        double scy = p.ColorH > 0 ? (double)colorH / p.ColorH : 1.0;

        _dfx = p.DepthFx * sdx; _dcx = p.DepthCx * sdx;
        _dfy = p.DepthFy * sdy; _dcy = p.DepthCy * sdy;
        _cfx = p.ColorFx * scx; _ccx = p.ColorCx * scx;
        _cfy = p.ColorFy * scy; _ccy = p.ColorCy * scy;
        _r = p.Rot; _t = p.Trans;
    }

    /// <summary>Depth 픽셀(u,v)+Z(mm) → Color 픽셀(u,v). Z≤0 또는 변환 후 카메라 뒤면 null.</summary>
    public (double u, double v)? DepthToColor(double u, double v, double zMm)
    {
        if (zMm <= 0) return null;
        // 1) Depth 카메라 좌표계 3D 역투영
        double x = (u - _dcx) / _dfx * zMm;
        double y = (v - _dcy) / _dfy * zMm;
        double z = zMm;
        // 2) Color 카메라 좌표계로 변환 (R·p + t)
        double xc = _r[0] * x + _r[1] * y + _r[2] * z + _t[0];
        double yc = _r[3] * x + _r[4] * y + _r[5] * z + _t[1];
        double zc = _r[6] * x + _r[7] * y + _r[8] * z + _t[2];
        if (zc <= 0) return null;
        // 3) Color 픽셀 투영
        return (_cfx * xc / zc + _ccx, _cfy * yc / zc + _ccy);
    }

    /// <summary>Color 픽셀 → Depth 픽셀(시차 무시 근사). ROI 영역 지정용.</summary>
    public (double u, double v) ColorToDepthApprox(double u, double v)
        => ((u - _ccx) * (_dfx / _cfx) + _dcx, (v - _ccy) * (_dfy / _cfy) + _dcy);

    /// <summary>Color 좌표 ROI 를 Depth 좌표 ROI 로 근사 매핑(클램프 포함).</summary>
    public RoiRect MapColorRoiToDepth(RoiRect r, int depthW, int depthH)
    {
        var (x0, y0) = ColorToDepthApprox(r.X, r.Y);
        var (x1, y1) = ColorToDepthApprox(r.X + r.Width, r.Y + r.Height);
        int nx = (int)Math.Round(Math.Min(x0, x1));
        int ny = (int)Math.Round(Math.Min(y0, y1));
        int nw = (int)Math.Round(Math.Abs(x1 - x0));
        int nh = (int)Math.Round(Math.Abs(y1 - y0));
        return new RoiRect(nx, ny, nw, nh).ClampTo(depthW, depthH) ?? new RoiRect(0, 0, depthW, depthH);
    }
}
