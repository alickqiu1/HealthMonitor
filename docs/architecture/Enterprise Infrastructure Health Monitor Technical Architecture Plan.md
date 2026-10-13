# Enterprise Infrastructure Health Monitor Dashboard
## Technical Architecture Plan
Review edition 1.3 • 12 October 2026

Build a polished, demonstrable dashboard in seven to ten focused working days. Approve a modular monolith: one ASP.NET Core Web API, two independently owned domain modules, one Supabase PostgreSQL database, and a separate Vite React application. The frontend emphasizes responsive interaction, useful telemetry, and dependable table behavior; the backend demonstrates clean boundaries, explicit persistence ownership, and testable business rules.

The intended implementer is an experienced C# and industrial telemetry developer. The project should show modern React and TypeScript capability without spending the schedule recreating an enterprise platform. This plan specifies architectural boundaries, conceptual models, public contracts, bootstrap commands, verification gates, and publication readiness. It deliberately leaves controllers, repository implementations, and React components to implementation.

### Approval recommendation
Approve the MVP and decisions D01–D10. Treat service extraction, full identity management, streaming, device provisioning, and advanced analytics as future work. Public HTTP access is read-only. A small server-owned producer keeps the hosted synthetic fleet active; local simulation remains available for deterministic demonstrations. The simulator and UI clearly identify all data as synthetic.

### Delivery boundary
The MVP contains a Metrics Summary, a TanStack Table data grid with server pagination and global filtering and sorting, and a Device Status Drawer with recent telemetry and incident history. It includes a controlled simulator, an internal periodic producer with bounded retention, cloud persistence, API documentation, focused tests, CI, screenshots, and a short demonstration video.

### Review navigation
| Section | Review focus |
| 1–2 | Decisions, topology, project boundaries |
| 3–6 | Windows bootstrap, frontend integration, secrets |
| 7–10 | Domain models, schema ownership, consistency |
| 11–14 | Health semantics, REST contracts, simulator and security |
| 15–17 | React interaction, server state, deployment |
| 18–20 | Roadmap, quality gates, risks, approval |
| 21–22 | Official references and verification notes |
| 23 | Provisioning and bootstrap completion checklist |
| 24 | Configuration files and reproducible validation commands |

**Feasibility judgment.** Target ten working days and approximately 60–70 focused hours. Seven days is an accelerated delivery requiring roughly nine to ten focused hours daily, familiar hosting, and no stretch work. Resolve cloud access on day one and freeze functional scope by day seven. Enterprise-grade here means understandable code, secure configuration, deterministic behavior, and meaningful verification; it does not imply high availability or industrial monitoring certification.

# 1 Architecture decisions
| Decision | Choice and reason |
| D01 | Modular monolith for the MVP. One process avoids deployment and messaging work while preserving domain seams. |
| D02 | Four backend projects: Domain, Application, Infrastructure, and API. Device and Telemetry are separate folders and namespaces within each layer. Project references enforce layer boundaries; conventions, review, and architecture tests protect module boundaries. |
| D03 | Two scoped DbContexts and two private schemas. The API composition root supplies one shared singleton NpgsqlDataSource for the identical runtime connection configuration. |
| D04 | Immutable UUID device identities cross boundaries as scalar values. No cross-schema foreign keys or EF navigation properties. Soft retirement preserves historical references. |
| D05 | A stable API facade owns /api/v1 contracts. Service extraction keeps this facade or an equivalent gateway at the same public address. |
| D06 | EF Core repositories for aggregates; purpose-built query ports for projections. No generic repository or IQueryable exposure. |
| D07 | Server pagination, filtering, and sorting operate over the same dataset. TanStack Table provides headless table behavior; the project supplies accessible grid markup and styling. |
| D08 | TanStack Query v5 owns server cache; Zustand v5 owns shared UI preferences. Keep request data out of Zustand. |
| D09 | Five-second polling with visible freshness for MVP. SignalR and event transport are stretch work. |
| D10 | Public HTTP reads only; an internal producer maintains synthetic telemetry with bounded retention. No browser-held database credential or privileged Supabase key. |

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

