# Serviceware Survey — On-Premise Documentation

Operational documentation for on-premise Serviceware Survey installations
(`DatabaseType = MSSQLServer`).

## Contents

| Document | What it covers |
|---|---|
| [`ON-PREM-HOST-CHANGE.md`](ON-PREM-HOST-CHANGE.md) | Manual runbook for restoring an existing on-premise Survey database onto a new server under a new host name. |
| [`Survey-OnPrem-Host-Change.docx`](Survey-OnPrem-Host-Change.docx) | The same runbook as a document, for handing to a customer administrator. |
| [`SurveyExporter/`](SurveyExporter) | .NET console tool that reads surveys, exams and configuration from an on-premise Survey SQL Server database and pushes them to a Survey (SCE) instance via its API. |

## Host change — the short version

Survey resolves the tenant from the **first label of the incoming host header**
and matches it against `srvytenant.name` exactly, with no fallback. The whole
move hangs on one rule:

```
first label of the new URL  ==  srvytenant.name  ==  Keycloak realm name  ==  last segment of srvytenant.issuer
```

If any of those four drift apart, every request fails at tenant resolution
(`Tenant with FQDN '<x>' not found`) or at token validation (401).

Three things that are **not** in the database and break restored data silently if
they change: `EncriptionKey`, `SaltKey` and `Survey:UnsubscribeHmacSecret`. The
SMTP password and the source-database password are stored encrypted with the
first two, so the rows keep looking fine while mail sending and every
distribution-list query fail at decrypt time.

On-premise tenants never call the user-management API, so the Keycloak realm,
realm roles, the per-domain roles, clients and users are **all manual**.

Read the full runbook before starting. It covers the schema drift between an old
backup and a new build (`realm`→`issuer`, `dns`→`fqdn`, dropped `realmid`,
`sharedkey` type change), the exact UPDATE, the columns that must not be touched,
the data that keeps the old host name, a nine-step verification list and
rollback.

## Survey Exporter

`SurveyExporter` is a .NET console application. It runs the stored queries in
`SurveyExporter/SurveyExporter/Sp/` (`configuration.sql`, `surveys.sql`,
`exams.sql`) against the source database, then sends the results in batches to
the target Survey API, authenticating with a Keycloak client-credentials token
(refreshed automatically on 401).

Configure `SurveyExporter/SurveyExporter/appsettings.json` before running:

| Key | Meaning |
|---|---|
| `ConnectionStrings:DefaultConnection` | Source on-premise Survey SQL Server database |
| `ApiUrl` | Target Survey API base URL (`https://<host>/api/`) |
| `TokenUrl` | Keycloak token endpoint of the target realm |
| `ClientId` / `ClientSecret` | Keycloak client used for the export |
| `BatchSize` | Number of records per API request (`500` in the shipped config; `5000` if unset) |

Never commit real credentials to `appsettings.json`.

```
cd SurveyExporter/SurveyExporter
dotnet run
```

## Audience

Customer administrator: DBA + application admin + Keycloak admin.

## Source

The runbook is maintained in the Survey repository at
[`backend/SWSurvey/Documentation/OnPrem`](https://github.com/serviceware-se/survey/tree/main/backend/SWSurvey/Documentation/OnPrem)
and mirrored here as the customer-facing copy.
