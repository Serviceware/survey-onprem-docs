namespace SurveyExporter.Models
{
    public class RuleModel
    {
        public Guid RuleId { get; set; }
        public string Name { get; set; }
        public int Type { get; set; }
        public int DaysToExpire { get; set; }
        public string RuleJson { get; set; }
        public int Status { get; set; }
        public Guid TenantId { get; set; }
        public Guid SettingsId { get; set; }
        public Guid EmailTemplateId { get; set; }
        public Guid? DistributionListId { get; set; }
        public Guid DomainId { get; set; }
    }
}