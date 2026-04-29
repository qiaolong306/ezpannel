namespace EZPannel.Api.Models
{
    public class SqlExecutionResult
    {
        public List<string> Columns { get; set; } = new List<string>();
        public List<List<object>> Rows { get; set; } = new List<List<object>>();
        public int AffectedRows { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public bool IsQuery { get; set; }
    }
}