# Enterprise Infrastructure Health Monitor Dashboard
## Technical Architecture Plan
Review edition 1.5 • 14 October 2026

Build a polished, demonstrable dashboard through eleven implementation steps, advancing when each completion gate passes. No fixed calendar deadline or working-hour limit applies. Approve a modular monolith: one ASP.NET Core Web API, two independently owned domain modules, one Supabase PostgreSQL database, and a separate Vite React application. The frontend emphasizes responsive interaction, useful telemetry, and dependable table behavior; the backend demonstrates clean boundaries, explicit persistence ownership, and testable business rules.

The intended implementer is an experienced C# and industrial telemetry developer. The project should show modern React and TypeScript capability without expanding the MVP into a full enterprise platform. This plan specifies architectural boundaries, conceptual models, public contracts, bootstrap commands, verification gates, and publication readiness. It deliberately leaves controllers, repository implementations, and React components to implementation.

### Edition 1 5 amendments and implementation status

**Approved Step 2.A configuration amendment.** The owner approved minimal hybrid reconciliation: one canonical runtime setting, separate Development maintenance credentials, and the existing isolated CI selection. This supersedes the former Device/Telemetry connection-key examples in sections 6 and 10. D03's shared source, scoped contexts, module boundaries, one physical database, private schemas, separate histories, runtime privilege restrictions, and reviewed hosted SQL releases remain authoritative. Historical startup evidence below describes the earlier revision.

Option A AI-generated structured queries is approved for the MVP and included in the eleven-step implementation roadmap. A user describes the desired device view in plain English; the model proposes a typed filter and sort object. The server validates it and the user applies it through the existing device-grid GET endpoint. EF Core and module query adapters remain responsible for database access. No free-form model SQL, new schema, third domain module, fifth production project, agent framework, or autonomous tool execution is introduced.

This edition preserves sections 1–24, original models, bootstrap commands, persistence decisions, security controls, reference register, and the approved D11 AI feature introduced in edition 1.4. Edition 1.5 replaces the time-bound roadmap with eleven implementation steps and completion gates. Fixed day counts and working-hour budgets no longer govern delivery; scope and verification requirements remain. The original dependency baselines and their 12 October verification record are retained rather than represented as newly executed checks.

**Step 2 implementation evidence.** Step 2 replaces the earlier Day 2 progress label; it is not a statement that every Step 2 completion gate has passed. The supplied 14 October startup code registers DeviceDbContext and TelemetryDbContext with separate configuration lookups and UseNpgsql calls, selects HealthMonitor.Infrastructure as the migrations assembly, registers controllers and native OpenAPI, and maps controllers after HTTPS redirection and authorization. Its context namespaces are Infrastructure.Data.DeviceContext and Infrastructure.Data.TelemetryContext. This is evidence of startup wiring only; it does not prove a successful database connection, schema deployment, seed, endpoint behavior, or tests.

**Planned alignment after Step 2.** D03 still requires one DI-owned shared NpgsqlDataSource, separate per-schema migration histories, and explicit module ownership. The historical startup code did not demonstrate those properties; the approved as-built folder exception below preserves existing migrations and namespaces without changing ownership. All AI components, contracts, policies, provider setup, UI, and AI tests below are proposed implementation work. No AI endpoint or provider integration is claimed complete.

**Approved Step 2.D foundation decisions.** Domain construction enforces the four canonical Device types. Capacity is 1,000 active registrations; retired history remains stored and does not consume that capacity. The unused Device write repository is deferred as described in section 7. The as-built layout and test-reference exceptions below are accepted without moving persistence history. These decisions do not close Step 2: independent implementation review and fresh CI verification of the corrections remain required.

**Documentation and testing amendment.** New and changed code must meet the professional documentation and incremental testing requirements in section 19. Complete Step 2 before the retrospective documentation review of older code. Focused tests accompany each step; Step 9 consolidates comprehensive CI and regression evidence.

### Approval recommendation
Retain the MVP and decisions D01–D10 and incorporate approved Option A as decision D11. Treat service extraction, full identity management, streaming, device provisioning, and advanced analytics as future work. Public HTTP access performs no application database writes; the proposed AI translation POST is a read-only operation that can incur model-provider usage. A small server-owned producer keeps the hosted synthetic fleet active; local simulation remains available for deterministic demonstrations. The simulator and UI clearly identify all data as synthetic.

### Delivery boundary
The MVP contains a Metrics Summary, a TanStack Table data grid with server pagination and global filtering and sorting, and a Device Status Drawer with recent telemetry and incident history. It includes a controlled simulator, an internal periodic producer with bounded retention, cloud persistence, API documentation, focused tests, CI, screenshots, and a short demonstration video.

### Review navigation
| Section | Review focus |
| --- | --- |
| 1–2 | Decisions, topology, project boundaries |
| 3–6 | Windows bootstrap, frontend integration, secrets |
| 7–10 | Domain models, schema ownership, consistency |
| 11–14 | Health semantics, REST contracts, simulator and security |
| 15–17 | React interaction, server state, deployment |
| 18–20 | Roadmap, quality gates, risks, approval |
| 21–22 | Official references and verification notes |
| 23 | Provisioning and bootstrap completion checklist |
| 24 | Configuration files and reproducible validation commands |

**Feasibility judgment.** Complete eleven implementation steps at the pace needed to satisfy their exit gates. A step can span multiple sessions; numbering expresses dependency and progress, not elapsed days. Resolve tooling and cloud access in Step 1, stabilize functional scope after Step 8, and complete verification before release in Step 11. Enterprise-grade here means understandable code, secure configuration, deterministic behavior, and meaningful verification; it does not imply high availability or industrial monitoring certification.

# 1 Architecture decisions
| Decision | Choice and reason |
| --- | --- |
| D01 | Modular monolith for the MVP. One process avoids deployment and messaging work while preserving domain seams. |
| D02 | Four backend projects: Domain, Application, Infrastructure, and API. Device and Telemetry are separate folders and namespaces within each layer. Project references enforce layer boundaries; conventions, review, and architecture tests protect module boundaries. |
| D03 | Two scoped DbContexts and two private schemas. The API composition root supplies one shared singleton NpgsqlDataSource for the identical runtime connection configuration. |
| D04 | Immutable UUID device identities cross boundaries as scalar values. No cross-schema foreign keys or EF navigation properties. Soft retirement preserves historical references. |
| D05 | A stable API facade owns /api/v1 contracts. Service extraction keeps this facade or an equivalent gateway at the same public address. |
| D06 | EF Core repositories for aggregates; purpose-built query ports for projections. No generic repository or IQueryable exposure. |
| D07 | Server pagination, filtering, and sorting operate over the same dataset. TanStack Table provides headless table behavior; the project supplies accessible grid markup and styling. |
| D08 | TanStack Query v5 owns server cache; Zustand v5 owns shared UI preferences. Keep request data out of Zustand. |
| D09 | Five-second polling with visible freshness for MVP. SignalR and event transport are stretch work. |
| D10 | Public HTTP operations perform no application database writes; an internal producer maintains synthetic telemetry with bounded retention. The AI translation POST is read-only with provider usage limits. No browser-held database credential or privileged Supabase key. |
| D11 | Approved Option A translates natural language into an allowlisted structured device-grid query. Server validation and existing query ports control execution; model-generated SQL is never executed. Proposed for Steps 4–8, not implemented Step 2. |

### Runtime topology
```text
Browser  React SPA  Vite build
    | HTTPS JSON /api/v1
API facade  ASP.NET Core 10  controllers and composition
    |                           |
Device application          Telemetry application
    | query and repository      | query and repository
Device infrastructure       Telemetry infrastructure
    | DeviceDbContext           | TelemetryDbContext
Supabase PostgreSQL
    device schema               telemetry schema
```

### What microservices readiness guarantees
The frontend uses only public DTOs and routes. Maintaining their semantics, status codes, query behavior, authentication requirements, and origin allows services to move behind the facade without changing React. Separate schema ownership and scalar identifiers allow tables to move to separate databases without redesigning the logical table structures. Repository abstractions alone do not provide these guarantees.

Zero refactoring is conditional, not absolute. Extraction requires backend adapters, independent deployment, data movement, observability, authentication between services, and usually durable events or a replicated catalog. Cross-module read composition and failure behavior must be preserved. Physical schema placement, privileges, migration execution, and history locations may change. New product requirements or incompatible API changes can require frontend and logical schema changes; this design cannot guarantee otherwise.

### Proposed AI orchestration path
```text
Plain English input -> API AI translation controller
  -> Application AiQueries use case and validator
  -> IStructuredQueryGenerator -> Infrastructure model adapter
  <- untrusted structured proposal -> validated query DTO
User reviews and applies -> GET /api/v1/devices with normal parameters
  -> existing API fleet composition -> Device and Telemetry query ports
  -> module owned projections -> existing filtering sorting and paging
```
Only the existing module query adapters access PostgreSQL. AI translates intent; it does not change health policy, incident lifecycle, retention, or consistency guarantees.

# 2 Repository and project boundaries
```text
HealthMonitor/
  HealthMonitor.slnx                   actual SLN format
  global.json                        pinned .NET SDK
  .config/dotnet-tools.json           local EF tool
  .github/workflows/ci.yml
  docs/architecture/                  plan and decision notes
  scripts/                           bootstrap and deployment notes
  src/backend/
    HealthMonitor.Api/
      HealthMonitor.Api.csproj
      Controllers/, Composition/, Errors/, Background/
      Contracts/V1/                 Public REST request and response DTOs
    HealthMonitor.Domain/
      HealthMonitor.Domain.csproj
      Device/                       Entities and device rules
      Telemetry/                    Entities and health policy
    HealthMonitor.Application/
      HealthMonitor.Application.csproj
      Device/                       UseCases, Ports, ReadModels
      Telemetry/                    UseCases, Ports, ReadModels
      IntegrationContracts/         Immutable module communication records
    HealthMonitor.Infrastructure/
      HealthMonitor.Infrastructure.csproj
      Device/Persistence/           DeviceDbContext, mappings, Migrations
      Device/Repositories/
      Telemetry/Persistence/        TelemetryDbContext, mappings, Migrations
      Telemetry/Repositories/
  src/frontend/health-monitor-web/
    health-monitor-web.esproj
    package.json, package-lock.json, vite.config.ts
    src/app/, src/components/, src/features/
    src/api/, src/store/, src/styles/
  tests/HealthMonitor.UnitTests/
    HealthMonitor.UnitTests.csproj
  tests/HealthMonitor.IntegrationTests/
    HealthMonitor.IntegrationTests.csproj
```

HealthMonitor.Api.csproj is the C# ASP.NET Core executable project. Domain, Application, and Infrastructure are its three C# class-library projects. The two test .csproj files are additional C# test projects, outside the four backend production projects. HealthMonitor.slnx includes these six .csproj files and the frontend health-monitor-web.esproj: seven solution projects in total. The frontend's package.json defines its npm dependencies and scripts, while the .esproj provides Visual Studio solution integration.

### Project reference rules
Domain references no other solution project, EF Core package, or web framework. Application references Domain only. Infrastructure references Application and Domain. API references Application and Infrastructure as the dependency injection composition root; it maps Application read models to its public V1 DTOs. No inner layer references API. Unit tests reference Domain and Application; the approved as-built exception also permits their API reference for database-free composition, configuration, and fixed-catalog tests. Integration tests reference API. This test-only exception does not relax production dependency rules.

Device and Telemetry retain separate folders and namespaces inside the shared layer assemblies. They must not use each other's domain entities or persistence implementations. Telemetry Application defines an IDeviceCatalog port returning IntegrationContracts records such as DeviceRegistration. An API-owned adapter implements that port by calling Device Application's IDeviceCatalogQueries. A future HTTP adapter can replace it. Sharing an assembly permits accidental cross-module references, so review and focused architecture tests must enforce the rule; the compiler enforces layer dependencies, not these module boundaries.

Public V1 DTOs live in API/Contracts/V1; module communication records live in Application/IntegrationContracts. There is no separate Contracts project. Neither contract category contains EF entities. Frontend types follow the OpenAPI JSON contract, not CLR assembly references. Checked-in TypeScript contracts are sufficient for MVP; automated generation can follow after the API stabilizes. Future extraction separates the module files and introduces transport adapters without changing public routes and DTOs, provided their semantics remain compatible.

For persistence, the section 2 tree expresses ownership rather than a required relocation. The approved as-built Infrastructure folders are DeviceContext and TelemetryContext, using Infrastructure.Data.DeviceContext and Infrastructure.Data.TelemetryContext namespaces. Initial Device migrations and its snapshot remain in Infrastructure/Migrations; subsequent Device migrations are in DeviceContext/Migrations. Telemetry migrations and snapshot are in TelemetryContext/Migrations. Preserve context attribution and independent histories; do not move or regenerate existing migrations merely to match the illustrative tree.

### Persistence and runtime rules
Repositories return aggregates, value records, or explicit paged results and accept cancellation tokens. Query ports support AsNoTracking projections and server aggregation without generic CRUD abstractions. SaveChanges is coordinated once per Telemetry use case. Scoped DbContexts are never shared across requests or simultaneous tasks.

