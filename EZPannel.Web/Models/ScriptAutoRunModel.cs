

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
    /// 时间表达式
    /// </summary>
    public string? CronExpression { get; set; }
    /// <summary>
    /// 运行的服务器ID列表 如果为空则运行所有服务器
    /// </summary>
    public List<int> ServerIds { get; set; } = new List<int>();
    /// <summary>
    /// 上次运行时间
    /// </summary>
    public DateTime LastRunTime { get; set; } = DateTime.MinValue;
}