# 2 Repository and project boundaries
```text
HealthMonitor/
  HealthMonitor.sln                   actual SLN format
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

HealthMonitor.Api.csproj is the C# ASP.NET Core executable project. Domain, Application, and Infrastructure are its three C# class-library projects. The two test .csproj files are additional C# test projects, outside the four backend production projects. HealthMonitor.sln includes these six .csproj files and the frontend health-monitor-web.esproj: seven solution projects in total. The frontend's package.json defines its npm dependencies and scripts, while the .esproj provides Visual Studio solution integration.

### Project reference rules
Domain references no other solution project, EF Core package, or web framework. Application references Domain only. Infrastructure references Application and Domain. API references Application and Infrastructure as the dependency injection composition root; it maps Application read models to its public V1 DTOs. No inner layer references API. Unit tests reference Domain and Application; integration tests reference API.

Device and Telemetry retain separate folders and namespaces inside the shared layer assemblies. They must not use each other's domain entities or persistence implementations. Telemetry Application defines an IDeviceCatalog port returning IntegrationContracts records such as DeviceRegistration. An API-owned adapter implements that port by calling Device Application's IDeviceCatalogQueries. A future HTTP adapter can replace it. Sharing an assembly permits accidental cross-module references, so review and focused architecture tests must enforce the rule; the compiler enforces layer dependencies, not these module boundaries.

Public V1 DTOs live in API/Contracts/V1; module communication records live in Application/IntegrationContracts. There is no separate Contracts project. Neither contract category contains EF entities. Frontend types follow the OpenAPI JSON contract, not CLR assembly references. Checked-in TypeScript contracts are sufficient for MVP; automated generation can follow after the API stabilizes. Future extraction separates the module files and introduces transport adapters without changing public routes and DTOs, provided their semantics remain compatible.

### Persistence and runtime rules
Repositories return aggregates, value records, or explicit paged results and accept cancellation tokens. Query ports support AsNoTracking projections and server aggregation without generic CRUD abstractions. SaveChanges is coordinated once per Telemetry use case. Scoped DbContexts are never shared across requests or simultaneous tasks.

The frontend .esproj is a sibling solution project located inside the independent frontend root. There is no API-to-esproj ProjectReference, SPA proxy package, or frontend build embedded in API publish. Visual Studio launches the two projects separately; CLI and CI build the two artifacts explicitly.

# 3 Bootstrap prerequisites and version policy
### Windows prerequisites
Use PowerShell 7 or Windows PowerShell 5.1, Git, .NET SDK 10.0.400, Node.js 24 LTS with npm, and a PostgreSQL psql client for section 23 provisioning. Visual Studio 2026 18.x must support net10.0 and have ASP.NET and web development plus JavaScript and TypeScript tooling installed. Version 18.0 is the documented target-framework floor; prefer the matching SDK and current Visual Studio servicing release. Visual Studio 2022 is not a supported net10.0 IDE. [R01–R03]

Sections 3–5 are scaffold commands; section 6 configures local secrets and startup. Sections 9–10 require implemented models and contexts; section 23 supplies provisioning and completion steps. The scaffold is not a functioning dashboard. Run each block from its stated directory. NuGet/npm access is required; an unresolved JavaScript SDK during sln add may indicate blocked NuGet access.

| Dependency | Baseline |
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
  dotnet sln HealthMonitor.sln add $_.FullName
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
dotnet sln HealthMonitor.sln add $unit $integration
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
dotnet sln HealthMonitor.sln add `
  src/frontend/health-monitor-web/health-monitor-web.esproj
