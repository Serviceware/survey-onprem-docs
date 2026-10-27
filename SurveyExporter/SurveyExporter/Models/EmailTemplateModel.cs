namespace SurveyExporter.Models;

public class EmailTemplateModel
{
    public Guid EmailTemplateId { get; set; }
    public string Name { get; set; }
    public string EmailTemplateJson { get; set; }
    public Guid TenantId { get; set; }
    public Guid SettingsId { get; set; }
    public Guid? DomainId { get; set; }
}