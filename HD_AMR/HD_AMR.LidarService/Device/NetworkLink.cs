namespace HD_AMR.LidarService.Device;

/// <summary>
/// 이더넷 링크(캐리어) 상태 조회.
///
/// 이게 필요한 이유는 <c>nsl_open</c> 의 반환값만으로는 <b>"케이블 단선"과 "잘못된 IP"를
/// 구분할 수 없기</b> 때문이다(실측상 둘 다 -2). 게다가 케이블이 빠진 상태에서는
/// <c>nsl_open</c> 이 <b>매번 3초를 꽉 채우고</b> 실패한다 — 잘못된 IP 는 두 번째부터
/// 69ms 로 캐시되지만 단선은 캐시되지 않는다.
///
/// 따라서 재접속 워치독은 링크가 없을 때 <c>nsl_open</c> 을 아예 호출하지 않는 편이 낫다.
/// 3초씩 블로킹하며 헛돌 이유가 없다.
/// </summary>
internal static class NetworkLink
{
    /// <summary>
    /// 링크가 올라와 있는지. <b>판단할 수 없으면 null</b> 을 반환한다 — Windows 개발 환경,
    /// 인터페이스명 미설정, sysfs 읽기 실패가 여기 해당한다.
    ///
    /// 호출 측은 null 을 "끊김"으로 다루면 안 된다. 모르는 것과 끊긴 것은 다르고,
    /// 모른다는 이유로 연결 시도를 막으면 링크가 멀쩡한데도 영영 붙지 못한다.
    /// </summary>
    public static bool? IsUp(string? interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName)) return null;

        // Linux sysfs 전용. 다른 플랫폼에서는 경로가 없어 자연히 null 이 된다.
        var path = $"/sys/class/net/{interfaceName}/carrier";

        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path).Trim() == "1";
        }
        catch (IOException)
        {
            // 인터페이스가 DOWN 이면 carrier 읽기가 EINVAL 로 실패한다. 이건 "링크 없음"에
            // 가깝지만 확실하지 않으므로 모름으로 둔다 — 잘못 단정해 연결을 막는 것보다
            // 3초 헛도는 편이 낫다.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
