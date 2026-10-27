namespace SurveyExporter.Models
{
    public class RuleHistoryModel
    {
        public Guid RuleHistoryId { get; set; }
        public DateTime ExecutionDate { get; set; }
        public int SuccessfulCount { get; set; }
        public int TotalCount { get; set; }
        public Guid RuleId { get; set; }
        public Guid TenantId { get; set; }
        public int FailGenerated { get; set; }
        public string FailureMessage { get; set; }
        public Guid DomainId { get; set; }
    }
}