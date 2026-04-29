using System.ComponentModel.DataAnnotations;

namespace EZPannel.Api.Models
{
    public class Database
    {

        public int Id { get; set; }
        
        [Required]
        public string Name { get; set; } = string.Empty;
        
        public string? Host { get; set; } = "localhost";
        
        public int Port { get; set; } = 3306;
        
        [Required]
        public string Username { get; set; } = string.Empty;
        // 实际应用应加密存储
        public string? Password { get; set; }
        public string Type { get; set; } = "MySQL"; // MySQL, PostgreSQL, SQLServer
        public string? Description { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}