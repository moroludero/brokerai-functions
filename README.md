# BrokerAi

AI-powered WhatsApp assistant for real estate brokers in Cancún / Riviera Maya. Qualifies leads, recommends properties, alerts brokers on hot leads, and lets brokers manage listings and Facebook ads entirely over WhatsApp.

Runs on Azure Functions (Consumption, .NET 8 isolated worker) + Azure SQL Database (free tier) + the official Anthropic C# SDK (Claude Haiku 4.5). Migrated from an earlier n8n + Supabase + Railway prototype — see `implementation/` for the original design docs, schema, and Claude prompts that were ported into this codebase.

## Solution structure

```
BrokerAi.sln
├── src/BrokerAi.Core/         # business logic — services, EF Core model, domain types
├── src/BrokerAi.Functions/    # Azure Functions: webhook, queue processor, digest timers
└── tests/BrokerAi.Core.Tests/ # xUnit + FluentAssertions (108 tests)
```

## Local development

**Prerequisites:** .NET 8 SDK, [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) v4, [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) (storage emulator), SQL Server LocalDB or a real Azure SQL connection string.

```powershell
# Restore, build, test
dotnet restore
dotnet build
dotnet test

# Apply the EF Core migration to your local DB
dotnet ef database update --project src\BrokerAi.Core --startup-project src\BrokerAi.Functions

# Start Azurite (separate terminal) then the Functions host
azurite
func start --script-root src\BrokerAi.Functions\bin\Debug\net8.0
```

Fill in `src/BrokerAi.Functions/local.settings.json` (gitignored) with your Meta, Anthropic, and Facebook credentials — see `implementation/env.template` for where to obtain each one (Meta app, Anthropic console, Facebook Business Manager).

### Testing the webhook against real WhatsApp traffic

Use [Microsoft dev tunnels](https://learn.microsoft.com/azure/developer/dev-tunnels/) to expose your local Functions host:

```powershell
devtunnel host -p 7071 --allow-anonymous
```

Point your Meta App's webhook at `https://<tunnel-url>/api/whatsapp`, subscribe to the `messages` field, and use a WhatsApp test number.

## Deployment

See `deploy/provision.ps1` for one-time Azure resource provisioning (Function App, Azure SQL free tier, Key Vault, Application Insights). After provisioning:

```powershell
dotnet ef database update --project src\BrokerAi.Core --startup-project src\BrokerAi.Functions
func azure functionapp publish brokerai-func
```

## Architecture notes

- **200-immediately webhook pattern**: the HTTP-triggered webhook parses the Meta payload and enqueues real messages to an Azure Storage Queue, returning 200 instantly — avoiding Meta's ~10s timeout/retry-duplicate behavior. `ProcessMessageFunction` (queue-triggered) does the actual work.
- **Idempotency**: `ProcessedMessage` table dedupes by WhatsApp `message_id` (Meta redelivers on timeout; queue retries can also re-run).
- **Canonical extraction schema**: `LeadExtraction` (`src/BrokerAi.Core/Domain/LeadExtraction.cs`) is the single JSON shape every classification/qualification Claude call returns — replacing three inconsistent schemas from the old n8n prompts.
- **Prompt caching**: Claude system prompts are embedded resources sent with `CacheControlEphemeral`; per-request data always goes in the user block so the cached prefix stays byte-identical.
