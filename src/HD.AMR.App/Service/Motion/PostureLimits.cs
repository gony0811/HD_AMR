namespace HD.AMR.App.Service.Motion;

/// <summary>
/// 코봇 자세 허용 범위 — <b>컨트롤러 기본 한계가 아니라 "이 장비에서 실제로 써도 되는" 범위</b>.
///
/// 플랜지에 붙은 검사 툴(카메라·레이저·브래킷·케이블)은 로봇 본체보다 먼저 한계를 만든다:
///  · 툴이 링크에 닿아 J5/J4 를 끝까지 못 돌린다 → 관절 소프트리밋이 하드웨어보다 좁다.
///  · 케이블이 감겨 J6 회전이 제한된다 → J6 은 홈 기준 ± 범위로 따로 묶는다.
///  · 손목 특이점(J5≈0) 근방에서는 직선 이동이 rc=38 로 거부되거나 손목이 급회전한다 → |J5| 최소 여유.
///
/// 이 값들은 "도달 가능한가"(역기구학)와 별개다. IK 는 해를 주지만 그 해가 이 범위 밖이면 쓰면 안 된다.
/// <see cref="ApproachPlanner"/> 가 이 판정을 목적함수로 써서 쓸 수 있는 자세를 고른다.
///
/// 특이점 규약은 FAIRINO FR 계열(UR 형 6축)을 전제로 한다:
///  · <b>손목</b> J5 = 0 — 4축과 6축이 평행해져 1자유도 상실. 가장 자주 걸린다.
///  · <b>팔꿈치</b> J3 = 0 — 팔이 완전히 펴진 자세.
///  · <b>어깨</b> 손목 중심이 J1 축 위 — 평면 반경으로 근사 판정한다(정확 판정은 로컬 기구학 필요).
/// </summary>
/// <param name="JointMinDeg">관절별 사용 하한 [도] — 6요소.</param>
/// <param name="JointMaxDeg">관절별 사용 상한 [도] — 6요소.</param>
/// <param name="WristMarginDeg">|J5| 최소 여유 [도]. 이보다 0 에 가까우면 손목 특이점 구간으로 본다.</param>
/// <param name="ElbowMarginDeg">|J3| 최소 여유 [도]. 팔이 완전히 펴진 자세(J3≈0) 회피.</param>
/// <param name="ShoulderRadiusMinMm">목표점의 BASE 수평 반경 최소값 [mm]. 이보다 가까우면 어깨 특이점 구간.</param>
/// <param name="MaxJointTravelDeg">한 번의 관절 이동에서 허용할 최대 관절 변화 [도] — 크게 휘두르는 경로 차단.</param>
public sealed record PostureLimits(
    double[] JointMinDeg,
    double[] JointMaxDeg,
    double WristMarginDeg = 15.0,
    double ElbowMarginDeg = 8.0,
    double ShoulderRadiusMinMm = 180.0,
    double MaxJointTravelDeg = 200.0)
{
    /// <summary>
    /// 기본값 — FR 계열 하드웨어 범위(±175°)에 툴 없는 상태를 가정한다. <b>실장 후에는 반드시 좁혀서
    /// 저장할 것</b>: 툴이 링크에 닿는 각을 조그로 찾아 그 안쪽으로 넣는다.
    /// </summary>
    public static PostureLimits Default => new(
        JointMinDeg: new[] { -175.0, -175.0, -160.0, -175.0, -175.0, -175.0 },
        JointMaxDeg: new[] { 175.0, 175.0, 160.0, 175.0, 175.0, 175.0 });

    public PostureLimits Normalized()
    {
        // 역직렬화로 null·길이 불일치가 들어올 수 있다 — 기본 범위로 대체하되 여유 설정은 살린다.
        if (JointMinDeg is not { Length: 6 } || JointMaxDeg is not { Length: 6 })
            return Default with
            {
                WristMarginDeg = WristMarginDeg,
                ElbowMarginDeg = ElbowMarginDeg,
                ShoulderRadiusMinMm = ShoulderRadiusMinMm,
                MaxJointTravelDeg = MaxJointTravelDeg,
            };

        var min = JointMinDeg.ToArray();
        var max = JointMaxDeg.ToArray();

        // min>max 로 저장된 값은 뒤집어 받아 준다 — 입력 실수로 전부 불가가 되는 것을 막는다.
        for (var i = 0; i < 6; i++)
            if (min[i] > max[i]) (min[i], max[i]) = (max[i], min[i]);

        return this with { JointMinDeg = min, JointMaxDeg = max };
    }

    /// <summary>
    /// 관절각 한 벌이 이 범위 안에 있는지와 <b>여유(margin)</b>를 계산한다.
    /// 여유는 모든 제약의 슬랙 중 최소값 [도]이며, 음수면 그 제약을 위반한 것이다.
    /// </summary>
    /// <param name="jointsDeg">관절각 [도] 6요소.</param>
    /// <param name="planarRadiusMm">목표점의 BASE 수평 반경 [mm] — 어깨 특이점 근사 판정. null 이면 생략.</param>
    /// <param name="fromJointsDeg">출발 관절각 [도] — 주면 이동량(MaxJointTravelDeg)까지 함께 본다.</param>
    public PostureMargin Evaluate(double[] jointsDeg, double? planarRadiusMm = null, double[]? fromJointsDeg = null)
    {
        if (jointsDeg is not { Length: 6 })
            return new PostureMargin(false, double.NegativeInfinity, "관절각 6요소가 아님", Array.Empty<double>(), 0, 0, 0);

        var n = Normalized();
        var slack = double.PositiveInfinity;
        var limiting = "여유 있음";
        var perJoint = new double[6];

        for (var i = 0; i < 6; i++)
        {
            var lo = jointsDeg[i] - n.JointMinDeg[i];
            var hi = n.JointMaxDeg[i] - jointsDeg[i];
            perJoint[i] = Math.Min(lo, hi);
            if (perJoint[i] < slack)
            {
                slack = perJoint[i];
                limiting = $"J{i + 1} 관절한계 ({jointsDeg[i]:0.0}° ∈ [{n.JointMinDeg[i]:0}, {n.JointMaxDeg[i]:0}])";
            }
        }

        // 손목 특이점 — |J5| 가 0 에서 얼마나 떨어져 있나.
        var wrist = Math.Abs(jointsDeg[4]);
        var wristSlack = wrist - n.WristMarginDeg;
        if (wristSlack < slack)
        {
            slack = wristSlack;
            limiting = $"손목 특이점 (|J5| {wrist:0.0}° < 여유 {n.WristMarginDeg:0}°)";
        }

        // 팔꿈치 특이점 — 완전히 편 자세(J3≈0).
        var elbow = Math.Abs(jointsDeg[2]);
        var elbowSlack = elbow - n.ElbowMarginDeg;
        if (elbowSlack < slack)
        {
            slack = elbowSlack;
            limiting = $"팔꿈치 특이점 (|J3| {elbow:0.0}° < 여유 {n.ElbowMarginDeg:0}°)";
        }

        // 어깨 특이점 — 목표가 BASE 수직축에 너무 가까움(근사).
        var shoulderSlack = double.PositiveInfinity;
        if (planarRadiusMm is { } r)
        {
            // mm 를 도와 직접 비교할 수 없으므로 "부족분 1mm = 0.1°" 로 환산해 한 축의 스칼라로 합친다.
            shoulderSlack = (r - n.ShoulderRadiusMinMm) * 0.1;
            if (shoulderSlack < slack)
            {
                slack = shoulderSlack;
                limiting = $"어깨 특이점 (BASE 수평 반경 {r:0}mm < {n.ShoulderRadiusMinMm:0}mm)";
            }
        }

        // 이동량 — 특이점은 아니지만 "손목을 한 바퀴 휘두르는" 해를 배제한다.
        var travel = 0.0;
        if (fromJointsDeg is { Length: 6 })
        {
            for (var i = 0; i < 6; i++) travel = Math.Max(travel, Math.Abs(jointsDeg[i] - fromJointsDeg[i]));
            var travelSlack = n.MaxJointTravelDeg - travel;
            if (travelSlack < slack)
            {
                slack = travelSlack;
                limiting = $"관절 이동량 과다 (최대 {travel:0.0}° > 허용 {n.MaxJointTravelDeg:0}°)";
            }
        }

        return new PostureMargin(slack > 0, slack, slack > 0 ? "여유 있음" : limiting, perJoint, wrist, elbow, travel);
    }

    /// <summary>
    /// 두 관절각 사이 <b>직선 관절 보간(MoveJ) 경로 전체</b>가 범위 안인지. 관절 이동은 각 축이
    /// 두 끝값 사이에서 <b>단조</b>로 변하므로, 끝점 두 개만 통과하면 중간도 통과한다 —
    /// 직교 직선 이동(MoveL)이 중간에서 J5 를 0 으로 쓸고 지나가는 것과 결정적으로 다른 점이다.
    /// (단 어깨 반경은 위치 보간이 아니라 근사이므로 여기서는 보지 않는다.)
    /// </summary>
    public bool JointPathWithin(double[] fromDeg, double[] toDeg)
    {
        if (fromDeg is not { Length: 6 } || toDeg is not { Length: 6 }) return false;
        if (!Evaluate(fromDeg).Feasible) return false;
        if (!Evaluate(toDeg, fromJointsDeg: fromDeg).Feasible) return false;

        // J5 가 양 끝에서 같은 부호여야 도중에 0(손목 특이점)을 지나지 않는다 — 관절 보간의 단조성.
        return Math.Sign(fromDeg[4]) == Math.Sign(toDeg[4]) && Math.Sign(toDeg[4]) != 0;
    }
}

/// <summary>자세 한 벌의 여유 평가 결과.</summary>
/// <param name="Feasible">모든 제약을 만족하는가.</param>
/// <param name="MarginDeg">가장 빠듯한 제약의 슬랙 [도] — 클수록 안전. 음수면 위반.</param>
/// <param name="Limiting">가장 빠듯한 제약의 설명.</param>
/// <param name="JointMarginDeg">관절별 한계까지 남은 여유 [도].</param>
/// <param name="WristDeg">|J5| [도].</param>
/// <param name="ElbowDeg">|J3| [도].</param>
/// <param name="TravelDeg">출발 자세 대비 최대 관절 변화 [도] — 출발값을 주지 않았으면 0.</param>
public sealed record PostureMargin(
    bool Feasible,
    double MarginDeg,
    string Limiting,
    double[] JointMarginDeg,
    double WristDeg,
    double ElbowDeg,
    double TravelDeg);
