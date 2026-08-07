# Runbook: Secret Rotation

**Project**: CmsEvents Service
**Last updated**: August 2026

## Purpose

Operational procedure for 90-day rotation of all production secrets per ADR-012. Applies to production only; local dev secrets are per-developer and rotate on-demand.

## When to run

- **Scheduled**: every 90 days. Owner: on-call rotation lead. Track via calendar reminder.
- **Ad-hoc triggers** (rotate immediately regardless of schedule):
  - Team member with credential access leaves the team.
  - Suspected credential leak (accidental commit, laptop stolen, phishing incident).
  - Compliance audit requires proof of recent rotation.

## Secrets in scope

| Key Vault secret | Type | Downstream impact |
|------------------|------|-------------------|
| `Users--CmsWebhookUser--PasswordHash` | BCrypt hash | CMS producer must receive the new password |
| `Users--ReadonlyUser--PasswordHash` | BCrypt hash | Authorized consumers must receive the new password |
| `Users--AdminUser--PasswordHash` | BCrypt hash | Admin(s) must receive the new password |
| `ConnectionStrings--Writer` | SQL connection string | Application restart required |
| `ConnectionStrings--Reader` | SQL connection string | Application restart required |
| `Observability--ApplicationInsightsConnectionString` | AI ingestion connection string | Telemetry gap of ~1 minute during restart |

Note: Key Vault secret names use `--` in place of `:` due to Key Vault naming restrictions. The `IConfiguration` provider translates back to `:` at runtime.

## Prerequisites

- Azure CLI installed and authenticated (`az login`).
- Access to Key Vault `kv-cmsevents-prod` (`Secret Officer` role or equivalent).
- Access to CI/CD pipeline or `az webapp restart` permission on the hosting resource.
- Access to Azure SQL for password rotation (`db_owner` on the SQL user).
- A secure channel to communicate new passwords to downstream parties (encrypted messaging, password manager share).

## Pre-rotation checklist

- [ ] Rotation window agreed with stakeholders (CMS team, consumers, admin team).
- [ ] Verify access to Key Vault, Azure CLI, SQL Server admin.
- [ ] On-call notified.
- [ ] Rollback plan reviewed (Key Vault soft-delete retains previous versions).
- [ ] Rotation log ready to update (see § Rotation log).

---

## Procedure — Rotate a Users password (BCrypt hash)

Repeat for each user (`cms-webhook-user`, `readonly-user`, `admin-user`).

### Step 1: Generate a new random password

```bash
NEW_PASSWORD=$(openssl rand -base64 32 | tr -d '/+=' | cut -c1-24)
echo "New password: $NEW_PASSWORD"
# Store this value securely for the delivery step below.
```

### Step 2: Generate the BCrypt hash

Use the `tools/BcryptHash` utility included in the solution (work factor 11 per ADR-011):

```bash
cd tools/BcryptHash
dotnet run -- --password "$NEW_PASSWORD"
# Output: BCrypt hash, e.g. $2a$11$abcdef...
```

Alternative one-liner via `dotnet-script`:

```bash
dotnet script eval "Console.WriteLine(BCrypt.Net.BCrypt.HashPassword(\"$NEW_PASSWORD\", 11))"
```

Copy the resulting hash to `$BCRYPT_HASH`.

### Step 3: Store the new hash in Key Vault

```bash
az keyvault secret set \
  --vault-name kv-cmsevents-prod \
  --name "Users--CmsWebhookUser--PasswordHash" \
  --value "$BCRYPT_HASH"
```

Repeat for `Users--ReadonlyUser--PasswordHash` and `Users--AdminUser--PasswordHash` with their respective new hashes.

### Step 4: Restart the service

```bash
az webapp restart --name cmsevents-prod --resource-group rg-cmsevents
```

Startup reads new secrets from Key Vault via the configuration provider. Users are re-seeded from configuration (idempotent — existing rows are updated, not duplicated).

### Step 5: Deliver the new password to downstream

- **CMS webhook user**: share `$NEW_PASSWORD` with the CMS team via an encrypted channel (password manager share, PGP-encrypted email, etc.). Coordinate a switchover time.
- **Readonly user**: share with authorized consumers.
- **Admin user**: share with admin(s).

### Step 6: Verify

```bash
# Test the CMS webhook user with an empty batch
curl -u "cms-webhook-user:$NEW_PASSWORD" \
  -X POST https://cmsevents-prod.azurewebsites.net/cms/events \
  -H "Content-Type: application/json" -d '[]'
# Expected: 200 OK with totalEvents: 0

# Test the readonly user
curl -u "readonly-user:$NEW_READONLY_PASSWORD" \
  https://cmsevents-prod.azurewebsites.net/entities
# Expected: 200 OK with entities list

# Test the admin user by disabling and re-enabling a known test entity
curl -u "admin-user:$NEW_ADMIN_PASSWORD" \
  -X POST https://cmsevents-prod.azurewebsites.net/entities/test-entity/disable
# Expected: 200 OK
```

---

## Procedure — Rotate a SQL connection string

Applies to both `ConnectionStrings--Writer` and `ConnectionStrings--Reader`.

### Step 1: Rotate the SQL user password