The frontend .esproj is a sibling solution project located inside the independent frontend root. There is no API-to-esproj ProjectReference, SPA proxy package, or frontend build embedded in API publish. Visual Studio launches the two projects separately; CLI and CI build the two artifacts explicitly.

### Proposed AI files and dependency boundaries
Add API/Controllers/AiDeviceQueriesController.cs and API/Contracts/V1/AiQueries for public request and response DTOs; API/Composition wires the services. Add Application/AiQueries containing TranslateDeviceQuery, DeviceGridQuery, QueryTranslationResult, StructuredQueryValidator, and Ports/IStructuredQueryGenerator. Add Infrastructure/AiQueries containing one provider adapter and its provider transport DTOs. Add frontend/src/features/ai-query for the input, proposed-filter preview, Apply, and Clear actions. These are additions within the existing four production projects.

Application defines the provider port using immutable records and CancellationToken, without provider SDK, HTTP, EF, SQL, or API DTO dependencies. Infrastructure implements it with a bounded HttpClient integration or one reviewed provider SDK. The API maps public DTOs and supplies a small, nonsecret query vocabulary from the seed configuration through an application contract. AiQueries never references module entities, DbContexts, repositories, or write use cases; applying the validated query uses the existing facade read composition. Domain contains no AI types. No cross-schema query shortcut is permitted.

Keep one provider and one model configuration for MVP. Do not introduce Semantic Kernel, multi-agent orchestration, vector search, conversation persistence, or a separate AI microservice. Provider/model and transport package versions must be selected and locked in Step 4; no specific provider is assumed implemented or approved by this plan.

# 3 Bootstrap prerequisites and version policy
### Windows prerequisites
Use PowerShell 7 or Windows PowerShell 5.1, Git, .NET SDK 10.0.400, Node.js 24 LTS with npm, and a PostgreSQL psql client for section 23 provisioning. Visual Studio 2026 18.x must support net10.0 and have ASP.NET and web development plus JavaScript and TypeScript tooling installed. Version 18.0 is the documented target-framework floor; prefer the matching SDK and current Visual Studio servicing release. Visual Studio 2022 is not a supported net10.0 IDE. [R01–R03]

Sections 3–5 are scaffold commands; section 6 configures local secrets and startup. Sections 9–10 require implemented models and contexts; section 23 supplies provisioning and completion steps. The scaffold is not a functioning dashboard. Run each block from its stated directory. NuGet/npm access is required; an unresolved JavaScript SDK during sln add may indicate blocked NuGet access.

| Dependency | Baseline |
| --- | --- |
| .NET SDK | 10.0.400; pin the installed serviced SDK in global.json |
| Microsoft EF Core and EF Design | 10.0.11, aligned; dotnet-ef 10.0.11 |
| ASP.NET Core OpenAPI and MVC Testing | 10.0.11 |
| Npgsql EF provider | 10.0.3; requires EF Core and Relational ≥10.0.4 and <11.0.0 |
| Microsoft JavaScript SDK | 1.0.3398286, pinned in the .esproj |
| React and TypeScript | create-vite 9.2.1 react-ts scaffold; lock resolved versions |
| Tailwind and Vite plugin | Major 4; CSS import and Vite plugin integration |
| TanStack Table, Query, Zustand | Table 8, Query 5, Zustand 5 |

Npgsql and Microsoft package patch numbers need not match. Their supported major and dependency range must match. The chosen provider accepts EF Core 10.0.11. Do not install EF Core 9 or 11, Microsoft.EntityFrameworkCore.SqlServer, or Supabase's JavaScript database client for this design. No direct Npgsql package pin is needed: the provider brings the driver. Microsoft.EntityFrameworkCore.Tools is unnecessary when using the local CLI tool. [R04–R06]

### Create the root and pin tooling
```powershell
dotnet --version
node --version
npm --version
git --version
New-Item -ItemType Directory -Path HealthMonitor
Set-Location HealthMonitor
git init
dotnet new globaljson --sdk-version 10.0.400 --roll-forward latestPatch
dotnet new sln --name HealthMonitor --format sln
dotnet new gitignore
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.11
New-Item -ItemType Directory -Path src/backend,src/frontend,tests,docs,scripts
```

The explicit --format sln is required because .NET 10 defaults to .slnx. The local tool manifest makes tool restoration repeatable without a global installation. Commit global.json and tool manifest; update servicing versions deliberately after a passing restore, build, and test cycle. [R01, R06]

# 4 Bootstrap backend projects
### Scaffold and connect the layers
```powershell
$parts = @('Domain','Application','Infrastructure')
foreach ($part in $parts) {
  dotnet new classlib -n "HealthMonitor.$part" `
    -o "src/backend/HealthMonitor.$part" -f net10.0 --no-restore
}
dotnet new webapi -n HealthMonitor.Api `
  -o src/backend/HealthMonitor.Api -f net10.0 `
  --use-controllers --no-restore
$api = 'src/backend/HealthMonitor.Api/HealthMonitor.Api.csproj'
$domain = 'src/backend/HealthMonitor.Domain/HealthMonitor.Domain.csproj'
$application = 'src/backend/HealthMonitor.Application/HealthMonitor.Application.csproj'
$infrastructure = 'src/backend/HealthMonitor.Infrastructure/HealthMonitor.Infrastructure.csproj'
dotnet add $application reference $domain
dotnet add $infrastructure reference $domain $application
dotnet add $api reference $application $infrastructure
dotnet add $infrastructure package Npgsql.EntityFrameworkCore.PostgreSQL `
  --version 10.0.3 --no-restore
dotnet add $infrastructure package Microsoft.EntityFrameworkCore `
  --version 10.0.11 --no-restore
dotnet add $api package Microsoft.EntityFrameworkCore.Design `
  --version 10.0.11 --no-restore
dotnet add $api package Microsoft.AspNetCore.OpenApi `
  --version 10.0.11 --no-restore
Get-ChildItem src/backend -Recurse -Filter *.csproj | ForEach-Object {
  dotnet sln HealthMonitor.slnx add $_.FullName
}
```

### Test projects and restore
```powershell
dotnet new xunit -n HealthMonitor.UnitTests `
  -o tests/HealthMonitor.UnitTests -f net10.0 --no-restore
dotnet new xunit -n HealthMonitor.IntegrationTests `
  -o tests/HealthMonitor.IntegrationTests -f net10.0 --no-restore
$unit = 'tests/HealthMonitor.UnitTests/HealthMonitor.UnitTests.csproj'
$integration = 'tests/HealthMonitor.IntegrationTests/HealthMonitor.IntegrationTests.csproj'
dotnet add $unit reference $domain $application

dotnet add $integration reference $api
dotnet add $integration package Microsoft.AspNetCore.Mvc.Testing `
  --version 10.0.11 --no-restore
dotnet sln HealthMonitor.slnx add $unit $integration
dotnet restore $api
dotnet build $api --no-restore
```

In the API project file, keep the EF Design reference private to the project: add PrivateAssets="all" and the standard IncludeAssets value runtime; build; native; contentfiles; analyzers; buildtransitive. Remove the sample WeatherForecast controller and unused Class1 files during implementation. Enable nullable reference types and warnings as errors after the clean scaffold builds. Native command failures must stop an automated bootstrap; PowerShell's ErrorActionPreference alone does not stop every failed external command.

# 5 Bootstrap React and Visual Studio integration
### Create the independent frontend
```powershell
npm create vite@9.2.1 src/frontend/health-monitor-web -- --template react-ts
Set-Location src/frontend/health-monitor-web
npm install
npm install zustand@5 @tanstack/react-table@8 @tanstack/react-query@5
npm install uuid@13
npm install -D tailwindcss@4 @tailwindcss/vite@4
npm install -D vitest @testing-library/react @testing-library/jest-dom jsdom
npm install -D @playwright/test
```

If prompted, decline automatic server startup and use the commands above. The scaffolder is pinned to the version previously exercised; npm resolves compatible packages within the listed majors. Commit package.json and package-lock.json immediately and record resolved versions. Subsequent clones use npm ci. Repeating initial package discovery later is not guaranteed to produce the same lockfile. Node 24 satisfies Vite's minimum; reject engine warnings. [R07]

### Frontend configuration snippets
Merge the plugins and server settings below into the generated vite.config.ts; preserve the React plugin. Add import "./styles/app.css" in the generated entrypoint, then create src/styles/app.css containing @import "tailwindcss";. Tailwind v4 does not require the obsolete npx tailwindcss init -p sequence. [R08]
```typescript
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: { port: 5173, strictPort: true },
});
```

### Add the solution project
From the frontend directory, write this exact .esproj file. It integrates the Vite root with Visual Studio; package.json remains the frontend build authority. [R09, R10]
```powershell
@'
<Project Sdk="Microsoft.VisualStudio.JavaScript.Sdk/1.0.3398286">
  <PropertyGroup>
    <StartupCommand>npm run dev -- --port 5173 --strictPort</StartupCommand>
    <ShouldRunNpmInstall>false</ShouldRunNpmInstall>
    <ShouldRunBuildScript>false</ShouldRunBuildScript>
    <BuildCommand>npm run build</BuildCommand>
  </PropertyGroup>
</Project>
'@ | Set-Content health-monitor-web.esproj -Encoding utf8
npm run build
Set-Location ../../..
dotnet sln HealthMonitor.slnx add `
  src/frontend/health-monitor-web/health-monitor-web.esproj
dotnet sln HealthMonitor.slnx list
```

ShouldRunNpmInstall=false avoids an implicit npm install overwriting the explicit npm ci workflow. ShouldRunBuildScript=false keeps a backend build from implying frontend validation; npm run build is always a separate gate. Configure both API and frontend as startup projects in Visual Studio. Set the API profile ports to the values in section 6 and suppress its automatic browser launch. The CLI two-terminal workflow remains authoritative if JavaScript debugging integration varies by IDE servicing release.

# 6 Secrets local startup and bootstrap checks
### Configuration contract
Commit nonsecret defaults only: Cors:AllowedOrigins=["http://localhost:5173"], Simulation:HttpEndpointEnabled=false, Simulation:AllowLocalAnonymous=false, DemoProducer:Enabled=false, DemoProducer:IntervalSeconds=60, Monitoring:OfflineAfterSeconds=90, Monitoring:LatencyWarningMs=250, Monitoring:CpuWarningPercent=80. Omit real connection strings. ConnectionStrings:HealthMonitorRuntime is the sole runtime database setting. Both contexts receive the same DI-owned data source; separate logical keys are unnecessary for the required single runtime role. Legacy Device/Telemetry keys are unused and never fallbacks; leftover legacy entries do not invalidate an otherwise valid canonical setting.

### Store the runtime credential outside Git
Copy the host, username, database, and port from Supabase's Connect dialog. Download the project's CA certificate to a local path outside Git. Use a restricted runtime role, not the postgres owner role. The connection format is Npgsql key/value syntax, not a PostgreSQL URI. [R11–R14]
```powershell
$api = 'src/backend/HealthMonitor.Api/HealthMonitor.Api.csproj'
dotnet user-secrets init --project $api
$db = Read-Host 'Paste runtime Npgsql connection string'
dotnet user-secrets set 'ConnectionStrings:HealthMonitorRuntime' $db --project $api
# Do not duplicate runtime credentials under legacy Device/Telemetry keys.
Remove-Variable db
dotnet user-secrets set 'Simulation:HttpEndpointEnabled' 'true' --project $api
dotnet user-secrets set 'Simulation:AllowLocalAnonymous' 'true' --project $api
dotnet dev-certs https --trust
```

The Read-Host value is runtime input, so the password is not written as a literal in shell history; keep transcription and terminal recording disabled while configuring secrets. User-secrets is a development convenience and is not encrypted. A real secret must never go into any tracked appsettings file. The certificate trust command may show the normal Windows trust prompt.
```text
Host=<copied-host>;Port=5432;Database=postgres;
Username=<runtime-role-and-pooler-suffix>;Password=<secret>;
SSL Mode=VerifyFull;Root Certificate=<absolute-CA-path>;
Maximum Pool Size=10;Timeout=15;Command Timeout=15
```

Create one singleton NpgsqlDataSource in the API composition root and pass that same instance to both scoped DbContexts. Set Maximum Pool Size=10 on that source; identical strings alone are not the ownership rule. Budget ten connections per API process plus migration/admin connections. Different roles or replicas create additional pool budgets. The deployment key is ConnectionStrings__HealthMonitorRuntime. Validate syntax and explicit Host/Database/Username before host construction without opening a connection. Normalize omitted Maximum Pool Size to 10; reject any explicitly different maximum or a minimum above that budget. Reject transaction-pooler port 6543; outside Development require SSL Mode=VerifyFull. Parsing cannot prove certificate trust, target identity, or effective grants; verify those operationally before release. Never fall back to maintenance or integration credentials, and never expose raw connection strings or provider diagnostics. [R25]

### Two terminal startup and verification
```powershell
# Terminal A, repository root; after application implementation
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project src/backend/HealthMonitor.Api --no-launch-profile `
  -- --urls 'https://localhost:7043;http://localhost:5043'
# Terminal B, repository root
Set-Location src/frontend/health-monitor-web
'VITE_API_BASE_URL=https://localhost:7043' | Set-Content .env.local
npm ci
npm run dev -- --port 5173 --strictPort
```

