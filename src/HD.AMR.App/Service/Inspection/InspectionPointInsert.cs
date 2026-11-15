namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 검사 프로파일 편집 — 포인트 사이에 새 행을 끼워 넣을 때의 자리와 초기값.
/// 새 행은 앞뒤 행의 중간값(X·Y·Z·Rx·Ry·Rz)으로 채운다 — 사이를 촘촘히 메우는 용도.
/// 맨 앞·맨 뒤처럼 이웃이 한쪽뿐이면 그 행을 그대로 복사한다.
/// </summary>
public static class InspectionPointInsert
{
    /// <summary>기준 행(<paramref name="index"/>)의 위(<paramref name="above"/>) 또는 아래에 넣을 자리.
    /// 기준이 범위를 벗어나면 맨 끝.</summary>
    public static int InsertIndex(int count, int index, bool above)
    {
        if (count <= 0) return 0;
        if (index < 0 || index >= count) return count;
        return above ? index : index + 1;
    }

    /// <summary>
    /// <paramref name="insertAt"/> 자리에 넣을 새 포즈 [X, Y, Z, Rx, Ry, Rz].
    /// 앞(insertAt − 1)·뒤(insertAt) 행이 모두 있으면 중간값, 한쪽만 있으면 그 행의 복사, 목록이 비었으면 null.
    /// </summary>
    public static double[]? NewPose(IReadOnlyList<double[]> poses, int insertAt)
    {
        if (poses.Count == 0) return null;
        var prev = insertAt - 1 >= 0 && insertAt - 1 < poses.Count ? poses[insertAt - 1] : null;
        var next = insertAt >= 0 && insertAt < poses.Count ? poses[insertAt] : null;
        if (prev is not null && next is not null)
        {
            var n = Math.Min(prev.Length, next.Length);
            var mid = new double[n];
            for (var i = 0; i < n; i++) mid[i] = (prev[i] + next[i]) / 2.0;
            return mid;
        }
        return (double[]?)(prev ?? next)?.Clone();
    }

    /// <summary>새 행이 물려받을 행(Surface 등 숫자가 아닌 값의 출처) — 앞 행 우선, 없으면 뒤 행. 없으면 −1.</summary>
    public static int InheritFrom(int count, int insertAt)
        => count <= 0 ? -1 : insertAt - 1 >= 0 ? Math.Min(insertAt - 1, count - 1) : 0;
}
