using HD.AMR.App.Communication;
using HD.AMR.App.Models;
using HD.AMR.App.Service.Sequence;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// ACS 용접선 좌표(<c>seamStartW</c>/<c>seamEndW</c>, VDA5050 §8.1) → 코봇 BASE 위치·자세 환산.
///
/// 계약상 seam 좌표는 <b>맵(SLAM) 프레임</b>이고 AMR 은 도면을 해석하지 않는다(부록 B). 따라서 필요한 것은
/// 도면 정합이 아니라 AMR 자기 측위 pose 하나뿐이며, 변환 사슬은 다음과 같다:
///
///   p_B = (T_W_A · T_A_B)⁻¹ · p_W
///     T_W_A = AMR SLAM pose (맵 → AMR 차체, x·y·yaw)
///     T_A_B = 장착 보정 (AMR 차체 → 코봇 BASE) — tz 는 텔레스코픽 스트로크에 따라 변한다
///
/// <b>면 법선(wall_code).</b> ACS 는 벽 법선을 전송하지 않지만(§8.1 — 툴 자세는 AMR 책임) `wall_code` 와
/// 정차 노드 theta 를 합치면 법선이 정해진다:
///   · 방위각(azimuth) = 정차 노드 theta(벽 정면 방향). 맵 프레임과 탱크 자세를 잇는 유일한 값이다.
///   · 앙각(elevation) = `wall_code` 의 면 자세 — 수직벽 0°, 바닥 −90°, 천장 +90°, 하부/상부 챔퍼 ∓45°
///     (<see cref="SurfaceOrientation"/>, KC-2B 팔각 단면).
/// 이 법선으로 ① 접근점(standoff 후퇴)과 ② TOOL 자세(광축이 면을 바라보게)를 함께 만든다.
/// wall_code 가 없으면 종전대로 수평 후퇴만 하고 자세는 만들지 않는다(호출측이 현재 TCP 자세 유지).
///
/// <b>z 기준(중요).</b> ACS 의 T_W_D 는 2D(x,y,yaw) 강체변환이라 z 는 변환되지 않고 <b>도면 전역 z</b>
/// (선창 바닥 = 0)가 그대로 실려 온다. AMR 은 자기 바닥을 0 으로 보므로 L2 이상에서는 층 바닥 높이
/// (<c>level_z</c>)만큼 어긋난다 — <see cref="SeamBaseInput.ZDatumOffsetMm"/> 로 그 값을 빼서 맞춘다(§10 N17).
///
/// 이 클래스는 순수 계산만 한다(하드웨어 접촉 없음) — 시험 화면과 단위 테스트가 같은 경로를 쓴다.
/// </summary>
public static class SeamBaseTransform
{
    /// <summary>기본 standoff [mm] — 레시피 카메라 목표거리(400mm)와 같은 값.</summary>
    public const double DefaultStandoffMm = 400.0;

    /// <summary>이 값 미만의 standoff 는 면 간섭 위험으로 경고한다 [mm].</summary>
    public const double MinSafeStandoffMm = 100.0;

    /// <summary>
    /// 가정한 벽 정면 방향과 "코봇 BASE 에서 용접선을 본 방위"가 이 각도 넘게 벌어지면 경고 [도].
    /// 용접선은 그 벽 위에 있으므로 정상 정차라면 둘은 비슷해야 한다 — 크게 어긋나면 theta(또는 AMR yaw
    /// 폴백)가 실제 벽 방향이 아니라는 뜻이고, 그대로 두면 standoff·TOOL 자세가 통째로 돌아간다.
    /// </summary>
    public const double FacingMismatchWarnDeg = 60.0;

