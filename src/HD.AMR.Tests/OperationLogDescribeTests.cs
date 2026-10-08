using System.Text.Json;
using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

public class OperationLogDescribeTests
{
    // 운영 로그의 액션 요약 — 최상위 스칼라와 중첩 핵심 키(jobRef·seamType·wall_code·anchor)만 뽑는다.
    [Fact]
    public void DescribeAction_PicksKeyParameters()
    {
        var json = """
            {"actionType":"startWeldInspection","actionId":"1826e57f-e3eb-4f91","actionParameters":[
              {"key":"jobRef","value":"JOB-CT1-L1-PL-A0002-1"},
              {"key":"position","value":{"seamStartW":[8.4,13.8,0.378],"drawingPos":{"wall_code":"PL","u":1.2}}},
              {"key":"params","value":{"seamType":"LINE","anchorGroupId":"G1","seqInGroup":1,"standoffMm":400}}]}
            """;
        var action = JsonSerializer.Deserialize<VdaAction>(json)!;

        var s = Vda5050OrderExecutor.DescribeAction(action);

        Assert.StartsWith("startWeldInspection#1826e57f", s);
        Assert.Contains("jobRef=JOB-CT1-L1-PL-A0002-1", s);
        Assert.Contains("wall_code=PL", s);
        Assert.Contains("seamType=LINE", s);
        Assert.Contains("anchorGroupId=G1", s);
        Assert.Contains("seamStartW=[8.4,13.8,0.378]", s);
        Assert.DoesNotContain("u=1.2", s);
    }

    [Fact]
    public void CategoryLabel_FallsBackToCode()
    {
        Assert.Equal("오더", OpCategory.LabelOf(OpCategory.Order));
        Assert.Equal("CUSTOM", OpCategory.LabelOf("CUSTOM"));
    }
}
