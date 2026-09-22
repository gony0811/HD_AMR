using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// `startWeldInspection` 액션의 <see cref="VdaAction.ActionParameters"/>(사양 §8.1 — jobRef/taskId/attempt/
/// position/params 5쌍)를 <see cref="WeldInspectionRequest"/>로 해석한다.
///
/// value 는 object 발행이 기본(§8.3)이라 역직렬화 시 <see cref="JsonElement"/>로 들어오지만,
/// 문자열로 실려 온 JSON 재파싱도 방어적으로 수용한다(§8.3/N7 — AMR은 둘 다 수용).
/// `drawingPos`의 u/v 부재는 허용(§8.2 각주). 실패 시 사유 문자열 반환 — 계약 위반이므로
/// 호출측이 액션 FAILED + orderValidationError 로 보고한다.
///
/// `taskId`·`attempt`(사양 §8.1.1 — ACS 발급 <b>필수</b>, N14)는 비전 CAPTURE_REQ 의
/// taskId(GUID 16B, = SAIGE productId)·attempt(UInt8)로 그대로 중계된다. 수신 위치는 최상위
/// actionParameters key 가 정본이며 `params` 안도 수용한다(전환 유예).
/// <b>전환 유예</b>: 미수신·형식 위반은 액션을 거부하지 않고 폴백(Guid.Empty/attempt=1)하며 원문
/// (<see cref="WeldInspectionRequest.TaskIdRaw"/>·<see cref="WeldInspectionRequest.AttemptRaw"/>)만
/// 보존한다 — 호출측이 경고 로그를 남긴다. 거부 승격 여부는 §10 N14 확정 대기.
/// </summary>
public static class WeldInspectionActionParser
{
    public static bool TryParse(VdaAction action, out WeldInspectionRequest? request, out string? error)
    {
        request = null;
        error = null;

        JsonElement? jobRefEl = null, positionEl = null, paramsEl = null;
        JsonElement? taskIdEl = null, attemptEl = null;
        foreach (var p in action.ActionParameters)
        {
            var el = ToElement(p.Value);
            switch (p.Key)
            {
                case "jobRef": jobRefEl = el; break;
                case "position": positionEl = el; break;
                case "params": paramsEl = el; break;
                // taskId/attempt 는 ACS 추가분 — 최상위 key 로도, params 안에도 올 수 있어 둘 다 수용(아래 병합).
                case "taskId": taskIdEl = el; break;
                case "attempt": attemptEl = el; break;
            }
        }

        if (jobRefEl is null || positionEl is null || paramsEl is null)
        {
            error = "actionParameters에 jobRef/position/params 3쌍이 모두 필요합니다 " +
                    $"(수신: {string.Join(",", action.ActionParameters.Select(p => p.Key))})";
            return false;
        }

        var jobRef = AsString(jobRefEl.Value);
        if (string.IsNullOrWhiteSpace(jobRef)) { error = "jobRef 가 비어 있습니다"; return false; }

        // ── position ────────────────────────────────────────────────
        var position = AsObject(positionEl.Value, "position", ref error);
        if (position is null) return false;
        var pos = position.Value;

        if (!TryVec3(pos, "seamStartW", out var seamStart, ref error)) return false;
        if (!TryVec3(pos, "seamEndW", out var seamEnd, ref error)) return false;

        if (!pos.TryGetProperty("drawingPos", out var dp) || dp.ValueKind != JsonValueKind.Object)
        { error = "position.drawingPos 누락"; return false; }

        var wallCode = GetString(dp, "wall_code");
        if (string.IsNullOrWhiteSpace(wallCode)) { error = "drawingPos.wall_code 누락"; return false; }

        var drawingPos = new WeldDrawingPos(
            Tank: GetString(dp, "tank") ?? "",
            Level: GetInt(dp, "level") ?? 0,
            WallCode: wallCode!,
            U: GetDouble(dp, "u"),
            V: GetDouble(dp, "v"),
            X: GetDouble(dp, "x") ?? 0,
            Y: GetDouble(dp, "y") ?? 0,
            Z: GetDouble(dp, "z") ?? 0);

        // ── params ──────────────────────────────────────────────────
        var prm = AsObject(paramsEl.Value, "params", ref error);
        if (prm is null) return false;
        var pr = prm.Value;

        var seamTypeRaw = GetString(pr, "seamType");
        SeamTypeKind seamType;
        switch (seamTypeRaw)
        {
            case "LINE": seamType = SeamTypeKind.Line; break;
            case "CROSS4": case "CROSS": seamType = SeamTypeKind.Cross; break;   // 십자 4갈래 (legacy "CROSS" 수용)
            case "CROSS3": seamType = SeamTypeKind.Cross3; break;                 // T자 3갈래
            case "CORNER3": case "CORNER": seamType = SeamTypeKind.Corner; break; // 3면 코너 (legacy "CORNER" 수용)
            case "CORNER2": seamType = SeamTypeKind.Corner2; break;               // 2면 코너
            default:
                // POLYLINE 포함 미정의 값은 계약 위반으로 거부(§8.1 — 2점 계약이라 세그먼트 방향 불명).
                error = $"미정의 seamType '{seamTypeRaw ?? "(누락)"}' — LINE/CROSS3/CROSS4/CORNER2/CORNER3 만 수용(legacy CROSS/CORNER 허용)";
                return false;
        }

        var sectionDxfId = GetString(pr, "sectionDxfId");
        if (string.IsNullOrWhiteSpace(sectionDxfId)) { error = "params.sectionDxfId 누락"; return false; }

        var anchorGroupId = GetString(pr, "anchorGroupId");
        if (string.IsNullOrWhiteSpace(anchorGroupId)) { error = "params.anchorGroupId 누락"; return false; }

        var seqInGroup = GetInt(pr, "seqInGroup");
        if (seqInGroup is null or < 1) { error = $"params.seqInGroup 부적합 ({seqInGroup?.ToString() ?? "누락"}, 1 이상)"; return false; }

        var standoff = GetDouble(pr, "standoffMm");
        if (standoff is null) { error = "params.standoffMm 누락"; return false; }

        // ── taskId / attempt (사양 §8.1.1, N14 — ACS 발급 필수) ─────
        // 위치: 최상위 actionParameters key 가 정본, 없으면 params 안(전환 유예).
        // 형식: taskId=GUID 문자열(하이픈/중괄호/무하이픈 모두 수용), attempt=1~255.
        // 전환 유예 중이라 미수신·형식 위반이어도 액션을 거부하지 않는다(null → 스텝에서 Guid.Empty/1 폴백).
        var taskIdRaw = ReadScalar(taskIdEl, pr, "taskId");
        Guid? taskId = null;
        if (!string.IsNullOrWhiteSpace(taskIdRaw))
        {
            if (Guid.TryParse(taskIdRaw, out var parsed)) taskId = parsed;
            // 파싱 실패는 원문(TaskIdRaw)만 보존 — 호출측이 경고 로그를 남기고 Guid.Empty 로 전송한다.
        }

        var attemptRaw = ReadScalar(attemptEl, pr, "attempt");
        byte? attempt = null;
        if (!string.IsNullOrWhiteSpace(attemptRaw)
            && int.TryParse(attemptRaw, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var a)
            && a is >= 1 and <= 255)   // 사양상 1부터 — 범위 밖/비수치는 무시하고 폴백(1)
            attempt = (byte)a;

        request = new WeldInspectionRequest(
            JobRef: jobRef!,
            SeamStartW: seamStart!,
            SeamEndW: seamEnd!,
            DrawingPos: drawingPos,
            SeamType: seamType,
            SectionDxfId: sectionDxfId!,
            InspectionProfileId: GetString(pr, "inspectionProfileId") ?? "",
            StandoffMm: standoff.Value,
            WorkingDistanceMm: GetDouble(pr, "workingDistanceMm"),
            AnchorGroupId: anchorGroupId!,
            SeqInGroup: seqInGroup.Value,
            TaskId: taskId,
            TaskIdRaw: string.IsNullOrWhiteSpace(taskIdRaw) ? null : taskIdRaw,
            Attempt: attempt,
            AttemptRaw: string.IsNullOrWhiteSpace(attemptRaw) ? null : attemptRaw);
        return true;
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────

    /// <summary>ActionParameter.Value(object?) → JsonElement. 이미 JsonElement 면 그대로,
    /// 문자열이면 JSON 재파싱 시도(§8.3 문자열 폴백 방어), 그 외 프리미티브는 직렬화 경유.</summary>
    private static JsonElement? ToElement(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonElement el:
                // 문자열 값이 JSON object/array 를 품고 있으면 재파싱(문자열 폴백 방어).
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (s is not null && LooksLikeJson(s))
                    {
                        try { return JsonDocument.Parse(s).RootElement.Clone(); }
                        catch (JsonException) { /* 평범한 문자열 — 그대로 사용 */ }
                    }
                }
                return el;
            case string str when LooksLikeJson(str):
                try { return JsonDocument.Parse(str).RootElement.Clone(); }
                catch (JsonException) { return JsonSerializer.SerializeToElement(str); }
            default:
                return JsonSerializer.SerializeToElement(value);
        }
    }

    private static bool LooksLikeJson(string s)
    {
        var t = s.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    private static JsonElement? AsObject(JsonElement el, string name, ref string? error)
    {
        if (el.ValueKind == JsonValueKind.Object) return el;
        error = $"{name} 은 JSON object 여야 합니다 (수신: {el.ValueKind})";
        return null;
    }

    /// <summary>스칼라 파라미터 1개를 문자열로 읽는다 — 최상위 actionParameters 값(<paramref name="topLevel"/>)을
    /// 우선 보고, 없으면 <c>params.&lt;prop&gt;</c> 를 본다. 문자열·숫자 둘 다 수용(숫자는 불변 문화권 표기).</summary>
    private static string? ReadScalar(JsonElement? topLevel, JsonElement prm, string prop)
    {
        if (topLevel is { } el)
        {
            var s = ScalarText(el);
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return prm.TryGetProperty(prop, out var inner) ? ScalarText(inner) : null;
    }

    private static string? ScalarText(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetRawText(),
        _ => null,
    };

    private static string? AsString(JsonElement el)
        => el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? GetString(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static double? GetDouble(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;

    private static int? GetInt(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : null;

    private static bool TryVec3(JsonElement obj, string prop, out double[]? vec, ref string? error)
    {
        vec = null;
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array || el.GetArrayLength() != 3)
        { error = $"position.{prop} 은 숫자 3개 배열이어야 합니다"; return false; }

        var result = new double[3];
        var i = 0;
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number)
            { error = $"position.{prop}[{i}] 이 숫자가 아닙니다"; return false; }
            result[i++] = item.GetDouble();
        }
        vec = result;
        return true;
    }
}
