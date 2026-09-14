namespace HD.AMR.App.Communication.Vda5050;

/// <summary>VDA 5050 MQTT 토픽: {prefix}/{version}/{manufacturer}/{serialNumber}/{channel}. ACS와 동일 규약.</summary>
public static class Vda5050Topics
{
    public static string Order(Vda5050AdapterSettings s)
        => $"{s.TopicPrefix}/{s.TopicVersion}/{s.Manufacturer}/{s.SerialNumber}/order";
    public static string InstantActions(Vda5050AdapterSettings s)
        => $"{s.TopicPrefix}/{s.TopicVersion}/{s.Manufacturer}/{s.SerialNumber}/instantActions";
    public static string State(Vda5050AdapterSettings s)
        => $"{s.TopicPrefix}/{s.TopicVersion}/{s.Manufacturer}/{s.SerialNumber}/state";
    public static string Connection(Vda5050AdapterSettings s)
        => $"{s.TopicPrefix}/{s.TopicVersion}/{s.Manufacturer}/{s.SerialNumber}/connection";

    /// <summary>ACS 생존 신호 토픽(§7.2 N12) — ACS 전용 identity, 모든 AMR이 공유 구독.</summary>
    public static string AcsConnection(Vda5050AdapterSettings s)
        => $"{s.TopicPrefix}/{s.TopicVersion}/{s.AcsManufacturer}/{s.AcsSerialNumber}/connection";
}
