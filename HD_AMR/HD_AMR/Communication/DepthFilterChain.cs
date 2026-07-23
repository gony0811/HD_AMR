using Intel.RealSense;

namespace HD_AMR.Communication;

/// <summary>
/// librealsense 깊이 후처리 체인: (decimation) → threshold → depth→disparity → spatial →
/// temporal → disparity→depth → (hole-filling). 금속 정반사 등으로 0(무효)이 된 픽셀을
/// 주변/이전 프레임 데이터로 복원하고, 작업 거리 창 밖의 반사 스파이크(유령값)를 제거한다. <see cref="RealSenseClient.RunAsync"/> 폴링 스레드에서만 생성/사용/해제
/// 한다(스레드 안전성 불필요). TemporalFilter 는 프레임 히스토리를 가지므로 스트림 세션마다
/// 새로 만든다 — 재연결 시 RunAsync 가 재호출되며 자동으로 리셋된다.
/// </summary>
public sealed class DepthFilterChain : IDisposable
{
    private readonly List<ProcessingBlock> _blocks;

    private DepthFilterChain(List<ProcessingBlock> blocks) => _blocks = blocks;

    /// <summary>설정에 따라 체인을 생성한다. 비활성이거나 켜진 필터가 없으면 null.</summary>
    public static DepthFilterChain? TryCreate(DepthFilterSettings s)
    {
        if (!s.Enabled) return null;
        var blocks = new List<ProcessingBlock>();
        try
        {
            if (s.UseDecimation)
            {
                var d = new DecimationFilter();
                d.Options[Option.FilterMagnitude].Value = s.DecimationMagnitude;
                blocks.Add(d);
            }

            if (s.UseThreshold)
            {
                var th = new ThresholdFilter();                                  // Z16 도메인, disparity 변환 전
                th.Options[Option.MinDistance].Value = s.ThresholdMinMm / 1000f; // SDK 단위: meter
                th.Options[Option.MaxDistance].Value = s.ThresholdMaxMm / 1000f;
                blocks.Add(th);
            }

            // disparity 왕복은 spatial/temporal 이 하나라도 켜져 있을 때만 의미가 있다. 같은
            // bool 로 열고 닫아 DISPARITY32 로 시작한 체인이 반드시 Z16 으로 복원됨을 보장.
            bool disparity = s.UseDisparityDomain && (s.UseSpatial || s.UseTemporal);
            if (disparity) blocks.Add(new DisparityTransform(true));   // Z16 → DISPARITY32

            if (s.UseSpatial)
            {
                var sp = new SpatialFilter();
                sp.Options[Option.FilterMagnitude].Value = s.SpatialMagnitude;
                sp.Options[Option.FilterSmoothAlpha].Value = s.SpatialSmoothAlpha;
                sp.Options[Option.FilterSmoothDelta].Value = s.SpatialSmoothDelta;
                sp.Options[Option.HolesFill].Value = s.SpatialHolesFill;
                blocks.Add(sp);
            }
            if (s.UseTemporal)
            {
                var t = new TemporalFilter();
                t.Options[Option.FilterSmoothAlpha].Value = s.TemporalSmoothAlpha;
                t.Options[Option.FilterSmoothDelta].Value = s.TemporalSmoothDelta;
                // librealsense 는 temporal filter 의 persistency index 를 HolesFill 옵션 슬롯에 싣는다.
                t.Options[Option.HolesFill].Value = s.TemporalPersistence;
                blocks.Add(t);
            }

            if (disparity) blocks.Add(new DisparityTransform(false));  // DISPARITY32 → Z16 복원

            if (s.UseHoleFilling)
            {
                var h = new HoleFillingFilter();
                h.Options[Option.HolesFill].Value = s.HoleFillingMode;
                blocks.Add(h);
            }

            return blocks.Count == 0 ? null : new DepthFilterChain(blocks);
        }
        catch
        {
            foreach (var b in blocks) { try { b.Dispose(); } catch { /* ignore */ } }
            throw;
        }
    }

    /// <summary>
    /// 체인을 실행한다. 입력 프레임 소유권은 호출자에게 남고(여기서 dispose 하지 않음), 반환
    /// 프레임은 호출자가 반드시 dispose 해야 한다. 중간 프레임은 여기서 전부 dispose 하므로
    /// librealsense 의 16개 네이티브 프레임 풀을 고갈시키지 않는다.
    /// </summary>
    public VideoFrame Process(VideoFrame input)
    {
        VideoFrame current = input;
        try
        {
            foreach (var block in _blocks)
            {
                var next = block.Process<VideoFrame>(current);
                if (!ReferenceEquals(current, input)) current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            if (!ReferenceEquals(current, input)) current.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var b in _blocks) { try { b.Dispose(); } catch { /* ignore */ } }
        _blocks.Clear();
    }
}
