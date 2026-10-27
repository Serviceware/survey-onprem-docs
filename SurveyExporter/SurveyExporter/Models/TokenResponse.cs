using Newtonsoft.Json;

namespace SurveyExporter.Models;

public class TokenResponse
{
    [JsonProperty("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonProperty("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonProperty("token_type")]
    public string TokenType { get; set; } = string.Empty;
}
