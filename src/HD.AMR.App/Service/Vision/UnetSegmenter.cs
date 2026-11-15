using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// U-Net(<c>weld_unet_{modality}.onnx</c>, 입력 [1,1,H,W] 그레이 /255, 출력 [1,1,H,W] logit) 추론.
/// 완전 합성곱 모델이라 입력을 32 배수로 <b>반사 패딩</b>만 하고(학습 크롭과 같은 축척) 출력에서 원래 크기를 잘라낸다.
/// YOLO-seg 와 달리 박스 검출 단계가 없어 "검출 0건"으로 마스크 전체가 사라지는 일이 없다.
/// OpenCV 불필요(배열 연산만) — ORT 네이티브가 있는 모든 플랫폼에서 동작·테스트 가능.
/// </summary>
public static class UnetSegmenter
{
    /// <summary>
    /// 그레이 영상(row-major w×h) → 이진 마스크(255/0). <paramref name="threshold"/> 는 확률(0~1) 기준이며
    /// logit 비교(ln(t/(1−t)))로 시그모이드 계산을 생략한다. <paramref name="prob"/> 를 주면 확률맵(0~1)도 채운다.
    /// </summary>
    public static byte[] Segment(InferenceSession session, byte[] gray, int w, int h, float threshold, float[]? prob = null)
    {
        int H = PadTo32(h), W = PadTo32(w);
        var input = new DenseTensor<float>(new[] { 1, 1, H, W });
        var buf = input.Buffer.Span;
        for (int y = 0; y < H; y++)
        {
            int sy = Reflect(y, h);
            for (int x = 0; x < W; x++)
                buf[y * W + x] = gray[sy * w + Reflect(x, w)] / 255f;
        }

        var inName = session.InputMetadata.Keys.First();
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, input) });
        var outT = results.First().AsTensor<float>();
        if (outT.Dimensions.Length != 4 || outT.Dimensions[2] != H || outT.Dimensions[3] != W)
            throw new InvalidOperationException(
                $"U-Net 출력 형태가 예상과 다릅니다: [{string.Join(',', outT.Dimensions.ToArray())}] (기대 [1,1,{H},{W}])");

        float t = Math.Clamp(threshold, 1e-4f, 1 - 1e-4f);
        float logitThr = MathF.Log(t / (1 - t));
        var mask = new byte[w * h];
        var flat = outT is DenseTensor<float> dt ? dt.Buffer.Span : outT.ToArray().AsSpan();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float z = flat[y * W + x];
                if (z > logitThr) mask[y * w + x] = 255;
                if (prob is not null) prob[y * w + x] = 1f / (1f + MathF.Exp(-z));
            }
        return mask;
    }

    /// <summary>32 배수로 올림(depth 4 U-Net 은 16 배수면 충분하지만 학습·평가 스크립트와 같은 규칙).</summary>
    public static int PadTo32(int v) => (v + 31) / 32 * 32;

    /// <summary>반사 패딩 인덱스(cv2.BORDER_REFLECT_101 과 동일: …2,1,0,1,2…, 끝은 n−2,n−1,n−2…).</summary>
    public static int Reflect(int i, int n)
    {
        if (n <= 1) return 0;
        int period = 2 * (n - 1);
        i %= period;
        if (i < 0) i += period;
        return i < n ? i : period - i;
    }

    /// <summary>모델이 1채널 입력 U-Net 형식인지(입력 rank4·채널 1, 출력 rank4).</summary>
    public static bool LooksLikeUnet(InferenceSession s)
    {
        var inDims = s.InputMetadata.Values.First().Dimensions;
        var outDims = s.OutputMetadata.Values.First().Dimensions;
        return inDims.Length == 4 && inDims[1] == 1 && outDims.Length == 4 && s.OutputMetadata.Count == 1;
    }
}
