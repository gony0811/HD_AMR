using HD.AMR.App.Communication;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// 컨트롤러에 작업물 좌표계를 등록하지 않고, 두 TCP 교시 포즈로 애플리케이션 내부 작업물 프레임을 만든다.
/// 점1은 코로게이션 Top, 점2는 용접선 X+ 방향이며, 점1 교시 자세의 툴 +Z를 작업물 Z+로 사용한다.
/// 실제 작업물 원점은 Top에서 작업물 Z- 방향으로 <see cref="DefaultCorrugationTopHeightMm"/> 내린 위치다.
/// </summary>
public static class InternalWorkpieceFrame
{
    /// <summary>시퀀스 실행 중 계산된 BASE 기준 내부 작업물 프레임(double[6]) 보관 키.</summary>
    public const string BagKey = "wobj.internalFrameBase";

    /// <summary>코로게이션 바닥(Z=0)에서 Top까지의 높이.</summary>
    public const double DefaultCorrugationTopHeightMm = 33.8;

    /// <summary>점1 자세에서 툴 +Z 방향을 만드는 가상점 거리. 실제 모션은 발생하지 않는다.</summary>
    private const double VirtualZPointOffsetMm = 50.0;

    /// <summary>
    /// 코로게이션 Top의 점1 포즈와 용접선 X+ 방향의 점2 포즈로 BASE 기준 작업물 프레임을 계산한다.
    /// 두 교시점의 위치 차이가 X+가 되고, 점1의 툴 +Z가 Z+ 기준이 된다.
    /// </summary>
    public static double[] Compute(
        double[] topPoseBase,
        double[] xDirectionPoseBase,
        double corrugationTopHeightMm = DefaultCorrugationTopHeightMm)
    {
        RequirePose(topPoseBase, nameof(topPoseBase));
        RequirePose(xDirectionPoseBase, nameof(xDirectionPoseBase));
        if (!double.IsFinite(corrugationTopHeightMm) || corrugationTopHeightMm < 0)
            throw new ArgumentOutOfRangeException(nameof(corrugationTopHeightMm),
                "코로게이션 Top 높이는 0 이상의 유한값이어야 합니다.");

        // 점1의 교시 자세에 고정된 툴 +Z를 표면 바깥쪽(작업물 Z+)으로 사용한다.
        var zPoint = FrameMath.FromFrame(
            new[] { 0.0, 0.0, VirtualZPointOffsetMm, 0.0, 0.0, 0.0 }, topPoseBase);
        var topFrame = FairinoRpcClient.ComputeFramePose(topPoseBase, xDirectionPoseBase, zPoint, method: 0);

        // 교시점은 Top이지만 레시피 Z=0은 코로게이션 바닥이다. 방향은 유지하고 원점만 Z-로 내린다.
        return FrameMath.FromFrame(
            new[] { 0.0, 0.0, -corrugationTopHeightMm, 0.0, 0.0, 0.0 }, topFrame);
    }

    /// <summary>
    /// 점1 툴 +Z 대비 점1→점2 가 기운 각 [도]. 두 점이 같은 WD(면 이격)에 있으면 이 각이 면을 따라 누운 기울기라
    /// <see cref="Compute"/> 가 X에 직교시킨 Z에 반영된다(묶음 Z 보정).
    /// </summary>
    public static double SlopeAlongSeamDeg(double[] topPoseBase, double[] xDirectionPoseBase)
    {
        RequirePose(topPoseBase, nameof(topPoseBase));
        RequirePose(xDirectionPoseBase, nameof(xDirectionPoseBase));
        var zPoint = FrameMath.FromFrame(new[] { 0.0, 0.0, VirtualZPointOffsetMm, 0.0, 0.0, 0.0 }, topPoseBase);
        var n = new[] { zPoint[0] - topPoseBase[0], zPoint[1] - topPoseBase[1], zPoint[2] - topPoseBase[2] };
        var nLen = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        if (nLen < 1e-9) return 0;
        var d = new[]
        {
            xDirectionPoseBase[0] - topPoseBase[0],
            xDirectionPoseBase[1] - topPoseBase[1],
            xDirectionPoseBase[2] - topPoseBase[2],
        };
        var along = (d[0] * n[0] + d[1] * n[1] + d[2] * n[2]) / nLen;
        var tang = Math.Sqrt(Math.Max(0, d[0] * d[0] + d[1] * d[1] + d[2] * d[2] - along * along));
        return Math.Atan2(along, tang) * 180.0 / Math.PI;
    }

