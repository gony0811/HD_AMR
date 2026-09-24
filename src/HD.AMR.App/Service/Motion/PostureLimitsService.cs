using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HD.AMR.App.Service.Motion;

/// <summary>
/// <see cref="PostureLimits"/> 영속화. 별도 테이블 없이 범용 key/value 저장소(Parameters)에 JSON 으로 둔다 —
/// <see cref="CalibrationService"/> 가 T_A_B 를 보관하는 방식과 같다.
/// </summary>
public class PostureLimitsService
{
    private const string Key = "Motion.PostureLimits.Json";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly ParameterService _param;
    private readonly ILogger<PostureLimitsService> _logger;

    public PostureLimitsService(ParameterService param, ILogger<PostureLimitsService> logger)
    {
        _param = param;
        _logger = logger;
    }

    /// <summary>저장된 자세 허용 범위. 없거나 깨졌으면 <see cref="PostureLimits.Default"/>.</summary>
    public async Task<PostureLimits> GetAsync()
    {
        var raw = await _param.GetAsync(Key);
        if (raw is not null)
        {
            try
            {
                var v = JsonSerializer.Deserialize<PostureLimits>(raw, JsonOpts);
                if (v is not null) return v.Normalized();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "자세 허용 범위 역직렬화 실패 — 기본값 사용"); }
        }
        return PostureLimits.Default;
    }

    /// <summary>저장된 값이 있는지 — 기본값을 쓰고 있다는 경고를 띄울지 판단용.</summary>
    public async Task<bool> IsConfiguredAsync() => await _param.GetAsync(Key) is not null;

    public Task SaveAsync(PostureLimits limits)
        => _param.SetAsync(Key, JsonSerializer.Serialize(limits.Normalized()),
            "코봇 자세 허용 범위 — 툴 장착 상태의 관절 소프트리밋과 특이점 여유");
}
