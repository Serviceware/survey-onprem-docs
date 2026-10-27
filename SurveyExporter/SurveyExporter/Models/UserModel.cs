namespace SurveyExporter.Models
{
    public class UserModel
    {
        public Guid UserId { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public DateTime CreationDate { get; set; }
        public DateTime LastLogin { get; set; }
        public Guid TenantId { get; set; }
    }
}