using HD.AMR.App.Models;
using HD.AMR.App.Service.Sequence.Steps;

namespace HD.AMR.App.Service;

/// <summary>정규화([0,1]) ROI — 카메라 페이지가 파라미터로 저장하는 형식. 해상도와 무관하게 보관한다.</summary>
public readonly record struct NormalizedRoi(double X, double Y, double W, double H)
{
    /// <summary>폭·높이가 양수이고 화면 안에 들어오는지(저장값 손상/미설정 판별).</summary>
    public bool IsValid => W > 0 && H > 0 && X + W <= 1.0001 && Y + H <= 1.0001;

    /// <summary>프레임 픽셀 ROI 로 변환(프레임 경계로 clamp). 빈 영역이면 null.</summary>
    public RoiRect? ToPixel(int frameW, int frameH)
        => new RoiRect(
            (int)Math.Round(X * frameW),
            (int)Math.Round(Y * frameH),
            (int)Math.Round(W * frameW),
            (int)Math.Round(H * frameH)).ClampTo(frameW, frameH);

    /// <summary>프레임 픽셀 ROI → 정규화. 프레임 크기가 0 이면 빈 영역.</summary>
    public static NormalizedRoi FromPixel(RoiRect r, int frameW, int frameH)
    {
        if (frameW <= 0 || frameH <= 0) return default;
        double x = Math.Clamp((double)r.X / frameW, 0, 1);
        double y = Math.Clamp((double)r.Y / frameH, 0, 1);
        double w = Math.Clamp((double)r.Width / frameW, 0, 1 - x);
        double h = Math.Clamp((double)r.Height / frameH, 0, 1 - y);
        return new NormalizedRoi(x, y, w, h);
    }
}

/// <summary>ROI 한 종류의 파라미터 키 세트. 키 문자열의 진실 원천은 <see cref="WeldSequenceSupport"/> 상수.</summary>
public sealed record CameraRoiKeys(string Enabled, string X, string Y, string W, string H, string Label)
{
    /// <summary>평탄면(③④) — 카메라 페이지 Depth 뷰.</summary>
    public static readonly CameraRoiKeys Depth = new(
        WeldSequenceSupport.RoiEnabledKey, WeldSequenceSupport.RoiXKey, WeldSequenceSupport.RoiYKey,
        WeldSequenceSupport.RoiWKey, WeldSequenceSupport.RoiHKey, "평탄면");

    /// <summary>Peak(⑤~⑪ 코로게이션) — 카메라 페이지 Depth 뷰 / 용접 추적 Peak ROI.</summary>
    public static readonly CameraRoiKeys Peak = new(
        WeldSequenceSupport.PeakRoiEnabledKey, WeldSequenceSupport.PeakRoiXKey, WeldSequenceSupport.PeakRoiYKey,
        WeldSequenceSupport.PeakRoiWKey, WeldSequenceSupport.PeakRoiHKey, "Peak");

    /// <summary>Bead(⑦⑪ 비드 검출) — 카메라 페이지 IR 뷰 / 용접 추적 Weld ROI.</summary>
    public static readonly CameraRoiKeys Bead = new(
        WeldSequenceSupport.BeadRoiEnabledKey, WeldSequenceSupport.BeadRoiXKey, WeldSequenceSupport.BeadRoiYKey,
        WeldSequenceSupport.BeadRoiWKey, WeldSequenceSupport.BeadRoiHKey, "Bead");

    public bool Contains(string name) => name == Enabled || name == X || name == Y || name == W || name == H;
}

/// <summary>
/// 카메라 페이지 ROI 파라미터 읽기/쓰기 — 시퀀스(⑤~⑪)와 용접 추적 페이지가 같은 값을 쓰도록 하는 단일 경로.
/// </summary>
public static class CameraRoiParameters
{
    /// <summary>정규화 ROI 를 읽는다. 비활성/무효/DB 미준비면 null.</summary>
    public static async Task<NormalizedRoi?> ReadAsync(ParameterService param, CameraRoiKeys k)
    {
        try
        {
            if (await param.GetBoolAsync(k.Enabled) != true) return null;
            var roi = new NormalizedRoi(
                await param.GetDoubleAsync(k.X) ?? 0,
                await param.GetDoubleAsync(k.Y) ?? 0,
                await param.GetDoubleAsync(k.W) ?? 0,
                await param.GetDoubleAsync(k.H) ?? 0);
            return roi.IsValid ? roi : null;
        }
        catch { return null; /* DB 미준비 등 — 호출부 폴백 */ }
    }

    /// <summary>정규화 ROI 저장. null 이면 사용 해제(Enabled=false)만 하고 좌표는 보존한다.</summary>
    public static async Task WriteAsync(ParameterService param, CameraRoiKeys k, NormalizedRoi? roi)
    {
        if (roi is not { } r)
        {
            await param.SetBoolAsync(k.Enabled, false);
            return;
        }
        await param.SetBoolAsync(k.Enabled, true, $"{k.Label} ROI 사용 여부");
        await param.SetDoubleAsync(k.X, r.X, $"{k.Label} ROI 좌상단 X (정규화 0~1)");
        await param.SetDoubleAsync(k.Y, r.Y, $"{k.Label} ROI 좌상단 Y (정규화 0~1)");
        await param.SetDoubleAsync(k.W, r.W, $"{k.Label} ROI 너비 (정규화 0~1)");
        await param.SetDoubleAsync(k.H, r.H, $"{k.Label} ROI 높이 (정규화 0~1)");
    }
}
