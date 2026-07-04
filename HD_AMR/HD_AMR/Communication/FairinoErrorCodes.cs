namespace HD_AMR.Communication;

/// <summary>
/// FAIRINO 컨트롤러 인터페이스(RPC) 반환코드(rc)를 사람이 읽을 수 있는 한글 안내로 변환한다.
/// 알림/로그에 원인·조치를 함께 보여주기 위한 것으로, 제어 흐름 판단용이 아니다.
/// 표 출처(공식): https://www.fairino.us/cobots-manual/error-code-comparison-table
/// (동일 표: https://fairino-doc-en.readthedocs.io/ SDKManual/errcode.html)
/// 여기 담은 코드는 이 앱이 실제로 마주칠 법한 이동/좌표계/기구학/오류 상태 위주로 추린 것이다.
/// </summary>
public static class FairinoErrorCodes
{
    // rc=0(성공)은 매핑에 두지 않는다 — 성공 시 호출부가 사유를 덧붙이지 않도록.
    private static readonly Dictionary<int, string> Map = new()
    {
        [-4] = "xmlrpc 인터페이스 실행 실패 — A/S 문의",
        [-3] = "xmlrpc 통신 실패 — 네트워크/서버 IP 확인",
        [-2] = "컨트롤러 통신 이상 — 하드웨어/소프트웨어 연결 확인",
        [-1] = "기타 오류 — A/S 문의",
        [3] = "파라미터 개수 불일치 — 인터페이스 인자 확인",
        [4] = "파라미터 값 이상 — 타입/범위 확인",
        [8] = "궤적 파일 열기 실패 — TPD 파일 존재/이름 확인",
        [14] = "인터페이스 실행 실패(fault 상태) — '오류 해제' 후 재시도",
        [18] = "로봇 프로그램 실행 중 — 먼저 정지 후 조작",
        [20] = "위빙 용접 공구 미설정 — 0이 아닌 공구 좌표계 설정",
        [22] = "3점법 공구 미설정 — 공구 좌표계 먼저 설정",
        [28] = "역기구학 계산 이상 — 자세가 합리적인지 확인",
        [29] = "ServoJ 관절 초과 — 관절 데이터 범위 확인",
        [30] = "복구 불가 fault — 컨트롤 박스 전원 재기동 필요",
        [31] = "비상정지 해제됨 — 컨트롤 박스 전원 재기동 필요",
        [32] = "관절 한계 초과 — 드래그 모드로 소프트리밋 범위 안으로 이동",
        [34] = "작업물 번호 오류 — 작업물(user) 번호 확인",
        [35] = "작업물 0번으로 전환 필요 — 작업 좌표계 번호 0으로 변경",
        [37] = "공구 번호 오류 — 공구(tool) 번호 확인",
        [38] = "특이자세로 계산 실패 — 자세를 바꾸세요(특이점 회피)",
        [40] = "속도 퍼센트 한계 초과 — 속도값 확인",
        [42] = "자세 변화가 과다 — 중간 경유 자세 추가",
        [44] = "로봇 자세각 한계 초과 — 목표 자세 확인",
        [99] = "안전 정지 트리거 — 세이프티 정지 신호 상태 확인",
        [101] = "로봇 미활성 — 먼저 서보(Enable) 활성화",
        [112] = "목표 자세 도달 불가 — 목표가 작업영역 안인지 확인",
        [154] = "관절 지령점 오류 — 관절 지령 확인",
        [170] = "작업물 좌표계 미적용 — 좌표계 확인",
        [185] = "fault 신호 트리거로 모션 정지 — fault 신호 확인",
        [186] = "비상정지 신호 트리거로 모션 정지 — 비상정지 신호 확인",
        [200] = "로봇이 조그 중 — 로봇 상태 확인",
        [204] = "명령 큐 가득 참 — 명령 전송 빈도 확인",
    };

    /// <summary>rc에 대응하는 한글 사유/조치. 알 수 없는 코드는 빈 문자열.</summary>
    public static string Describe(int rc)
        => Map.TryGetValue(rc, out var desc) ? desc : string.Empty;

    /// <summary>rc가 실패(0이 아님)일 때만 ": 사유"를 붙인 접미사. 성공/미지 코드는 빈 문자열.
    /// 예: <c>$"조그 완료 (rc={rc}){FairinoErrorCodes.Suffix(rc)}"</c>.</summary>
    public static string Suffix(int rc)
    {
        if (rc == 0) return string.Empty;
        var desc = Describe(rc);
        return desc.Length == 0 ? string.Empty : $": {desc}";
    }
}
