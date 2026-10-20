# Verification closeout

This records the 2026-10-20 verification and presentation checkpoint, not full Edition 1.5 acceptance or a production deployment. The [architecture plan](../architecture/Enterprise%20Infrastructure%20Health%20Monitor%20Technical%20Architecture%20Plan.md) remains authoritative; the [progress log](ImplementationProgress.md) preserves milestone history, scoped approvals and deferred work.

## Public-clone rehearsal

A shallow clone of the public repository at `f70ceb5bff26c57b0ab0f71cc8750dee316967af` was tested on Windows with .NET SDK 10.0.400, Node.js 24.21.0 and npm 11.19.0. No owner secrets, private settings, budget files or frontend environment files were copied. Process configuration used an isolated application-data profile, disabled AI/producer/simulation, cleared known credential overrides and selected public package sources. The frontend used only the checked-in public environment example and an unreachable loopback API override; no backend or model was needed.

| Check actually executed | Result |
| --- | --- |
| Backend unit project restore and Release build | Passed; build: zero warnings/errors. |
| Windows unit suite, `Platform!=Unix` | 1,357 passed, zero failed/not-executed; local TRX inspected. Four Unix-only cases were excluded by the filter, not executed here. |
| Integration project restore and Release build | Passed; build: zero warnings/errors. No PostgreSQL integration tests were run locally in this rehearsal. |
| Database-free integration-result verifier self-tests | 41 passed; synthetic reports do not count as database test evidence. |
| Frontend `npm ci` | Passed using the lockfile; install-time audit reported zero vulnerabilities. This is not a continuing security guarantee. |
| Frontend `npm test` | Eight test files, 285 tests passed. |
| Frontend `npm run lint` / `npm run build` | Both passed; TypeScript and Vite production assets built. |
| API with explicitly empty runtime connection configuration | Rejected `ConnectionStrings:HealthMonitorRuntime` before HTTP startup; nonzero exit and no listening message. Negative configuration validation only, not successful database startup. |

The initial backend build failed when the isolated package cache was placed beneath a deep temporary path: localized Roslyn resource files were missing at a 262-character path. Keeping credential isolation while explicitly selecting the normal nonsecret NuGet package cache resolved the build; no project/dependency change was needed. This rehearsal reused installed tools and package caches and is **not a cold-machine or empty-cache proof**. Raw command logs, step receipts and local unit TRX were retained outside the repository for review; they are not public CI artifacts.

No local PostgreSQL service/client or Docker executable was available. No infrastructure was installed, and empty-database creation, role grants, migrations, catalog provisioning and positive API startup were not rehearsed locally. Existing disposable PostgreSQL CI is separate evidence. The cloned sources remained unchanged. The owner's running demo processes and private AI counter were not modified.

## Evidence boundaries

| Area | Evidence established | Remaining limit |
| --- | --- | --- |
| Backend / PostgreSQL CI | [CI #84](https://github.com/alickqiu1/HealthMonitor/actions/runs/39425286263), source `110bc49fcc4dfe5e3f95d2cbfd2ceb2af29201f1`: backend 51 successful steps, frontend 10; independent scoped closure approved. Both migration streams and strict integration-result checks are included. | Run/job status inspection does not mean every report was opened. Separately inspected CI #83 unit TRX had 1,361 Linux passes and eight real MVC AI host cases; local Windows counts are separate. |
| Frontend | Local 285 tests, lint/build and successful CI #84 job; controlled component tests cover pre-Apply isolation, stale responses, cancellation, manual fallback and details focus logic. | Frontend CI case rows were not inspected. DOM tests do not prove real-browser layout, native keyboard behavior or a deployed journey. |
| Live Groq | One authorized application HTTP submission returned Ready/no-store. The ordinary GET returned two matching Calgary / Server / Offline devices; reservation count increased exactly 0 to 1. | HTTP-only agent execution. No retries, second agent submission, broad accuracy study, live OpenAI test or billing-cancellation guarantee. That single-call authorization is consumed. |
| Browser output | Five owner-provided after-Apply screenshots independently reviewed: Calgary offline servers, Calgary only, all servers, name descending and site descending. The [included AI screenshot](../screenshots/ai-filter-preview.png) is an unaltered owner capture. | No agent-operated browser run. Screenshots do not show pre-Apply grid isolation, request counts, invisible row ordering or keyboard behavior. |
| Manual details | Owner reported populated output/details look correct and Tab navigation works. | Escape, focus restoration, saved polling preference and specified narrow-screen layouts lack complete manual confirmation. |
| Automation / recording | One practical browser-tool initialization attempt failed before a browser session could be established in the automation runtime. A [60–90 second demo guide](../demo/DemoGuide.md) is provided. | No new E2E framework, browser automation proof or video recording was produced. |
| Operations | Bounded local API access to the owner-confirmed dedicated HealthMonitor Supabase target used session access and VerifyFull TLS. | No public deployment, effective least-privilege audit, public-write/DDL denial proof, hosted restart/retention acceptance or fresh-user database bootstrap is claimed. |

## Supported installation boundaries

- PostgreSQL is the only implemented persistence engine. CI uses PostgreSQL 17; that does not automatically verify every host/version. SQLite and SQL Server have no adapters. Use your own dedicated database, private Device/Telemetry schemas and verified runtime/maintenance roles; never reuse the owner's credentials or point tests at a live dataset.
- Keep `ConnectionStrings:HealthMonitorRuntime`, maintenance `ConnectionStrings:HealthMonitor` and `ConnectionStrings:HealthMonitorIntegrationTest` separate. Follow the architecture's migrations and privileges procedure: Device first, Telemetry second; startup does not migrate or provision the catalog. Integration tests require the explicitly validated disposable `healthmonitor_test` database.
- Supabase direct/session access on port 5432 is supported by the configured design; transaction pooling on 6543 is rejected. Verify certificate trust and effective permissions for the actual installation. Configuration validation alone cannot prove those properties.
- AI is optional and disabled by default. Use your own backend provider credentials and privately persisted counter; never put them in Vite settings. Fixed Groq and OpenAI profiles are implemented; only Groq has the bounded live result above. Other models/providers require a reviewed profile/protocol or adapter; there is no arbitrary endpoint or automatic fallback.
- Current AI admission is a single-replica design with local client/global controls and an exclusive counter owner. Hosted proxy topology, filesystem protection, persistence and provider spending controls remain operator responsibilities requiring verification. Ordinary dashboard reads do not depend on AI.

## Release assessment and remaining work

The repository supports a bounded local synthetic demonstration, with source review, automated checks and authentic screenshots. It is ready to review as that checkpoint; it is **not declared fully compliant with every architecture requirement or production-release ready**. No tag, deployment, public release or video was created in this pass.

Before a full release, complete the remaining mandatory gates in the approved roadmap: reproducible fresh-database setup and effective role verification; deployment/TLS/proxy/secrets/counter/restart/retention acceptance; focused real-browser keyboard/responsive checks and deterministic browser journeys. UI simulation/recovery and ordinary read timeout behavior remain unimplemented scope in the roadmap. Deterministic mid-insert provisioning failure injection also remains deferred; pre-insert failures do not prove rollback after partial insertion. Historical labels and approved deferrals remain unchanged.

No feature expansion is bundled into this closeout. A future tag should identify an independently reviewed, CI-verified checkpoint and describe its limits; tagging and publishing require their own explicit instruction.
