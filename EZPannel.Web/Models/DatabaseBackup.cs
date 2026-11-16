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
        /// <summary>
        /// 调度类型：Daily=按日, Monthly=按月, Yearly=按年, Once=按日期
        /// </summary>
        public string? ScheduleType { get; set; }
        /// <summary>
        /// 执行时间（格式 HH:mm）
        /// </summary>
        public string? RunTime { get; set; }
        /// <summary>
        /// 按月/按年：几号（1-31）
        /// </summary>
        public int? DayOfMonth { get; set; }
        /// <summary>
        /// 按年：几月（1-12）
        /// </summary>
        public int? Month { get; set; }
        /// <summary>
        /// 按日期：具体执行日期
        /// </summary>
        public DateTime? RunDate { get; set; }
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