    public static SeamBaseTarget Resolve(SeamBaseInput input)
    {
        var notes = new List<string>();

        if (input.SeamStartW is not { Length: 3 })
            throw new ArgumentException("seamStartW 는 [x,y,z] 3요소여야 합니다.", nameof(input));
        if (input.MountAtHome is not { Length: 6 })
            throw new ArgumentException("T_A_B 는 6요소여야 합니다.", nameof(input));

        // ── ① seam 시작점: m → mm, z 기준 보정 ────────────────────────
        var seamMap = new[]
        {
            input.SeamStartW[0] * 1000.0,
            input.SeamStartW[1] * 1000.0,
            input.SeamStartW[2] * 1000.0 - input.ZDatumOffsetMm,
        };

        if (Math.Abs(input.ZDatumOffsetMm) < 1e-9)
            notes.Add("z 보정 0 — ACS z 가 도면 전역(선창 바닥) 기준이면 L2 이상에서 층 바닥 높이(level_z)만큼 " +
                      "높게 잡힙니다. 1층이 아니면 z 기준 오프셋을 입력하세요.");

        // ── ② 벽 정면 방향(방위각) — 노드 theta 우선, 없으면 AMR yaw ──
        var facing = input.WallFacingThetaRad ?? input.AmrYawRad;
        if (input.WallFacingThetaRad is null)
            notes.Add("노드 theta 미입력 — AMR yaw 를 벽 정면 방향으로 사용했습니다(정차 자세가 벽을 향한다는 전제).");

        // ── ③ 면 법선 — wall_code(앙각) × theta(방위각) ───────────────
        var wall = WallCodes.Find(input.WallCode);
        if (input.WallCode is { Length: > 0 } && wall is null)
            notes.Add($"미정의 wall_code '{input.WallCode}' — 법선을 만들 수 없어 수평 후퇴로 계산했습니다 " +
                      $"(정본 10코드: {string.Join("/", WallCodes.All.Select(w => w.Code))}).");

        // 면을 향하는(안쪽) 단위 법선. wall_code 가 없으면 수평(수직벽과 동일)으로 둔다 — 종전 동작 유지.
        var normal = SurfaceNormal(wall?.Orientation, facing);
        if (wall is null)
            notes.Add("wall_code 미지정 — 수직벽으로 가정해 수평으로만 물러납니다. TOOL 자세는 만들지 않습니다" +
                      "(호출측이 현재 자세 유지).");

        // ── ④ 접근점: 면 법선의 반대로 standoff 만큼 후퇴 ─────────────
        var approachMap = new[]
        {
            seamMap[0] - input.StandoffMm * normal[0],
            seamMap[1] - input.StandoffMm * normal[1],
            seamMap[2] - input.StandoffMm * normal[2],
        };

        if (input.StandoffMm < MinSafeStandoffMm)
            notes.Add($"standoff {input.StandoffMm:0}mm < {MinSafeStandoffMm:0}mm — 면 간섭 위험 구간입니다.");

        // ── ⑤ 변환 사슬 ──────────────────────────────────────────────
        var amrPose = MapCalibration.AmrPoseToMmDeg(input.AmrXm, input.AmrYm, input.AmrYawRad);
        var mount = MapCalibration.MountPoseAtStroke(input.MountAtHome, input.TelescopicStrokeMm);

        if (input.MountAtHome.All(v => Math.Abs(v) < 1e-9))
            notes.Add("T_A_B 가 전부 0 입니다 — 장착 보정 미수행. 환산 결과는 AMR 차체 기준과 같아 의미가 없습니다.");

        var seamBase = MapCalibration.MapPointToBase(amrPose, mount, seamMap);
        var approachBase = MapCalibration.MapPointToBase(amrPose, mount, approachMap);

        // ── ⑥ TOOL 자세 — 광축을 면 법선에 정렬 (wall_code 있을 때만) ─
        double[]? targetPose = null;
        double[]? toolXMap = null, toolYMap = null;
        if (wall is not null)
        {
            var tangent = SurfaceTangent(input, facing, normal);
            var rMap = ToolRotationMap(normal, tangent, input.OpticalAxis, input.ToolSpinDeg);
            var rBase = RotationToBase(amrPose, mount, rMap);
            targetPose = PoseFrom(approachBase, rBase);

            // 툴 X·Y 축의 맵 방향 — 오일러각(ry≈±90 근방에서 짐벌락으로 값이 요동친다)을 읽지 않고
            // "어디를 향하는가"로 확인할 수 있게 그대로 노출한다.
            toolXMap = new[] { rMap[0, 0], rMap[1, 0], rMap[2, 0] };
            toolYMap = new[] { rMap[0, 1], rMap[1, 1], rMap[2, 1] };
        }

        // ── ⑦ 벽 정면 방향 검증 ──────────────────────────────────────
        // 바닥·천장은 법선이 연직이라 방위각이 의미 없으므로 제외한다.
        if (wall?.Orientation is not (SurfaceOrientation.Floor or SurfaceOrientation.Ceiling))
        {
            var tWB = FrameMath.Multiply(FrameMath.PoseToMatrix(amrPose), FrameMath.PoseToMatrix(mount));
            var dx = seamMap[0] - tWB[0, 3];
            var dy = seamMap[1] - tWB[1, 3];
            if (Math.Sqrt(dx * dx + dy * dy) > 50.0)
            {
                var seamAzDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                var facingDeg = facing * 180.0 / Math.PI;
                var gap = Math.Abs(MapCalibration.NormalizeDeg(seamAzDeg - facingDeg));
                if (gap > FacingMismatchWarnDeg)
                    notes.Add(
                        $"벽 정면 방향({facingDeg:0.0}°)과 코봇 BASE→용접선 방위({seamAzDeg:0.0}°)가 {gap:0}° 어긋납니다 — " +
                        "용접선은 그 벽 위에 있어야 하므로 정상 정차라면 비슷해야 합니다. " +
                        "노드 theta 를 확인하세요(AMR 이 벽과 나란히 서 있으면 AMR yaw 폴백은 90° 틀립니다). " +
                        "지금 값 그대로면 standoff 후퇴 방향과 TOOL 자세가 함께 돌아갑니다.");
            }
        }

        // ── ⑧ 거리 진단 ──────────────────────────────────────────────
        var planar = Math.Sqrt(approachBase[0] * approachBase[0] + approachBase[1] * approachBase[1]);
        var dist = Math.Sqrt(planar * planar + approachBase[2] * approachBase[2]);

        InspectionMoveDirection? direction = null;
        string? directionReason = null;
        if (input.SeamEndW is { Length: 3 })
        {
            direction = SeamDirectionResolver.Resolve(input.SeamStartW, input.SeamEndW, facing, out var reason);
            directionReason = reason;
        }

        return new SeamBaseTarget(
            SeamStartMapMm: seamMap,
            ApproachMapMm: approachMap,
            SeamStartBaseMm: seamBase,
            ApproachBaseMm: approachBase,
            SurfaceNormalMap: normal,
            Surface: wall?.Orientation,
            TargetPoseBase: targetPose,
            ToolXMap: toolXMap,
            ToolYMap: toolYMap,
            MountUsed: mount,
            PlanarDistanceMm: planar,
            DistanceMm: dist,
            Direction: direction,
            DirectionReason: directionReason,
            Notes: notes);
    }

