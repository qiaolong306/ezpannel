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
        
        public int Port { get; set; } = 22;
        
        [Required]
        public string Username { get; set; } = string.Empty;
        
        // 实际应用应加密存储或使用密钥
        public string? Password { get; set; }
        
        public string? PrivateKey { get; set; }
        
        public string OperatingSystem { get; set; } = "Linux"; // Linux, Windows
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
