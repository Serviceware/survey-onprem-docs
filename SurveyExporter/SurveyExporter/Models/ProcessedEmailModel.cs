namespace SurveyExporter.Models
{
    public class ProcessedEmailModel
    {
        public Guid ProcessedMailId { get; set; }
        public string Recipient { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public int RetryCount { get; set; }
        public DateTime SentAt { get; set; }
        public string ResponseMessage { get; set; }
        public Guid? RuleId { get; set; }
        public Guid? SettingsId { get; set; }
        public Guid TenantId { get; set; }
        public Guid? DomainId { get; set; }
    }
}