Add .env*, node_modules/, dist/, .vs/, *.user, and local cert/secret paths to .gitignore; allow a placeholder .env.example. VITE_API_BASE_URL is public. Step 1 checks prerequisites, project loading, independent scaffold builds, and cloud TLS access. Migrations and seeded reads follow in Step 2; drawer, simulation, and browser CORS checks follow after frontend integration in Steps 7 and 8. The final solution contains four backend projects, two tests, and one esproj. [R15]

### Proposed AI configuration and secrets
Store AiQueries:ApiKey only in API user-secrets or hosting secrets. The browser receives neither the key nor a provider endpoint. Commit only the disabled defaults in section 24. Configure the provider, model, and HTTPS base URL server-side, validate them at startup when enabled, and allowlist the intended provider host. No prompt or model field can override that host, configuration, or credentials.

Do not send telemetry rows, asset names, addresses, database schema, connection strings, or repository code to the model. Send only the user prompt and fixed contract vocabulary: filter fields, enum tokens, supported site/type values, and sorting semantics. Label the prompt input for synthetic demo data only. Keep prompts and full provider outputs out of routine logs and persistent storage.

# 7 Device domain model
MonitoredDevice owns identity and inventory metadata, not measurements or computed online status. The catalog is seeded with 24 stable, fictional devices across three sites and several device types. No real asset names, industrial network addresses, or customer data belong in the demo.

### Conceptual C sharp model
Properties illustrate shape rather than full implementation. Production code supplies controlled constructors and domain methods instead of unrestricted setters. GUIDs are generated in the application, so PostgreSQL 18 UUID generation is not required.
```csharp
public sealed class MonitoredDevice
{
    public Guid Id { get; private set; }
    public string AssetTag { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public string DeviceType { get; private set; } = "";
    public string SiteCode { get; private set; } = "";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? RetiredAtUtc { get; private set; }
}
```

| Field | PostgreSQL and rule |
| --- | --- |
| Id | uuid primary key; immutable identity |
| AssetTag | varchar(64), required; uppercase canonical unique tag |
| DisplayName | varchar(120), required; trim, preserve display case |
| DeviceType | varchar(32), required; after trimming, exactly Server, Database, Gateway, or SCADA; domain construction rejects case variants and unsupported categories |
| SiteCode | varchar(32), required; uppercase canonical site code |
| CreatedAtUtc | timestamptz, required; server UTC clock |
| RetiredAtUtc | nullable timestamptz; no physical delete in MVP |

### Device interfaces and invariants
IMonitoredDeviceRepository (GetAsync, AddAsync, SaveChangesAsync) is explicitly deferred until a genuine Device write use case requires it. The approved private fixed-catalog provisioner remains the current maintenance path; do not add an unused repository merely to mirror a planned interface. IDeviceCatalogQueries returns immutable DeviceRegistration records and catalog lists; it does not return domain entities or IQueryable. Application query projections may return identifiers, names, tags, site, type, and retirement state for API composition.

DeviceRegistration carries DeviceId, DisplayName, AssetTag, SiteCode, DeviceType, and IsRetired. Telemetry ingestion checks this port before accepting a device identity. The API facade is the only place that combines catalog metadata with telemetry summary records.

Retired devices remain addressable in historical detail but are omitted from active fleet summaries by default. There is no hard delete or inventory editing UI in the MVP. A retirement change racing with a telemetry write may leave one accepted measurement; preserving history is intentional. Strict distributed referential integrity would require a stronger ingestion policy after extraction.

**Decision D04 consequence.** A UUID does not prove the device exists. Catalog validation is a business check, and catalog synchronization becomes an explicit operational responsibility when modules move into different processes.

Domain constructors validate application-created entities; they do not automatically constrain direct SQL writes or EF materialization. The current private provisioner independently validates its fixed seed vocabulary. No Device-type database constraint or schema change is introduced by this correction; direct writers remain responsible for the required invariants.

# 8 Telemetry and incident models
Telemetry owns measurements, health evaluation, and incident history. Ping means a synthetic health observation, not an actual ICMP or industrial protocol probe. All demo ingestion timestamps come from the server; a future real collector may require distinct observed and received timestamps and clock-skew validation.

### Conceptual C sharp models
```csharp
public enum PingOutcome { Success, Timeout }
public enum HealthState { Unknown, Healthy, Degraded, Offline }
public enum IncidentKind { Latency, Cpu, Connectivity }

public sealed class TelemetryPing
{
    public Guid Id { get; private set; }
    public Guid DeviceId { get; private set; }
    public Guid BatchId { get; private set; }
    public DateTimeOffset ObservedAtUtc { get; private set; }
    public PingOutcome Outcome { get; private set; }
    public int? LatencyMs { get; private set; }
    public decimal? CpuPercent { get; private set; }
}

public sealed class IncidentLog
{
    public Guid Id { get; private set; }
    public Guid DeviceId { get; private set; }
    public IncidentKind Kind { get; private set; }
    public DateTimeOffset OpenedAtUtc { get; private set; }
    public DateTimeOffset? ResolvedAtUtc { get; private set; }
    public Guid? OpenedByPingId { get; private set; }
    public Guid? ResolvedByPingId { get; private set; }
    public string Summary { get; private set; } = "";
}
```

### Minimal persistent read state
DeviceTelemetryState has DeviceId as its primary key, LastPingId, LastObservedAtUtc, LastSuccessfulAtUtc, LatestOutcome, LatestLatencyMs, and LatestCpuPercent. It belongs exclusively to Telemetry and is updated in the same transaction as each ping. It supports fleet reads without scanning the full measurement history. It contains no device name or site duplication.

SimulationBatch is an operational receipt with BatchId, RequestHash, Scenario, RequestedAtUtc, CommittedAtUtc, and AffectedDeviceCount. RequestHash uses the canonical scenario and sorted distinct device IDs. Each new action uses a caller-generated UUIDv7, with clock skew bounded to five minutes. Existing receipts replay for at least seven days; a missing receipt for an expired ID is rejected rather than treated as a new write. This bounds replay storage without duplicating very old requests. [R29]

Repositories are ITelemetryRepository, IIncidentRepository, and ISimulationBatchRepository. A Telemetry unit of work coordinates all three and DeviceTelemetryState through the same TelemetryDbContext. IDeviceHealthQueries returns read projections. Do not place a MonitoredDevice navigation property on any Telemetry entity.

# 9 PostgreSQL ownership and EF mapping
DeviceDbContext owns device.monitored_devices and device.__ef_migrations_history. TelemetryDbContext owns telemetry.telemetry_pings, telemetry.incident_logs, telemetry.device_telemetry_state, telemetry.simulation_batches, and telemetry.__ef_migrations_history. Schema names are explicit in mapping and history configuration; do not depend on search_path. Contexts have separate model snapshots and migrations directories. [R16]

| Table or mapping | Required constraints and indexes |
| --- | --- |
| monitored_devices | UUID PK; unique asset_tag; index (site_code, id); lengths in section 7 |
| telemetry_pings | UUID PK; unique (batch_id, device_id); index (device_id, observed_at_utc DESC, id DESC) |
| incident_logs | UUID PK; index (device_id, opened_at_utc DESC, id DESC); unique partial index (device_id, kind) WHERE resolved_at_utc IS NULL |
| device_telemetry_state | device_id PK; optional index on last_observed_at_utc if profiling justifies it |
| simulation_batches | batch_id PK; request_hash varchar(64); timestamps required at commit |
| UTC fields | timestamptz; write DateTimeOffset with zero offset; JSON ISO 8601 UTC |
| Enumerations | varchar conversion and check constraints; serialize as string enum values |
| Numeric fields | latency integer 0–60000; CPU numeric(5,2) 0–100; timeout requires null measurements |

Local foreign keys connect ping BatchId to SimulationBatch and state LastPingId to TelemetryPing. Incident ping references are nullable with ON DELETE SET NULL; an incident receives its opening reference at creation and keeps its own kind, timestamps, and summary after measurement pruning. There is no FK to the Device schema. Retention preserves the state's latest ping, keeps pings for 24 hours, keeps resolved incidents for seven days, and keeps open incidents until recovery. Receipts survive at least seven days and while referenced by any retained ping.

### Representative mapping configuration
```csharp
// Configuration snippets only; each context maps only its own entities.
// sharedDataSource is one DI-owned singleton, disposed with the host.
deviceOptions.UseNpgsql(sharedDataSource, pg =>
    pg.MigrationsHistoryTable("__ef_migrations_history", "device"));
telemetryOptions.UseNpgsql(sharedDataSource, pg =>
    pg.MigrationsHistoryTable("__ef_migrations_history", "telemetry"));
modelBuilder.Entity<TelemetryPing>()
    .ToTable("telemetry_pings", "telemetry");
modelBuilder.Entity<TelemetryPing>()
    .HasIndex(x => new { x.DeviceId, x.ObservedAtUtc, x.Id })
    .IsDescending(false, true, true);
modelBuilder.Entity<IncidentLog>()
    .HasIndex(x => new { x.DeviceId, x.Kind })
    .IsUnique().HasFilter("resolved_at_utc IS NULL");
```

Explicitly map all columns to the snake_case names used above, including resolved_at_utc; no naming-convention package is assumed. Configure required strings, decimal precision, enum conversions, primary keys, and check constraints using Fluent API. Application checks improve errors; database constraints preserve integrity under concurrency.

Catalog and telemetry queries are composed through ports. There are no cross-schema SQL views, joins, or shared entity mappings. A shared migration role may own both private schemas for the MVP; runtime grants give only the operations the API needs. Module ownership remains a design boundary rather than full database-user isolation until separate runtime roles are introduced.

# 10 Migrations and consistency
### Migration workflow
Implement and register both DbContexts in the API before these commands. Both contexts reside in HealthMonitor.Infrastructure, with separate mappings, context-attributed migration streams, and model snapshots in the approved as-built locations described in section 2. Explicit context selection and separate history tables keep their migration streams independent even though they share an assembly. Both design-time factories use the same explicit maintenance-selection policy, independently of runtime registration. Authorized integration mode has highest priority and selects only ConnectionStrings__HealthMonitorIntegrationTest with the existing strict target checks; GitHub Actions without exact test authorization fails. Outside authorized test mode, require explicit Development. Match WebApplication environment-variable precedence: DOTNET_ENVIRONMENT before ASPNETCORE_ENVIRONMENT; absence defaults to Production, and a blank/unknown higher-priority value does not authorize Development fallback. Factory arguments do not override this policy. See [ASP.NET Core environments](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/environments?view=aspnetcore-10.0).
```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$api = 'src/backend/HealthMonitor.Api/HealthMonitor.Api.csproj'
$infrastructure = 'src/backend/HealthMonitor.Infrastructure/HealthMonitor.Infrastructure.csproj'
dotnet ef migrations add InitialDevice --context DeviceDbContext `
  --project $infrastructure --startup-project $api `
  --output-dir DeviceContext/Migrations
dotnet ef migrations add InitialTelemetry --context TelemetryDbContext `
  --project $infrastructure --startup-project $api `
  --output-dir TelemetryContext/Migrations
```

Set ConnectionStrings__HealthMonitor temporarily using the DDL-capable maintenance role. In explicit Development this environment value overrides the API ConnectionStrings:HealthMonitor User Secret; an explicitly blank, malformed, or incomplete override fails without fallback. Only absence permits the maintenance User Secret. Non-Development selection must not load User Secrets, and runtime credentials are never a substitute. The private Development-only provisioning command uses the same maintenance key through normal application configuration; it remains separate from HTTP startup. Apply Device migrations before Telemetry. Review generated SQL and create schemas, roles, and default grants deliberately. Migration tooling is not a runtime API operation. [R06, R16]
```powershell
try {
    # The runtime setting is not overridden with maintenance credentials.
    $env:ConnectionStrings__HealthMonitor = Read-Host 'Paste migration role Npgsql connection string'
    dotnet ef database update --context DeviceDbContext `
      --project $infrastructure --startup-project $api
    if ($LASTEXITCODE -ne 0) { throw "Device migration failed (exit code $LASTEXITCODE)." }

    dotnet ef database update --context TelemetryDbContext `
      --project $infrastructure --startup-project $api
    if ($LASTEXITCODE -ne 0) { throw "Telemetry migration failed (exit code $LASTEXITCODE)." }
}
finally {
    try {
        [Environment]::SetEnvironmentVariable(
            'ConnectionStrings__HealthMonitor', $null, [EnvironmentVariableTarget]::Process)
    }
    catch {
        # Cleanup diagnostics must not replace an existing migration failure.
        Write-Warning 'Could not clear ConnectionStrings__HealthMonitor; remove it manually before continuing.' -WarningAction Continue
    }
}
```