    /// <summary>내부 작업물 pose를 선택된 TCP의 BASE 목표 pose로 변환한다.</summary>
    public static double[] ToBasePose(double[] poseInWorkpiece, double[] frameBase)
    {
        RequirePose(poseInWorkpiece, nameof(poseInWorkpiece));
        RequirePose(frameBase, nameof(frameBase));
        return FrameMath.FromFrame(poseInWorkpiece, frameBase);
    }

    /// <summary>
    /// anchor 공유 프레임을 다음 task 위치로 평행 이동한다 — 자세는 그대로, 원점만 두 task 의 ② 시작 접근점 차이
    /// (<paramref name="fromStartBase"/>→<paramref name="toStartBase"/>)에서 <b>프레임 Z(면 법선) 성분을 뺀 만큼</b>
    /// 옮긴다(면 거리 유지 — <see cref="Sequence.Steps.CobotSeamEndMoveStep.ParallelFromPoint1"/> 와 같은 식).
    /// 이동하지 않으면 경유점이 첫 task 구간을 그대로 다시 돈다(2026-10-30 SU L4-R03C05 #2·#3 이 #1 끝점에서 종료).
    /// </summary>
    /// <returns>(이동한 프레임, 버린 법선 성분 mm)</returns>
    public static (double[] Frame, double NormalMm) TranslateInPlane(
        double[] frameBase, double[] fromStartBase, double[] toStartBase)
    {
        RequirePose(frameBase, nameof(frameBase));
        var (pos, normalMm) = Sequence.Steps.CobotSeamEndMoveStep.ParallelFromPoint1(
            frameBase, fromStartBase, toStartBase, ToolAxisDir.PlusZ);
        return (new[] { pos[0], pos[1], pos[2], frameBase[3], frameBase[4], frameBase[5] }, normalMm);
    }

    /// <summary>
    /// <see cref="TranslateAlongX"/> 가 버리는 옆(프레임 Y) 성분의 상한 [mm]. 이보다 크면 같은 그룹 task 가 실제로 옆으로
    /// 떨어져 있는 것으로 보고 버리지 않는다(<see cref="TranslateInPlane"/> 로 폴백).
    /// </summary>
    public const double MaxDroppedLateralMm = 10.0;

    /// <summary>
    /// anchor 공유 프레임을 다음 task 위치로 옮기되 <b>프레임 X(비드 두 점으로 잰 용접선 방향) 성분만</b> 쓴다.
    /// ACS 시작점 차이에는 맵 좌표·AMR 방향 오차로 옆(프레임 Y) 성분이 섞여, LINE 마다 그만큼 비드 옆으로 누적됐다
    /// (실기 2026-11-09 L4-R08C08: ACS 이동 방향이 비드 방향과 0.09~0.27° 달라 LINE 당 0.55~1.7mm). 면 법선(Z) 성분도 버린다.
    /// 옆 성분이 <see cref="MaxDroppedLateralMm"/> 를 넘으면 종전 면 평행 이동(<see cref="TranslateInPlane"/>)으로 폴백한다.
    /// </summary>
    /// <returns>(이동한 프레임, 프레임 Y 성분 mm, 프레임 Z 성분 mm, X 만 썼는지)</returns>
    public static (double[] Frame, double LateralMm, double NormalMm, bool AlongXOnly) TranslateAlongX(
        double[] frameBase, double[] fromStartBase, double[] toStartBase)
    {
        RequirePose(frameBase, nameof(frameBase));
        var m = FrameMath.PoseToMatrix(frameBase);
        var d = new[] { toStartBase[0] - fromStartBase[0], toStartBase[1] - fromStartBase[1], toStartBase[2] - fromStartBase[2] };
        double Comp(int col) => d[0] * m[0, col] + d[1] * m[1, col] + d[2] * m[2, col];
        var (along, lateral, normal) = (Comp(0), Comp(1), Comp(2));

        if (Math.Abs(lateral) > MaxDroppedLateralMm)
        {
            var (moved, _) = TranslateInPlane(frameBase, fromStartBase, toStartBase);
            return (moved, lateral, normal, false);
        }
        return (new[]
        {
            frameBase[0] + along * m[0, 0], frameBase[1] + along * m[1, 0], frameBase[2] + along * m[2, 0],
            frameBase[3], frameBase[4], frameBase[5],
        }, lateral, normal, true);
    }

    private static void RequirePose(double[] pose, string name)
    {
        if (pose is not { Length: 6 } || pose.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("포즈는 유한한 값 6개[x,y,z,rx,ry,rz]여야 합니다.", name);
    }
}
