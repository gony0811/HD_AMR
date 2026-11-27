using HD.AMR.App.Communication;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

/// <summary>검사 프로파일 페이지 경유점의 광축 둘레 비틀림(0/180°) 보정 — 실기 2026-11-27 11:44 CROSS-4-R-V1, user=#5, errcode=112.</summary>
public class ManualPointTwistTests
{
    // 5번 작업물 좌표계(Cobot 페이지 불러오기 값) — 점1 위치, 프레임 X 는 점1→점2 방향이라 Rz = TCP rz − 180°.
    private static readonly double[] Frame5 = { -51.65, 492.16, 1309.06, -179.71, -1.85, -161.27 };

    [Fact]
    public void 비틀림_0은_그대로()
    {
        var p = new[] { 4.91, 0.0, 33.37, 0.0, 10.0, 0.0 };
        Assert.Same(p, ManualPointTwist.Apply(p, 0.0));
    }

    [Fact]
    public void 비틀림_180은_위치와_광축을_두고_둘레만_돌린다()
    {
        var p = new[] { 4.91, 0.0, 33.37, 0.0, 10.0, 0.0 };
        var q = ManualPointTwist.Apply(p, 180.0);

        // 위치 그대로.
        Assert.Equal(p[0], q[0], 6);
        Assert.Equal(p[1], q[1], 6);
        Assert.Equal(p[2], q[2], 6);

        // p 기준으로 본 q 는 툴 Z 둘레 180° 뿐.
        var rel = FrameMath.ToFrame(q, p);
        Assert.Equal(0.0, rel[0], 6);
        Assert.Equal(0.0, rel[1], 6);
        Assert.Equal(0.0, rel[2], 6);
        Assert.Equal(180.0, Math.Abs(rel[5]), 4);
        Assert.Equal(0.0, Math.Abs(rel[3]) % 180.0, 4);
        Assert.Equal(0.0, rel[4], 4);
    }

    [Fact]
    public void 점1_자세는_180_보정으로_교시_당시_TCP_자세가_된다()
    {
        // 페이지 1번 점 [0,0,33.8,0,0,0] → 베이스. 보정 없으면 rz −161.27(교시 자세를 광축 둘레로 180° 돌린 자세),
        // 180° 보정하면 점1 에서 읽은 TCP 자세(rx 179.72, ry 1.84, rz 18.73)와 같다.
        var first = new[] { 0.0, 0.0, 33.8, 0.0, 0.0, 0.0 };

        var raw = ManualPointTwist.ToBase(first, Frame5, 0.0);
        Assert.Equal(-161.27, raw[5], 2);

        var twisted = ManualPointTwist.ToBase(first, Frame5, 180.0);
        Assert.Equal(-52.63, twisted[0], 1);
        Assert.Equal(491.65, twisted[1], 1);
        Assert.Equal(1275.28, twisted[2], 1);
        Assert.Equal(179.71, twisted[3], 1);
        Assert.Equal(1.85, twisted[4], 1);
        Assert.Equal(18.73, twisted[5], 1);
    }
}