Use a direct database connection for migrations where reachable. Test session pooler 5432 as the IPv4 fallback; do not use transaction pooler 6543 for this workflow. Hosted deployment applies reviewed EF migration SQL as a controlled release step. Do not call Database.Migrate automatically on each API startup or mix Supabase CLI migrations with EF ownership of the same tables. [R12]

### Write consistency and concurrent simulation
Focused advisory-lock acquisition/timeout/release and atomic ingestion transaction verification belong to historical Step 3.4. Receipt repository tests alone do not verify those guarantees; expanded ingestion concurrency remains Step 3.5. Deterministic mid-insert provisioning failure injection remains deferred: pre-insert conflict/capacity preservation tests do not prove rollback after partial insertion.

Authenticate and validate request shape, then compute the canonical hash. Look up an existing receipt before current catalog checks. A matching receipt returns the saved result even if a device has since retired; a changed hash returns 409. Only a new request validates UUIDv7 age and current catalog eligibility. Unknown or retired identities reject the whole new batch with 400 and no writes.

Begin a Telemetry transaction and acquire its fixed transaction-scoped simulator advisory lock; recheck the receipt under the lock before writing. Bound lock waiting to two seconds and return 503 with Retry-After on contention. Capture observation time after the lock, append pings, update state and incidents, persist the receipt, and commit atomically. This protects concurrent first observations and incident openings across API instances. A process-only SemaphoreSlim is insufficient. [R26]

For a lost response, retry the same POST and BatchId; no separate receipt endpoint is required. Matching replay returns replayed=true. A new UUIDv7 older than five minutes is 400 with code batch_id_expired; a timestamp more than five minutes in the future is 400 with code batch_id_future. Check version and timestamp before accepting a new ID. Do not enable automatic write retries with a fresh ID. High-throughput ingestion and an outbox remain future work.

### Hosted synthetic producer and retention
An API-owned BackgroundService runs one nonoverlapping cycle every 60 seconds, creates a fresh DI scope, and calls Telemetry Application directly. It partitions the 24 fixed devices into four groups and generates Healthy, HighLatency, HighCpu, and Timeout observations, rotating groups every four cycles. It creates UUIDv7 batch IDs, shares the same transaction/replay rules, and exposes no public write route. Use one API replica with the producer enabled; scale requires a separate leader or worker design. [R27]

At startup and hourly, clean up under the same advisory lock: remove eligible old pings except state references, resolved old incidents, then old unreferenced receipts. Delete at most 1,000 rows per transaction and ten batches per pass; report cleanup backlog. At 24 observations per minute, normal hourly inflow is 1,440 pings. Catch failures with backoff and cancellation; show stale status during outages. Local demos disable the producer so it cannot undo an operator scenario.

# 11 Health semantics and read composition
### Deterministic classification
Use an injected TimeProvider and one captured asOfUtc per request. Threshold comparisons are defined below; tests exercise the exact boundaries. A device's catalog retirement state is separate from its health.

| Condition in priority order | Returned health |
| --- | --- |
| No telemetry state or no observation | Unknown |
| asOfUtc minus LastObservedAtUtc > 90 seconds | Offline |
| Fresh latest outcome is Timeout | Offline |
| Fresh successful ping with latency >250 ms or CPU >80 percent | Degraded |
| Fresh successful ping at or below both thresholds | Healthy |

Latency and CPU are null for timeout observations. An unknown device has no numeric measurement; the UI renders an em dash, not zero. Offline due to elapsed time is computed at read time and is not a scheduled write.

### Incident lifecycle
Successful high-latency measurements open a Latency incident; high CPU opens a Cpu incident. A timeout opens Connectivity. The first violating ping opens the incident; repeated violations keep that incident open. A successful ping resolves Connectivity and resolves Latency or Cpu when that particular measurement returns within threshold. Timeout does not prove CPU or latency recovery, so those incidents remain open. Summary text is derived from the kind and measured threshold, never arbitrary HTML.

No-ping staleness produces Offline status but does not automatically create an IncidentLog in MVP. The drawer explicitly distinguishes stale observation from a recorded timeout incident. A background stale-device evaluator with durable incident writes is stretch work. This avoids presenting incomplete incident history as an exhaustive operational alerting system.

### Fleet and drawer query strategy
For the seeded fleet, the API loads at most 1,000 active catalog projections plus the matching telemetry state projections through module query ports. It composes lightweight immutable rows in memory, applies global search, site/type/status filters, allowed sort, and a deterministic DeviceId tie-breaker, then slices the requested page. Count and page use the same composed snapshot. This is server-side pagination; no measurements are paged into the browser for client-wide processing.

The device cap is explicitly 1,000 active registrations. Retired registrations remain stored for history but consume no active capacity. Under the existing provisioning table lock and transaction, count existing active rows plus missing active fixed seeds; still inspect all stored rows for identity/tag conflicts, and never reactivate a retired fixed seed. Prevent a seed or future inventory endpoint from exceeding active capacity; return a documented service-capacity error rather than silently truncate a larger catalog. This bounded composition trades scale for a straightforward service seam. It is appropriate for 24 demo devices, not for an unconstrained industrial fleet. If scale expands, introduce a purpose-built dashboard projection with events and documented lag, or a backend aggregation service; the UI contract can remain stable.

Fleet summary is global across active devices, independent of grid filters. It returns total, healthy, degraded, offline, unknown, and average latency of fresh successful devices only, plus sampleCount. Recent drawer telemetry and incidents are server-paged database queries. GET domain responses expose asOfUtc; catalog and telemetry are separate reads, so no cross-module serializable snapshot is promised.

### AI reuse of fleet semantics
The AI-supported view is limited to the active device grid and the exact section 12 search, site, type, health, and sort semantics. All predicates combine with AND. It cannot request historical analytics, aggregates, arbitrary threshold predicates, date ranges, multiple status values, OR expressions, retired-only views, or inventory writes. It must return an unsupported or clarification result when the request cannot be represented. For example, CPU above 90 percent cannot be replaced silently with Degraded, whose definition also includes latency and uses a different CPU threshold.

AI never calculates health or fleet metrics. The existing GET captures asOfUtc and composes active catalog and telemetry projections within the 1,000-device cap. Global fleet cards remain unfiltered. A translated Offline query includes both fresh timeouts and stale observations under the existing policy; its preview makes that meaning visible.

# 12 Versioned REST surface
| Method and route | Response and behavior |
| --- | --- |
| GET /api/v1/devices | 200 PagedResult<DeviceRowDto>; active catalog by default |
| GET /api/v1/devices/{deviceId} | 200 DeviceDetailDto; 404 unknown UUID |
| GET /api/v1/devices/{deviceId}/telemetry | 200 PagedResult<TelemetryPingDto>; newest first |
| GET /api/v1/devices/{deviceId}/incidents | 200 PagedResult<IncidentDto>; newest first |
| GET /api/v1/metrics/summary | 200 MetricsSummaryDto; active fleet, not current page |
| POST /api/v1/ai/device-queries | Proposed: 200 AiDeviceQueryResponseDto; translates only, does not query or write the database; absent when disabled |
| POST /api/v1/simulation/batches | 200 SimulationBatchResultDto after atomic commit |
| GET /health/live | Minimal process liveness; no database details |
| GET /health/ready | 200 or 503; database connectivity verified, sanitized |

V1 is part of the route. No API-versioning package is required for a single version; future breaking contracts go under /api/v2. Keep endpoint registration and OpenAPI descriptions explicit. Device and Telemetry routes can be forwarded behind a gateway later without exposing deployment topology to the SPA.

### Device grid query contract
```text
GET /api/v1/devices?page=1&pageSize=25&q=pump&site=DEN
    &type=Gateway&status=Degraded&sort=displayName&direction=asc
```

page is one-based ≥1; pageSize is 1–100, default 25. q is trimmed, maximum 100 characters, and performs a case-insensitive literal substring match on DisplayName or AssetTag. site and type use exact normalized allowed values; status is one of Unknown, Healthy, Degraded, Offline. Unknown query values are 400, not silently ignored.

Allow sort values displayName, assetTag, siteCode, status, lastObservedAtUtc, latencyMs, cpuPercent. Default is displayName asc. Sort numbers numerically, timestamps chronologically, names with OrdinalIgnoreCase, and status by severity Offline, Degraded, Unknown, Healthy in ascending order; descending reverses that order. Nulls sort last in both directions. Append DeviceId ascending as the stable tie-breaker. Arbitrary property names and raw SQL sort expressions are rejected.

An out-of-range page returns 200 with an empty items array and the true totalCount; the client navigates to a valid page. Filtering and totalCount apply before pagination. Offset pages may shift between polling requests; no fixed snapshot across pages is claimed.

### Drawer queries
Telemetry and incident endpoints support page and pageSize with the same bounds. Telemetry also supports fromUtc and toUtc as a half-open UTC range [fromUtc,toUtc), maximum seven days, default last 24 hours. Invalid ranges are 400. Telemetry order is ObservedAtUtc DESC, Id DESC. Incidents optionally accept state=open|resolved|all, default all, and order by OpenedAtUtc DESC, Id DESC. Unknown catalog IDs return 404; a known device without telemetry returns an empty list.

### Proposed AI endpoint behavior
POST /api/v1/ai/device-queries accepts AiDeviceQueryRequestDto and returns a translation decision synchronously. It performs no database query or write. A supported response provides canonical parameters for GET /api/v1/devices; the browser applies them only after review. The model is called once per explicit submission, never during grid polling, keystrokes, page changes, or sorts. POST is used because user intent belongs in a bounded JSON body rather than a URL.

When AiQueries:Enabled=false, do not register the route and return normal 404 for requests. When enabled, use the dedicated limits in section 14. Unknown input properties and unsupported contract versions are rejected. Document this route separately from simulation: enabling read-only AI never enables a simulation write route.

# 13 DTOs validation and simulation
### Wire contracts
These are JSON shapes, not full application code. Numbers use JSON numbers, UUIDs use strings, and dates use ISO 8601 UTC with Z. Optional measurements are explicit nulls. Enum tokens use the casing shown here. The API configures string enum serialization and disallows numeric enum input.
```text
PagedResult<T> = { items: T[], page: number, pageSize: number,
  totalCount: number, asOfUtc: string }
DeviceRowDto = { id: UUID, assetTag: string, displayName: string,
  deviceType: string, siteCode: string, health: HealthState,
  lastObservedAtUtc: string|null, latencyMs: number|null,
  cpuPercent: number|null }
DeviceDetailDto = DeviceRowDto + { createdAtUtc: string,
  retiredAtUtc: string|null, lastSuccessfulAtUtc: string|null,
  asOfUtc: string }
TelemetryPingDto = { id: UUID, deviceId: UUID,
  observedAtUtc: string, outcome: 'Success'|'Timeout',
  latencyMs: number|null, cpuPercent: number|null }
IncidentDto = { id: UUID, deviceId: UUID,
  kind: 'Latency'|'Cpu'|'Connectivity', openedAtUtc: string,
  resolvedAtUtc: string|null, summary: string }
MetricsSummaryDto = { total: number, healthy: number,
  degraded: number, offline: number, unknown: number,
  averageLatencyMs: number|null, sampleCount: number,
  asOfUtc: string }
SimulationBatchRequestDto = { batchId: UUID, deviceIds: UUID[],
  scenario: 'Healthy'|'HighLatency'|'HighCpu'|'Timeout' }
SimulationBatchResultDto = { batchId: UUID,
  affectedDeviceCount: number, committedAtUtc: string,
  replayed: boolean }
```

### Simulation scope
Each request contains 1–25 distinct IDs and a UUIDv7 BatchId. The browser creates that ID with a maintained UUIDv7 utility; its time component is used only for replay expiry, never as measurement time. The server captures observation time after the write lock and uses Healthy=40 ms/30 percent CPU, HighLatency=600 ms/30 percent, HighCpu=40 ms/95 percent, Timeout=null measurements. No host, network address, shell command, or client measurement timestamp is accepted.

Healthy can demonstrate recovery after any scenario. Simulation requests execute synchronously, are small and atomic, and return 200 only after commit; there is no background job to justify 202. Preserve the same BatchId on network retry and create a new BatchId for a new user action. The simulator is explicit about producing synthetic telemetry rather than probing actual infrastructure.

### Validation and errors
Use controller binding plus DataAnnotations for shape and ranges, and Application validation for distinct IDs, scenario rules, catalog existence, retirement, and request hash. Reject malformed UUIDs, unknown enum tokens, oversized arrays, invalid query bounds, and unknown sort names before database work. Limit the request body to 16 KB and apply a named simulator rate limiter, e.g. five requests per minute per operator, no queued writes. MVP returns 429 with Retry-After when limited.

