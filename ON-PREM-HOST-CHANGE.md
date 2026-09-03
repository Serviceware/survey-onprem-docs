# Serviceware Survey — On-Premise Move to a New Host

Manual runbook for restoring an existing on-premise Survey database onto a new
server with a new host name.

Audience: customer administrator (DBA + application admin + Keycloak admin).
Applies to: on-premise installations (`DatabaseType = MSSQLServer`).

---

## 1. What the host name is actually wired to

Survey resolves the tenant from the **first label of the incoming host header**
and looks it up against `srvytenant.name`:

| Layer | Behaviour |
|---|---|
| `HostResolutionStrategy` | takes `X-Forwarded-Host` (fallback `Host`), splits on `.`, keeps `parts[0]` |
| `MultiTenantService` | `SELECT ... FROM srvytenant WHERE name = <that prefix>` — exact match, no fallback |
| `TenantKeycloakAuthenticationOptionsService` | `Realm = tenant.name`, `Authority = tenant.issuer`, audience `survey-api` |

Consequence — this is the rule the whole move hangs on:

```
first label of the new URL  ==  srvytenant.name  ==  Keycloak realm name  ==  last segment of srvytenant.issuer
```

If any of those four drift apart, every request fails at tenant resolution
(`Tenant with FQDN '<x>' not found`) or at token validation (401).

Other host-derived values, all read at runtime out of `srvytenant`:

| Column | Used for |
|---|---|
| `fqdn` | base URL of every generated survey/exam link (`<fqdn>surveyvieweranonymous/<code>` etc.) |
| `webApiUrl` | base of the unsubscribe link in outgoing mail |
| `webApiDocumentationUrl` | Swagger/docs link in the UI |
| `issuer` | OIDC authority; must equal the `iss` claim Keycloak puts in tokens |
| `clientId` | OIDC client used by the UI |

> There is an API (`UpdateTenantConfiguration`) that derives `issuer`,
> `webApiUrl` and `webApiDocumentationUrl` from `fqdn` — but it only runs when
> `webApiDocumentationUrl` is empty. On a restored database it is a no-op, so
> the update below has to be done by hand.

---

## 2. Schema check before you start — old backup vs. new build

The tenant table was reshaped twice. If the backup predates the target build,
the columns you know are not the columns the new build reads:

| Original (initial schema) | Renamed by migration | Current name |
|---|---|---|
| `realm` | `AdaptForMultiIssuer` (2024-05-30) | `issuer` |
| `dns` | `TenantUpdate` (2025-07-17) | `fqdn` |
| `realmid` | dropped by `TenantUpdate` | — |
| `sharedkey` `uniqueidentifier` | `ChangeDataTypeSharedKey` (2025-11-11) | `nvarchar(64)` |

`TenantUpdate` also **adds** `clientId`, `onPremise`, `webApiUrl` and
`webApiDocumentationUrl` with empty-string defaults.

So if the backup still has `[sharedkey] [realm] [dns]`:

1. The migrations service renames `realm` → `issuer` and `dns` → `fqdn` on first
   start. It does **not** convert the values.
2. The old `realm` column held a bare realm name in most installations. `issuer`
   must be a **full absolute URL**
   (`https://<keycloak-host>/auth/realms/<realm>`) — the application parses it
   with `new Uri(...)` and a bare name throws.
3. `clientId`, `webApiUrl` and `webApiDocumentationUrl` land as empty strings and
   must be filled in.

**Order of work: restore → let migrations run → then run the UPDATE statements.**
Running the updates against the un-migrated schema hits column names that no
longer exist.

---

## 3. Things that must be carried over unchanged from the old host

These are **not** in the database, and getting them wrong silently breaks data
that looks perfectly fine in the tables:

| Setting | Where | Why it matters |
|---|---|---|
| `EncriptionKey` | `appsettings.json` / env | AES-GCM key for stored secrets |
| `SaltKey` | `appsettings.json` / env | salt for the same |
| `Survey:UnsubscribeHmacSecret` | `appsettings.json` / env | changing it invalidates unsubscribe links already in the wild |

`srvysettings.settingsjson` (SMTP password) and
`srvysqlconnection.sqlconnectionjson` (source-database password) hold values
encrypted with `SaltKey` + `EncriptionKey`. Copy both values verbatim to the new
host. If they differ, mail sending and every distribution-list query fail at
decrypt time — while the restored rows still look normal.

Also re-point in configuration:

