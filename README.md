# HealthMonitor

HealthMonitor monitors a fictional infrastructure fleet using device registrations, simulated telemetry, health assessments and incident history. Its intended dashboard helps users find unhealthy devices and inspect their recent measurements and incidents.

**Current state:** the backend APIs and persistence foundations are implemented and tested. The React application is still the starter screen; the working dashboard is the next development milestone. AI translation is disabled in production composition.

## Functionality

- Device catalog and fleet summary APIs, with validated filtering, sorting and pagination.
- Per-device details, telemetry history and incident history.
- Atomic telemetry ingestion with idempotency, incident lifecycle processing and concurrency safeguards.
- An optional telemetry producer, retention processing and a separately controlled local simulation API.
- A provider-neutral AI query contract, strict proposal validation and bounded Groq transport. Local admission controls have automated coverage, but remaining configuration, operational controls and activation are incomplete. No live model evaluation is claimed.

All inventory and telemetry are synthetic. This application is not connected to real infrastructure and should not be used for confidential operational data without a separately designed access policy.

## Architecture

The backend is a .NET 10 modular application with a single ASP.NET Core facade:

```text
React client (planned dashboard)
            |
ASP.NET Core API — HTTP contracts and composition
            |
Application — use cases and ports
            |
Domain — business rules and invariants

Infrastructure — port implementations, EF Core and PostgreSQL
                 separate Device and Telemetry contexts/schemas
```

Device owns registrations; Telemetry owns measurements and incidents. Application contracts preserve that ownership while the API composes read results. AI translates text into a validated filter proposal; it has no database or write-use-case access. Applying a proposal will use the ordinary read APIs.

| Location | Responsibility |
| --- | --- |
| `src/backend/HealthMonitor.Api` | Facade, policies and service composition |
| `src/backend/HealthMonitor.Application` | Use cases, query contracts and ports |
| `src/backend/HealthMonitor.Domain` | Domain rules and models |
| `src/backend/HealthMonitor.Infrastructure` | PostgreSQL persistence and provider adapters |
| `src/frontend/health-monitor-web` | React, TypeScript, Vite and Tailwind frontend scaffold |
| `tests` | Backend unit, HTTP and isolated PostgreSQL tests |
| `scripts` | Strict integration-result verification and its database-free self-tests |

## Get started without a database

Install the .NET SDK specified in [global.json](global.json) (10.0.400, with serviced patch roll-forward). From the repository root:

```powershell
dotnet build tests/HealthMonitor.UnitTests/HealthMonitor.UnitTests.csproj -c Release
dotnet test tests/HealthMonitor.UnitTests/HealthMonitor.UnitTests.csproj -c Release --no-build
```

These commands build the backend and run unit tests without PostgreSQL, provider credentials or a running application. They may restore NuGet packages. Build backend projects explicitly: the solution also contains an independent JavaScript project.

To view the **frontend starter only**, use Node.js 24 LTS and npm in a separate terminal:

```powershell
cd src/frontend/health-monitor-web
npm ci
npm run dev
```

This currently shows the scaffold, not a connected monitoring dashboard.

## Run the API

A running API needs a prepared PostgreSQL database and an explicitly configured restricted runtime account. Follow the architecture plan's [local configuration and startup](docs/architecture/Enterprise%20Infrastructure%20Health%20Monitor%20Technical%20Architecture%20Plan.md#6-secrets-local-startup-and-bootstrap-checks) and [migration procedure](docs/architecture/Enterprise%20Infrastructure%20Health%20Monitor%20Technical%20Architecture%20Plan.md#10-migrations-and-consistency) before starting it:

```powershell
dotnet run --project src/backend/HealthMonitor.Api --launch-profile https
```

The checked-in HTTPS profile uses `https://localhost:7080`; a trusted local development certificate is required. Runtime credentials belong in user secrets or hosting secrets under `ConnectionStrings:HealthMonitorRuntime`. Maintenance credentials use a separate key and privilege boundary; never substitute them for the runtime account or commit credentials. API startup does not apply migrations or provision the catalog.

The public read routes include `/api/v1/metrics/summary`, `/api/v1/devices`, and device detail/history routes. Simulation and the producer require explicit enablement. AI routing remains disabled; trusted ingress parsing alone does not configure forwarding or complete AI activation. Ordinary reads do not depend on AI availability.

## AI-assisted development

The owner did not personally write the application code. AI coding agents generated the implementation and tests. The owner helped organize the architecture plan and guided the first two development steps to establish the working method.

The owner then established separate AI development and read-only reviewer chats, both following the working method demonstrated in the first two steps. The reviewer checks the development thread's plans and implementation for errors and gaps, requests corrections, and examines test evidence. This is AI-assisted review, not human line-by-line code review.

Work proceeds in small increments with local checks and GitHub Actions verification. The progress log distinguishes implementation, source review, local execution and CI evidence; these checks do not establish hosted production readiness.

## Verification and development

[GitHub Actions](https://github.com/alickqiu1/HealthMonitor/actions/workflows/healthmonitor-ci.yml) builds and tests the backend, provisions disposable PostgreSQL, applies both schema migrations, and verifies required integration cases. Database tests must target the dedicated, verified `healthmonitor_test` database; never point them at development or production Supabase. The current workflow does not verify a completed frontend or a live AI provider.

- [Implementation progress](docs/development/ImplementationProgress.md): current milestone, approved roadmap, executed test evidence and known gaps.
- [Technical Architecture Plan, Edition 1.5](docs/architecture/Enterprise%20Infrastructure%20Health%20Monitor%20Technical%20Architecture%20Plan.md): authoritative requirements, module boundaries, setup and operational procedures.
- [AI provider decision](docs/development/AiProviderDecision.md): selected provider/model, rationale and remaining evaluation limits.

The next visible application increment is the read-only dashboard built on the existing APIs. Remaining AI ingress, credential composition, counter provisioning and activation work stays open; resume it from the progress document rather than treating tested components as an enabled feature.
