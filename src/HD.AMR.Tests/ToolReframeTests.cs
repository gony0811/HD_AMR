using HD.AMR.App.Communication;

namespace HD.AMR.Tests;

/// <summary>
/// 공구 재프레임의 <b>대칭성</b> 회귀 — 앵커 조회(<c>GetTcpPoseInBaseAsync</c>: 활성 공구→이동 공구)와
/// 역기구학 입력(<c>GetInverseKinForMoveAsync</c>: 이동 공구→활성 공구)이 같은 식을 서로 반대 방향으로
/// 써야 한다. 한쪽만 적용하던 시절, 카메라 보정(tool 0)을 활성 공구 #1 상태에서 실행하면 IK 가 공구 오프셋
/// 만큼 어긋난 목표를 받아 <c>errcode=112</c>(목표 자세 도달 불가)로 거부했다.
/// </summary>
public class ToolReframeTests
{
    // 플랜지에서 300mm 뻗고 30° 기운 공구(예: 토치 #1). 공구 0 = identity.
    private static readonly double[] Flange = new double[6];
    private static readonly double[] Torch = [0, 0, 300, 0, 30, 0];

    [Fact]
    public void ReframeTool_SameOffsets_IsIdentity()
    {
        double[] pose = [-880.8, -73.3, -1254.6, -167, -14.1, 0.9];
        var same = PoseMath.ReframeTool(pose, Torch, Torch);
        for (int i = 0; i < 6; i++) Assert.True(Math.Abs(same[i] - pose[i]) < 1e-9, $"성분 {i}: {same[i]}");
    }

    /// <summary>앵커(활성→이동 공구) 뒤에 IK 보정(이동→활성 공구)을 태우면 원래 pose 로 돌아와야 한다.
    /// 이것이 성립해야 "로봇이 지금 서 있는 자세"의 IK 가 성공한다.</summary>
    [Fact]
    public void AnchorThenIkReframe_RoundTripsToActiveToolPose()
    {
        // 실패 당시 로그의 IK 입력 pose — 이미 베이스에서 ~1535mm 로 작업영역 테두리다.
        double[] poseInActiveTool = [-880.8, -73.3, -1254.6, -167, -14.1, 0.9];

        var anchor = PoseMath.ReframeTool(poseInActiveTool, Torch, Flange);   // 활성 #1 → 이동 공구 #0
        var ikInput = PoseMath.ReframeTool(anchor, Flange, Torch);            // 이동 #0 → 활성 #1

        for (int i = 0; i < 6; i++)
            Assert.True(Math.Abs(ikInput[i] - poseInActiveTool[i]) < 1e-6,
                $"성분 {i}: {ikInput[i]} != {poseInActiveTool[i]}");
    }

    /// <summary>IK 보정을 빠뜨리면 목표가 공구 오프셋만큼 밀린다 — 112 의 물리적 원인.</summary>
    [Fact]
    public void MissingIkReframe_ShiftsTargetByToolOffset()
    {
        double[] poseInActiveTool = [-880.8, -73.3, -1254.6, -167, -14.1, 0.9];
        var anchor = PoseMath.ReframeTool(poseInActiveTool, Torch, Flange);

        // 보정 없이 앵커를 그대로 IK 에 넣으면(구버전) 활성 공구 기준으로 오해석된다.
        double shift = Math.Sqrt(
            Math.Pow(anchor[0] - poseInActiveTool[0], 2) +
            Math.Pow(anchor[1] - poseInActiveTool[1], 2) +
            Math.Pow(anchor[2] - poseInActiveTool[2], 2));
        Assert.True(shift > 290, $"공구 오프셋 300mm 에 상응하는 어긋남이 나와야 한다: {shift:0.#}mm");

        // 어긋나는 방향은 공구 자세에 따라 안쪽/바깥쪽 어디로도 간다 — 중요한 것은 "로봇이 지금 서 있는
        // 점"이 아닌 다른 점을 IK 가 요구받는다는 사실이다. 실패 자세는 베이스에서 ~1535mm(작업영역
        // 테두리)라 300mm 급 어긋남이면 도달 불가·손목 한계로 넘어가기 쉽다.
        double reach = Math.Sqrt(poseInActiveTool[0] * poseInActiveTool[0] +
                                 poseInActiveTool[1] * poseInActiveTool[1] +
                                 poseInActiveTool[2] * poseInActiveTool[2]);
        Assert.InRange(reach, 1500, 1570);
    }

    /// <summary>공구 0(플랜지)은 identity 라 RPC 조회 없이 항등이어야 한다 — 뎁스 카메라 보정이 쓰는 경로.</summary>
    [Fact]
    public void ReframeTool_FlangeToFlange_IsIdentity()
    {
        double[] pose = [100, -200, 300, 10, -20, 30];
        var r = PoseMath.ReframeTool(pose, Flange, Flange);
        for (int i = 0; i < 6; i++) Assert.True(Math.Abs(r[i] - pose[i]) < 1e-9, $"성분 {i}: {r[i]}");
    }
}
