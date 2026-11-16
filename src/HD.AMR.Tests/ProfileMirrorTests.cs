using HD.AMR.App.Communication;
using HD.AMR.App.Communication.Vision;
using HD.AMR.App.Data.Entities;
using HD.AMR.App.Service.Inspection;

namespace HD.AMR.Tests;

/// <summary>우현 면에서 티칭 프로필을 거울 반전(<see cref="ProfileMirror"/>)하는 규칙.</summary>
public class ProfileMirrorTests
{
    private static InspectionWaypoint Wp(double x, double y, double z, double theta = 0, double rz = 0, double rx = 0)
        => new(X: x, Z: z, Theta: theta, ThetaManual: false, Y: y, RzDeg: rz, RxDeg: rx);

    [Theory]
    [InlineData("SM", true)]
    [InlineData("SL", true)]
    [InlineData("SU", true)]
    [InlineData("PM", false)]
    [InlineData("PL", false)]
    [InlineData("PU", false)]
    [InlineData("T", false)]   // 왼손 좌표계지만 좌현의 거울상인지 확인되지 않아 대상이 아니다
    [InlineData("B", false)]
    [InlineData("F", false)]
    [InlineData("A", false)]
    [InlineData(null, false)]
    public void 우현_면만_반전한다(string? wall, bool expected)
        => Assert.Equal(expected, ProfileMirror.ShouldMirror(wall, new FaceSeamPlacement(0, 0, FaceSeamDirection.PlusU)));

    [Fact]
    public void 방향_정보가_없으면_플러스_방향으로_보고_마이너스_방향과_끔은_반전하지_않는다()
    {
        Assert.True(ProfileMirror.ShouldMirror("SM", null));
        Assert.True(ProfileMirror.ShouldMirror("SM", new FaceSeamPlacement(0, 0, FaceSeamDirection.PlusV)));
        Assert.False(ProfileMirror.ShouldMirror("SM", new FaceSeamPlacement(0, 0, FaceSeamDirection.MinusU)));
        Assert.False(ProfileMirror.ShouldMirror("SM", new FaceSeamPlacement(0, 0, FaceSeamDirection.MinusV)));
        Assert.False(ProfileMirror.ShouldMirror("SM", null, enabled: false));
    }

    [Fact]
    public void 면_ID로도_같은_판정을_한다()
    {
        Assert.True(ProfileMirror.ShouldMirror(0x04, null));    // SM
        Assert.True(ProfileMirror.ShouldMirror(0x08, null));    // SL
        Assert.True(ProfileMirror.ShouldMirror(0x0A, null));    // SU
        Assert.False(ProfileMirror.ShouldMirror(0x02, null));   // T
        Assert.False(ProfileMirror.ShouldMirror(0, null));
    }

    [Fact]
    public void 가로_팔은_그대로이고_세로_팔은_위아래가_바뀐다()
    {
        // CROSS-4-V1(2026-11-14): 가로 팔 Y=0, 세로 팔 X=175·Rz −90, 양 끝 top Y −195 / +170.
        var horizontal = Wp(329.36, 0, 11.88, theta: -45);
        Assert.Equal(horizontal, ProfileMirror.Mirror(horizontal));

        var lowTop = ProfileMirror.Mirror(Wp(175, -195, 33.8, rz: -90));
        var highTop = ProfileMirror.Mirror(Wp(175, 170, 33.8, rz: -90));
        Assert.Equal(195, lowTop.Y);
        Assert.Equal(-170, highTop.Y);
        Assert.Equal(90, lowTop.RzDeg);
        Assert.Equal(175, lowTop.X);
        Assert.Equal(33.8, lowTop.Z);

        var slope = ProfileMirror.Mirror(Wp(175, -169.35, 21.68, theta: -45, rz: -90, rx: 3));
        Assert.Equal(169.35, slope.Y);
        Assert.Equal(-45, slope.Theta);   // 틸트(Ry)는 그대로
        Assert.Equal(-3, slope.RxDeg);
    }

    [Fact]
    public void 반전한_자세는_교시_자세의_거울상이다()
    {
        // 작업물 XZ 평면 거울 M = diag(1, −1, 1): 위치 p → M·p, 자세 R → M·R·M.
        foreach (var w in new[]
                 {
                     Wp(175, -169.35, 21.68, theta: -45, rz: -90),
                     Wp(175, 141.8, 17.17, theta: 45, rz: -90),
                     Wp(120, 30, 5, theta: 20, rz: 35, rx: -12),
                 })
        {
            var taught = FrameMath.PoseToMatrix(new[] { w.X, w.Y, w.Z, w.RxDeg, w.Theta, w.RzDeg });
            var m = ProfileMirror.Mirror(w);
            var mirrored = FrameMath.PoseToMatrix(new[] { m.X, m.Y, m.Z, m.RxDeg, m.Theta, m.RzDeg });

            double[] s = { 1, -1, 1 };
            for (var r = 0; r < 3; r++)
            {
                for (var c = 0; c < 3; c++)
                    Assert.Equal(s[r] * taught[r, c] * s[c], mirrored[r, c], 9);
                Assert.Equal(s[r] * taught[r, 3], mirrored[r, 3], 9);
            }
        }
    }

    [Fact]
    public void 반전하면_우현의_촬영_위치가_좌현과_같은_면_좌표_오프셋이_된다()
    {
        // 좌현 PM(3)·우현 SM(4) 모두 용접선 시작점 (u0, v0) = (1000, 2000) mm, 방향 +u.
        var placement = new FaceSeamPlacement(1000, 2000, FaceSeamDirection.PlusU);
        var taught = Wp(175, -195, 33.8, rz: -90);

        var port = FaceLocalMapper.ToFaceLocal(3, taught.X, taught.Y, taught.Z, placement);
        var m = ProfileMirror.Mirror(taught);
        var starboard = FaceLocalMapper.ToFaceLocal(4, m.X, m.Y, m.Z, placement);

        Assert.Equal((1175, 1805, 34), port);
        Assert.Equal(port, starboard);
    }
}