dotnet sln HealthMonitor.sln list
```

ShouldRunNpmInstall=false avoids an implicit npm install overwriting the explicit npm ci workflow. ShouldRunBuildScript=false keeps a backend build from implying frontend validation; npm run build is always a separate gate. Configure both API and frontend as startup projects in Visual Studio. Set the API profile ports to the values in section 6 and suppress its automatic browser launch. The CLI two-terminal workflow remains authoritative if JavaScript debugging integration varies by IDE servicing release.

# 6 Secrets local startup and bootstrap checks
### Configuration contract
Commit nonsecret defaults only: Cors:AllowedOrigins=["http://localhost:5173"], Simulation:HttpEndpointEnabled=false, Simulation:AllowLocalAnonymous=false, DemoProducer:Enabled=false, DemoProducer:IntervalSeconds=60, Monitoring:OfflineAfterSeconds=90, Monitoring:LatencyWarningMs=250, Monitoring:CpuWarningPercent=80. Omit real connection strings. Device and Telemetry configuration keys point to the same runtime role; startup validates their equality before creating the shared data source.

### Store the runtime credential outside Git
Copy the host, username, database, and port from Supabase's Connect dialog. Download the project's CA certificate to a local path outside Git. Use a restricted runtime role, not the postgres owner role. The connection format is Npgsql key/value syntax, not a PostgreSQL URI. [R11–R14]
```powershell
$api = 'src/backend/HealthMonitor.Api/HealthMonitor.Api.csproj'
dotnet user-secrets init --project $api
$db = Read-Host 'Paste runtime Npgsql connection string'
dotnet user-secrets set 'ConnectionStrings:Device' $db --project $api
dotnet user-secrets set 'ConnectionStrings:Telemetry' $db --project $api
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

Create one singleton NpgsqlDataSource in the API composition root and pass that same instance to both scoped DbContexts. Set Maximum Pool Size=10 on that source; identical strings alone are not the ownership rule. Budget ten connections per API process plus migration/admin connections. Different roles or replicas create additional pool budgets. Deployment keys are ConnectionStrings__Device and ConnectionStrings__Telemetry. [R25]

### Two terminal startup and verification
```powershell
# Terminal A, repository root; after application implementation
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/backend/HealthMonitor.Api --no-launch-profile `
  -- --urls 'https://localhost:7043;http://localhost:5043'
# Terminal B, repository root
Set-Location src/frontend/health-monitor-web
'VITE_API_BASE_URL=https://localhost:7043' | Set-Content .env.local
npm ci
npm run dev -- --port 5173 --strictPort
```

Add .env*, node_modules/, dist/, .vs/, *.user, and local cert/secret paths to .gitignore; allow a placeholder .env.example. VITE_API_BASE_URL is public. Day one checks prerequisites, project loading, independent scaffold builds, and cloud TLS access. Migrations and seeded reads follow on day two; drawer, simulation, and browser CORS checks follow after frontend integration on days six and seven. The final solution contains four backend projects, two tests, and one esproj. [R15]

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
| Id | uuid primary key; immutable identity |
| AssetTag | varchar(64), required; uppercase canonical unique tag |
| DisplayName | varchar(120), required; trim, preserve display case |
| DeviceType | varchar(32), required; allowlisted demo categories |
| SiteCode | varchar(32), required; uppercase canonical site code |
| CreatedAtUtc | timestamptz, required; server UTC clock |
| RetiredAtUtc | nullable timestamptz; no physical delete in MVP |

### Device interfaces and invariants
IMonitoredDeviceRepository supports GetAsync, AddAsync, and SaveChangesAsync for seed or future inventory use cases. IDeviceCatalogQueries returns immutable DeviceRegistration records and catalog lists; it does not return domain entities or IQueryable. Application query projections may return identifiers, names, tags, site, type, and retirement state for API composition.

DeviceRegistration carries DeviceId, DisplayName, AssetTag, SiteCode, DeviceType, and IsRetired. Telemetry ingestion checks this port before accepting a device identity. The API facade is the only place that combines catalog metadata with telemetry summary records.

Retired devices remain addressable in historical detail but are omitted from active fleet summaries by default. There is no hard delete or inventory editing UI in the MVP. A retirement change racing with a telemetry write may leave one accepted measurement; preserving history is intentional. Strict distributed referential integrity would require a stronger ingestion policy after extraction.

**Decision D04 consequence.** A UUID does not prove the device exists. Catalog validation is a business check, and catalog synchronization becomes an explicit operational responsibility when modules move into different processes.

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
Implement and register both DbContexts in the API before these commands. Both contexts reside in HealthMonitor.Infrastructure, with separate mappings, migration folders, and model snapshots under Device/Persistence and Telemetry/Persistence. Explicit context selection and separate history tables keep their migration streams independent even though they share an assembly. The startup project loads API user-secrets in Development; do not build a separate design-time factory that ignores the intended configuration source.
```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$api = 'src/backend/HealthMonitor.Api/HealthMonitor.Api.csproj'
$infrastructure = 'src/backend/HealthMonitor.Infrastructure/HealthMonitor.Infrastructure.csproj'
dotnet ef migrations add InitialDevice --context DeviceDbContext `
  --project $infrastructure --startup-project $api `
  --output-dir Device/Persistence/Migrations
