using HD.AMR.App.Communication.Vda5050;
using HD.AMR.App.Enums;
using HD.AMR.App.Service;

namespace HD.AMR.Tests;

/// <summary>
/// <see cref="OrderAdmissionGate.Evaluate"/> 결정 규칙 검증(사양 §4).
/// </summary>
public class OrderAdmissionGateTests
{
    private static Vda5050Order OrderWith(params string[] actionTypes)
    {
        var actions = actionTypes.Select((t, i) => new VdaAction
        {
            ActionId = $"a{i}", ActionType = t,
        }).ToList();
        return new Vda5050Order
        {
            OrderId = "o1",
            Nodes = new List<OrderNode>
            {
                new OrderNode
                {
                    NodeId = "n1", SequenceId = 0, Released = true,
                    NodePosition = new NodePosition { MapId = "m1", X = 0, Y = 0 },
                    Actions = actions,
                },
            },
        };
    }

    [Fact]
    public void Normal_모든_Order_수락()
    {
        var decision = OrderAdmissionGate.Evaluate(PowerMode.Normal, "AUTOMATIC",
            OrderWith("startWeldInspection"));
        Assert.Equal(OrderAdmissionGate.Decision.Accept, decision);
    }

    [Fact]
    public void Normal_액션_없어도_수락()
    {
        var decision = OrderAdmissionGate.Evaluate(PowerMode.Normal, "AUTOMATIC", OrderWith());
        Assert.Equal(OrderAdmissionGate.Decision.Accept, decision);
    }

    [Fact]
    public void Low_AUTO_비허용_액션_거부()
    {
        var decision = OrderAdmissionGate.Evaluate(PowerMode.Low, "AUTOMATIC",
            OrderWith("startWeldInspection"));
        Assert.Equal(OrderAdmissionGate.Decision.RejectBatteryLow, decision);
    }

    [Fact]
    public void Critical_AUTO_비허용_액션_거부()
    {
        var decision = OrderAdmissionGate.Evaluate(PowerMode.Critical, "AUTOMATIC",
            OrderWith("startWeldInspection"));
        Assert.Equal(OrderAdmissionGate.Decision.RejectBatteryLow, decision);
    }

    [Fact]
    public void Low_AUTO_batterySwapMove_수락()
    {
        var decision = OrderAdmissionGate.Evaluate(PowerMode.Low, "AUTOMATIC",
            OrderWith("batterySwapMove"));
        Assert.Equal(OrderAdmissionGate.Decision.Accept, decision);
    }

    [Fact]
    public void Critical_AUTO_batterySwapMove_수락()
    {
        var decision = OrderAdmissionGate.Evaluate(PowerMode.Critical, "AUTOMATIC",
            OrderWith("batterySwapMove"));
        Assert.Equal(OrderAdmissionGate.Decision.Accept, decision);
    }

    [Fact]
    public void Low_MANUAL_모든_Order_거부()
    {
        // MANUAL 에서는 ACS 가 교체 Order 를 자동 발행하지 않는 전제 — 작업자 수동 조작(사양 §9.7).
        var decision = OrderAdmissionGate.Evaluate(PowerMode.Low, "MANUAL",
            OrderWith("batterySwapMove"));
        Assert.Equal(OrderAdmissionGate.Decision.RejectBatteryLow, decision);
    }

    [Fact]
    public void Low_AUTO_액션_없는_순수_주행_Order_거부()
    {
        // 순수 주행은 복귀 여유 잠식 — 어떤 상황에서도 LOW/CRITICAL 에서 금지.
        var decision = OrderAdmissionGate.Evaluate(PowerMode.Low, "AUTOMATIC", OrderWith());
        Assert.Equal(OrderAdmissionGate.Decision.RejectBatteryLow, decision);
    }

    [Fact]
    public void Low_AUTO_혼합_액션중_하나라도_비허용이면_거부()
    {
        var decision = OrderAdmissionGate.Evaluate(PowerMode.Low, "AUTOMATIC",
            OrderWith("batterySwapMove", "startWeldInspection"));
        Assert.Equal(OrderAdmissionGate.Decision.RejectBatteryLow, decision);
    }

    [Fact]
    public void Low_빈문자열_operatingMode_거부()
    {
        var decision = OrderAdmissionGate.Evaluate(PowerMode.Low, "",
            OrderWith("batterySwapMove"));
        Assert.Equal(OrderAdmissionGate.Decision.RejectBatteryLow, decision);
    }
}
