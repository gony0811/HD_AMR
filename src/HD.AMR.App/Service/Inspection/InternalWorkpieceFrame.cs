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
    /// <param name="forceAlongX">true 면 옆 성분이 커도 폴백하지 않는다 — 맵(도면) 좌표로 같은 용접선 줄임을 확인했을 때
    /// (<see cref="MapSeamOffsetMm"/>). BASE 로 바꾼 ACS 시작점은 AMR 위치·방향 오차를 그때그때 따로 먹어 옆 성분이
    /// 수십 mm 생길 수 있다(실기 2026-11-19 PU L4-R03C25 -3·-4: 도면상 한 줄인데 28.8·33.9mm → 카메라가 비드 옆을 찍음).</param>
    /// <returns>(이동한 프레임, 프레임 Y 성분 mm, 프레임 Z 성분 mm, X 만 썼는지)</returns>
    public static (double[] Frame, double LateralMm, double NormalMm, bool AlongXOnly) TranslateAlongX(
        double[] frameBase, double[] fromStartBase, double[] toStartBase, bool forceAlongX = false)
    {
        RequirePose(frameBase, nameof(frameBase));
        var m = FrameMath.PoseToMatrix(frameBase);
        var d = new[] { toStartBase[0] - fromStartBase[0], toStartBase[1] - fromStartBase[1], toStartBase[2] - fromStartBase[2] };
        double Comp(int col) => d[0] * m[0, col] + d[1] * m[1, col] + d[2] * m[2, col];
        var (along, lateral, normal) = (Comp(0), Comp(1), Comp(2));

        if (!forceAlongX && Math.Abs(lateral) > MaxDroppedLateralMm)
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

    /// <summary>다른 줄 좌표계 재사용 파라미터 키 — true 면 같은 지령·같은 정차·같은 면에서 용접선 줄이나 방향(수직↔수평)이
    /// 바뀌어도 재교시하지 않고 <b>하나의</b> 좌표계를 옮겨 쓴다(<see cref="MoveToOtherLine"/>). 면 높이와 기울기는 줄마다
    /// 시작·끝점 레이저 측정으로 보정한다(<see cref="CorrectBySurfaceHeights"/>). 기본 true — false 면 줄이 바뀔 때 재교시.</summary>
    public const string ReuseAcrossLinesKey = "inspection.reuseFrameAcrossLines";

    /// <summary>다른 줄 재사용에서 용접선 방향이 직전 좌표계 축(0·90·180·270°)에서 벗어나도 되는 한계 [도].</summary>
    public const double CrossLineAxisTolDeg = 20.0;

    /// <summary>
    /// 두 용접선(시작→끝, 맵 좌표 m)의 방향 차가 0·90·180° 중 가장 가까운 것에서 벗어난 각 [도]. 멤브레인 용접선은 격자라
    /// 이 값이 작아야 좌표계를 90° 단위로 돌려 물려줄 수 있다. 끝점이 없거나 길이가 0 이면 null.
    /// </summary>
    public static double? GridAngleErrorDeg(double[]? startA, double[]? endA, double[]? startB, double[]? endB)
    {
        if (startA is not { Length: 3 } || endA is not { Length: 3 } || startB is not { Length: 3 } || endB is not { Length: 3 })
            return null;
        double ax = endA[0] - startA[0], ay = endA[1] - startA[1], az = endA[2] - startA[2];
        double bx = endB[0] - startB[0], by = endB[1] - startB[1], bz = endB[2] - startB[2];
        var la = Math.Sqrt(ax * ax + ay * ay + az * az);
        var lb = Math.Sqrt(bx * bx + by * by + bz * bz);
        if (la < 1e-6 || lb < 1e-6) return null;
        var deg = Math.Acos(Math.Clamp((ax * bx + ay * by + az * bz) / (la * lb), -1, 1)) * 180.0 / Math.PI;
        return Math.Min(Math.Min(deg, Math.Abs(deg - 90)), Math.Abs(deg - 180));
    }

    /// <summary>
    /// 직전 좌표계를 <b>다른 줄</b>의 용접선으로 옮긴다(재교시 없이) — ① 원점을 두 시작 접근점 차이만큼 면을 따라 옮기고
    /// (법선 성분은 버림) ② 프레임 Z(면 법선) 둘레로 90° 단위로 돌려 X 를 이번 용접선 방향(<paramref name="seamDirBase"/>,
    /// 시작→끝)에 맞춘다. 방향은 직전 좌표계의 축을 따르고 ACS 방향은 어느 축인지 고르는 데만 쓴다.
    /// 면 높이·기울기는 여기서 바꾸지 않는다 — 시작·끝점 레이저 측정(<see cref="CorrectBySurfaceHeights"/>)이 보정한다.
    /// </summary>
    /// <returns>(옮긴 프레임, 돌린 각 °, 축에서 벗어난 각 °, 버린 법선 성분 mm). 방향이 축에서
    /// <see cref="CrossLineAxisTolDeg"/> 넘게 벗어나거나 면에 수직이면 Frame = null.</returns>
    public static (double[]? Frame, double RotatedDeg, double AxisErrorDeg, double NormalMm) MoveToOtherLine(
        double[] frameBase, double[] fromStartBase, double[] toStartBase, double[] seamDirBase)
    {
        RequirePose(frameBase, nameof(frameBase));
        var m = FrameMath.PoseToMatrix(frameBase);
        double Comp(int col) => seamDirBase[0] * m[0, col] + seamDirBase[1] * m[1, col] + seamDirBase[2] * m[2, col];
        var (a, b) = (Comp(0), Comp(1));
        if (Math.Sqrt(a * a + b * b) < 1e-6) return (null, 0, 90, 0);

        var phi = Math.Atan2(b, a) * 180.0 / Math.PI;          // 프레임 X → 용접선 방향, Z 둘레 [도]
        var snapped = Math.Round(phi / 90.0) * 90.0;
        var err = phi - snapped;
        if (Math.Abs(err) > CrossLineAxisTolDeg) return (null, snapped, err, 0);

        var (moved, normalMm) = TranslateInPlane(frameBase, fromStartBase, toStartBase);
        var rotated = FrameMath.FromFrame(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, snapped }, moved);
        return (new[] { moved[0], moved[1], moved[2], rotated[3], rotated[4], rotated[5] }, snapped, err, normalMm);
    }

    /// <summary>
    /// 다른 줄 좌표계 재사용에서 미리 주는 <b>수직 방향 정적 기울기</b> [도] 파라미터 키. 수평 줄 교시는 수직 방향 기울기를 재지 않아
    /// 좌표계를 위아래로 멀리 옮기면 그만큼 벽에서 벗어난다(2026-11-23 M2: 1.6m 아래에서 37~44mm, 평균 1.3~1.6°).
    /// + = 위(BASE +Z)로 갈수록 벽이 로봇 쪽으로 다가온다(= 로봇이 벽 쪽으로 기울었다). 0 또는 빈 값 = 끔. ±<see cref="MaxVerticalTiltDeg"/> 밖이면 끔.
    /// </summary>
    public const string VerticalTiltKey = "inspection.reuseFrameVerticalTiltDeg";
    public const double MaxVerticalTiltDeg = 5.0;

    /// <summary>Bag 키(double, 도) — 이번에 맞출 수직 방향 정적 기울기(<see cref="VerticalTiltKey"/>). 오케스트레이터가 넣는다.</summary>
    public const string VerticalTiltTargetBagKey = "wobj.verticalTiltTargetDeg";

    /// <summary>Bag 키(double, 도) — Bag 의 좌표계에 이미 들어 있는 수직 방향 정적 기울기. 물려받을 때 두 번 주지 않게 좌표계와 함께 넘긴다.</summary>
    public const string VerticalTiltAppliedBagKey = "wobj.verticalTiltAppliedDeg";

    /// <summary>파라미터 값 → 쓸 기울기 [도]. 비었거나 숫자가 아니거나 한계 밖이면 0(끔).</summary>
    public static double ResolveVerticalTiltDeg(double? value)
        => value is { } v && double.IsFinite(v) && Math.Abs(v) <= MaxVerticalTiltDeg ? v : 0.0;

    /// <summary>
    /// 좌표계 면을 <paramref name="pivotBase"/> 를 지나는 <b>수평축</b>(면 안에 놓인 수평 방향) 둘레로 <paramref name="deg"/> 만큼 기울인다.
    /// + 는 위(BASE +Z)로 갈수록 면이 프레임 +Z(벽 바깥, 로봇 쪽)로 나오는 방향 — 피벗보다 아래에서는 그만큼 벽 쪽으로 들어간다.
    /// 좌표계 전체(원점·세 축)를 같이 돌리므로 면 위의 거리·각도는 그대로다. 면이 수평(바닥·천장)이면 수직 방향이 없어 그대로 돌려준다.
    /// </summary>
    public static double[] TiltAboutHorizontal(double[] frameBase, double[] pivotBase, double deg)
    {
        RequirePose(frameBase, nameof(frameBase));
        var m = FrameMath.PoseToMatrix(frameBase);
        double[] n = { m[0, 2], m[1, 2], m[2, 2] };
        // h = up × n — 면 안의 수평 방향. 이 축 둘레 +회전은 면 안의 위쪽(u = n × h)을 n 쪽으로 보낸다.
        double[] h = { -n[1], n[0], 0.0 };
        var hl = Math.Sqrt(h[0] * h[0] + h[1] * h[1]);
        if (hl < 1e-6 || Math.Abs(deg) < 1e-12) return (double[])frameBase.Clone();
        h[0] /= hl; h[1] /= hl;

        var a = deg * Math.PI / 180.0;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        double[] Rot(double[] v)   // 로드리게스 회전(축 h, 각 a)
        {
            var dot = h[0] * v[0] + h[1] * v[1] + h[2] * v[2];
            double[] cr = { h[1] * v[2] - h[2] * v[1], h[2] * v[0] - h[0] * v[2], h[0] * v[1] - h[1] * v[0] };
            return new[]
            {
                v[0] * c + cr[0] * s + h[0] * dot * (1 - c),
                v[1] * c + cr[1] * s + h[1] * dot * (1 - c),
                v[2] * c + cr[2] * s + h[2] * dot * (1 - c),
            };
        }

        var r = (double[,])m.Clone();
        for (var col = 0; col < 3; col++)
        {
            var v = Rot(new[] { m[0, col], m[1, col], m[2, col] });
            for (var i = 0; i < 3; i++) r[i, col] = v[i];
        }
        var o = Rot(new[] { frameBase[0] - pivotBase[0], frameBase[1] - pivotBase[1], frameBase[2] - pivotBase[2] });
        for (var i = 0; i < 3; i++) r[i, 3] = pivotBase[i] + o[i];
        return FrameMath.MatrixToPose(r);
    }

    /// <summary>점의 프레임 좌표(X 방향 거리, Z 방향 높이) [mm].</summary>
    public static (double Along, double Height) AlongAndHeight(double[] frameBase, double[] pointBase)
    {
        RequirePose(frameBase, nameof(frameBase));
        var m = FrameMath.PoseToMatrix(frameBase);
        double[] d = { pointBase[0] - frameBase[0], pointBase[1] - frameBase[1], pointBase[2] - frameBase[2] };
        return (d[0] * m[0, 0] + d[1] * m[1, 0] + d[2] * m[2, 0], d[0] * m[0, 2] + d[1] * m[1, 2] + d[2] * m[2, 2]);
    }

    /// <summary>
    /// 줄 시작·끝 면 실측점(레이저 평면 중심, BASE mm)으로 좌표계 높이와 기울기를 맞춘다.
    /// ① 기울기 — 두 점의 높이 차를 X 방향 거리로 나눈 만큼 좌표계를 Y 축 둘레로 돌려 X 가 면을 따라 눕게 한다.
    /// ② 높이 — 돌린 좌표계에서 시작 측정점의 높이가 <paramref name="referenceHeightMm"/>(교시한 줄에서 같은 방법으로 잰 면 높이)가
    /// 되도록 원점을 Z 로 옮긴다. 기준이 없으면 시작 측정점 높이를 그대로 둔다(기울기만 보정).
    /// 자세는 Y 축 둘레 회전만 바뀌고 Y 축은 그대로다.
    /// </summary>
    /// <returns>(보정한 프레임, 높이 보정 mm(+ = 면이 생각보다 바깥), 기울기 보정 °(+ = X 끝이 바깥으로 들림), 시작→끝 높이 차 mm, X 거리 mm).
    /// 두 측정점의 X 거리가 <paramref name="minSpanMm"/> 미만이면 기울기는 0 으로 두고 높이만 맞춘다.</returns>
    public static (double[] Frame, double OffsetMm, double TiltDeg, double RiseMm, double SpanMm) CorrectBySurfaceHeights(
        double[] frameBase, double[] startPointBase, double[]? endPointBase, double? referenceHeightMm, double minSpanMm = 50.0)
    {
        RequirePose(frameBase, nameof(frameBase));
        var (a0, h0) = AlongAndHeight(frameBase, startPointBase);
        double rise = 0, span = 0, slope = 0;
        if (endPointBase is { Length: >= 3 })
        {
            var (a1, h1) = AlongAndHeight(frameBase, endPointBase);
            span = a1 - a0;
            rise = h1 - h0;
            if (Math.Abs(span) >= minSpanMm) slope = rise / span;
        }

        var f = FrameMath.PoseToMatrix(frameBase);
        double[] x = { f[0, 0], f[1, 0], f[2, 0] };
        double[] y = { f[0, 1], f[1, 1], f[2, 1] };
        double[] z = { f[0, 2], f[1, 2], f[2, 2] };
        // Y 축 둘레 회전 — X' = X + slope·Z, Z' = Z − slope·X (정규화). Y 는 그대로라 오른손 좌표계가 유지된다.
        var n = Math.Sqrt(1 + slope * slope);
        double[] nx = new double[3], nz = new double[3];
        for (var i = 0; i < 3; i++)
        {
            nx[i] = (x[i] + slope * z[i]) / n;
            nz[i] = (z[i] - slope * x[i]) / n;
        }

        // 돌린 좌표계에서 시작 측정점이 기준 높이에 오게 원점을 새 Z 로 옮긴다.
        double[] o = { frameBase[0], frameBase[1], frameBase[2] };
        var target = referenceHeightMm ?? h0;
        double hRot = 0;
        for (var i = 0; i < 3; i++) hRot += (startPointBase[i] - o[i]) * nz[i];
        var shift = hRot - target;
        var m = new double[4, 4];
        for (var i = 0; i < 3; i++)
        {
            m[i, 0] = nx[i]; m[i, 1] = y[i]; m[i, 2] = nz[i];
            m[i, 3] = o[i] + shift * nz[i];
        }
        m[3, 3] = 1;
        var pose = FrameMath.MatrixToPose(m);
        return (pose, h0 - target, Math.Atan(slope) * 180.0 / Math.PI, rise, span);
    }

    /// <summary>
    /// 맵(ACS 도면) 좌표로 본 이번 용접선 시작점의 위치 — 기준 용접선(시작→끝) 직선을 따라 간 거리와 직선에서 벗어난 거리 [mm].
    /// 맵 좌표는 AMR 위치 오차가 섞이지 않아 같은 줄인지 판단하는 데 쓴다. 입력은 m 단위 [x,y,z], 기준 용접선이 1mm 미만이면 null.
    /// </summary>
    public static (double AlongMm, double LateralMm)? MapSeamOffsetMm(double[]? anchorStartW, double[]? anchorEndW, double[]? startW)
    {
        if (anchorStartW is not { Length: 3 } a || anchorEndW is not { Length: 3 } b || startW is not { Length: 3 } p) return null;
        var dir = new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
        var len = Math.Sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
        if (!(len > 1e-3)) return null;
        var d = new[] { p[0] - a[0], p[1] - a[1], p[2] - a[2] };
        var along = (d[0] * dir[0] + d[1] * dir[1] + d[2] * dir[2]) / len;
        var off = new[] { d[0] - dir[0] / len * along, d[1] - dir[1] / len * along, d[2] - dir[2] / len * along };
        var lateral = Math.Sqrt(off[0] * off[0] + off[1] * off[1] + off[2] * off[2]);
        return double.IsFinite(along) && double.IsFinite(lateral) ? (along * 1000, lateral * 1000) : null;
    }

    private static void RequirePose(double[] pose, string name)
    {
        if (pose is not { Length: 6 } || pose.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("포즈는 유한한 값 6개[x,y,z,rx,ry,rz]여야 합니다.", name);
    }
}
