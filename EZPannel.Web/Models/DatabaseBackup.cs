using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EZPannel.Api.Models
{
    public class DatabaseBackupConfig
    {
        public int Id { get; set; }
        [Required]
        public string Name { get; set; } = string.Empty;
        public int DatabaseId { get; set; }  // 关联的数据库ID
        [Required]
        public string DatabaseNames { get; set; } = string.Empty; // 逗号分隔的数据库名列表
        [Required]
        public string CronExpression { get; set; } = "0 0 2 * * ?"; // Default: every day at 2 AM
        public bool IsEnabled { get; set; } 
        public DateTime? LastBackupTime { get; set; }
    }

    public class BackupLog
    {
        public int Id { get; set; }
        public int DatabaseBackupConfigId { get; set; }
        public string DatabaseName { get; set; } = string.Empty;
        public DateTime BackupTime { get; set; }
        public bool IsSuccess { get; set; }
        public string? Message { get; set; }
        public string? FilePath { get; set; }
        public long FileSize { get; set; }
        [NotMapped]
        public string DatabaseConfigName { get; set; } = string.Empty;
    }
}