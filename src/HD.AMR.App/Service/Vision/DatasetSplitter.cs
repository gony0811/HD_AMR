using System.Globalization;
using System.Text.RegularExpressions;

namespace HD.AMR.App.Service.Vision;

/// <summary>
/// 학습/검증 분리를 <b>촬영 세션 단위</b>로 한다. 같은 자리에서 연달아 찍은 프레임은 거의 같은 영상이라,
/// 랜덤으로 나누면 검증셋이 학습셋의 복사본이 되어 일반화 성능을 측정할 수 없다.
/// 세션 = stem 의 타임스탬프(yyyyMMdd_HHmmss)를 시간순으로 정렬해 간격이 <c>gap</c> 이상 벌어지는 곳에서 끊은 묶음.
/// 회전 증강본(_cw90/_ccw90)은 원본과 같은 세션으로 묶어 누수를 막는다. 순수 함수(파일 I/O 없음).
/// </summary>
public static class DatasetSplitter
{
    private static readonly Regex TsRx = new(@"(\d{8}_\d{6})", RegexOptions.Compiled);
    private static readonly Regex RotRx = new(@"_c?cw90$", RegexOptions.Compiled);

    /// <summary>회전 증강 접미를 뗀 원본 stem.</summary>
    public static string BaseStem(string stem) => RotRx.Replace(stem, "");

    /// <summary>stem 의 촬영 시각. 없으면 null(세션 묶음 불가 → 단독 세션 취급).</summary>
    public static DateTime? ParseTimestamp(string stem)
    {
        var m = TsRx.Match(stem);
        return m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    /// <summary>
    /// stem 목록을 세션으로 묶은 뒤, 뒤쪽(최근) 세션부터 val 로 보내 val 비율이 <paramref name="valFraction"/> 에
    /// 가장 가깝게 만든다(최소 1세션). 세션이 1개뿐이면 그 세션을 시간순으로 앞(train)/뒤(val)로 자르고
    /// <see cref="SplitResult.SingleSession"/>=true 로 알린다(누수 가능성 경고용).
    /// </summary>
    public static SplitResult Split(IEnumerable<string> stems, double valFraction = 0.2, TimeSpan? gap = null)
    {
        var g = gap ?? TimeSpan.FromMinutes(10);
        var all = stems.Distinct().ToList();
        if (all.Count == 0) return new SplitResult(new(), new(), 0, false);

        // 원본 stem 기준으로 시각 정렬(타임스탬프 없는 건 맨 뒤, 각자 단독 세션).
        var bases = all.Select(BaseStem).Distinct()
            .Select(b => (Stem: b, T: ParseTimestamp(b)))
            .OrderBy(x => x.T ?? DateTime.MaxValue).ThenBy(x => x.Stem, StringComparer.Ordinal)
            .ToList();

        var sessions = new List<List<string>>();
        DateTime? prev = null;
        foreach (var (stem, t) in bases)
        {
            bool newSession = sessions.Count == 0 || t is null || prev is null || t.Value - prev.Value >= g;
            if (newSession) sessions.Add(new List<string>());
            sessions[^1].Add(stem);
            prev = t;
        }

        HashSet<string> valBases;
        bool single = sessions.Count == 1;
        if (single)
        {
            var s = sessions[0];
            int nVal = s.Count < 2 ? 0 : Math.Max(1, (int)Math.Round(s.Count * valFraction));
            valBases = s.Skip(s.Count - nVal).ToHashSet();
        }
        else
        {
            int target = (int)Math.Round(bases.Count * valFraction);
            valBases = new HashSet<string>();
            for (int i = sessions.Count - 1; i >= 1; i--)   // 첫 세션은 항상 train 에 남긴다
            {
                int with = valBases.Count + sessions[i].Count;
                // 아직 하나도 없으면 무조건 1세션, 그 외에는 목표에 더 가까워질 때만 추가.
                if (valBases.Count > 0 && Math.Abs(with - target) >= Math.Abs(valBases.Count - target)) break;
                foreach (var b in sessions[i]) valBases.Add(b);
            }
        }

        var train = new List<string>();
        var val = new List<string>();
        foreach (var s in all)
            (valBases.Contains(BaseStem(s)) ? val : train).Add(s);
        train.Sort(StringComparer.Ordinal);
        val.Sort(StringComparer.Ordinal);
        return new SplitResult(train, val, sessions.Count, single);
    }
}

public sealed record SplitResult(List<string> Train, List<string> Val, int SessionCount, bool SingleSession);