### Proposed AI wire contracts
```text
AiDeviceQueryRequestDto = { prompt: string }
DeviceGridQueryDto = {
  q: string|null, site: string|null, type: string|null,
  status: 'Unknown'|'Healthy'|'Degraded'|'Offline'|null,
  sort: 'displayName'|'assetTag'|'siteCode'|'status'|
        'lastObservedAtUtc'|'latencyMs'|'cpuPercent',
  direction: 'asc'|'desc'
}
AiDeviceQueryResponseDto = {
  contractVersion: '1',
  outcome: 'Ready'|'NeedsClarification'|'Unsupported',
  query: DeviceGridQueryDto|null,
  message: string
}
```

The provider proposal uses the same outcome and query shape plus reasonCode, but not the public message or contractVersion. Allowed reasonCode values are ready, ambiguous, unsupported, and no_interpretation. Reject additional properties at every nesting level. All fields are present; absent optional filters use null. Ready requires a nonnull query and reasonCode=ready. NeedsClarification requires query=null and reasonCode=ambiguous or no_interpretation. Unsupported requires query=null and reasonCode=unsupported. The server emits contractVersion=1 and a fixed, plain-text message from the outcome/reason code; do not render provider narrative or HTML.

Trim prompt and require 1–500 characters within an 8 KB JSON body. Validate UTF-8 JSON shape and string types. Trim q, allow null or 1–100 characters, and apply the existing literal substring semantics. Canonicalize valid site/type tokens against the configured seed vocabulary and reject unknown values. Validate exact health, sort, direction, reasonCode, and outcome tokens; numeric enum inputs, duplicate JSON property names, nested expressions, SQL, identifiers, and operator objects are invalid. Require strict parsing in the adapter; deserialization that silently ignores unknown members is insufficient. [R33]

The model does not choose page or pageSize. Applying Ready sets page=1 and retains the current valid pageSize (default 25, maximum 100). All omitted filters become null, replacing the prior filter set. sort defaults to displayName and direction to asc in the prompt instructions; the returned object must still contain both valid fields. GET independently revalidates every parameter, so a modified client cannot bypass validation.

Example supported request and normalized response:
```json
{ "prompt": "Show offline devices at DEN with the latest observations first" }
```
```json
{
  "contractVersion": "1",
  "outcome": "Ready",
  "query": {
    "q": null, "site": "DEN", "type": null,
    "status": "Offline",
    "sort": "lastObservedAtUtc", "direction": "desc"
  },
  "message": "Review the proposed filters before applying them."
}
```
After Apply, the browser calls GET /api/v1/devices?page=1&pageSize=25&site=DEN&status=Offline&sort=lastObservedAtUtc&direction=desc. The effective pageSize follows the current grid setting. Empty results are legitimate, never a reason to loosen the query silently.

An ambiguous request such as “show troubled devices” returns 200 with outcome=NeedsClarification, query=null, and a fixed message asking the user to name Healthy, Degraded, Offline, or Unknown and any desired site/type. An unsupported request such as “delete all offline devices” returns 200 with outcome=Unsupported and query=null. These are successful translation decisions, not transport failures. A syntactically valid model proposal remains untrusted; semantic correctness requires human review and evaluation cases, not confidence scores.

# 14 API errors and access controls
### Uniform error contract
Use ASP.NET Core ProblemDetails for failed requests, with ValidationProblemDetails for field errors. Add a stable code and traceId extension. Do not wrap successful DTOs in a second data envelope. Configure model validation, exception handling, authentication failures, rate-limit rejection, and readiness failures deliberately so their status and body behavior are tested. [R17]
```json
{
  "type": "about:blank",
  "title": "Validation failed",
  "status": 400,
  "instance": "/api/v1/simulation/batches",
  "code": "validation_failed",
  "traceId": "request-correlation-id",
  "errors": { "deviceIds": ["Select 1 to 25 distinct devices."] }
}
```

| Status | Contract meaning |
| --- | --- |
| 400 | Invalid input or unsupported query value; field errors when applicable |
| 401 / 403 | Missing or invalid operator credential / authenticated policy denial |
| 404 | Unknown device or route; disabled public simulator route is absent |
| 409 | Batch ID reused with a different canonical request |
| 413 / 429 | Request body too large / request limit reached |
| 503 | Database unavailable, catalog unavailable, or bounded simulator lock timeout |
| 500 | Unexpected server failure; sanitized generic message and traceId |

Use AddProblemDetails, a central exception handler, and logging with correlation identifiers. Log a handled unexpected exception once; .NET 10 may suppress automatic diagnostics for exceptions handled by IExceptionHandler, so explicitly retain needed observability. SQL, connection strings, secret headers, and stack traces never appear in public responses or production logs. [R17]

### MVP security modes
Production public-demo mode registers anonymous GET routes and leaves simulator endpoints unregistered with Simulation:HttpEndpointEnabled=false. DemoProducer:Enabled=true independently enables internal synthetic generation. A development-only policy permits local simulator calls only when Development, HttpEndpointEnabled, AllowLocalAnonymous, and loopback binding all apply. Hiding the button or checking CORS does not prevent writes.

If remote operator writes are needed, register the simulator only on a private operator environment or require a backend-validated X-Demo-Operator-Key policy over HTTPS. Supply Simulation:OperatorKey through a secret store, compare securely, restrict trusted ingress where possible, and invoke it from an operator terminal; the SPA never embeds or persists the key. Full OIDC/JWT login, roles, and user administration remain stretch work. Public data is intentionally fictional and public-readable; this is not a policy for confidential enterprise telemetry.

CORS allows exact origins and needed methods and headers. For the public SPA, allow GET and required content headers, plus POST and Content-Type only for the explicitly enabled AI translation route; local development additionally permits the enabled simulator POST. Route and policy controls still prevent public simulation writes. No wildcard with credentials, no cookie authentication, and no AllowCredentials in the MVP. CORS is a browser rule, not authentication. Apply it before endpoint authorization. [R18]

### Proposed AI validation and security architecture
Before a provider call, check route enablement, body size, request validation, named rate policy, concurrency permits, and the atomic daily request budget. Build a fixed instruction and contract separately from the user text. Tell the model to classify unsupported or ambiguous intent and to emit only the strict structured proposal. Provider-supported schema enforcement is helpful but never substitutes for local validation. Treat prompts and provider responses as untrusted data; instructions to ignore rules, disclose secrets, execute SQL, fetch URLs, or invoke tools have no execution path.

The adapter parses at most 8 KB of output, rejects malformed JSON, duplicate members, extra fields, invalid unions, or code fences, and caps generation at 400 output tokens. Application validation then enforces the allowlists and query semantics. No database work occurs during translation, and the existing GET validator runs again on Apply. SQL-looking text in q is only literal search data; it cannot become an executable expression. Existing EF projections remain responsible for translated database queries. There is no raw SQL endpoint, SQL text response, dynamic property access, or provider-selected query method. [R32, R33]

Use a ten-second provider timeout, linked request cancellation, no automatic provider retries, and no repair loop. Return sanitized ProblemDetails: 400 ai_prompt_invalid for invalid input, 413 request_too_large, 429 ai_rate_limited with Retry-After, 502 ai_output_invalid for invalid provider output, 503 ai_provider_unavailable for provider failures or quota exhaustion, and 504 ai_provider_timeout. Distinguish local daily budget exhaustion as 429 ai_budget_exhausted. Cancellation on browser disconnect stops downstream work where supported; it does not guarantee cancellation of provider billing. Do not echo prompts, provider payloads, URLs, credentials, SQL, or stack traces in errors.

For the single-replica public synthetic demo, limit five translations per minute per client IP obtained only through trusted proxy configuration, ten per minute globally, two concurrent provider requests, no waiting queue, and 100 admitted calls per UTC day. Count each admitted provider attempt even when it fails; restore the daily count across restarts in a bounded host-local counter file outside the web root, with atomic access. This operational counter creates no application database table and contains no prompt data. If the counter cannot be read or persisted, disable translation until repaired rather than reset the budget silently. Multiple replicas require a shared limiter/counter and are outside MVP. Configure a provider-side spending cap as a second control; the call cap alone is not a currency guarantee. [R31]

Public anonymous AI translation is permitted only for fictional public data and only when explicitly enabled with these controls. AiQueries:Enabled and simulation flags are independent. Keep authentication and future confidential-data access behind the normal facade policy; AI never expands caller authorization. Production AI responses use Cache-Control: no-store; no persistent prompt history or shared response cache is introduced. Log traceId, duration, outcome, validation reason, and available token counts without prompt or output content. The Apply action is a usability review step; server-side validation remains the security boundary.

# 15 React layout and interaction map
### Component ownership
```text
AppRoot
  QueryClientProvider
  AppShell                       responsive header and content frame
    DashboardPage
      DashboardHeader            synthetic badge and last updated
      MetricsSummary             4 status cards plus unknown and latency
      AiQueryPanel               proposed plain English input and review
      DeviceToolbar              global search, site, type, status filters
      DeviceDataGrid             TanStack Table v8, semantic HTML table
        DeviceStatusBadge        text, icon, color
        PaginationControls       page size, counts, previous and next
      DeviceStatusDrawer         detail query keyed by selected UUID
        DeviceOverview
        RecentTelemetryList
        IncidentHistory
      SimulationControls         local or operator mode only
    ErrorBoundary
```

Use Tailwind v4 tokens for spacing, typography, borders, and status colors. Keep a restrained visual system with consistent cards and grid density. The status badge always includes text; color alone cannot carry meaning. Avoid building a full design system in the first week. Reuse a small set of Button, Input, Select, EmptyState, and LoadingState primitives.

At desktop width the grid is the primary surface and the drawer opens on the right. On mobile the drawer fills the viewport; the grid allows contained horizontal scrolling and hides lower-priority columns through controlled visibility. Keep device identity, health, and the detail action visible. Do not allow the whole page to overflow horizontally.

### Interaction requirements
Row activation uses a visible keyboard-accessible detail button; a pointer row click is optional. Drawer focus moves to its heading or first control, Escape closes it, a focus trap prevents navigation behind it, and close returns focus to the originating button. Loading, empty, error, and stale states are distinct. Retain readable previous data while refreshing and show a subtle updating indicator; a failed poll displays the last successful timestamp rather than erasing usable data.

### State boundaries
Zustand stores selectedDeviceId, drawerOpen, pollingEnabled, and columnVisibility. Persist only nonsecret preferences such as visibility and polling choice; do not persist credentials, response entities, or drawer selection. Controlled grid pagination, sorting, and filters live together in the DashboardPage or one dedicated grid-state hook, with no duplicate Zustand mirror. Optionally synchronize filters to URL search parameters after MVP stabilization.

TanStack Query stores device pages, device details, fleet metrics, recent pings, and incidents. Query keys include every request-changing input. Local form state and hover/focus state stay in components. A shared api client supplies the base URL, AbortSignal, JSON handling, and ProblemDetails mapping. [R19–R21, R24]

### Proposed plain English interaction
Add AiQueryPanel above DeviceToolbar with a labelled input, 500-character limit, example prompt, Submit, a loading state, a normalized-filter preview, Apply, and Clear. Explain that it searches the active synthetic fleet using the supported filters. Show the existing thresholds and stale/timeout interpretation when previewing Degraded or Offline. Display no SQL and make no claim that the model has diagnosed infrastructure.

Submit runs a TanStack Query mutation for translation; keep input and unapplied preview in component state. Apply a Ready result atomically to the existing controlled grid state, reset pageIndex=0, and let the ordinary devices query fetch. The preview is replaced on a new submission. Abort or ignore superseded responses, clear any old preview when the prompt changes, and disable Apply until the latest response is valid. Clarification, unsupported intent, rate limiting, timeout, or provider failure leave existing filters and rows intact. Manual filters remain available when AI is unavailable.

Clear removes the AI preview and restores neutral grid filters and displayName ascending, preserving pageSize; manual changes after Apply become the current visible filter state. Use live status announcements and keyboard-accessible controls. AI state is not persisted in Zustand or browser storage. The existing drawer, table behavior, metrics scope, and freshness labels remain authoritative.

# 16 Grid operations and server cache
### Table v8 configuration contract
Use useReactTable with getCoreRowModel, controlled pagination/sorting/filter state, manualPagination=true, manualSorting=true, and manualFiltering=true. Pass totalCount as rowCount and use getRowId(row)=row.id. Do not install client pagination, sorting, or filtering row models for the main grid. Table is headless: it supplies state and utilities, while the application supplies header buttons, aria-sort, markup, styling, and focus behavior. [R19, R20]

Translate the zero-based table pageIndex to API page=pageIndex+1. Reset pageIndex explicitly to zero when filters, sort, or page size change; manual pagination disables the normal automatic reset. Map each sortable column ID to the API allowlist, disable unsupported columns, and cap sorting to one active column. On an empty out-of-range response, calculate the last valid page from totalCount and refetch once. A zero-row result remains pageIndex=0.

### Client operations on paged data
Client-side sorting or text filtering over items only affects the fetched page. It cannot provide global ordering, search all devices, or a correct filtered total. Do not label such operations as fleet-wide search. If a future drawer uses a local search, label it “Search loaded history” and keep it separate from the server-paged grid. Full browser processing is valid only when the entire bounded dataset has been explicitly loaded and that mode has a separate contract. [R20]