- `ConnectionStrings:SQLServer` → the new database server.
- `Authority` → the new Keycloak base (`https://<keycloak-host>/auth/realms/`).
- Frontend `assets/configuration.json`: `SWSurveyAPI`, `SWSurveySwagger`,
  `SWAuthenticator`, `SWAuthenticatorClient`, `SWFallbackRealm`,
  `"OnPremise": true`.
- Reverse proxy must forward `X-Forwarded-Host` with the new host name.

---

## 4. Keycloak — manual work on the new host

On-premise tenants **never** call the user-management API (`DomainService` skips
role provisioning when `tenant.onPremise = 1`). Nothing in this list is created
automatically — all of it is manual.

### 4.1 Realm

Realm name must equal `srvytenant.name` and the first label of the new URL.
Easiest path is to export the old realm and import it under the new name rather
than rebuilding it by hand.

### 4.2 Realm roles (not client roles)

Authorization uses `RequireRealmRoles`, and the API reads the `realm_access.roles`
claim. Client roles are ignored.

| Realm role | Purpose |
|---|---|
| `survey-admin` | full admin — settings, SQL connections, tenants, domains, templates |
| `survey-designer` | designer — surveys, rules, e-mail templates |
| `serviceware-survey` | module/access role for the product |
| `survey-filler` | filler; filtered out of domain matching |

### 4.3 One realm role per domain

`API/Helper/UserUtilities.GetDomains` takes every realm role in the user's token
(minus `uma_authorization`, `offline_access`, `survey-designer`, `survey-admin`,
`survey-filler`) and matches it against `srvydomain.role`. A user sees only the
domains whose role they hold — so **every value in `srvydomain.role` must exist
as a realm role in the new realm, spelled identically.**

```sql
SELECT domainid, name, role, [default], status FROM dbo.srvydomain;
```

Default single-domain installations have one row with
`role = 'survey-default-participants'`. Naming convention used by the product is
`survey-<domain-name>-participants`, lowercase, spaces replaced by `-`.

### 4.4 Clients

| Client | Notes |
|---|---|
| `survey-api` | the API's audience; the token must carry it in `aud`. Configured audiences are `survey-api\|SSP\|account`. |
| UI client (`SWAuthenticatorClient` in `configuration.json`, e.g. `survey-client`) | public client used by the Angular app |

On the UI client, update for the new host:

- **Valid redirect URIs** — `https://<new-host>/*`
- **Valid post logout redirect URIs** — `https://<new-host>/*`
- **Web origins** — `https://<new-host>`
- keep the audience mapper that puts `survey-api` into `aud`, otherwise every
  authenticated call returns 401 with a valid-looking login.

### 4.5 Users

Survey matches users by **e-mail address within a tenant** (`srvyuser.email`) —
no Keycloak subject id is stored. Accounts survive the move as long as the new
realm issues the same e-mail addresses. Role assignments do **not** survive: they
have to be re-assigned in the new realm.

---

## 5. Restore and update procedure

### 5.1 Preparation

1. Stop the Survey API, the scheduler and the retention-policy worker on the old
   host. A running scheduler will generate surveys carrying the old links.
2. Take a fresh full backup of the old database.
3. Note down, from the old system:
   - `SELECT tenantid, name, fqdn, issuer, clientId, sharedkey FROM dbo.srvytenant;`
   - `SELECT domainid, name, role, sharedkey, [default] FROM dbo.srvydomain;`
   - the `EncriptionKey`, `SaltKey` and `UnsubscribeHmacSecret` values.

### 5.2 Restore

4. Restore the backup onto the new SQL Server (keeping the database name keeps
   the connection string simple).
5. Deploy the new configuration files **including the copied SaltKey /
   EncriptionKey**.
6. Start the migrations job and let it finish. Verify:

```sql
SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.srvytenant');
-- expected: tenantid, name, sharedkey, fqdn, clientId, issuer, webApiUrl,
--           webApiDocumentationUrl, onPremise, modules, status,
--           ShowEnvironmentLabel, FixedEnvironmentLabel,
--           RetentionPolicyMonthsTimespan
```

### 5.3 The manual update

Fill in the four variables, then run. `@NewName` is the **first label of the new
URL** and must equal the Keycloak realm name.