    // ── 면 기하 ──────────────────────────────────────────────────────

    /// <summary>
    /// 면을 향하는(안쪽) 단위 법선. 방위각은 <paramref name="facingRad"/>(벽 정면), 앙각은 면 자세에서 온다.
    /// 바닥/천장은 방위각과 무관하게 연직이고, 챔퍼는 45° 기운다(KC-2B 팔각 단면 — 전 챔퍼 45° 등각).
    /// </summary>
    public static double[] SurfaceNormal(SurfaceOrientation? orientation, double facingRad)
    {
        var c = Math.Cos(facingRad);
        var s = Math.Sin(facingRad);
        const double R = 0.70710678118654752;   // cos45° = sin45°

        return orientation switch
        {
            SurfaceOrientation.Floor => new[] { 0.0, 0.0, -1.0 },
            SurfaceOrientation.Ceiling => new[] { 0.0, 0.0, 1.0 },
            SurfaceOrientation.ChamferLower => new[] { c * R, s * R, -R },
            SurfaceOrientation.ChamferUpper => new[] { c * R, s * R, R },
            _ => new[] { c, s, 0.0 },           // Wall / Any / 미지정 — 수평
        };
    }

    /// <summary>
    /// 면 위에서 TOOL 회전의 기준이 될 접선. seamEnd 가 있으면 용접선 방향을, 없으면 벽면 수평 탄젠트를
    /// 면에 투영해 쓴다. 법선과 거의 평행하면(퇴화) 다른 축으로 대체한다.
    /// </summary>
    private static double[] SurfaceTangent(SeamBaseInput input, double facingRad, double[] normal)
    {
        double[] raw;
        if (input.SeamEndW is { Length: 3 } end)
        {
            raw = new[]
            {
                end[0] - input.SeamStartW[0],
                end[1] - input.SeamStartW[1],
                end[2] - input.SeamStartW[2],
            };
            if (Norm(raw) < 1e-6) raw = HorizontalTangent(facingRad);
        }
        else raw = HorizontalTangent(facingRad);

        var projected = Reject(raw, normal);
        if (Norm(projected) < 1e-6)
        {
            // 접선이 법선과 평행 — 바닥/천장에서 수평 탄젠트가 퇴화하는 경우는 없지만 방어.
            projected = Reject(new[] { 1.0, 0.0, 0.0 }, normal);
            if (Norm(projected) < 1e-6) projected = Reject(new[] { 0.0, 1.0, 0.0 }, normal);
        }
        return Normalize(projected);
    }

