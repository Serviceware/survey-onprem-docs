namespace SurveyExporter.Models
{
    public class SettingsModel
    {
        public string Name { get; set; }
        public Guid SettingsId { get; set; }
        public string SettingsJson { get; set; }
        public int Type { get; set; }
        public Guid TenantId { get; set; }
        public Guid? DomainId { get; set; }
        public bool CrossDomainVisible { get; set; }
    }
}