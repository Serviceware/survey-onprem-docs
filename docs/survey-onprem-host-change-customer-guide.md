# Moving Survey to a New Server

**Serviceware Survey · On-Premise**

How to restore your existing Survey database on a new server with a new web address. Follow the steps in order.

| | |
|---|---|
| **Who performs this** | Database admin, Windows admin, Keycloak admin |
| **Downtime** | Yes — Survey must be stopped |
| **Database changes** | One row, one `UPDATE` statement |
| **Estimated effort** | Half a day plus testing |

> **The one rule to get right**
>
> The first part of the new web address, the tenant name in the database, and the Keycloak realm name must all be identical.
>
> Example: if the new address is `https://survey.company.com`, then the tenant name in the database must be `survey` and the Keycloak realm must be called `survey`.
>
> If these three do not match exactly, nobody can log in.

## Contents

1. [Before you start](#1-before-you-start)
2. [Procedure](#2-procedure)
3. [Keycloak setup](#3-keycloak-setup)
4. [Check that it works](#4-check-that-it-works)
5. [If something does not work](#5-if-something-does-not-work)
6. [E-mails already sent](#6-e-mails-already-sent)
7. [Going back](#7-going-back)

---

## 1. Before you start

### Collect from the old system

Write these down — you need them later, and for rolling back if anything goes wrong.

1. The current tenant settings. Run this on the old database and save the result:

   ```sql
   SELECT * FROM dbo.srvytenant;
   SELECT domainid, name, role, [default] FROM dbo.srvydomain;
   ```

2. Three values from the Survey configuration file (`appsettings.json`) on the old server:
   - `SaltKey`
   - `EncriptionKey`
   - `Survey:UnsubscribeHmacSecret`

> **Do not generate new values for these three keys.**
>
> Your saved e-mail passwords and database passwords are encrypted with them. If the new server uses different values, Survey will look completely normal but will not be able to send any e-mail and will not be able to read any of your data source connections. Copy the three values across exactly as they are.

### Decide the new addresses

| You need | Example | Notes |
|---|---|---|
| New Survey web address | `https://survey.company.com` | The first part (`survey`) becomes the tenant name and realm name |
| Keycloak address | `https://sso.company.com` | Can stay the same if Keycloak is not moving |
| Realm name | `survey` | Must match the first part of the web address |

---

## 2. Procedure

### Step 1 — Stop the old system

Stop Survey on the old server, including the scheduler. If the scheduler keeps running it will keep sending surveys that point at the old address.

### Step 2 — Back up and restore the database

1. Take a full backup of the Survey database on the old server.
2. Restore it on the new database server. Keeping the same database name makes the rest simpler.

### Step 3 — Install and configure Survey on the new server

Install Survey as usual, then set the following in the configuration:

| Setting | Value |
|---|---|
| Database connection | Points at the restored database on the new server |
| `SaltKey`, `EncriptionKey`, `Survey:UnsubscribeHmacSecret` | Copied from the old server, unchanged |
| `Authority` | `https://<keycloak-address>/auth/realms/` |
| Web front-end `configuration.json` | New Survey address, new Keycloak address, realm name, and `"OnPremise": true` |

Your reverse proxy or load balancer must pass the new host name through to Survey in the `X-Forwarded-Host` header. This is how Survey knows which tenant is being addressed.

### Step 4 — Start Survey once, then stop it

Start Survey and let it complete its database update. This brings the restored database up to the version of the software you installed. Wait until it has finished, then continue.

> **If your backup is from an older Survey version**
>
> The database update renames some columns automatically — but it does not adjust the values inside them. That is exactly what Step 5 is for, so do not skip it even if the system appears to start normally.

### Step 5 — Update the tenant record

This is the only manual database change. Fill in the four values at the top and run the whole script in SQL Server Management Studio.

```sql
USE [<your Survey database>];
GO

DECLARE @NewName     nvarchar(255) = N'survey';                      -- first part of the web address
DECLARE @NewBaseUrl  nvarchar(255) = N'https://survey.company.com';  -- no slash at the end
DECLARE @KeycloakUrl nvarchar(255) = N'https://sso.company.com';     -- no slash, no /auth
DECLARE @ClientId    nvarchar(255) = N'survey-client';               -- Keycloak client for the web UI

-- look at the current values first
SELECT name, fqdn, issuer, clientId, webApiUrl, webApiDocumentationUrl, onPremise, status
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

-- check the result, then keep or discard it
SELECT name, fqdn, issuer, clientId, webApiUrl, webApiDocumentationUrl, onPremise, status
FROM dbo.srvytenant;

COMMIT TRANSACTION;   -- run ROLLBACK TRANSACTION instead if the values look wrong
GO
```

What each value is for:

| Column | What it is |
|---|---|
| `name` | Tenant name. Must match the first part of the web address and the Keycloak realm. |
| `fqdn` | Web address used in every survey link sent by e-mail |
| `webApiUrl` | Used for the unsubscribe link in e-mails |
| `webApiDocumentationUrl` | API documentation link shown in the interface |
| `issuer` | Full address of your Keycloak realm |
| `clientId` | Keycloak client used by the web interface |

> **Never change these**
>
> `tenantid` and `sharedkey` in `srvytenant`, and `domainid` and `sharedkey` in `srvydomain`.
>
> The shared keys are the password your service desk uses to request surveys. Changing them stops all automatic survey creation until the calling system is updated too. The IDs link every survey, template and rule to the tenant.
>
> `status` must stay `0`, which means active.

---

## 3. Keycloak setup

Nothing in Keycloak is created automatically for on-premise installations. All of the following has to exist before anyone can log in.

### Realm

Create a realm with the same name you used for `@NewName` in Step 5. The safest approach is to export the realm from the old Keycloak and import it under the new name.

### Roles

These must exist as **realm roles**, not client roles:

| Role | Who gets it |
|---|---|
| `survey-admin` | Administrators — settings, data connections, tenants, domains |
| `survey-designer` | Survey designers — surveys, rules, e-mail templates |
| `serviceware-survey` | Everyone who uses Survey |
| `survey-filler` | Users who only fill in surveys |

In addition, one role per domain. Run this on the restored database and create a realm role for every value in the `role` column:

```sql
SELECT name, role FROM dbo.srvydomain;
```

> **A missing domain role shows no error message**
>
> Users will simply see an empty list of domains and will not understand why. Check the spelling character by character. A standard installation has one entry, usually `survey-default-participants`.

### Clients

Two clients are involved:

| Client | What to do |
|---|---|
| `survey-api` | Must be present, and must appear in the audience of the login token. Keep the audience mapper that adds it. |
| Web interface client (for example `survey-client`) | Update the addresses below for the new server |

For the web interface client:

- Valid redirect URIs — `https://<new address>/*`
- Valid post logout redirect URIs — `https://<new address>/*`
- Web origins — `https://<new address>`

### Users

Survey recognises users by e-mail address, so existing accounts and their surveys are kept as long as the new realm uses the same e-mail addresses. Role assignments are not carried over — assign the roles above to your users in the new realm.

---

## 4. Check that it works

1. Open the new Survey address in a browser and log in as an administrator.
2. Check that the domain list is not empty.
3. Open an existing survey template — your existing data should all be there.
4. Go to Settings and send a test e-mail. This confirms the three configuration keys were copied correctly.
5. Open a distribution list that reads from a database connection and refresh it.
6. Send a survey by e-mail and confirm the link in the e-mail points at the new address and opens.
7. Check that the unsubscribe link at the bottom of that e-mail works.
8. Start the scheduler and let one rule run.

---

## 5. If something does not work

| Symptom | Cause | Fix |
|---|---|---|
| Page does not load, error mentions tenant not found | The tenant name does not match the web address | Check `name` in `srvytenant` equals the first part of the address, and that the proxy forwards `X-Forwarded-Host` |
| Login succeeds but Survey shows an error or stays empty | Keycloak realm address or audience is wrong | Check `issuer` in `srvytenant`, and that `survey-api` is in the token audience |
| Logged in, but domain list is empty | Domain roles missing in the new realm | Create a realm role for every value in `srvydomain.role` |
| E-mails are not sent | `SaltKey` or `EncriptionKey` differ from the old server | Copy both values from the old configuration file |
| Data source connections fail | Same cause as above, or the source database also moved | Copy the two keys; re-enter the connection in the interface if the source server changed |
| Survey links in new e-mails point at the old address | `fqdn` was not updated | Re-run the update script in Step 5 |

---

## 6. E-mails already sent

Surveys sent before the move contain links to the old address. New e-mails use the new address automatically. If open surveys matter to you, keep a redirect from the old address to the new one for as long as those surveys stay open.

If anyone typed a fixed link into an e-mail template by hand, that link still points at the old server. This query lists the templates to check — replace `oldserver` with your old address:

```sql
SELECT name
FROM dbo.srvyemailtemplate
WHERE emailtemplatejson LIKE N'%oldserver%';
```

---

## 7. Going back

The database change is a single `UPDATE` on a single row. Put back the values you saved in section 1 and the system returns to its previous state.

If you need to undo the version upgrade as well, restore the backup you took in Step 2. The database update cannot be reversed on its own.
