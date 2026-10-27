namespace SurveyExporter.Models
{
    public class PendingEmailModel
    {
        public Guid PendingMailId { get; set; }
        public string Recipient { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public int RetryCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string Error { get; set; }
        public Guid? RuleId { get; set; }
        public Guid? SettingsId { get; set; }
        public Guid TenantId { get; set; }
        public Guid? DomainId { get; set; }
    }
}