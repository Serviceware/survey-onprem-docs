namespace SurveyExporter.Models
{
    public class SurveyTemplateModel
    {
        public Guid SurveyTemplateId { get; set; }
        public string Name { get; set; }
        public DateTime CreationDate { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime? ExpirationDate { get; set; }
        public string SurveyJson { get; set; }
        public int Type { get; set; }
        public int Status { get; set; }
        public Guid AuthorId { get; set; }
        public Guid TenantId { get; set; }
        public Guid DomainId { get; set; }
    }
}