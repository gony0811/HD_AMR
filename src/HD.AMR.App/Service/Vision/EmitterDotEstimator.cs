using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace HD.AMR.App.Service.Vision;

/// <summary>저장된 IR 캡처에서 추정한 이미터 상태. Unknown = 기준값 사이(직접 확인 필요) 또는 읽기 실패.</summary>
public enum EmitterState { Unknown, Off, On }

/// <summary>
/// IR 캡처 한 장만 보고 이미터 ON/OFF 를 추정한다(캡처 시 기록이 없는 기존 데이터용).
/// 이미터 도트 = 주변보다 튀는 작고 밝은 점 → 화이트 탑햇(원본 − 7×7 열림)에서 임계 이상인 2~30px 덩어리 개수로 센다.
/// 선창 현장 IR 29장 기준 OFF 70~197개, ON 511~2,781개(LED 로 도트가 묻힌 ON 포함)로 뚜렷이 갈려
/// <see cref="OffMax"/>·<see cref="OnMin"/> 사이는 Unknown 으로 둔다. 순수 C#(OpenCV 불필요), 파일별 결과 캐시.
/// </summary>
public static class EmitterDotEstimator
{
    /// <summary>이 개수 이하면 OFF.</summary>
    public const int OffMax = 300;
    /// <summary>이 개수 이상이면 ON.</summary>
    public const int OnMin = 450;

    private const int Kernel = 7;          // 열림 커널(도트보다 크게)
    private const int TopHatThr = 25;      // 주변 대비 밝기 차
    private const int MinArea = 2, MaxArea = 30;

    private static readonly ConcurrentDictionary<string, (DateTime Mtime, int Dots)> Cache = new();

    public static EmitterState Classify(int dots) => dots <= OffMax ? EmitterState.Off : dots >= OnMin ? EmitterState.On : EmitterState.Unknown;

    /// <summary>IR PNG 파일의 추정 상태(파일 수정시각 기준 캐시). 읽기 실패면 Unknown.</summary>
    public static EmitterState ClassifyFile(string path) => CountFile(path) is { } n ? Classify(n) : EmitterState.Unknown;

    /// <summary>IR PNG 파일의 도트 개수(캐시). 읽기 실패면 null.</summary>
    public static int? CountFile(string path)
    {
        try
        {
            var mtime = File.GetLastWriteTimeUtc(path);
            if (Cache.TryGetValue(path, out var c) && c.Mtime == mtime) return c.Dots;
            using var img = Image.Load<L8>(path);
            var g = new byte[img.Width * img.Height];
            img.CopyPixelDataTo(g);
            int n = CountDots(g, img.Width, img.Height);
            Cache[path] = (mtime, n);
            return n;
        }
        catch { return null; }
    }

    /// <summary>표시용 짧은 문구.</summary>
    public static string Text(EmitterState s) => s switch { EmitterState.On => "ON", EmitterState.Off => "OFF", _ => "?" };

    /// <summary>작고 밝은 점(도트) 개수. gray = row-major w×h.</summary>
    public static int CountDots(byte[] gray, int w, int h)
    {
        // 화이트 탑햇: g − dilate(erode(g)). 정사각 커널이라 가로·세로 분리 min/max 로 빠르게.
        var er = MinMax(gray, w, h, Kernel, max: false);
        var op = MinMax(er, w, h, Kernel, max: true);
        var bin = new bool[w * h];
        for (int i = 0; i < bin.Length; i++) bin[i] = gray[i] - op[i] > TopHatThr;

        // 8-연결 덩어리 중 크기 2~30px 개수.
        int count = 0;
        var stack = new Stack<int>();
        for (int s = 0; s < bin.Length; s++)
        {
            if (!bin[s]) continue;
            int area = 0;
            bin[s] = false; stack.Push(s);
            while (stack.Count > 0)
            {
                int p = stack.Pop(); area++;
                int px = p % w, py = p / w;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int y = py + dy; if (y < 0 || y >= h) continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = px + dx; if (x < 0 || x >= w) continue;
                        int q = y * w + x;
                        if (bin[q]) { bin[q] = false; stack.Push(q); }
                    }
                }
            }
            if (area >= MinArea && area <= MaxArea) count++;
        }
        return count;
    }

    // k×k 정사각 최소/최대 필터(가로→세로 분리, 가장자리는 영상 안쪽만).
    private static byte[] MinMax(byte[] src, int w, int h, int k, bool max)
    {
        int r = k / 2;
        var tmp = new byte[w * h];
        var dst = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int a = Math.Max(0, x - r), b = Math.Min(w - 1, x + r);
                byte v = src[row + a];
                for (int i = a + 1; i <= b; i++) { byte t = src[row + i]; if (max ? t > v : t < v) v = t; }
                tmp[row + x] = v;
            }
        }
        for (int y = 0; y < h; y++)
        {
            int a = Math.Max(0, y - r), b = Math.Min(h - 1, y + r);
            for (int x = 0; x < w; x++)
            {
                byte v = tmp[a * w + x];
                for (int i = a + 1; i <= b; i++) { byte t = tmp[i * w + x]; if (max ? t > v : t < v) v = t; }
                dst[y * w + x] = v;
            }
        }
        return dst;
    }
}