dotnet ef migrations add InitialTelemetry --context TelemetryDbContext `
  --project $infrastructure --startup-project $api `
  --output-dir Telemetry/Persistence/Migrations
```

Set the migration connection strings temporarily through environment variables, using the DDL-capable migration role; they override runtime user-secrets. Apply Device migrations before Telemetry. Review generated SQL and create schemas, roles, and default grants deliberately. Migration tooling is not a runtime API operation. [R06, R16]
```powershell
$migrationDb = Read-Host 'Paste migration role Npgsql connection string'
$env:ConnectionStrings__Device = $migrationDb
$env:ConnectionStrings__Telemetry = $migrationDb
Remove-Variable migrationDb
dotnet ef database update --context DeviceDbContext `
  --project $infrastructure --startup-project $api
dotnet ef database update --context TelemetryDbContext `
  --project $infrastructure --startup-project $api
Remove-Item Env:ConnectionStrings__Device -ErrorAction SilentlyContinue
Remove-Item Env:ConnectionStrings__Telemetry -ErrorAction SilentlyContinue
```

Use a direct database connection for migrations where reachable. Test session pooler 5432 as the IPv4 fallback; do not use transaction pooler 6543 for this workflow. Hosted deployment applies reviewed EF migration SQL as a controlled release step. Do not call Database.Migrate automatically on each API startup or mix Supabase CLI migrations with EF ownership of the same tables. [R12]

### Write consistency and concurrent simulation
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

The device cap is explicit. Prevent a seed or future inventory endpoint from exceeding it; return a documented service-capacity error rather than silently truncate a larger catalog. This bounded composition trades scale for a straightforward service seam. It is appropriate for 24 demo devices, not for an unconstrained industrial fleet. If scale expands, introduce a purpose-built dashboard projection with events and documented lag, or a backend aggregation service; the UI contract can remain stable.

Fleet summary is global across active devices, independent of grid filters. It returns total, healthy, degraded, offline, unknown, and average latency of fresh successful devices only, plus sampleCount. Recent drawer telemetry and incidents are server-paged database queries. GET domain responses expose asOfUtc; catalog and telemetry are separate reads, so no cross-module serializable snapshot is promised.

# 12 Versioned REST surface
| Method and route | Response and behavior |
| GET /api/v1/devices | 200 PagedResult<DeviceRowDto>; active catalog by default |
| GET /api/v1/devices/{deviceId} | 200 DeviceDetailDto; 404 unknown UUID |
| GET /api/v1/devices/{deviceId}/telemetry | 200 PagedResult<TelemetryPingDto>; newest first |
| GET /api/v1/devices/{deviceId}/incidents | 200 PagedResult<IncidentDto>; newest first |
| GET /api/v1/metrics/summary | 200 MetricsSummaryDto; active fleet, not current page |
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

CORS allows exact origins and needed methods and headers. For the public SPA, allow GET and required content headers; local development additionally needs POST and Content-Type. No wildcard with credentials, no cookie authentication, and no AllowCredentials in the MVP. CORS is a browser rule, not authentication. Apply it before endpoint authorization. [R18]

# 15 React layout and interaction map
### Component ownership
```text
AppRoot
  QueryClientProvider
  AppShell                       responsive header and content frame
    DashboardPage
      DashboardHeader            synthetic badge and last updated
      MetricsSummary             4 status cards plus unknown and latency
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

