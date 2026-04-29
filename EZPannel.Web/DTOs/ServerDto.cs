namespace EZPannel.Api.DTOs
{
    public class ServerDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 22;
        public string Username { get; set; } = string.Empty;
        public string OperatingSystem { get; set; } = "Linux";
        public DateTime CreatedAt { get; set; }
        
        // 用于测试连接或添加时传入，不持久化在 DTO 中返回给列表
        public string? Password { get; set; }
        public string? PrivateKey { get; set; }

        // 实时状态数据
        public ServerStatus? Status { get; set; }
    }

    public class ServerStatus
    {
        public string Load { get; set; } = "0.00 / 0.00 / 0.00";
        public string NetworkUp { get; set; } = "0 KB";
        public string NetworkDown { get; set; } = "0 KB";
        public string CpuCores { get; set; } = "1";
        public double CpuUsage { get; set; } = 0.0;
        public long MemUsed { get; set; } = 0;
        public long MemTotal { get; set; } = 0;
        public double MemUsage { get; set; } = 0.0;
        public string DiskUsed { get; set; } = "0 GB";
        public string DiskTotal { get; set; } = "0 GB";
        public double DiskUsage { get; set; } = 0.0;
        public bool IsOnline { get; set; } = false;
    }


    public class ServerProcessDto
    {
        public int id { get; set; }
        public string name { get; set; } = string.Empty;
        public float cpu { get; set; } = 0.0f;
        public float mem { get; set; } = 0.0f;
        
    }
}
