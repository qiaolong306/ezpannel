namespace EZPannel.Api.Models
{
    public class SqlExecutionRequest
    {
        public string Sql { get; set; }
        public bool IncludeMetadata { get; set; } = true;
    }
}