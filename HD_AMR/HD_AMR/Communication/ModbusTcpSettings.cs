namespace HD_AMR.Communication;

public class ModbusTcpSettings
{
    public string Name { get; set; } = "ModbusTcp";
    public string IpAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    public byte SlaveId { get; set; } = 1;
    public int ReadTimeoutMs { get; set; } = 3000;
    public int WriteTimeoutMs { get; set; } = 3000;
}