### Query lifecycle
```text
['devices', { page, pageSize, q, site, type, status, sort, direction }]
['device', deviceId]
['telemetry', deviceId, { page, pageSize, fromUtc, toUtc }]
['incidents', deviceId, { page, pageSize, state }]
['metrics', 'active-fleet']
```

Debounce global search by 300 ms and cancel superseded fetches using the signal provided by Query. Use Query v5 placeholderData: keepPreviousData for page transitions, not the removed v4 keepPreviousData boolean option. While placeholder data is displayed, mark it as updating and disable Next if the next-page bounds are not yet known. Use a five-second refetch interval for grid, fleet metrics, and the open drawer, with refetchIntervalInBackground=false. Visible timestamps distinguish client fetch time from server asOfUtc. [R21]

Define a short staleTime such as two seconds, stop intervals when polling is paused, and retry transient GET failures at most twice. Do not automatically retry POST with a newly generated BatchId. On successful simulation, invalidate the device page family, metrics, and affected drawer keys; avoid optimistic health changes because incidents and timestamps are server-owned.

Fleet cards describe the entire active fleet and carry a “Fleet totals” label. Filtered result count belongs to the grid toolbar. Drawer queries are enabled only for a selected UUID, reset their own paging when selection changes, and never render one device's previous data under another device's title. A background refresh must preserve drawer focus and selection.

### AI server state integration
Use a mutation key such as ['ai-device-query', 'v1'] for explicit translation submissions. Do not put prompts into URLs or GET cache keys. Do not automatically retry the AI POST because another provider call consumes budget. On Apply, the normal ['devices', { page, pageSize, q, site, type, status, sort, direction }] key remains the only cache identity for the device view. Grid polling refreshes that translated filter set through GET and never invokes the model again. Translation does not invalidate metrics, telemetry, incidents, or simulation state because it changes no stored data.

# 17 Supabase and deployment
### Database connection and exposure
Use Supabase as hosted PostgreSQL accessed only by the .NET API. A persistent backend uses the direct endpoint when its network can reach it; an IPv4-only environment uses the session pooler on port 5432. Copy each host and username from the dashboard rather than constructing a regional endpoint. Transaction pooling on 6543 has different semantics and prepared-statement limitations; it is not the MVP choice. [R12]

Enforce SSL on the database and use Npgsql SSL Mode=VerifyFull with Root Certificate pointing to the Supabase CA, or an appropriately configured trust store. Encryption alone with Require does not validate the hostname. Never use Trust Server Certificate=true to fix a connection failure. Test with the real deployment host and mounted CA before release. [R13, R14]

Keep both schemas outside the Supabase Data API exposed schemas. Disable the Data API if unused, or remove exposure and anonymous grants. Default privileges must be set by the actual object owner. Direct Npgsql connections use role privileges rather than browser JWT RLS automatically. Runtime grants include catalog SELECT, telemetry SELECT/INSERT/UPDATE, and DELETE only for retention tables. No ownership or DDL privileges belong to runtime. Section 23 supplies the provisioning sequence. [R22, R28]

### Independently deployable artifacts
Build the SPA to dist and deploy it to a static HTTPS host. Publish only HealthMonitor.Api to a host supporting .NET 10, or a pinned .NET 10 container. Preserve the facade address and /api/v1 route tree. The static host must provide index.html fallback for SPA routes, while API routes are sent to the API origin or a reverse proxy. No deployment provider or paid tier is mandatory for the architecture.

```powershell
# Repository root; frontend build configuration is public
$env:VITE_API_BASE_URL = 'https://<api-demo-origin>'
Push-Location src/frontend/health-monitor-web
npm ci
npm run build
Pop-Location
dotnet publish src/backend/HealthMonitor.Api/HealthMonitor.Api.csproj `
  -c Release -o artifacts/api
