

namespace EZPannel.Api.Models;

public class ScriptAutoRunModel {

    public int Id { get; set; }
    public string Name { get; set; }
    public string? Description { get; set; }
    public int ScriptId { get; set; }
    /// <summary>
    /// 是否自动运行
    /// </summary>
    public bool IsAutoRun { get; set; }
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
    /// <summary>
    /// 运行的服务器ID列表 如果为空则运行所有服务器
    /// </summary>
    public List<int> ServerIds { get; set; } = new List<int>();
    /// <summary>
    /// 上次运行时间
    /// </summary>
    public DateTime LastRunTime { get; set; } = DateTime.MinValue;
}