```sql
USE [<SurveyDatabase>];
GO

DECLARE @NewName     nvarchar(255) = N'survey';                       -- host prefix == realm
DECLARE @NewBaseUrl  nvarchar(255) = N'https://survey.customer.tld';  -- no trailing slash
DECLARE @KeycloakUrl nvarchar(255) = N'https://sso.customer.tld';     -- no trailing slash, no /auth
DECLARE @ClientId    nvarchar(255) = N'survey-client';

-- inspect first
SELECT tenantid, name, fqdn, issuer, clientId, webApiUrl, webApiDocumentationUrl,
       onPremise, status
FROM dbo.srvytenant;

BEGIN TRANSACTION;

UPDATE dbo.srvytenant
SET name                   = @NewName,
    fqdn                   = @NewBaseUrl + N'/',
    webApiUrl              = @NewBaseUrl + N'/api',
    webApiDocumentationUrl = @NewBaseUrl + N'/api/docs/index.html',
    issuer                 = @KeycloakUrl + N'/auth/realms/' + @NewName,
    clientId               = @ClientId,
    onPremise              = 1;

-- verify before committing
SELECT tenantid, name, fqdn, issuer, clientId, webApiUrl, webApiDocumentationUrl,
       onPremise, status
FROM dbo.srvytenant;

COMMIT TRANSACTION;   -- or ROLLBACK TRANSACTION
GO
```

Single-tenant on-premise installations have exactly one row in `srvytenant`; if
the SELECT returns more than one, add `WHERE tenantid = '<guid>'` to the UPDATE.

Optional, cosmetic — the default domain was created carrying the tenant's name
and keeps the old one:

```sql
UPDATE d
SET d.name = t.name
FROM dbo.srvydomain d
JOIN dbo.srvytenant t ON t.tenantid = d.tenantid
WHERE d.[default] = 1;
```

Do **not** change `srvydomain.role` unless the matching Keycloak realm role is
renamed at the same time.

### 5.4 Do not change

| Column | Reason |
|---|---|
| `srvytenant.tenantid` | foreign key of every other table |
| `srvytenant.sharedkey` | the calling system (Service Desk / SSP) authenticates survey requests with it; rotating it breaks every integration until the caller is updated too |
| `srvydomain.domainid` / `srvydomain.sharedkey` | same, per domain |
| `srvytenant.status` | must stay `0` (Active) |
| any GUID primary key | template ids are referenced from inside `srvyemailtemplate.emailtemplatejson` |

---

## 6. Data that keeps the old host name

Survey links are **generated at send time** from `srvytenant.fqdn`, so new mail
picks up the new host automatically. The following still carry the old name and
deserve a decision rather than a blind update:

| Where | What | Recommendation |
|---|---|---|
| Mail already delivered to recipients | absolute links to the old host | keep a redirect from the old host, or accept that in-flight surveys are lost |
| `srvyemailtemplate.emailtemplatejson` | any absolute URL an author typed by hand into a template | search for the old host and edit those templates in the UI |
| `srvysqlconnection.sqlconnectionjson` | `server` of the *source* system | only change if the source database moved too; the password field is encrypted — edit via the UI, never in SQL |
| `srvysettings.settingsjson` | SMTP server name / sender address | only change if the mail relay moved; password is encrypted, edit via the UI |
| `srvylogs` | old host names in historical log rows | leave as is |

Quick check for hardcoded links:

```sql
SELECT emailtemplateid, name
FROM dbo.srvyemailtemplate
WHERE emailtemplatejson LIKE N'%<old-host-name>%';
```

---

## 7. Verification after the move

1. `GET https://<new-host>/api/healthcheck` → 200. This endpoint bypasses tenant
   resolution, so it only proves the app is up.
2. Open the UI on the new host and log in. `Tenant with FQDN '<x>' not found` in
   the log means `srvytenant.name` does not match the first label of the host the
   proxy forwards.
3. A 401 after a successful Keycloak login means either `issuer` does not match
   the `iss` claim — compare against
   `https://<keycloak-host>/auth/realms/<realm>/.well-known/openid-configuration`
   — or `survey-api` is missing from the token's `aud`.
4. Log in as an admin and as a designer; confirm the menu and the domain list are
   populated. An empty domain list means the domain roles from `srvydomain.role`
   are missing or misspelled in the new realm.
5. Open an existing survey template, send a test mail, confirm the link in the
   mail points at the new host and opens.
6. Send an SMTP test mail from Settings — proves `SaltKey` / `EncriptionKey` were
   carried over correctly.
7. Open a distribution list backed by a SQL connection and refresh it — proves
   the same for the source-database password.
8. Trigger one rule manually and confirm the generated survey link.
9. Verify the unsubscribe link in a test mail resolves.

---

## 8. Rollback

The database change is one UPDATE against one row. Keep the pre-change values
from step 5.1 and restore them if needed.

If the schema migration itself has to be undone, restore the pre-migration
backup — EF migrations are not reversed automatically, so a rollback across a
version change means restoring the backup, not down-migrating.