    /// <summary>벽면 수평 탄젠트 = 벽 정면의 좌회전 90° (SeamDirectionResolver 의 u 축과 같은 규약).</summary>
    private static double[] HorizontalTangent(double facingRad)
        => new[] { -Math.Sin(facingRad), Math.Cos(facingRad), 0.0 };

    // ── TOOL 자세 ────────────────────────────────────────────────────

    /// <summary>
    /// 맵 프레임 기준 TOOL 회전 행렬 — <paramref name="opticalAxis"/>(광축 툴축, 기본 +Z)가
    /// <paramref name="normal"/>(면을 향하는 법선)과 일치하도록 만든다. 남는 1자유도(광축 둘레 회전)는
    /// <paramref name="tangent"/> 를 기준으로 잡고 <paramref name="spinDeg"/> 만큼 더 돌린다.
    /// </summary>
    private static double[,] ToolRotationMap(double[] normal, double[] tangent, ToolAxisDir opticalAxis, double spinDeg)
    {
        // 목표 프레임 F: z=법선, x=접선, y=z×x (오른손).
        var fz = Normalize(normal);
        var fx = Normalize(Reject(tangent, fz));
        var fy = Cross(fz, fx);

        // spin: 광축 둘레 회전 — F 의 x·y 를 돌린다.
        var a = spinDeg * Math.PI / 180.0;
        var ca = Math.Cos(a);
        var sa = Math.Sin(a);
        var sx = new[] { fx[0] * ca + fy[0] * sa, fx[1] * ca + fy[1] * sa, fx[2] * ca + fy[2] * sa };
        var sy = Cross(fz, sx);

        var f = new double[3, 3];
        for (var i = 0; i < 3; i++) { f[i, 0] = sx[i]; f[i, 1] = sy[i]; f[i, 2] = fz[i]; }

        // 광축이 툴 +Z 가 아니면, 그 축을 +Z 로 보내는 고정 회전 A 를 끼운다 (R = F·A ⇒ R·a = F·e_z = 법선).
        var aMat = AxisToZ(opticalAxis);
        return Mul3(f, aMat);
    }

    /// <summary>툴축 <paramref name="axis"/> 를 +Z 로 보내는 회전(3×3). +Z 면 항등.</summary>
    private static double[,] AxisToZ(ToolAxisDir axis)
    {
        // 축 단위벡터 — enum 값/2 = 축 인덱스, 짝수=+/홀수=− (FlatSurfaceCenteringService.ApplyAxis 와 같은 규약).
        var a = new double[3];
        a[(int)axis / 2] = (int)axis % 2 == 0 ? 1.0 : -1.0;

        // a = +Z → 항등, a = −Z → X축 180°. 그 외는 (a × e_z) 축으로 acos(a·e_z) 회전.
        if (Math.Abs(a[2] - 1.0) < 1e-12) return Identity3();
        if (Math.Abs(a[2] + 1.0) < 1e-12) return Rodrigues(new[] { 1.0, 0.0, 0.0 }, Math.PI);

        var axisVec = Cross(a, new[] { 0.0, 0.0, 1.0 });
        var angle = Math.Acos(Math.Clamp(a[2], -1.0, 1.0));
        return Rodrigues(Normalize(axisVec), angle);
    }

