namespace SurveyExporter.Models
{
    public class DistributionListModel
    {
        public Guid DistributionListId { get; set; }
        public string Name { get; set; }
        public int Type { get; set; }
        public string DistributionListJson { get; set; }
        public Guid TenantId { get; set; }
        public Guid? SqlConnectionId { get; set; }
        public Guid DomainId { get; set; }
    }
}