namespace SurveyExporter.Models
{
    public class RuleCounterModel
    {
        public string Wildcard { get; set; }
        public int RuleCounterValue { get; set; }
        public Guid TenantId { get; set; }
        public Guid RuleId { get; set; }
        public Guid RuleCounterId { get; set; }
        public Guid? DomainId { get; set; }
    }
}