    /// <summary>맵 기준 회전 → 코봇 BASE 기준 회전. R_B = R_WB⁻¹ · R_W (R_WB = T_W_A·T_A_B 의 회전부).</summary>
    private static double[,] RotationToBase(double[] amrPose, double[] mount, double[,] rMap)
    {
        var tWB = FrameMath.Multiply(FrameMath.PoseToMatrix(amrPose), FrameMath.PoseToMatrix(mount));
        var r = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                double sum = 0;
                for (var k = 0; k < 3; k++) sum += tWB[k, i] * rMap[k, j];   // Rᵀ · R_map
                r[i, j] = sum;
            }
        return r;
    }

    /// <summary>위치(mm) + 회전(3×3) → FAIRINO pose [x,y,z,rx,ry,rz](mm/도, FrameMath ZYX 규약).</summary>
    private static double[] PoseFrom(double[] position, double[,] rotation)
    {
        var m = new double[4, 4];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                m[i, j] = rotation[i, j];
        m[0, 3] = position[0];
        m[1, 3] = position[1];
        m[2, 3] = position[2];
        m[3, 3] = 1.0;
        return FrameMath.MatrixToPose(m);
    }

    // ── 3D 벡터·행렬 소도구 ──────────────────────────────────────────

    private static double Norm(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

    private static double[] Normalize(double[] v)
    {
        var n = Norm(v);
        return n < 1e-12 ? new[] { 0.0, 0.0, 1.0 } : new[] { v[0] / n, v[1] / n, v[2] / n };
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static double[] Cross(double[] a, double[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    };

    /// <summary>v 에서 축 <paramref name="axis"/>(단위) 성분을 제거 — 면 위로의 투영.</summary>
    private static double[] Reject(double[] v, double[] axis)
    {
        var d = Dot(v, axis);
        return new[] { v[0] - d * axis[0], v[1] - d * axis[1], v[2] - d * axis[2] };
    }

    private static double[,] Identity3()
    {
        var m = new double[3, 3];
        m[0, 0] = m[1, 1] = m[2, 2] = 1.0;
        return m;
    }

    /// <summary>로드리게스 회전(단위 축, 라디안).</summary>
    private static double[,] Rodrigues(double[] axis, double angle)
    {
        double x = axis[0], y = axis[1], z = axis[2];
        double c = Math.Cos(angle), s = Math.Sin(angle), t = 1 - c;
        return new[,]
        {
            { t * x * x + c,     t * x * y - s * z, t * x * z + s * y },
            { t * x * y + s * z, t * y * y + c,     t * y * z - s * x },
            { t * x * z - s * y, t * y * z + s * x, t * z * z + c     },
        };
    }

    private static double[,] Mul3(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                double sum = 0;
                for (var k = 0; k < 3; k++) sum += a[i, k] * b[k, j];
                r[i, j] = sum;
            }
        return r;
    }
}

/// <summary>
/// <see cref="SeamBaseTransform.Resolve"/> 입력. 좌표 단위는 계약(부록 B)대로 seam 은 m, 나머지는 mm/도.
/// </summary>
/// <param name="SeamStartW">용접선 시작점 [x,y,z] m — 맵(SLAM) 좌표, z 는 도면 전역 z.</param>
/// <param name="SeamEndW">용접선 끝점 [x,y,z] m — 방향 유도·TOOL 회전 기준(선택).</param>
/// <param name="AmrXm">AMR SLAM x [m].</param>
/// <param name="AmrYm">AMR SLAM y [m].</param>
/// <param name="AmrYawRad">AMR SLAM yaw [rad] — 맵 X축 기준 CCW.</param>
/// <param name="MountAtHome">T_A_B [x,y,z,rx,ry,rz] mm/도 — <b>완전 하강(스트로크 0) 기준 저장값</b>.</param>
/// <param name="TelescopicStrokeMm">현재 텔레스코픽 스트로크 [mm, 완전 하강 = 0].</param>
/// <param name="ZDatumOffsetMm">도면 전역 z → AMR 바닥 기준 보정 [mm] — 해당 층 바닥 높이(level_z)를 넣는다(뺄셈).</param>
/// <param name="StandoffMm">면 이격 [mm] — 접근점을 면 법선 반대로 이만큼 물린다.</param>
/// <param name="WallFacingThetaRad">벽 정면 방향 [rad] — 정차 노드 theta. null 이면 AMR yaw 사용.</param>
/// <param name="WallCode">ACS `drawingPos.wall_code` — 면 자세(법선 앙각) 결정. null/미정의면 자세 미생성.</param>
/// <param name="OpticalAxis">광축(대상을 향하는) 툴축 — 카메라 페이지 설정값, 기본 +Z.</param>
/// <param name="ToolSpinDeg">광축 둘레 추가 회전 [도] — 접선 기준 0°.</param>
public sealed record SeamBaseInput(
    double[] SeamStartW,
    double[]? SeamEndW,
    double AmrXm,
    double AmrYm,
    double AmrYawRad,
    double[] MountAtHome,
    double TelescopicStrokeMm,
    double ZDatumOffsetMm,
    double StandoffMm,
    double? WallFacingThetaRad,
    string? WallCode = null,
    ToolAxisDir OpticalAxis = ToolAxisDir.PlusZ,
    double ToolSpinDeg = 0);

/// <summary>환산 결과. 좌표는 전부 mm.</summary>
/// <param name="SeamStartMapMm">z 보정까지 반영한 맵 좌표 용접선 시작점.</param>
/// <param name="ApproachMapMm">standoff 를 적용한 맵 좌표 접근점.</param>
/// <param name="SeamStartBaseMm">코봇 BASE 기준 용접선 시작점(참고 — 면 위라 이동 목표 아님).</param>
/// <param name="ApproachBaseMm">코봇 BASE 기준 접근점 — <b>이동 목표 위치</b>.</param>
/// <param name="SurfaceNormalMap">면을 향하는 단위 법선(맵 프레임).</param>
/// <param name="Surface">wall_code 가 가리키는 면 자세. 미지정·미정의면 null.</param>
/// <param name="TargetPoseBase">BASE 기준 이동 목표 pose [x,y,z,rx,ry,rz] — 광축이 법선을 향한다.
/// wall_code 가 없으면 null(호출측이 현재 TCP 자세 유지).</param>
/// <param name="ToolXMap">목표 자세의 툴 X 축(맵 프레임 단위벡터). 오일러각 짐벌락과 무관한 확인용.</param>
/// <param name="ToolYMap">목표 자세의 툴 Y 축(맵 프레임 단위벡터).</param>
/// <param name="MountUsed">스트로크를 반영해 실제 사용한 T_A_B.</param>
/// <param name="PlanarDistanceMm">BASE 원점에서 접근점까지 수평 거리 — 리치 판단용.</param>
/// <param name="DistanceMm">BASE 원점에서 접근점까지 3D 거리.</param>
/// <param name="Direction">seamEnd 를 준 경우의 검사 방향 유도 결과(§4.4).</param>
/// <param name="DirectionReason">방향 판정 근거 문자열.</param>
/// <param name="Notes">해석 시 붙은 경고·주의 문구.</param>
public sealed record SeamBaseTarget(
    double[] SeamStartMapMm,
    double[] ApproachMapMm,
    double[] SeamStartBaseMm,
    double[] ApproachBaseMm,
    double[] SurfaceNormalMap,
    SurfaceOrientation? Surface,
    double[]? TargetPoseBase,
    double[]? ToolXMap,
    double[]? ToolYMap,
    double[] MountUsed,
    double PlanarDistanceMm,
    double DistanceMm,
    InspectionMoveDirection? Direction,
    string? DirectionReason,
    IReadOnlyList<string> Notes);
