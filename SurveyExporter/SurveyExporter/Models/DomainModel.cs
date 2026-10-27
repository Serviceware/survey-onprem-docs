namespace SurveyExporter.Models
{
    public class DomainModel
    {
        public Guid DomainId { get; set; }
        public string Name { get; set; }
        public string Role { get; set; }
        public Guid SharedKey { get; set; }
        public bool Default { get; set; }
        public Guid TenantId { get; set; }
        public int Status { get; set; }
    }
}