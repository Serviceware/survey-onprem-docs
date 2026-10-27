namespace SurveyExporter.Models
{
    public class SurveyModel
    {
        public Guid SurveyId { get; set; }
        public string Code { get; set; }
        public DateTime CreationDate { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime? ExpirationDate { get; set; }
        public string SurveyJson { get; set; }
        public int Type { get; set; }
        public int Status { get; set; }
        public string? ExternalId { get; set; }
        public string? Recipient { get; set; }
        public string? Account { get; set; }
        public Guid TenantId { get; set; }
        public Guid? FillerId { get; set; }
        public Guid SurveyTemplateId { get; set; }
        public Guid? RuleId { get; set; }
        public Guid DomainId { get; set; }
    }
}