Remove-Item Env:VITE_API_BASE_URL
```

Set Production, Simulation__HttpEndpointEnabled=false, DemoProducer__Enabled=true, DemoProducer__IntervalSeconds=60, Cors__AllowedOrigins__0 to the SPA origin, and connection strings through hosting secrets. Mount the CA at the configured deployed path. Deploy one producer-enabled API replica on hosting that runs continuously; an idle-sleeping instance cannot guarantee fresh telemetry. VITE variables are public build configuration; a same-origin reverse proxy can avoid browser CORS. [R15]

Use minimal probes, structured request-duration logs, trusted forwarded headers, HTTPS ingress, and sanitized failures. Apply migrations once with release credentials, then seed the fixed fictional catalog through a private provisioning command. The internal producer supplies continuing measurements and hourly retention; startup does not recreate inventory. Verify recovery after an API restart, at least three successful producer cycles, and bounded cleanup before publishing the hosted demo.

### Proposed AI deployment gate
Enable AiQueries only after the selected provider/model, schema adapter, limits, cancellation, sanitized errors, trusted ingress IP behavior, persistent daily counter, and server secret injection pass verification. Set provider credentials through hosting secrets and configure a provider spending cap. Keep outbound HTTPS restricted to the configured provider. If disabled or unavailable, the normal dashboard remains usable; report the AI release gate as incomplete rather than describing manual filtering as completion of Option A.

# 18 Critical path and scope control
| Step | Implementation focus | Completion gate |
| --- | --- | --- |
| 1 | Tooling and environment | Prerequisites, SLN/esproj loading, independent scaffold builds, Supabase TLS/provisioning, and hosting access are verified. |
| 2 | Domain and persistence foundation | Models, mappings, two contexts and migrations, fixed catalog seed, database reads, and the shared pool pass verification. Existing startup wiring alone does not establish completion; AI remains proposed. |
| 3 | Telemetry lifecycle | Atomic ingestion, receipt-first replay, incident rules, periodic producer, bounded retention, and focused domain tests work. |
| 4 | API and AI query contracts | Fleet/drawer REST reads, DTOs, filtering/paging/sorting, errors, OpenAPI, and public-write policy work. AI contracts, validator, and fake-provider slice pass; one provider/model transport is selected and locked. |
| 5 | AI provider and security integration | Bounded provider adapter, strict parsing, cancellation/timeout, sanitized failures, rate/concurrency limits, and persistent daily usage budget pass focused checks. Translation performs no database access. |
| 6 | React dashboard foundation | React shell, Tailwind styling, API client, Query/Zustand boundaries, real fleet metrics, and basic grid work against the API. |
| 7 | Complete user journeys and AI interaction | Global search/sort/paging, drawer history, local simulation and recovery work. Plain English Submit, preview, Apply, and Clear use the existing query flow; a live provider translation is verified. |
| 8 | Interaction polish and functional scope freeze | Responsive/keyboard behavior, loading/error/stale states, cancellation, and AI unsupported/failure states work. Freeze functional scope before final verification. |
| 9 | Tests and CI | PostgreSQL/API, retention/replay, AI validation/boundary, frontend, and E2E checks pass; CI and dependency review are complete. Deterministic CI remains separate from live-model evaluation. |
| 10 | Deployment and hosted operations | Single-replica continuous hosting, controlled migrations/seed, TLS/CORS, producer restart/cleanup, AI secrets, public limits, restart budget persistence, and provider spending cap are verified. |
| 11 | Release and project evidence | Clean-clone rehearsal and all mandatory release gates pass. README/D01–D11, screenshots, video, and release tag are complete; implementation evidence and limitations are recorded accurately. |

All eleven steps retain their existing order and scope. Each completion gate also requires the applicable documentation and focused tests described in section 19, created alongside the work. Step 9 consolidates comprehensive CI/regression coverage rather than deferring testing until that step.

The critical path is tooling and cloud access → schemas → telemetry lifecycle → API contracts → AI integration and React foundation → complete user journeys → verification → deployment → release evidence. Step 5 and Step 6 can proceed independently after their prerequisites pass; integrate both in Step 7. The eleven-step roadmap is a progress sequence, not an eleven-day schedule or a promise of effort hours. Advance based on completion evidence and spend additional time within a step when necessary.

### MVP commitment
Ship the grid, fleet summary, drawer, deterministic local scenarios, incident recovery, separate contexts, stable routes, secure secrets, cloud persistence, and approved Option A AI query workflow. The hosted demo continuously generates synthetic data internally while its public API performs no application database writes. Include 24-hour telemetry retention, seven-day resolved incident history, bounded receipts, focused tests, and publication evidence. Stop stretch work until all these gates pass.

### Stretch choices
Trend charts, SignalR with reconnection, and URL-persisted filters remain optional work after the eleven-step MVP release. OIDC, multi-tenancy, editable provisioning, retention partitioning, large fleets, brokers, multiple producer replicas, and service extraction remain future projects. Removing the deadline does not automatically expand the approved MVP scope.

### Blockers and incomplete gates
If an API or AI prerequisite is incomplete, continue the current step until its gate passes; independent work can proceed where dependencies allow. Optional animations, column preferences, and remote operator access can still be deferred to reduce complexity, but quality and security gates cannot be waived to keep a calendar date. Preserve correct filtering, keyboard drawer behavior, replay integrity, and public-write policy.

If continuous hosting is unavailable in Step 10, maintain a reproducible local demo and video and record the hosted gate as incomplete while resolving the blocker. A paused or sleeping host may show truthful stale status but does not pass the continuous-demo gate. A local demonstration can be shared as progress; it does not establish a complete hosted release.

### Scope control for Option A
AI remains limited to one endpoint, one provider, one typed device-grid query, and one compact React panel. Existing domain, persistence, producer, security, drawer, and verification gates remain mandatory. Complete the contract/fake-provider slice in Step 4, the provider/security integration in Step 5, the live end-to-end translation in Step 7, and failure behavior before the Step 8 freeze.

If the provider connection or a mandatory AI control is incomplete, keep the AI gate open and resolve it before release. Do not switch to unrestricted SQL or skip security tests. A disabled feature or manual filtering fallback preserves dashboard usability but is not completion of the approved AI requirement. Step 11 release requires passing all mandatory gates; there is no calendar-based cutoff or silent reduction in requirements.
# 19 Acceptance tests CI and publication
### Required acceptance evidence
| Gate | Observable pass condition |
| --- | --- |
| Setup | Fresh Windows clone opens the actual .sln; all seven projects load: four backend, two tests, one frontend; API and frontend build independently with documented prerequisites. |
| Grid | Use pageSize=10 with the 24-device seed; search finds an item initially on another page; numeric sorting, nulls, tie-breakers, and counts are correct. |
| Metrics | Sum of four health counts equals total; filters do not change global totals; average uses only the declared fresh successful sample. |
| Drawer | Correct selected UUID; telemetry and incidents page correctly; no history shows an explicit empty state; keyboard focus returns on close. |
| Simulation | Scenarios and recovery work; replay after retirement returns the saved result; changed hash=409; expired missing-receipt ID causes no new write. |
| Failure | Invalid input=400; missing device=404; changed replay=409; unavailable DB=503; polling retains last good data with stale indication. |
| Security | No secret in files/history/logs or dist; TLS validation succeeds; unauthorized public POST cannot write; runtime role cannot execute DDL. |
| Release | HTTPS deployment smoke check, CI green, clean README rehearsal, screenshots/video and a tagged release are present. |
| Hosted lifecycle | At least three one-minute cycles maintain a mixed fleet; restart recovers generation; cleanup preserves latest state and incident history without FK failures. |

### Professional code documentation requirements
Apply this standard immediately to all new or materially changed code, including the remaining Step 2 work. Finish Step 2 and its completion gates before starting a retrospective documentation review of older files. Record older-code documentation gaps for that later review; do not claim they have already been remediated.

Document each source file or cohesive module with its purpose, ownership, architectural layer, responsibilities, key collaborators, and important constraints. Explain public types, interfaces, use cases, and nontrivial members using C# XML documentation or TypeScript TSDoc/JSDoc as appropriate. Describe behavior and intent rather than restating signatures.

Document inputs, outputs, nullability, units, UTC/time assumptions, validation, invariants, exceptions or error results, side effects, cancellation, and resource ownership where relevant. For persistence and background work, explain transaction boundaries, schema ownership, ordering, replay/idempotency, concurrency and locking, retention, and failure/recovery behavior. For API and AI boundaries, document authorization, trust boundaries, allowlists, limits, sanitized errors, and secret-handling constraints.

Use concise inline comments for non-obvious algorithms, business decisions, security controls, and tradeoffs; explain why the code exists. Keep examples, OpenAPI descriptions, configuration/setup notes, and architecture decision records aligned with implemented behavior. Never put credentials or sensitive payloads in documentation. Avoid boilerplate comments, speculative claims, and comments that merely repeat the code.

Documentation is part of each change and completion review: a maintainer must be able to understand the contract and safely modify the code. Review accuracy, consistency with tests, and unresolved assumptions before marking the affected step complete.

Explicit code documentation standard. The following requirements are mandatory for each applicable change and supplement the documentation and incremental testing requirements in this section.

XML documentation and contracts. Use C# XML documentation to state responsibilities and invariants for public types, interfaces, use cases, and nontrivial members. Document each parameter and return value, including nullability, units, and time assumptions; document applicable exceptions and the conditions that cause them. State contracts, including preconditions, postconditions, side effects, cancellation, and resource ownership. Use summary, remarks, param, returns, and exception elements as applicable; explain non-applicable elements rather than inventing behavior. Use equivalent TSDoc/JSDoc for TypeScript.

Adjacent domain decision comments. Place concise comments immediately adjacent to each non-obvious domain or business decision branch, explaining WHY the rule selects that outcome and which invariant it preserves. In particular, explain why a new incident is opened versus why the existing incident is preserved, how recovery is recognized and applied, why threshold boundaries and timeout behavior have their specified semantics, and how incident identity and historical continuity are maintained. Document the rationale for the implemented rules without introducing new incident behavior or changing existing architecture decisions.

Technical rationale comments. Place comments beside non-obvious technical, security, transaction, concurrency, ordering, and retention decisions. Explain the constraint or failure mode motivating the implementation, including relevant trust boundaries, atomicity, locking or race prevention, event ordering and replay, and retention or deletion choices. Keep broader architectural rationale in the appropriate architecture decision record and retain the local explanation needed to understand the code.

Comment quality. Explain intent, constraints, and WHY; avoid redundant narration of signatures, assignments, conditions, or other self-evident code. Keep documentation accurate when behavior changes, and remove obsolete or misleading comments.

Two-pass review and matching tests. Complete two explicit review passes before marking an affected step complete. Pass 1 checks implementation correctness against contracts, invariants, domain decisions, security and persistence behavior, and matching focused tests. Pass 2 checks XML documentation and adjacent rationale comments for completeness, accuracy, and agreement with the implementation and those tests. Add or update focused tests in the same step for the documented decision outcomes and boundaries, including incident opening versus preservation, recovery, thresholds, timeout behavior, and historical continuity where affected; use focused integration tests when transactions, concurrency, ordering, retention, or component interactions determine correctness. Record both review outcomes, test evidence, and unresolved gaps. Documentation review does not substitute for executable tests, and passing tests does not substitute for documentation review.

### Incremental unit and integration testing requirements
Create focused automated tests alongside implementation in every step, rather than postponing test creation until Step 9. At minimum, add or update unit tests for new or changed business rules, validators, application behavior, and frontend logic. Add focused integration tests in the same step whenever correctness depends on PostgreSQL behavior, migrations, transactions, API serialization/middleware, module adapters, provider transport, or interaction between components. For tooling, deployment, and release work, execute relevant smoke/configuration checks and existing tests; record any genuinely inapplicable unit or integration cases with a reason.

For each affected behavior, cover success cases; invalid input and dependency/error cases; empty, null, minimum/maximum and exact threshold boundaries; and replay/idempotency, concurrency, and security cases wherever applicable. Record applicability and expected outcomes so omissions are deliberate and reviewable, rather than treating every category as a mandatory artificial test for every function.

Replay/idempotency tests verify duplicate requests return the saved result without duplicate writes, changed payloads conflict, expired receipts cannot cause a new write, and retirement does not break a valid replay. Concurrency tests exercise simultaneous duplicate and competing requests, atomic rollback, incident uniqueness, scoped context isolation, and AI admission/budget limits as applicable. Assert persisted state and external call counts as well as responses.

Security tests cover authorization and public-write denial, input and query allowlists, malformed or oversized payloads, prompt injection and unsafe provider output, secret/error/log redaction, runtime database privileges, and rate/budget enforcement as relevant. Verify success and denied/failure paths; use isolated fixtures and fictional data.

Keep unit tests deterministic with controlled clocks, inputs, and dependency fakes. Use an isolated real PostgreSQL instance and both migrations for provider-dependent integration tests; do not substitute EF InMemory or SQLite for PostgreSQL guarantees. Stub provider responses for deterministic CI, including timeout, cancellation, malformed output, and quota failures. Keep live-model evaluation separate, explicitly configured, and outside ordinary CI secrets.

Each step completion gate includes the applicable focused tests passing, updated documentation, reproducible run instructions, and recorded evidence or unresolved gaps. Steps 2–8 therefore accumulate executable coverage as features are built; Steps 10–11 retain relevant regression and operational checks. Step 9 remains the comprehensive integration, regression, frontend/E2E, architecture/security, dependency-review, and CI consolidation step: close coverage gaps, run the accumulated suite together, and establish repeatable CI. It is not the first point at which tests are written. Existing section 19 acceptance matrices and mandatory release gates remain in force.

### Tests worth writing
Unit tests cover time thresholds at 90 seconds, 250 ms, and 80 percent; unknown and timeout precedence; incident opening and recovery; summary denominators; sorting and pagination boundaries. PostgreSQL integration tests use an isolated database and both real migrations to check timestamptz, partial uniqueness, rollback, duplicate BatchId, and simultaneous requests. EF InMemory and SQLite cannot prove these provider behaviors. WebApplicationFactory verifies serialization, ProblemDetails, query bounds, and security modes; make the API entrypoint accessible to its test assembly. [R23]

Add replay-after-retirement, UUIDv7 expiry, simultaneous writes, and retention FK tests. Disable the periodic producer in deterministic test fixtures; test its scheduling separately with a fake clock. React tests cover paging, stale/error states, and drawer focus. One Playwright journey searches an off-page device and verifies timeout/recovery. Use an isolated database; never reset the live Supabase project from tests.

### Architecture boundary checks
Add focused architecture tests that reject Device-to-Telemetry and Telemetry-to-Device entity dependencies, require cross-module communication through Application ports and IntegrationContracts, and keep EF types out of Domain and Application. Because modules share layer assemblies, these tests and review enforce the boundaries inside each layer. They are particularly useful before considering service extraction.

### CI contract
On pull requests and main, install the pinned .NET SDK and Node 24; restore the local EF tool and NuGet packages; start an ephemeral PostgreSQL service pinned to the cloud project's major; run unit and integration tests; run npm ci, npm run lint, npm run test -- --run, and npm run build. Add the test script to package.json as vitest and set up its jsdom configuration before using it. Run the Playwright journey once the host startup fixture is reliable; install browsers in CI with npx playwright install --with-deps chromium. Build backend project/test paths directly so the JS SDK is not required on Linux CI.

Commit npm/NuGet lock files; section 24 supplies their generation and subsequent locked restore commands. Supply ephemeral test credentials only. Public PR jobs receive no production secrets. Pin workflow actions to reviewed commits and archive evidence; automated production deployment is optional.

### Showcase readiness
The README opens with a screenshot, purpose, stack, demo link/status, and a 60–90 second video. Include a compact topology, setup steps, secret placeholders, simulation scenarios, decisions D01–D11, tests, limitations, and an extraction roadmap. Screenshots show synthetic labels and no keys. Tag v1.0.0 after a clean-clone rehearsal. The LinkedIn draft explains the project, a concrete frontend interaction, one backend design choice, and evidence links without claiming production industrial deployment. Publishing the post is a separate owner action.

### Proposed AI acceptance and test matrix
| Gate | Observable pass condition |
| --- | --- |
| Supported intent | Prompt for Offline at DEN produces the canonical typed filter; Apply calls the normal GET and finds matching devices across pages. Equivalent manual parameters yield identical rows/count/asOf semantics with a fixed clock. |
| Strict validation | Malformed JSON, duplicate/extra properties, numeric enums, unknown site/type/sort, missing fields, invalid outcome/query combinations and overlong q are rejected before Apply or database access. |
| Unsafe and unsupported intent | SQL/write/tool instructions cannot invoke any database or network tool. Unsupported thresholds, OR/multiple-status, historical requests and ambiguity produce no executable query or silent approximation. |
| Failure isolation | Timeouts, unavailable provider, invalid output, quotas, disabled route and cancellation have documented behavior; existing filters/data remain usable. GET and internal producer work independently. |
| Cost and privacy | Per-client/global/concurrency/daily limits tested, zero queued calls, count survives restart, counter failures disable AI, no automatic retries and no secrets/prompts/provider text in logs or dist. |
| UI consistency | Apply resets page only and replaces filters, Clear resets neutral filters, late results cannot overwrite newer intent, manual filters remain usable, polling never calls the provider and fleet cards stay global. |
| Module boundary | AiQueries has no EF/SQL/DbContext/module-entity or write-use-case dependency; provider adapter does not query either schema. |

Use deterministic IStructuredQueryGenerator fakes for unit tests and WebApplicationFactory endpoint tests. Spy on module ports to prove translation does not access storage; prove invalid GET input causes no database work. Use an isolated PostgreSQL fixture and fixed clock to compare manual versus AI-applied queries. Test rate windows, daily reset at UTC midnight, restart persistence, concurrency rejection, timeout, cancellation, disabled-route 404, CORS, and ProblemDetails. Add prompt-evaluation cases for supported, ambiguous, unsupported, and adversarial language; a fake only proves control flow, not model accuracy.

React tests cover preview, Apply/Clear, stale responses, error retention and accessible status. Extend the existing Playwright journey with a stubbed translation followed by real GET filtering; it remains deterministic. Add a small owner-run live-provider smoke/evaluation set for actual schema adherence and interpretation in Step 7 and before release. CI contains no production AI credentials, has no live model dependency, and never spends provider budget by default. Record live evaluation date, model configuration, cases and outcomes without secrets or raw prompts containing private information.

# 20 Risks review and approval
| Risk | Mitigation and review consequence |
| --- | --- |
| Scope grows beyond the approved MVP | Eleven implementation steps with completion gates; functional scope freezes after Step 8 and stretch work waits until mandatory MVP gates pass. |
| esproj fails to load | Verify the pinned JS SDK and IDE workloads in Step 1; keep the documented independent CLI workflow usable. |
| IPv6 or SSL connection failure | Test session pooler and CA path from the actual host early; preserve certificate verification. |
| Pool exhaustion | Explicit shared singleton data source, ten-connection pool per API process; budget admin connections and extra replicas. |
| Frontend implies fleet search over one page | All main-grid operations execute server-side; add an off-page search acceptance test. |
| Telemetry races create duplicate incidents | Transaction-scoped simulator lock, persistent receipt, and partial unique index; integration test simultaneous batches. |
| Service readiness oversold | Preserve facade/contracts, avoid cross-domain FKs, and document extraction adapters, events, and changed consistency. |
| Secret leaked to Git or JavaScript | User-secrets/hosting secrets, placeholders, ignore rules, dist inspection, and public-write disablement. |
| Public demo looks dead | Required internal producer, continuous host, freshness display, restart gate; public writes remain disabled. |
| History grows indefinitely | Required bounded cleanup, nullable incident audit references, protected state ping, expiring UUIDv7 replay policy. |

### Technical review conclusions
The reference graph is acyclic, both contexts own independent migration histories, and no domain model depends on infrastructure. Fleet composition explicitly bounds the catalog and avoids a hidden cross-schema join. Telemetry, incidents, state, and receipt share one transaction; inventory validation does not imply distributed atomicity. The health model distinguishes observed failures from elapsed-time staleness, and the incident model makes that limitation visible.

Pagination, filtering, sorting, count, and summary semantics agree between REST and React. Public writes are disabled at the route and policy layers, not merely through UI visibility. Frontend build configuration is public while runtime database credentials remain server-side. Version compatibility follows package dependency ranges rather than equal patch-number assumptions. These choices support the staged implementation while leaving clear expansion points.

### Owner approval checklist
Approve the module/schema boundaries, seed-only inventory, deterministic observations, five-second polling, global grid processing, internal producer, bounded retention, and public read-only database access. The owner has selected eleven implementation steps with completion gates instead of a fixed-day or working-hour schedule. Stabilize functional scope after Step 8 and require mandatory verification before release. Technical review establishes a coherent plan; it does not certify an application that has not yet been built.

| Approval item | Owner entry |
| --- | --- |
| Architecture and scope | Approved / amendments required |
| Security and demo exposure | Approved / amendments required |
| Eleven implementation steps including Option A | Selected by owner; completion evidence pending |
| Reviewer and date | ______________________________ |

### Option A risk and approval amendment
| Risk | Mitigation and review consequence |
| --- | --- |
| Model proposes a valid but wrong filter | Visible normalized preview, explicit Apply, manual correction and live evaluation cases; never assert guaranteed intent accuracy. |
| Prompt injection or unsafe query proposal | Strict typed contract and duplicate/unknown member rejection, independent validators, no SQL/tool execution or database access in the adapter. |
| Public AI usage exceeds budget | Dedicated per-client/global/concurrency/daily limits, persistent count and provider spending cap; fail closed on counter failure and no provider retries. |
| AI adds implementation complexity | One provider and grid-only scope; Step 4 contracts, Step 5 secure provider integration, Step 7 live journey, and Step 8 scope freeze. Resolve incomplete gates without introducing a deadline or dropping required controls. |
| AI is confused with implemented Step 2 work | Edition status records startup evidence separately; all new AI components remain proposed until their implementation gates pass. |

Option A is the approved architecture choice. Its implementation and release evidence remain pending. Retain D01–D10 and approve the bounded D11 contracts, validation, provider isolation and eleven-step implementation sequence without a fixed time limit for implementation. The approval checklist does not imply the model adapter, production budget controls, or Step 2 database gates have been verified.

# 21 Official references
References are linked to official documentation or publisher package metadata. Verification date is 12 October 2026. Reference IDs in the body point to this register. Versioned documentation is used where available; moving documentation should be checked again if the implementation starts after dependency upgrades.

R01  Microsoft  .NET 10 solution format default and explicit SLN option
https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-new-sln-slnx-default

R02  Microsoft  SDK and Visual Studio version compatibility
https://learn.microsoft.com/en-us/dotnet/core/porting/versioning-sdk-msbuild-vs

R03  Microsoft  Built in dotnet new templates and CLI options
https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-new-sdk-templates

R04  Npgsql publisher metadata  EF provider 10.0.3 dependency range
https://api.nuget.org/v3-flatcontainer/npgsql.entityframeworkcore.postgresql/10.0.3/npgsql.entityframeworkcore.postgresql.nuspec

R05  Npgsql  EF Core provider 10 release notes
https://www.npgsql.org/efcore/release-notes/10.0.html

R06  Microsoft  EF Core dotnet CLI tools
https://learn.microsoft.com/en-us/ef/core/cli/dotnet

R07  Vite  Scaffolding and Node prerequisites
https://vite.dev/guide/

R08  Tailwind  Vite plugin installation for version 4
https://tailwindcss.com/docs/installation/using-vite

R09  Microsoft  JavaScript project MSBuild properties
https://learn.microsoft.com/en-us/visualstudio/javascript/javascript-project-system-msbuild-reference?view=visualstudio

R10  Microsoft  Visual Studio React and ASP NET Core integration
https://learn.microsoft.com/en-us/visualstudio/javascript/tutorial-asp-net-core-with-react?view=visualstudio

R11  Microsoft  Development secrets and configuration
https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-10.0

R12  Supabase  Database endpoints poolers and migration connections
https://supabase.com/docs/guides/database/connecting-to-postgres

# 22 References and verification record
R13  Supabase  SSL enforcement and server verification
https://supabase.com/docs/guides/platform/ssl-enforcement

R14  Npgsql  Connection parameters and certificate validation
https://www.npgsql.org/doc/connection-string-parameters
https://www.npgsql.org/doc/security.html

R15  Vite  Public environment variables and build modes
https://vite.dev/guide/env-and-mode

R16  Microsoft  EF migration projects and startup assembly configuration
https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/projects

R17  Microsoft  API error handling and ProblemDetails
https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0

R18  Microsoft  CORS policies and middleware
https://learn.microsoft.com/en-us/aspnet/core/security/cors?view=aspnetcore-10.0

R19  TanStack Table version 8  Manual pagination and row counts
https://tanstack.com/table/v8/docs/guide/pagination

R20  TanStack Table version 8  Sorting consistency with pagination
https://tanstack.com/table/v8/docs/guide/sorting

R21  TanStack Query version 5  Placeholder data migration
https://tanstack.com/query/latest/docs/framework/react/guides/migrating-to-v5

R22  Supabase  Data API exposure grants and RLS
https://supabase.com/docs/guides/api/securing-your-api

R23  Microsoft  ASP NET Core integration testing
https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0

R24  Zustand maintainers  React state store and selector usage
https://github.com/pmndrs/zustand

R25  Npgsql  Data source ownership and EF registration
https://www.npgsql.org/doc/basic-usage.html
https://www.npgsql.org/efcore/

R26  PostgreSQL  Transaction scoped advisory locks
https://www.postgresql.org/docs/current/functions-admin.html

R27  Microsoft  Hosted services and scoped dependencies
https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0

R28  PostgreSQL  Privileges and default privileges
https://www.postgresql.org/docs/current/ddl-priv.html
https://www.postgresql.org/docs/current/sql-alterdefaultprivileges.html

R29  UUID maintainers  Version 7 generation for the browser
https://github.com/uuidjs/uuid

R30  PostgreSQL and Supabase  Schema creation and inherited privileges
https://www.postgresql.org/docs/current/sql-createschema.html
https://www.postgresql.org/docs/current/app-psql.html
https://supabase.com/docs/guides/troubleshooting/resolving-permission-denied-for-database-postgres-during-migrations-78f33a
https://supabase.com/docs/guides/troubleshooting/custom-role-inherits-privileges-that-were-not-explicitly-granted-ddaa1c

### Bootstrap verification record
Earlier scaffold checks exercised SLN creation, net10.0 controller/library templates, project-reference commands, NuGet restore, and a backend build with SDK 10.0.400, EF Core 10.0.11, and Npgsql EF provider 10.0.3. The esproj was added and create-vite 9.2.1 was exercised. The four-project revision has been checked for consistent names, references, and migration paths; its revised bootstrap has not been executed. Frontend package installation/build, real Supabase access, and complete setup were not verified end to end. Record fresh clean-clone evidence when implementing this edition.

Runtime secrets, role provisioning, migrations, IDE debugging, business endpoints, tests, and hosting require the eventual implementation environment. Record resolved versions and clean-clone evidence in the README. This document supplies planned commands and configurations; it does not claim that the dashboard has been implemented.

### Edition 1 5 reference and verification amendment
R31 Microsoft ASP NET Core rate limiting middleware
https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0

R32 Microsoft EF Core querying data
https://learn.microsoft.com/en-us/ef/core/querying/

R33 Microsoft System Text Json reject unmapped members
https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/missing-members

R31–R33 support framework mechanics only; endpoint fields, limits, budget persistence, and the implementation sequence are project design decisions. Provider-specific schema and token-limit documentation must be recorded after provider selection. Existing R01–R30 and their original verification date are preserved. Edition 1.5 does not claim new execution of the bootstrap, Step 2 migrations, or application tests. Step 2 implementation evidence is limited to the supplied startup code described in the opening status record.

# 23 Database provisioning and privileges
Effective hosted PostgreSQL grants, inherited privileges, runtime DDL denial, TLS certificate trust, and deployment behavior require separately authorized operational verification. Configuration parsing and disposable CI execution do not establish these hosted properties and are not permission to access Supabase.

Use a new Supabase project dedicated to synthetic data. Before migrations, provision hm_migrator as the owner of the two private schemas and hm_runtime as a separate restricted login. These administrative steps are prerequisites, not commands to run as the runtime user. A PostgreSQL psql client is required for the interactive password commands below; it is an additional setup prerequisite, not a NuGet package.

### Administrative connection
Copy the admin endpoint from Supabase Connect, using direct access where reachable or a tested session pooler for IPv4. The -W prompt avoids embedding an admin password in a command. Use the project's CA file and exact hostname. The following is a template with project-specific placeholders. Do not commit the resulting secrets.
```powershell
$env:PGSSLMODE = 'verify-full'
$env:PGSSLROOTCERT = '<absolute-path-to-project-CA>'
psql --host '<copied-admin-host>' --port 5432 `
  --dbname postgres --username '<copied-admin-username>' -W `
  --set ON_ERROR_STOP=on