```sql
-- Executed as a SQL admin (via Azure Portal query editor, sqlcmd, or SSMS)
ALTER LOGIN cmsevents_app WITH PASSWORD = '<new-random-password>';
```

### Step 2: Construct new connection strings

Writer:

```
Server=tcp:sqlserver-cmsevents.database.windows.net,1433;
Initial Catalog=CmsEventsDb;
User ID=cmsevents_app;
Password=<new-password>;
Encrypt=True;
TrustServerCertificate=False;
Connection Timeout=30;
```

Reader (append `ApplicationIntent=ReadOnly` per ADR-010):

```
Server=tcp:sqlserver-cmsevents.database.windows.net,1433;
Initial Catalog=CmsEventsDb;
User ID=cmsevents_app;
Password=<new-password>;
Encrypt=True;
TrustServerCertificate=False;
Connection Timeout=30;
ApplicationIntent=ReadOnly;
```

### Step 3: Store in Key Vault

```bash
az keyvault secret set \
  --vault-name kv-cmsevents-prod \
  --name "ConnectionStrings--Writer" \
  --value "$NEW_WRITER_CONNECTION_STRING"

az keyvault secret set \
  --vault-name kv-cmsevents-prod \
  --name "ConnectionStrings--Reader" \
  --value "$NEW_READER_CONNECTION_STRING"
```

### Step 4: Restart the service

```bash
az webapp restart --name cmsevents-prod --resource-group rg-cmsevents
```

### Step 5: Verify

Check Application Insights for a clean startup log (`Application started`) and a successful test query response.

```bash
curl -u "readonly-user:$READONLY_PASSWORD" \
  https://cmsevents-prod.azurewebsites.net/entities
# Expected: 200 OK
```

---

## Procedure — Rotate Application Insights connection string

### Step 1: Regenerate in Azure Portal

1. Navigate to the Application Insights resource (`ai-cmsevents-prod`).
2. Open `Configure → Properties → Connection String`.
3. Click `Regenerate` and copy the new connection string.

### Step 2: Store in Key Vault

```bash
az keyvault secret set \
  --vault-name kv-cmsevents-prod \
  --name "Observability--ApplicationInsightsConnectionString" \
  --value "$NEW_AI_CONNECTION_STRING"
```

### Step 3: Restart the service

```bash
az webapp restart --name cmsevents-prod --resource-group rg-cmsevents
```

### Step 4: Verify

Confirm new telemetry appears in Application Insights within 5 minutes. Query:

```
requests
| where timestamp > ago(10m)
| take 10
```

---

## Post-rotation verification checklist

- [ ] All three user credentials authenticate successfully (see verification commands above).
- [ ] `POST /cms/events` returns 200 with an empty batch.
- [ ] `GET /entities` returns expected data (readonly and admin credentials).
- [ ] `POST /entities/{id}/disable` and `/enable` succeed with admin credential.
- [ ] Application Insights shows fresh telemetry (< 5 minutes old).
- [ ] No error spikes in the past 30 minutes (Application Insights failures panel).
- [ ] Rotation log updated.

## Rollback

Key Vault retains previous versions of every secret (soft-delete + versioning). To roll back:

### Step 1: List previous versions of the affected secret

```bash
az keyvault secret list-versions \
  --vault-name kv-cmsevents-prod \
  --name "Users--CmsWebhookUser--PasswordHash" \
  --query "[].{Version:id, Created:attributes.created, Enabled:attributes.enabled}" \
  --output table
```

### Step 2: Retrieve the previous value

```bash
az keyvault secret show \
  --vault-name kv-cmsevents-prod \
  --name "Users--CmsWebhookUser--PasswordHash" \
  --version "<previous-version-id>" \
  --query "value" \
  --output tsv
```

### Step 3: Restore as current version

```bash
az keyvault secret set \
  --vault-name kv-cmsevents-prod \
  --name "Users--CmsWebhookUser--PasswordHash" \
  --value "$PREVIOUS_HASH"
```

### Step 4: Restart the service

```bash
az webapp restart --name cmsevents-prod --resource-group rg-cmsevents
```

### Step 5: Notify

Notify all parties who received the new password that the rotation was rolled back. Investigate the failure before retrying.

---

## Rotation log

Maintain a rotation log in a secure location (not committed to the repo). Suggested format:

| Date | Secrets rotated | Rotator | Issues | Sign-off |
|------|----------------|---------|--------|----------|
| 2026-08-06 | All | Diego Lastorta | None | Diego Lastorta |
| 2026-11-06 | All | ... | ... | ... |

## Related documents

- ADR-011 — user model and BCrypt work factor.
- ADR-012 — secret management design.
- ADR-014 — observability (telemetry connection string is one of the rotated secrets).

## Notes

- The `tools/BcryptHash` utility is a small console app included in the solution for exactly this purpose. It has no dependencies beyond `BCrypt.Net-Next` and does not connect to any external system.
- Rotating a Users password does NOT require a schema migration — the seed logic updates the existing row's `PasswordHash` on startup.
- Rotating a SQL connection string does NOT require a schema migration — only credentials change.
- Application Insights retains historical telemetry across connection string rotations; the new string ingests new telemetry into the same resource.
