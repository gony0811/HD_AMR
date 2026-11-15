namespace HD.AMR.App.Communication;

/// <summary>
/// 이미터 ON/OFF 교번(<c>Option.EmitterOnOff</c>) 모드에서 한 프레임이 이미터 ON(도트 있음)인지 OFF(깨끗)인지
/// 판정한다. 1순위는 프레임 메타데이터(<c>FrameEmitterMode</c>/<c>FrameLaserPowerMode</c>)이지만, Windows 에서
/// UVC 메타데이터 레지스트리 등록이 안 된 경우 메타데이터가 없으므로 IR 영상의 도트 에너지로 판정하는
/// 폴백을 제공한다. 도트는 2~3px 밝은 점이라 국소 라플라시안 에너지가 OFF 프레임보다 확연히 크다.
/// 네이티브 의존이 없는 순수 함수라 단위 테스트 가능.
/// </summary>
public sealed class EmitterFrameClassifier
{
    /// <summary>연속 프레임 간 도트 에너지 비가 이 값 이상 차이 나야 ON/OFF 를 확정한다.</summary>
    public const double RatioThreshold = 1.3;

    private double _prevEnergy = double.NaN;

    /// <summary>
    /// 메타데이터가 없을 때 IR 프레임(Y8) 도트 에너지로 판정. 교번 모드이므로 직전 프레임과 비교해
    /// 확연히 크면 ON, 확연히 작으면 OFF. 애매하거나 첫 프레임처럼 근거가 없으면 null
    /// (판정 보류 — 호출자는 깊이/IR 갱신을 모두 건너뛴다).
    /// </summary>
    public bool? ClassifyByEnergy(byte[] gray, int width, int height)
    {
        var e = DotEnergy(gray, width, height);
        bool? on;
        if (double.IsNaN(_prevEnergy) || _prevEnergy <= 0 || e <= 0)
            on = null;
        else if (e >= _prevEnergy * RatioThreshold)
            on = true;
        else if (e * RatioThreshold <= _prevEnergy)
            on = false;
        else
            on = null;   // 프레임 드롭 등으로 같은 상태가 연속되면 차이가 작다 — 추측하지 않고 보류.

        _prevEnergy = e;
        return on;
    }

    public void Reset() => _prevEnergy = double.NaN;

    /// <summary>
    /// 도트 에너지 = 픽셀과 4-이웃(거리 2) 평균의 양(+)의 차이 평균. 밝은 점(도트)에 민감하고 넓은 밝기
    /// 변화(조명·노출)에는 둔감하다. 속도를 위해 2px 간격으로 샘플링.
    /// </summary>
    public static double DotEnergy(byte[] gray, int width, int height)
    {
        if (width < 5 || height < 5 || gray.Length < width * height) return 0;
        long sum = 0;
        long n = 0;
        for (int y = 2; y < height - 2; y += 2)
        {
            int row = y * width;
            for (int x = 2; x < width - 2; x += 2)
            {
                int c = gray[row + x];
                int nb = gray[row + x - 2] + gray[row + x + 2] + gray[row - 2 * width + x] + gray[row + 2 * width + x];
                int d = 4 * c - nb;
                if (d > 0) sum += d;
                n++;
            }
        }
        return n == 0 ? 0 : sum / (4.0 * n);
    }
}