```

Run the following inside psql as the project's authorized administrator. Role/schema creation is a one-time operation; do not blindly repeat it or change an existing project's ownership. The \password commands prompt interactively. Grant temporary database CREATE to the migration role so it can create its own schemas without depending on administrator membership in that role. Never grant the runtime role membership in postgres or hm_migrator. [R28, R30]
```sql
CREATE ROLE hm_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE ROLE hm_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
\password hm_migrator
\password hm_runtime
GRANT CONNECT ON DATABASE postgres TO hm_migrator, hm_runtime;
GRANT CREATE ON DATABASE postgres TO hm_migrator;
```

Exit with \q and reconnect using the same psql template with the hm_migrator login, adding the project suffix for a session-pooler username. Run the following as hm_migrator; both schemas and later EF tables will have that owner:
```sql
CREATE SCHEMA device;
CREATE SCHEMA telemetry;
GRANT USAGE ON SCHEMA device, telemetry TO hm_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE hm_migrator
  IN SCHEMA device, telemetry
  REVOKE ALL ON TABLES FROM PUBLIC, anon, authenticated;
```

Exit psql with \q and reconnect as the administrator to run REVOKE CREATE ON DATABASE postgres FROM hm_migrator; schema ownership still permits migrations inside those existing schemas. Use the two role passwords only in local secrets or the hosting/release secret stores. Apply section 10 migrations using hm_migrator, then reconnect as that owner and grant access to named business tables only:
```sql
GRANT SELECT ON device.monitored_devices TO hm_runtime;
GRANT SELECT, INSERT, UPDATE ON
  telemetry.telemetry_pings, telemetry.incident_logs,
  telemetry.device_telemetry_state, telemetry.simulation_batches
  TO hm_runtime;
GRANT DELETE ON telemetry.telemetry_pings,
  telemetry.incident_logs, telemetry.simulation_batches TO hm_runtime;
```

No runtime grant is needed on migration history. Explicitly review grants for each future table, keep these schemas unexposed to the Data API, and inspect inherited privileges before asserting DDL denial. UUID keys avoid identity-sequence grants. Seed the catalog with the migration/provisioning role, then start the API with hm_runtime. Verify that runtime can query the catalog and ingest synthetic telemetry but cannot create a table in either private schema. Remove the PGSSL environment overrides from the provisioning shell when finished.

# 24 Configuration and reproducible validation
### Nonsecret API defaults
Merge this configuration into the API appsettings.json. The public hosting environment overrides producer enablement; the local demo overrides the HTTP simulator flags using user-secrets. Never add a real database password to this file.
```json
{
  "Cors": { "AllowedOrigins": ["http://localhost:5173"] },
  "Simulation": {
    "HttpEndpointEnabled": false,
    "AllowLocalAnonymous": false
  },
  "DemoProducer": { "Enabled": false, "IntervalSeconds": 60 },
  "Monitoring": {
    "OfflineAfterSeconds": 90,
    "LatencyWarningMs": 250,
    "CpuWarningPercent": 80
  }
}
```

### Lock files and scripts
After scaffold/package configuration, generate NuGet locks from the repository root. These commands run against backend and test projects explicitly; the .esproj does not stand in for the npm validation gate. No complete application behavior is implied by a successful scaffold restore.
```powershell
Get-ChildItem src/backend,tests -Recurse -Filter *.csproj |
  ForEach-Object {
    dotnet restore $_.FullName --use-lock-file
  }
Push-Location src/frontend/health-monitor-web
npm pkg set 'scripts.test=vitest' 'scripts.test:e2e=playwright test'
npm ls --depth=0
Pop-Location
```

Commit all packages.lock.json files, package-lock.json, package.json, global.json, and .config/dotnet-tools.json. Add the frontend test runner configuration with jsdom, Testing Library setup, and separate Playwright discovery during implementation. The test script is only executable as a quality gate once meaningful tests and fixtures exist. Record the resolved npm versions; CI and subsequent clones use these locks rather than repeat dependency discovery.

### Validation after implementation
```powershell
dotnet tool restore
Get-ChildItem src/backend,tests -Recurse -Filter *.csproj |
  ForEach-Object { dotnet restore $_.FullName --locked-mode }
dotnet build src/backend/HealthMonitor.Api -c Release --no-restore
dotnet test tests/HealthMonitor.UnitTests -c Release --no-restore
dotnet test tests/HealthMonitor.IntegrationTests -c Release --no-restore
Push-Location src/frontend/health-monitor-web
npm ci
npm run lint
npm run test -- --run
npm run build
Pop-Location
```

Integration tests require the isolated PostgreSQL fixture; never point them at the hosted demonstration database. The Playwright journey additionally requires its configured API/SPA startup fixture and installed browser. Automated bootstrap/CI scripts must check each native exit code and stop on failure. Visual Studio startup settings, test fixtures, business code, initial seeding, and hosting configuration are explicit implementation tasks rather than silently omitted scaffold commands.

### Proposed nonsecret AI defaults
Merge this additional top-level block into section 24 appsettings.json; keep existing defaults unchanged. Startup validates the provider/model/host and secret when enabled. None of these defaults enables the AI route by itself.
```json
{
  "AiQueries": {
    "Enabled": false,
    "Provider": "",
    "Model": "",
    "BaseUrl": "",
    "TimeoutSeconds": 10,
    "MaxPromptCharacters": 500,
    "MaxRequestBodyBytes": 8192,
    "MaxResponseBytes": 8192,
    "MaxOutputTokens": 400,
    "RequestsPerClientPerMinute": 5,
    "RequestsGlobalPerMinute": 10,
    "MaxConcurrentRequests": 2,
    "DailyRequestLimit": 100,
    "BudgetCounterPath": "<host-local-private-writable-path>"
  }
}
```

AiQueries__ApiKey is a hosting secret; no VITE_AI key or browser provider SDK is permitted. Add startup option validation, the named limiter and middleware, provider HttpClient cancellation, and the private budget-counter path during implementation. These are proposed additions rather than runnable completed integration code. Extend the existing validation commands with the AI tests in section 19; those tests run under the same backend and frontend test gates, without adding another solution project. Record provider transport dependencies in the existing lock files.
