namespace SurveyExporter;

using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Models;
using Newtonsoft.Json;

public class Program
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(30) };

    private static string _tokenUrl = null!;
    private static string _clientId = null!;
    private static string _clientSecret = null!;
    private static string _currentToken = string.Empty;
    private static DateTime _tokenExpiresAt = DateTime.MinValue;

    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    private static DateTime GetUtcDateTime(SqlDataReader reader, string column) =>
        DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal(column)), DateTimeKind.Utc);

    private static DateTime? GetNullableUtcDateTime(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
    }

    private static string GenerateCode(Guid id) =>
        Convert.ToBase64String(id.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task Main(string[] args)
    {
        var startTime = DateTime.Now;
        Log("=== Survey Exporter starting ===");

        try
        {
            await RunAsync();
        }
        catch (Exception e)
        {
            Log($"FATAL ERROR: {e.Message}");
            Log($"Details: {e}");
        }

        Log($"=== Total elapsed: {(DateTime.Now - startTime).TotalSeconds:F1}s ===");
        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }

    private static async Task RunAsync()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", false, true)
            .Build();

        var connectionString = configuration["ConnectionStrings:DefaultConnection"];
        var apiBaseUrl = configuration["ApiUrl"];
        var batchSize = int.TryParse(configuration["BatchSize"], out var bs) && bs > 0 ? bs : 5000;
        var tokenUrl = configuration["TokenUrl"];
        var clientId = configuration["ClientId"];
        var clientSecret = configuration["ClientSecret"];

        // Validate config
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Log("ERROR: ConnectionStrings:DefaultConnection is missing in appsettings.json.");
            return;
        }
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            Log("ERROR: ApiUrl is missing in appsettings.json.");
            return;
        }
        if (string.IsNullOrWhiteSpace(tokenUrl))
        {
            Log("ERROR: TokenUrl is missing in appsettings.json.");
            return;
        }
        if (string.IsNullOrWhiteSpace(clientId))
        {
            Log("ERROR: ClientId is missing in appsettings.json.");
            return;
        }
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            Log("ERROR: ClientSecret is missing in appsettings.json.");
            return;
        }

        _tokenUrl = tokenUrl;
        _clientId = clientId;
        _clientSecret = clientSecret;

        Log($"Target API: {apiBaseUrl}");

        // Test DB connection before asking for token
        Log("Testing database connection...");
        try
        {
            using var testConn = new SqlConnection(connectionString);
            await testConn.OpenAsync();
            Log($"Database connection OK. Server: {testConn.DataSource}, Database: {testConn.Database}");
        }
        catch (Exception e)
        {
            Log($"ERROR: Cannot connect to database — {e.Message}");
            Log("Check the ConnectionStrings:DefaultConnection value in appsettings.json.");
            return;
        }

        Log("Authenticating via OIDC client credentials...");
        try
        {
            await RefreshTokenAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log($"ERROR: Authentication failed — {e.Message}");
            return;
        }

        var errors = new List<string>();

        // Configuration data
        Log("--- Fetching configuration data from database ---");
        ConfigurationData configurationData;
        try
        {
            configurationData = FetchConfigurationData(connectionString);
            Log($"Fetched: {configurationData.Domains.Count} domains, {configurationData.DistributionLists.Count} distribution lists, " +
                $"{configurationData.SurveyTemplates.Count} survey templates, {configurationData.ExamTemplates.Count} exam templates, " +
                $"{configurationData.Rules.Count} rules, {configurationData.Settings.Count} settings, " +
                $"{configurationData.Users.Count} users, {configurationData.EmailTemplates.Count} email templates, " +
                $"{configurationData.PendingEmails.Count} pending emails, {configurationData.ProcessedEmails.Count} processed emails, " +
                $"{configurationData.RuleCounters.Count} rule counters, {configurationData.RuleHistories.Count} rule histories");
        }
        catch (Exception e)
        {
            Log($"ERROR fetching configuration data: {e.Message}");
            Log($"Full details: {e}");
            errors.Add("Configuration data fetch failed.");
            goto Summary;
        }

        Log("Sending configuration data to SCE...");
        try
        {
            await SendConfigurationDataAsync(configurationData, apiBaseUrl, batchSize).ConfigureAwait(false);
            Log("Configuration data sent successfully.");
        }
        catch (Exception e)
        {
            Log($"ERROR sending configuration data: {e.Message}");
            errors.Add("Configuration data send failed.");
            goto Summary;
        }

        // Surveys
        Log("--- Fetching surveys from database ---");
        Dictionary<Guid, List<SurveyModel>> surveysByTemplate;
        try
        {
            surveysByTemplate = FetchSurveysGroupedByTemplate(connectionString);
            var totalSurveys = surveysByTemplate.Values.Sum(s => s.Count);
            Log($"Fetched {totalSurveys} surveys across {surveysByTemplate.Count} templates.");
        }
        catch (Exception e)
        {
            Log($"ERROR fetching surveys: {e.Message}");
            errors.Add("Surveys fetch failed.");
            surveysByTemplate = new Dictionary<Guid, List<SurveyModel>>();
        }

        foreach (var kvp in surveysByTemplate)
        {
            var templateId = kvp.Key;
            var surveys = kvp.Value;
            var batches = surveys.Chunk(batchSize).ToList();
            Log($"Sending {surveys.Count} surveys for template {templateId} in {batches.Count} batch(es) of up to {batchSize}...");
            var templateFailed = false;
            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i].ToList();
                Log($"  Batch {i + 1}/{batches.Count} — {batch.Count} surveys...");
                try
                {
                    await SendSurveysAsync(batch, configurationData.Domains, apiBaseUrl, templateId).ConfigureAwait(false);
                    Log($"  Batch {i + 1}/{batches.Count} OK.");
                }
                catch (Exception e)
                {
                    Log($"  ERROR on batch {i + 1}/{batches.Count} for template {templateId}: {e.Message}");
                    errors.Add($"Surveys for template {templateId}, batch {i + 1} failed.");
                    templateFailed = true;
                    break;
                }
            }
            if (!templateFailed)
                Log($"  All batches OK — surveys for template {templateId} sent.");
        }

        // Exams
        Log("--- Fetching exams from database ---");
        Dictionary<Guid, List<ExamModel>> examsByTemplate;
        try
        {
            examsByTemplate = FetchExamsGroupedByTemplate(connectionString);
            var totalExams = examsByTemplate.Values.Sum(e => e.Count);
            Log($"Fetched {totalExams} exams across {examsByTemplate.Count} templates.");
        }
        catch (Exception e)
        {
            Log($"ERROR fetching exams: {e.Message}");
            errors.Add("Exams fetch failed.");
            examsByTemplate = new Dictionary<Guid, List<ExamModel>>();
        }

        foreach (var kvp in examsByTemplate)
        {
            var templateId = kvp.Key;
            var exams = kvp.Value;
            var batches = exams.Chunk(batchSize).ToList();
            Log($"Sending {exams.Count} exams for template {templateId} in {batches.Count} batch(es) of up to {batchSize}...");
            var templateFailed = false;
            for (var i = 0; i < batches.Count; i++)
            {
                var batch = batches[i].ToList();
                Log($"  Batch {i + 1}/{batches.Count} — {batch.Count} exams...");
                try
                {
                    await SendExamsAsync(batch, configurationData.Domains, apiBaseUrl, templateId).ConfigureAwait(false);
                    Log($"  Batch {i + 1}/{batches.Count} OK.");
                }
                catch (Exception e)
                {
                    Log($"  ERROR on batch {i + 1}/{batches.Count} for template {templateId}: {e.Message}");
                    errors.Add($"Exams for template {templateId}, batch {i + 1} failed.");
                    templateFailed = true;
                    break;
                }
            }
            if (!templateFailed)
                Log($"  All batches OK — exams for template {templateId} sent.");
        }

        Summary:
        Log("--- Summary ---");
        if (errors.Count == 0)
        {
            Log("All data exported successfully. No errors.");
        }
        else
        {
            Log($"Completed with {errors.Count} error(s):");
            foreach (var err in errors)
                Log($"  - {err}");
        }
    }

    private static ConfigurationData FetchConfigurationData(string connectionString)
    {
        var configurationData = new ConfigurationData();

        var filePath = Path.Combine(AppContext.BaseDirectory, "Sp", "configuration.sql");

        if (!File.Exists(filePath))
        {
            Log($"File not found: {filePath}");
            throw new FileNotFoundException($"SQL file not found at path: {filePath}");
        }
        var sqlQueries = File.ReadAllLines(filePath);

        using var connection = new SqlConnection(connectionString);
        connection.Open();

        foreach (var sqlQuery in sqlQueries)
        {
            if (string.IsNullOrWhiteSpace(sqlQuery)) continue;
            Log($"Executing: {sqlQuery[..Math.Min(80, sqlQuery.Length)]}...");
            using var command = new SqlCommand(sqlQuery, connection);
            using var reader = command.ExecuteReader();

            if (sqlQuery.Contains("FROM dbo.srvydomain AS d"))
            {
                Log("Fetching domains...");
                while (reader.Read())
                    configurationData.Domains.Add(new DomainModel
                    {
                        DomainId = reader.GetGuid(reader.GetOrdinal("DomainId")),
                        Name = reader.GetString(reader.GetOrdinal("Name")),
                        Role = reader.GetString(reader.GetOrdinal("Role")),
                        SharedKey = reader.GetGuid(reader.GetOrdinal("SharedKey")),
                        Default = reader.GetBoolean(reader.GetOrdinal("Default")),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                        Status = reader.GetInt32(reader.GetOrdinal("Status"))
                    });
                Log($"  -> {configurationData.Domains.Count} domain(s) fetched.");
            }


            else if (sqlQuery.Contains("FROM dbo.srvydistributionlist AS dl"))
            {
                Log("Fetching distribution lists...");
                while (reader.Read())
                    configurationData.DistributionLists.Add(new DistributionListModel
                    {
                        DistributionListId = reader.GetGuid(reader.GetOrdinal("DistributionListId")),
                        Name = reader.GetString(reader.GetOrdinal("Name")),
                        Type = reader.GetInt32(reader.GetOrdinal("Type")),
                        DistributionListJson = reader.GetString(reader.GetOrdinal("DistributionListJson")),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                        DomainId = reader.GetGuid(reader.GetOrdinal("DomainId"))
                    });
                Log($"  -> {configurationData.DistributionLists.Count} distribution list(s) fetched.");
            }


            else if (sqlQuery.Contains("FROM dbo.srvysurveytemplate AS st"))
            {
                Log("Fetching survey templates...");
                while (reader.Read())
                    configurationData.SurveyTemplates.Add(new SurveyTemplateModel
                    {
                        SurveyTemplateId = reader.GetGuid(reader.GetOrdinal("SurveyTemplateId")),
                        Name = reader.GetString(reader.GetOrdinal("Name")),
                        CreationDate = GetUtcDateTime(reader, "CreationDate"),
                        LastModified = GetUtcDateTime(reader, "LastModified"),
                        ExpirationDate = GetNullableUtcDateTime(reader, "ExpirationDate"),
                        SurveyJson = reader.GetString(reader.GetOrdinal("SurveyJson")),
                        Type = reader.GetInt32(reader.GetOrdinal("Type")),
                        Status = reader.GetInt32(reader.GetOrdinal("Status")),
                        AuthorId = reader.GetGuid(reader.GetOrdinal("AuthorId")),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                        DomainId = reader.GetGuid(reader.GetOrdinal("DomainId"))
                    });
                Log($"  -> {configurationData.SurveyTemplates.Count} survey template(s) fetched.");
            }


            else if (sqlQuery.Contains("FROM dbo.srvyexamtemplate AS et"))
            {
                Log("Fetching exam templates...");
                while (reader.Read())
                    configurationData.ExamTemplates.Add(new ExamTemplateModel
                    {
                        ExamTemplateId = reader.GetGuid(reader.GetOrdinal("ExamTemplateId")),
                        Name = reader.GetString(reader.GetOrdinal("Name")),
                        CreationDate = GetUtcDateTime(reader, "CreationDate"),
                        LastModified = GetUtcDateTime(reader, "LastModified"),
                        ExpirationDate = GetNullableUtcDateTime(reader, "ExpirationDate"),
                        ExamJson = reader.GetString(reader.GetOrdinal("ExamJson")),
                        Type = reader.GetInt32(reader.GetOrdinal("Type")),
                        Status = reader.GetInt32(reader.GetOrdinal("Status")),
                        AuthorId = reader.GetGuid(reader.GetOrdinal("AuthorId")),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                        DomainId = reader.GetGuid(reader.GetOrdinal("DomainId"))
                    });
                Log($"  -> {configurationData.ExamTemplates.Count} exam template(s) fetched.");
            }


            else if (sqlQuery.Contains("FROM dbo.srvypendingmail AS pe"))
            {
                Log("Fetching pending emails...");
                while (reader.Read())
                {
                    configurationData.PendingEmails.Add(new PendingEmailModel
                    {
                        PendingMailId = reader.GetGuid(reader.GetOrdinal("PendingMailId")),
                        Recipient = reader.GetString(reader.GetOrdinal("Recipient")),
                        Subject = reader.GetString(reader.GetOrdinal("Subject")),
                        Body = reader.GetString(reader.GetOrdinal("Body")),
                        RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                        CreatedAt = GetUtcDateTime(reader, "CreatedAt"),
                        UpdatedAt = GetUtcDateTime(reader, "UpdatedAt"),
                        Error = reader.GetString(reader.GetOrdinal("Error")),
                        RuleId = reader.IsDBNull(reader.GetOrdinal("RuleId")) ? null : reader.GetGuid(reader.GetOrdinal("RuleId")),
                        SettingsId = reader.IsDBNull(reader.GetOrdinal("SettingsId")) ? null : reader.GetGuid(reader.GetOrdinal("SettingsId")),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                        DomainId = reader.IsDBNull(reader.GetOrdinal("DomainId")) ? null : reader.GetGuid(reader.GetOrdinal("DomainId"))
                    });
                    if (configurationData.PendingEmails.Count % 500 == 0)
                        Log($"  -> {configurationData.PendingEmails.Count} pending emails read so far...");
                }
                Log($"  -> {configurationData.PendingEmails.Count} pending email(s) fetched.");
            }


            else if (sqlQuery.Contains("FROM dbo.srvyprocessedmail AS pr"))
            {
                Log("Fetching processed emails...");
                while (reader.Read())
                {
                    configurationData.ProcessedEmails.Add(new ProcessedEmailModel
                    {
                        ProcessedMailId = reader.GetGuid(reader.GetOrdinal("ProcessedMailId")),
                        Recipient = reader.GetString(reader.GetOrdinal("Recipient")),
                        Subject = reader.GetString(reader.GetOrdinal("Subject")),
                        Body = reader.GetString(reader.GetOrdinal("Body")),
                        RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                        SentAt = GetUtcDateTime(reader, "SentAt"),
                        ResponseMessage = reader.GetString(reader.GetOrdinal("ResponseMessage")),
                        RuleId = reader.IsDBNull(reader.GetOrdinal("RuleId")) ? null : reader.GetGuid(reader.GetOrdinal("RuleId")),
                        SettingsId = reader.IsDBNull(reader.GetOrdinal("SettingsId")) ? null : reader.GetGuid(reader.GetOrdinal("SettingsId")),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                        DomainId = reader.IsDBNull(reader.GetOrdinal("DomainId")) ? null : reader.GetGuid(reader.GetOrdinal("DomainId"))
                    });
                    if (configurationData.ProcessedEmails.Count % 500 == 0)
                        Log($"  -> {configurationData.ProcessedEmails.Count} processed emails read so far...");
                }
                Log($"  -> {configurationData.ProcessedEmails.Count} processed email(s) fetched.");
            }


            else if (sqlQuery.Contains("FROM dbo.srvyrule AS r"))
            {
                Log("Fetching rules...");
                while (reader.Read())
                    configurationData.Rules.Add(new RuleModel
                    {
                        RuleId = reader.GetGuid(reader.GetOrdinal("RuleId")),
                        Name = reader.GetString(reader.GetOrdinal("Name")),
                        Type = reader.GetInt32(reader.GetOrdinal("Type")),
                        DaysToExpire = reader.GetInt32(reader.GetOrdinal("DaysToExpire")),
                        RuleJson = reader.GetString(reader.GetOrdinal("RuleJson")),
                        Status = reader.GetInt32(reader.GetOrdinal("Status")),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                        SettingsId = reader.GetGuid(reader.GetOrdinal("SettingsId")),
                        EmailTemplateId = reader.GetGuid(reader.GetOrdinal("EmailTemplateId")),
                        DistributionListId = reader.IsDBNull(reader.GetOrdinal("DistributionListId"))
                            ? null
                            : reader.GetGuid(reader.GetOrdinal("DistributionListId")),
                        DomainId = reader.GetGuid(reader.GetOrdinal("DomainId"))
                    });
                Log($"  -> {configurationData.Rules.Count} rule(s) fetched.");
            }


            else if (sqlQuery.Contains("FROM dbo.srvyrulecounter AS rc"))
            {
                Log("Fetching rule counters...");
                while (reader.Read())
                    configurationData.RuleCounters.Add(new RuleCounterModel
                    {
                        Wildcard = reader.GetString(reader.GetOrdinal("Wildcard")),
                        RuleCounterValue = reader.GetInt32(reader.GetOrdinal("RuleCounterValue")),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                        RuleId = reader.GetGuid(reader.GetOrdinal("RuleId")),
                        RuleCounterId = reader.GetGuid(reader.GetOrdinal("RuleCounterId")),
                        DomainId = reader.IsDBNull(reader.GetOrdinal("DomainId")) ? null : reader.GetGuid(reader.GetOrdinal("DomainId"))
                    });
                Log($"  -> {configurationData.RuleCounters.Count} rule counter(s) fetched.");
            }


            else if (sqlQuery.Contains("FROM dbo.srvyrulehistory AS rh"))
            {
                Log("Fetching rule histories...");
                while (reader.Read())
                    configurationData.RuleHistories.Add(new RuleHistoryModel
                    {
                        RuleHistoryId = reader.GetGuid(reader.GetOrdinal("RuleHistoryId")),
                        ExecutionDate = GetUtcDateTime(reader, "ExecutionDate"),
                        SuccessfulCount = reader.GetInt32(reader.GetOrdinal("SuccessfulCount")),
                        TotalCount = reader.GetInt32(reader.GetOrdinal("TotalCount")),
                        RuleId = reader.GetGuid(reader.GetOrdinal("RuleId")),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                        FailGenerated = reader.GetInt32(reader.GetOrdinal("FailGenerated")),
                        FailureMessage = reader.GetString(reader.GetOrdinal("FailureMessage")),
                        DomainId = reader.GetGuid(reader.GetOrdinal("DomainId"))
                    });
                Log($"  -> {configurationData.RuleHistories.Count} rule histor(y/ies) fetched.");
            }

            else if (sqlQuery.Contains("FROM dbo.srvysettings AS s"))
            {
                Log("Fetching settings...");
                while (reader.Read())
                    configurationData.Settings.Add(new SettingsModel
                    {
                        Name = reader.GetString(reader.GetOrdinal("Name")),
                        SettingsId = reader.GetGuid(reader.GetOrdinal("SettingsId")),
                        SettingsJson = reader.GetString(reader.GetOrdinal("SettingsJson")),
                        Type = reader.GetInt32(reader.GetOrdinal("Type")),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                        DomainId = reader.IsDBNull(reader.GetOrdinal("DomainId")) ? null : reader.GetGuid(reader.GetOrdinal("DomainId")),
                        CrossDomainVisible = reader.GetBoolean(reader.GetOrdinal("CrossDomainVisible"))
                    });
                Log($"  -> {configurationData.Settings.Count} settings record(s) fetched.");
            }


            else if (sqlQuery.Contains("FROM dbo.srvyuser AS u"))
            {
                Log("Fetching users...");
                while (reader.Read())
                    configurationData.Users.Add(new UserModel
                    {
                        UserId = reader.GetGuid(reader.GetOrdinal("UserId")),
                        Username = reader.GetString(reader.GetOrdinal("Username")),
                        Email = reader.GetString(reader.GetOrdinal("Email")),
                        CreationDate = GetUtcDateTime(reader, "CreationDate"),
                        LastLogin = GetUtcDateTime(reader, "LastLogin"),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId"))
                    });
                Log($"  -> {configurationData.Users.Count} user(s) fetched.");
            }


            else if (sqlQuery.Contains("FROM dbo.srvyemailtemplate AS et"))
            {
                Log("Fetching email templates...");
                while (reader.Read())
                    configurationData.EmailTemplates.Add(new EmailTemplateModel
                    {
                        EmailTemplateId = reader.GetGuid(reader.GetOrdinal("EmailTemplateId")),
                        Name = reader.GetString(reader.GetOrdinal("Name")),
                        EmailTemplateJson = reader.GetString(reader.GetOrdinal("EmailTemplateJson")),
                        TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                        SettingsId = reader.GetGuid(reader.GetOrdinal("SettingsId")),
                        DomainId = reader.IsDBNull(reader.GetOrdinal("DomainId")) ? null : reader.GetGuid(reader.GetOrdinal("DomainId"))
                    });
                Log($"  -> {configurationData.EmailTemplates.Count} email template(s) fetched.");
            }
        }
        return configurationData;
    }

    private static Dictionary<Guid, List<SurveyModel>> FetchSurveysGroupedByTemplate(string connectionString)
    {
        var surveysByTemplate = new Dictionary<Guid, List<SurveyModel>>();

        using var connection = new SqlConnection(connectionString);
        connection.Open();

        var surveysSqlPath = Path.Combine(AppContext.BaseDirectory, "Sp", "surveys.sql");
        Log($"Loading SQL from: {surveysSqlPath}");
        var query = File.ReadAllText(surveysSqlPath);
        using var command = new SqlCommand(query, connection);
        command.CommandTimeout = 60;
        Log("Executing surveys query...");
        using var reader = command.ExecuteReader();

        var surveyRowCount = 0;
        while (reader.Read())
        {
            surveyRowCount++;
            if (surveyRowCount % 100 == 0)
                Log($"  Read {surveyRowCount} survey rows so far...");

            var templateId = reader.GetGuid(reader.GetOrdinal("SurveyTemplateId"));

            if (!surveysByTemplate.ContainsKey(templateId))
            {
                surveysByTemplate[templateId] = new List<SurveyModel>();
                Log($"  New survey template encountered: {templateId}");
            }

            var surveyId = reader.GetGuid(reader.GetOrdinal("SurveyId"));
            surveysByTemplate[templateId].Add(new SurveyModel
            {
                SurveyId = surveyId,
                Code = GenerateCode(surveyId),
                CreationDate = GetUtcDateTime(reader, "CreationDate"),
                LastModified = GetUtcDateTime(reader, "LastModified"),
                ExpirationDate = GetNullableUtcDateTime(reader, "ExpirationDate"),
                SurveyJson = reader.GetString(reader.GetOrdinal("SurveyJson")),
                Type = reader.GetInt32(reader.GetOrdinal("Type")),
                Status = reader.GetInt32(reader.GetOrdinal("Status")),
                ExternalId = reader.IsDBNull(reader.GetOrdinal("ExternalId")) ? null : reader.GetString(reader.GetOrdinal("ExternalId")),
                Recipient = reader.IsDBNull(reader.GetOrdinal("Recipient")) ? null :  reader.GetString(reader.GetOrdinal("Recipient")),
                Account = reader.IsDBNull(reader.GetOrdinal("Account")) ? null :  reader.GetString(reader.GetOrdinal("Account")),
                TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                FillerId = reader.IsDBNull(reader.GetOrdinal("FillerId")) ? null :  reader.GetGuid(reader.GetOrdinal("FillerId")),
                RuleId = reader.IsDBNull(reader.GetOrdinal("RuleId")) ? null :  reader.GetGuid(reader.GetOrdinal("RuleId")),
                DomainId = reader.GetGuid(reader.GetOrdinal("DomainId")),
                SurveyTemplateId = reader.GetGuid(reader.GetOrdinal("SurveyTemplateId"))
            });
        }

        return surveysByTemplate;
    }

    private static Dictionary<Guid, List<ExamModel>> FetchExamsGroupedByTemplate(string connectionString)
    {
        var examsByTemplate = new Dictionary<Guid, List<ExamModel>>();

        using var connection = new SqlConnection(connectionString);
        connection.Open();

        var examsSqlPath = Path.Combine(AppContext.BaseDirectory, "Sp", "exams.sql");
        Log($"Loading SQL from: {examsSqlPath}");
        var query = File.ReadAllText(examsSqlPath);
        using var command = new SqlCommand(query, connection);
        command.CommandTimeout = 60;
        Log("Executing exams query...");
        using var reader = command.ExecuteReader();

        var examRowCount = 0;
        while (reader.Read())
        {
            examRowCount++;
            if (examRowCount % 100 == 0)
                Log($"  Read {examRowCount} exam rows so far...");

            var templateId = reader.GetGuid(reader.GetOrdinal("ExamTemplateId"));

            if (!examsByTemplate.ContainsKey(templateId))
            {
                examsByTemplate[templateId] = new List<ExamModel>();
                Log($"  New exam template encountered: {templateId}");
            }

            var examId = reader.GetGuid(reader.GetOrdinal("ExamId"));
            examsByTemplate[templateId].Add(new ExamModel
            {
                ExamId = examId,
                Code = GenerateCode(examId),
                CreationDate = GetUtcDateTime(reader, "CreationDate"),
                LastModified = GetUtcDateTime(reader, "LastModified"),
                ExpirationDate = GetNullableUtcDateTime(reader, "ExpirationDate"),
                ExamJson = reader.GetString(reader.GetOrdinal("ExamJson")),
                Type = reader.GetInt32(reader.GetOrdinal("Type")),
                Status = reader.GetInt32(reader.GetOrdinal("Status")),
                ExternalId = reader.IsDBNull(reader.GetOrdinal("ExternalId"))
                    ? null : reader.GetString(reader.GetOrdinal("ExternalId")),
                Recipient = reader.IsDBNull(reader.GetOrdinal("Recipient"))
                    ? null : reader.GetString(reader.GetOrdinal("Recipient")),
                Account = reader.IsDBNull(reader.GetOrdinal("Account"))
                    ? null : reader.GetString(reader.GetOrdinal("Account")),
                TenantId = reader.GetGuid(reader.GetOrdinal("TenantId")),
                FillerId = reader.IsDBNull(reader.GetOrdinal("FillerId"))
                    ? null : reader.GetGuid(reader.GetOrdinal("FillerId")),
                RuleId = reader.IsDBNull(reader.GetOrdinal("RuleId"))
                    ? null : reader.GetGuid(reader.GetOrdinal("RuleId")),
                DomainId = reader.GetGuid(reader.GetOrdinal("DomainId")),
                ExamTemplateId = reader.GetGuid(reader.GetOrdinal("ExamTemplateId"))
            });
        }
        return examsByTemplate;
    }

    private static async Task SendSurveysAsync(List<SurveyModel> surveys, List<DomainModel> domains, string baseUrl, Guid surveyTemplateId)
    {
        var url = $"{baseUrl}import/survey/{surveyTemplateId}";
        var json = JsonConvert.SerializeObject(new { Domains = domains, Surveys = surveys });
        Log($"  POST {url} | payload: {json.Length / 1024.0:F1} KB");
        var response = await PostWithTokenRefreshAsync(url, json).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} — {body}");

        Log($"  API response {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] + "..." : body)}");
    }

    private static async Task SendExamsAsync(List<ExamModel> exams, List<DomainModel> domains, string baseUrl, Guid examTemplateId)
    {
        var url = $"{baseUrl}import/exams/{examTemplateId}";
        var json = JsonConvert.SerializeObject(new { Domains = domains, Exams = exams });
        Log($"  POST {url} | payload: {json.Length / 1024.0:F1} KB");
        var response = await PostWithTokenRefreshAsync(url, json).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} — {body}");

        Log($"  API response {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] + "..." : body)}");
    }

    private static async Task SendConfigurationDataAsync(ConfigurationData data, string baseUrl, int batchSize)
    {
        var base_ = $"{baseUrl}import/configuration/";

        await PostEntityAsync(base_ + "domains",
            new { Domains = data.Domains }, "domains");
        await PostEntityAsync(base_ + "settings",
            new { Domains = data.Domains, Settings = data.Settings }, "settings");
        await PostEntityAsync(base_ + "users",
            new { Users = data.Users }, "users");
        await PostEntityAsync(base_ + "survey-templates",
            new { Domains = data.Domains, SurveyTemplates = data.SurveyTemplates }, "survey-templates");
        await PostEntityAsync(base_ + "exam-templates",
            new { Domains = data.Domains, ExamTemplates = data.ExamTemplates }, "exam-templates");
        await PostEntityAsync(base_ + "distribution-lists",
            new { Domains = data.Domains, DistributionLists = data.DistributionLists }, "distribution-lists");
        await PostEntityAsync(base_ + "email-templates",
            new { Domains = data.Domains, EmailTemplates = data.EmailTemplates }, "email-templates");
        await PostEntityAsync(base_ + "rules",
            new { Domains = data.Domains, Rules = data.Rules }, "rules");
        await PostEntityAsync(base_ + "rule-counters",
            new { Domains = data.Domains, RuleCounters = data.RuleCounters }, "rule-counters");
        await PostEntityAsync(base_ + "rule-histories",
            new { Domains = data.Domains, RuleHistories = data.RuleHistories }, "rule-histories");

        var pendingBatches = data.PendingEmails.Chunk(batchSize).ToList();
        Log($"Sending {data.PendingEmails.Count} pending emails in {pendingBatches.Count} batch(es)...");
        for (var i = 0; i < pendingBatches.Count; i++)
            await PostEntityAsync(base_ + "pending-emails",
                new { Domains = data.Domains, PendingEmails = pendingBatches[i].ToList() },
                $"pending-emails {i + 1}/{pendingBatches.Count}");

        var processedBatches = data.ProcessedEmails.Chunk(batchSize).ToList();
        Log($"Sending {data.ProcessedEmails.Count} processed emails in {processedBatches.Count} batch(es)...");
        for (var i = 0; i < processedBatches.Count; i++)
            await PostEntityAsync(base_ + "processed-emails",
                new { Domains = data.Domains, ProcessedEmails = processedBatches[i].ToList() },
                $"processed-emails {i + 1}/{processedBatches.Count}");
    }

    private static async Task PostEntityAsync(string url, object payload, string label)
    {
        var json = await WithTickerAsync(
            () => Task.Run(() => JsonConvert.SerializeObject(payload)),
            $"  [{label}] Still serializing");
        Log($"  [{label}] POST {url} ({json.Length / 1024.0:F1} KB)");
        var response = await WithTickerAsync(
            () => PostWithTokenRefreshAsync(url, json),
            $"  [{label}] Still waiting");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"[{label}] HTTP {(int)response.StatusCode} {response.ReasonPhrase} — {body}");
        Log($"  [{label}] OK ({(int)response.StatusCode})");
    }

    private static async Task<T> WithTickerAsync<T>(Func<Task<T>> work, string tickerPrefix, int intervalSeconds = 10)
    {
        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ticker = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(intervalSeconds * 1000, cts.Token); } catch (OperationCanceledException) { break; }
                if (!cts.Token.IsCancellationRequested)
                    Log($"{tickerPrefix}... {sw.Elapsed.TotalSeconds:F0}s elapsed");
            }
        });

        var result = await work().ConfigureAwait(false);
        await cts.CancelAsync();
        await ticker;
        Log($"{tickerPrefix.TrimEnd('.')} done in {sw.Elapsed.TotalSeconds:F1}s");
        return result;
    }

    private static async Task<HttpResponseMessage> PostWithTokenRefreshAsync(string url, string json)
    {
        await EnsureTokenAsync().ConfigureAwait(false);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await HttpClient.PostAsync(url, content).ConfigureAwait(false);

        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            return response;

        var wwwAuth = response.Headers.WwwAuthenticate.ToString();
        Log($"HTTP 401 received — WWW-Authenticate: {(string.IsNullOrEmpty(wwwAuth) ? "(none)" : wwwAuth)}");
        Log("Forcing token refresh and retrying...");
        await RefreshTokenAsync().ConfigureAwait(false);
        var retryResponse = await HttpClient.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json")).ConfigureAwait(false);
        if (retryResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            var retryWwwAuth = retryResponse.Headers.WwwAuthenticate.ToString();
            Log($"Retry also 401 — WWW-Authenticate: {(string.IsNullOrEmpty(retryWwwAuth) ? "(none)" : retryWwwAuth)}");
        }
        return retryResponse;
    }

    private static async Task EnsureTokenAsync()
    {
        if (!string.IsNullOrEmpty(_currentToken) && DateTime.UtcNow < _tokenExpiresAt.AddSeconds(-60))
            return;
        await RefreshTokenAsync().ConfigureAwait(false);
    }

    private static async Task RefreshTokenAsync()
    {
        Log("Fetching access token from token endpoint...");
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("client_secret", _clientSecret)
        });

        var response = await HttpClient.PostAsync(_tokenUrl, content).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Token request failed: HTTP {(int)response.StatusCode} — {body}");

        var tokenResponse = JsonConvert.DeserializeObject<TokenResponse>(body)
            ?? throw new InvalidOperationException("Token endpoint returned null response.");

        _currentToken = tokenResponse.AccessToken;
        _tokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
        HttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _currentToken);
        Log($"Access token obtained. Expires in {tokenResponse.ExpiresIn}s (at {_tokenExpiresAt:HH:mm:ss} UTC).");

        // Decode JWT payload to diagnose audience issues
        var parts = _currentToken.Split('.');
        if (parts.Length == 3)
        {
            var padded = parts[1].PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '=');
            var payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var claims = JsonConvert.DeserializeObject<Dictionary<string, object>>(payload);
            if (claims != null)
            {
                var aud = claims.TryGetValue("aud", out var a) ? a?.ToString() : "(none)";
                var iss = claims.TryGetValue("iss", out var i) ? i?.ToString() : "(none)";
                Log($"  Token iss: {iss}");
                Log($"  Token aud: {aud}");
            }
        }
    }
}