using System.Text.Json.Serialization;

namespace HD_AMR.Communication.Vda5050;

// VDA 5050 v2.0 메시지 모델 (HD_AMR 어댑터 측 — 운영 서브셋).
// ACS(HD_ACS) 측 모델과 동일 shape. 사양: HD_ACS docs/VDA5050_SPEC_PLAN.

public abstract class Vda5050Header
{
    [JsonPropertyName("headerId")] public int HeaderId { get; set; }
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    [JsonPropertyName("version")] public string Version { get; set; } = "2.0.0";
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
    [JsonPropertyName("serialNumber")] public string SerialNumber { get; set; } = "";
}

// ── connection ───────────────────────────────────────────
public sealed class Vda5050Connection : Vda5050Header
{
    // ONLINE | OFFLINE | CONNECTIONBROKEN (Last Will)
    [JsonPropertyName("connectionState")] public string ConnectionState { get; set; } = "ONLINE";
}

// ── state ────────────────────────────────────────────────
public sealed class Vda5050State : Vda5050Header
{
    [JsonPropertyName("orderId")] public string OrderId { get; set; } = "";
    [JsonPropertyName("orderUpdateId")] public int OrderUpdateId { get; set; }
    [JsonPropertyName("lastNodeId")] public string LastNodeId { get; set; } = "";
    [JsonPropertyName("lastNodeSequenceId")] public int LastNodeSequenceId { get; set; }
    [JsonPropertyName("driving")] public bool Driving { get; set; }
    [JsonPropertyName("paused")] public bool Paused { get; set; }
    [JsonPropertyName("operatingMode")] public string OperatingMode { get; set; } = "AUTOMATIC";
    [JsonPropertyName("agvPosition")] public AgvPosition? AgvPosition { get; set; }
    [JsonPropertyName("batteryState")] public BatteryState? BatteryState { get; set; }
    [JsonPropertyName("safetyState")] public SafetyState? SafetyState { get; set; }
    [JsonPropertyName("actionStates")] public List<ActionState> ActionStates { get; set; } = new();
    [JsonPropertyName("nodeStates")] public List<NodeState> NodeStates { get; set; } = new();
    [JsonPropertyName("edgeStates")] public List<EdgeState> EdgeStates { get; set; } = new();
    [JsonPropertyName("errors")] public List<VdaError> Errors { get; set; } = new();
    [JsonPropertyName("information")] public List<VdaInformation> Information { get; set; } = new();
}

public sealed class AgvPosition
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("theta")] public double Theta { get; set; }
    [JsonPropertyName("mapId")] public string MapId { get; set; } = "";
    [JsonPropertyName("positionInitialized")] public bool PositionInitialized { get; set; }
}

public sealed class BatteryState
{
    [JsonPropertyName("batteryCharge")] public double BatteryCharge { get; set; }
    [JsonPropertyName("charging")] public bool Charging { get; set; }
}

public sealed class SafetyState
{
    // eStop: NONE | MANUAL | AUTOACK
    [JsonPropertyName("eStop")] public string EStop { get; set; } = "NONE";
    [JsonPropertyName("fieldViolation")] public bool FieldViolation { get; set; }
}

public sealed class ActionState
{
    [JsonPropertyName("actionId")] public string ActionId { get; set; } = "";
    [JsonPropertyName("actionType")] public string? ActionType { get; set; }
    [JsonPropertyName("actionStatus")] public string ActionStatus { get; set; } = "WAITING";
    [JsonPropertyName("resultDescription")] public string? ResultDescription { get; set; }
}

public sealed class NodeState
{
    [JsonPropertyName("nodeId")] public string NodeId { get; set; } = "";
    [JsonPropertyName("sequenceId")] public int SequenceId { get; set; }
    [JsonPropertyName("released")] public bool Released { get; set; }
}

public sealed class EdgeState
{
    [JsonPropertyName("edgeId")] public string EdgeId { get; set; } = "";
    [JsonPropertyName("sequenceId")] public int SequenceId { get; set; }
    [JsonPropertyName("released")] public bool Released { get; set; }
}

public sealed class VdaError
{
    [JsonPropertyName("errorType")] public string ErrorType { get; set; } = "";
    [JsonPropertyName("errorLevel")] public string ErrorLevel { get; set; } = "WARNING";
    [JsonPropertyName("errorDescription")] public string? ErrorDescription { get; set; }
}

public sealed class VdaInformation
{
    [JsonPropertyName("infoType")] public string InfoType { get; set; } = "";
    [JsonPropertyName("infoLevel")] public string InfoLevel { get; set; } = "INFO";
    [JsonPropertyName("infoDescription")] public string? InfoDescription { get; set; }
}

// ── order (수신) ─────────────────────────────────────────
public sealed class Vda5050Order : Vda5050Header
{
    [JsonPropertyName("orderId")] public string OrderId { get; set; } = "";
    [JsonPropertyName("orderUpdateId")] public int OrderUpdateId { get; set; }
    [JsonPropertyName("nodes")] public List<OrderNode> Nodes { get; set; } = new();
    [JsonPropertyName("edges")] public List<OrderEdge> Edges { get; set; } = new();
}

public sealed class OrderNode
{
    [JsonPropertyName("nodeId")] public string NodeId { get; set; } = "";
    [JsonPropertyName("sequenceId")] public int SequenceId { get; set; }
    [JsonPropertyName("released")] public bool Released { get; set; } = true;
    [JsonPropertyName("nodePosition")] public NodePosition? NodePosition { get; set; }
    [JsonPropertyName("actions")] public List<VdaAction> Actions { get; set; } = new();
}

public sealed class NodePosition
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("theta")] public double? Theta { get; set; }
    [JsonPropertyName("mapId")] public string MapId { get; set; } = "";
}

public sealed class OrderEdge
{
    [JsonPropertyName("edgeId")] public string EdgeId { get; set; } = "";
    [JsonPropertyName("sequenceId")] public int SequenceId { get; set; }
    [JsonPropertyName("released")] public bool Released { get; set; } = true;
    [JsonPropertyName("startNodeId")] public string StartNodeId { get; set; } = "";
    [JsonPropertyName("endNodeId")] public string EndNodeId { get; set; } = "";
}

// ── instantActions (수신) ────────────────────────────────
public sealed class Vda5050InstantActions : Vda5050Header
{
    [JsonPropertyName("actions")] public List<VdaAction> Actions { get; set; } = new();
}

public sealed class VdaAction
{
    [JsonPropertyName("actionType")] public string ActionType { get; set; } = "";
    [JsonPropertyName("actionId")] public string ActionId { get; set; } = "";
    [JsonPropertyName("blockingType")] public string BlockingType { get; set; } = "HARD";
    [JsonPropertyName("actionParameters")] public List<ActionParameter> ActionParameters { get; set; } = new();
}

public sealed class ActionParameter
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("value")] public object? Value { get; set; }
}
