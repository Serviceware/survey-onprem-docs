namespace SurveyExporter.Models
{
    public class UITranslationModel
    {
        public Guid UITranslationId { get; set; }
        public string Language { get; set; }
        public string LanguageJson { get; set; }
        public string TranslationJson { get; set; }
        public Guid TenantId { get; set; }
    }
}