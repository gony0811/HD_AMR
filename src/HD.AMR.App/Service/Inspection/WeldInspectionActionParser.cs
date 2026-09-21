using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;

namespace HD.AMR.App.Service.Inspection;

/// <summary>
/// `startWeldInspection` 액션의 <see cref="VdaAction.ActionParameters"/>(jobRef/position/params 3쌍, 사양 §8.1)를
/// <see cref="WeldInspectionRequest"/>로 해석한다.
///
/// value 는 object 발행이 기본(§8.3)이라 역직렬화 시 <see cref="JsonElement"/>로 들어오지만,
/// 문자열로 실려 온 JSON 재파싱도 방어적으로 수용한다(§8.3/N7 — AMR은 둘 다 수용).
/// `drawingPos`의 u/v 부재는 허용(§8.2 각주). 실패 시 사유 문자열 반환 — 계약 위반이므로
/// 호출측이 액션 FAILED + orderValidationError 로 보고한다.
/// </summary>
public static class WeldInspectionActionParser
{
    public static bool TryParse(VdaAction action, out WeldInspectionRequest? request, out string? error)
    {
        request = null;
        error = null;

        JsonElement? jobRefEl = null, positionEl = null, paramsEl = null;
        foreach (var p in action.ActionParameters)
        {
            var el = ToElement(p.Value);
            switch (p.Key)
            {
                case "jobRef": jobRefEl = el; break;
                case "position": positionEl = el; break;
                case "params": paramsEl = el; break;
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

        // taskId·attempt [VDA 사양서 1.6 §8.1/§8.6, N14] — 선택 필드: 없으면 null(실행 스텝이 Empty/1 폴백, 구버전 ACS 호환).
        // 실려 왔는데 형식이 틀리면 계약 위반으로 거부한다(§8.2 각주) — 엉뚱한 taskId 로 촬영이 나가면
        // SAIGE 에서 다른 용접선의 이력에 섞이는 조용한 오귀속이 되기 때문.
        Guid? taskId = null;
        if (pr.TryGetProperty("taskId", out var taskIdEl) && taskIdEl.ValueKind != JsonValueKind.Null)
        {
            if (taskIdEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(taskIdEl.GetString(), out var parsedTaskId) || parsedTaskId == Guid.Empty)
            { error = "params.taskId 부적합 (GUID 문자열 아님)"; return false; }
            taskId = parsedTaskId;
        }

        byte? attempt = null;
        if (pr.TryGetProperty("attempt", out var attemptEl) && attemptEl.ValueKind != JsonValueKind.Null)
        {
            if (attemptEl.ValueKind != JsonValueKind.Number
                || !attemptEl.TryGetInt32(out var parsedAttempt) || parsedAttempt is < 1 or > 255)
            { error = "params.attempt 부적합 (1~255 정수)"; return false; }
            attempt = (byte)parsedAttempt;
        }

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
            Attempt: attempt);
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
