namespace SurveyExporter.Models
{
    public class ConfigurationData
    {
        public List<DistributionListModel> DistributionLists { get; set; } = new();
        public List<DomainModel> Domains { get; set; } = new();
        public List<SurveyTemplateModel> SurveyTemplates { get; set; } = new();
        public List<ExamTemplateModel> ExamTemplates { get; set; } = new();
        public List<PendingEmailModel> PendingEmails { get; set; } = new();
        public List<ProcessedEmailModel> ProcessedEmails { get; set; } = new();
        public List<RuleModel> Rules { get; set; } = new();
        public List<RuleCounterModel> RuleCounters { get; set; } = new();
        public List<RuleHistoryModel> RuleHistories { get; set; } = new();
        public List<SettingsModel> Settings { get; set; } = new();
        public List<UserModel> Users { get; set; } = new();
        public List<EmailTemplateModel> EmailTemplates { get; set; } = new();
    }
}