# 18 Critical path and scope control
| Day | Work and exit gate | Budget |
| 1 | Prerequisites, SLN/esproj loading, independent scaffold builds, Supabase TLS, provisioning and hosting access. No drawer or complete simulator gate yet. | 5–6 h |
| 2 | Domain models and mappings, two contexts and migrations, fixed catalog seed, basic database read and shared pool registration. | 7–8 h |
| 3 | Telemetry transaction, receipt-first replay, incident rules, periodic producer and retention policy; domain tests. | 7–8 h |
| 4 | Fleet and drawer REST queries, DTOs, pagination/filter/sort semantics, errors, API documentation and public-write policy. | 6–7 h |
| 5 | React shell, Tailwind styling, API client, Query/Zustand wiring, real metrics and basic grid. | 6–7 h |
| 6 | Global search/sorting, paging, drawer history and local simulation; demonstrate timeout and recovery end to end. | 7–8 h |
| 7 | Responsive and keyboard behavior, loading/error/stale states, request cancellation; functional scope freeze. | 5–6 h |
| 8 | PostgreSQL and API integration tests, retention/replay cases, frontend smoke tests, CI and dependency review. | 6–7 h |
| 9 | Continuous single-replica hosting, controlled migrations/seed, TLS/CORS, producer restart and cleanup checks. | 6–7 h |
| 10 | Clean-clone rehearsal, README/decisions, screenshots, short video and release tag; resolve remaining showcase defects. | 5–6 h |

The critical path is tooling and cloud access → schemas → API vertical slice → frontend integration → quality gates → deployment → publication evidence. Check hosting access on day one. Reserve days five through seven for React interaction and polish. The ten-day budget totals 60–70 hours; a seven-day variant compresses the same MVP into longer days and has no stretch allocation.

### MVP commitment
Ship the grid, fleet summary, drawer, deterministic local scenarios, incident recovery, separate contexts, stable routes, secure secrets, and cloud persistence. The hosted demo continuously generates synthetic data internally while its public API remains read-only. Include 24-hour telemetry retention, seven-day resolved incident history, bounded receipts, focused tests, and publication evidence. Stop stretch work until all these gates pass.

### Stretch choices
If the MVP finishes early, select at most one: a small trend chart, SignalR with reconnection, or URL-persisted filters. OIDC, multi-tenancy, editable provisioning, retention partitioning, large fleets, brokers, multiple producer replicas, and service extraction remain future projects.

### Schedule fallback
If the API slice slips past day four, remove optional animations, column preferences, and remote operator access. Preserve correct filtering, keyboard drawer behavior, replay integrity, and public-write policy. If continuous hosting is unavailable by day nine, ship a reproducible local demo and video, clearly identify the missing hosted gate, and use day ten for remediation. A paused or sleeping host may show truthful stale status, but does not pass the continuous-demo gate.

# 19 Acceptance tests CI and publication
### Required acceptance evidence
| Gate | Observable pass condition |
| Setup | Fresh Windows clone opens the actual .sln; all seven projects load: four backend, two tests, one frontend; API and frontend build independently with documented prerequisites. |
| Grid | Use pageSize=10 with the 24-device seed; search finds an item initially on another page; numeric sorting, nulls, tie-breakers, and counts are correct. |
| Metrics | Sum of four health counts equals total; filters do not change global totals; average uses only the declared fresh successful sample. |
| Drawer | Correct selected UUID; telemetry and incidents page correctly; no history shows an explicit empty state; keyboard focus returns on close. |
| Simulation | Scenarios and recovery work; replay after retirement returns the saved result; changed hash=409; expired missing-receipt ID causes no new write. |
| Failure | Invalid input=400; missing device=404; changed replay=409; unavailable DB=503; polling retains last good data with stale indication. |
| Security | No secret in files/history/logs or dist; TLS validation succeeds; unauthorized public POST cannot write; runtime role cannot execute DDL. |
| Release | HTTPS deployment smoke check, CI green, clean README rehearsal, screenshots/video and a tagged release are present. |
| Hosted lifecycle | At least three one-minute cycles maintain a mixed fleet; restart recovers generation; cleanup preserves latest state and incident history without FK failures. |

