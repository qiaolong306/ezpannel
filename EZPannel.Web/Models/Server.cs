using System.ComponentModel.DataAnnotations;

namespace EZPannel.Api.Models
{
    public class Server
    {
        public int Id { get; set; }

        [Required]
        public string Name { get; set; } = string.Empty;

        [Required]
        public string Host { get; set; } = string.Empty;

        // EZPannelClient Agent（HTTP/WebSocket 方式，替代 SSH）
        public string? AgentKey { get; set; }

        public int AgentPort { get; set; } = 9527;

        public string OperatingSystem { get; set; } = "Linux"; // Linux, Windows

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}