namespace SurveyExporter.Models
{
    public class ExamModel
    {
        public Guid ExamId { get; set; }
        public string Code { get; set; }
        public DateTime CreationDate { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime? ExpirationDate { get; set; }
        public string ExamJson { get; set; }
        public int Type { get; set; }
        public int Status { get; set; }
        public string? ExternalId { get; set; }
        public string? Recipient { get; set; }
        public string? Account { get; set; }
        public Guid? FillerId { get; set; }
        public Guid? RuleId { get; set; }
        public Guid ExamTemplateId { get; set; }
        public Guid TenantId { get; set; }
        public Guid DomainId { get; set; }
    }
}