### Tests worth writing
Unit tests cover time thresholds at 90 seconds, 250 ms, and 80 percent; unknown and timeout precedence; incident opening and recovery; summary denominators; sorting and pagination boundaries. PostgreSQL integration tests use an isolated database and both real migrations to check timestamptz, partial uniqueness, rollback, duplicate BatchId, and simultaneous requests. EF InMemory and SQLite cannot prove these provider behaviors. WebApplicationFactory verifies serialization, ProblemDetails, query bounds, and security modes; make the API entrypoint accessible to its test assembly. [R23]

Add replay-after-retirement, UUIDv7 expiry, simultaneous writes, and retention FK tests. Disable the periodic producer in deterministic test fixtures; test its scheduling separately with a fake clock. React tests cover paging, stale/error states, and drawer focus. One Playwright journey searches an off-page device and verifies timeout/recovery. Use an isolated database; never reset the live Supabase project from tests.

### Architecture boundary checks
Add focused architecture tests that reject Device-to-Telemetry and Telemetry-to-Device entity dependencies, require cross-module communication through Application ports and IntegrationContracts, and keep EF types out of Domain and Application. Because modules share layer assemblies, these tests and review enforce the boundaries inside each layer. They are particularly useful before considering service extraction.

### CI contract
On pull requests and main, install the pinned .NET SDK and Node 24; restore the local EF tool and NuGet packages; start an ephemeral PostgreSQL service pinned to the cloud project's major; run unit and integration tests; run npm ci, npm run lint, npm run test -- --run, and npm run build. Add the test script to package.json as vitest and set up its jsdom configuration before using it. Run the Playwright journey once the host startup fixture is reliable; install browsers in CI with npx playwright install --with-deps chromium. Build backend project/test paths directly so the JS SDK is not required on Linux CI.

Commit npm/NuGet lock files; section 24 supplies their generation and subsequent locked restore commands. Supply ephemeral test credentials only. Public PR jobs receive no production secrets. Pin workflow actions to reviewed commits and archive evidence; automated production deployment is optional.

### Showcase readiness
The README opens with a screenshot, purpose, stack, demo link/status, and a 60–90 second video. Include a compact topology, setup steps, secret placeholders, simulation scenarios, decisions D01–D10, tests, limitations, and an extraction roadmap. Screenshots show synthetic labels and no keys. Tag v1.0.0 after a clean-clone rehearsal. The LinkedIn draft explains the project, a concrete frontend interaction, one backend design choice, and evidence links without claiming production industrial deployment. Publishing the post is a separate owner action.

# 20 Risks review and approval
| Risk | Mitigation and review consequence |
| Scope exceeds available time | Ten-day baseline, day-seven functional freeze, no stretch before all MVP gates pass. |
| esproj fails to load | Verify the pinned JS SDK and IDE workloads on day one; keep the documented independent CLI workflow usable. |
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

Pagination, filtering, sorting, count, and summary semantics agree between REST and React. Public writes are disabled at the route and policy layers, not merely through UI visibility. Frontend build configuration is public while runtime database credentials remain server-side. Version compatibility follows package dependency ranges rather than equal patch-number assumptions. These choices support the timetable while leaving clear expansion points.

### Owner approval checklist
Approve the module/schema boundaries, seed-only inventory, deterministic observations, five-second polling, global grid processing, internal producer, bounded retention, and public read-only API. Approve the ten-day target and day-seven functional freeze. Seven-day delivery is an accelerated option, not the baseline promise. Technical review establishes a coherent plan; it does not certify an application that has not yet been built.

| Approval item | Owner entry |
| Architecture and scope | Approved / amendments required |
| Security and demo exposure | Approved / amendments required |
| Seven to ten day schedule | Approved / revised target |
| Reviewer and date | ______________________________ |

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

# 23 Database provisioning and privileges
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
