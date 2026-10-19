# AI Query Provider Decision

Decision date: 2026-10-18. The owner selected the provider and model used by SmartLogApi. This records an implementation target under Technical Architecture Plan Edition 1.5, sections 12, 14, and 18; it does not amend that architecture or authorize production AI enablement.

| Item | Selected target |
| --- | --- |
| Provider | GroqCloud |
| Model identifier | `openai/gpt-oss-20b` |
| Transport | Framework-provided .NET `HttpClient` and `System.Text.Json`; no provider SDK or new transport package |
| Endpoint | `https://api.groq.com/openai/v1/chat/completions` |
| Response format | Nonstreaming JSON Schema with `strict: true`; all proposal fields required and additional properties rejected |
| Reasoning | `reasoning_effort: low`, `include_reasoning: false`; do not send unsupported `reasoning_format` |
| Tools and fallback | No tools or execution path; no alternate provider/model, automatic retries, or repair loop |
| Existing build baseline | `net10.0`; `global.json` requests SDK `10.0.400` with `rollForward: latestPatch`. This permits SDK patch roll-forward; it is not an immutable patch lock. |

## Basis and boundaries

[SmartLogApi's service](https://github.com/alickqiu1/SmartLogApi/blob/master/src/Services/AiLogService.cs) currently selects this endpoint/model and basic `json_object` mode. The inspected public file blob was `421b96e51e71288c97240def8cecfaf106f72320`. HealthMonitor selects the same provider/model, while retaining its own bounded transport and stricter output contract; no SmartLogApi implementation or credentials are copied.

[Groq's structured-output documentation](https://console.groq.com/docs/structured-outputs) lists this exact model as supporting strict schema output. The adapter will still use HealthMonitor's strict proposal parser and Application semantic validator; a structurally valid proposal does not prove that natural-language intent was understood correctly.

[Groq's reasoning documentation](https://console.groq.com/docs/reasoning) supports low reasoning effort and excluding returned reasoning for GPT-OSS. Excluding reasoning from the response does not disable model reasoning or prove that it fits the generation budget. Do not return or log reasoning, prompts, or raw provider payloads.

The identifier is a provider-managed alias, not an immutable weight snapshot. Lock this exact identifier and endpoint in the reviewed implementation; do not silently substitute a model. Reevaluate representative translation cases when the provider changes model behavior or the identifier must change.

## Requirements retained for Step 5 and acceptance

- Preserve the architecture's 400-token generation cap, ten-second provider timeout, bounded response/proposal bytes, linked cancellation, and no-retry policy. Reliability within these limits is unverified; do not raise them silently to accommodate reasoning.
- Implement bounded envelope reading/extraction, strict failure handling, safe logging, and fixed-host transport tests before treating the adapter as implemented. A schema-capable model does not replace those controls.
- Keep production AI disabled until named request limits, concurrency admission, the persistent atomic daily counter, and the other activation requirements are implemented and verified.
- Provider account access and actual quotas have not been inspected. [Groq's published limits](https://console.groq.com/docs/rate-limits) include token as well as request limits; local admission does not guarantee the provider will accept every admitted call. Handle provider throttling safely.
- No API key or secret store was read, no provider call was made, and no live quality, latency, quota, or budget-fit result is claimed. Separately authorized evaluation remains required before live acceptance.

## Roadmap interpretation

Section 18 places contracts, validation, the fake-provider HTTP slice, and provider/model transport selection in Step 4. Real provider integration and security controls belong to Step 5. The earlier progress language treating those Step 5 controls as Step 4 closure prerequisites was too broad; correcting that sequencing does not waive the controls or require an architecture amendment.

At the 2026-10-18 selection checkpoint, this decision completed the preparatory Step 4 prerequisite; Step 5.1 transport, Step 5.2 admission controls, live evaluation and production activation were still planned and unverified. This is historical decision evidence, not their current implementation status. The decision itself changed no runtime configuration, production code or dependency files.

## Installation credentials and provider portability

Clarification date: 2026-10-19. Edition 1.5 sections 2, 6, 12, 14, 15 and 16 remain authoritative. The required user flow is explicit submission, server-validated supported query preview, and Apply through the existing device-grid GET API; no executable SQL or model access to storage. Unsupported or ambiguous intent and provider failures preserve the current grid, and ordinary grid refresh never regenerates a translation.

The owner selected Groq for their installation. Other installations must use their own provider account and backend-stored credentials, never the owner's account or a shared browser key. Section 6 proposes `AiQueries:ApiKey` in API user-secrets or hosting secrets; the browser receives neither credentials nor a provider endpoint. The actual disabled API composition currently reads only `AiQueries:Enabled`, rejects enablement and does not load the provider credential or register a generator.

The owner also requested that other installations be able to configure their own LLM/provider. This is a recorded future requirement, not a claim that arbitrary providers are supported. Keep one selected provider/model per installation; no runtime provider-switching framework or automatic fallback is proposed. `IStructuredQueryGenerator` supplies the existing provider-neutral boundary, while `GroqQueryProtocol` currently fixes both the selected model and HTTPS endpoint and checks response model identity. Changing a key, adding a configuration field or claiming an OpenAI-compatible API does not establish protocol compatibility.

A different provider or model needs a separately reviewed configuration/protocol decision, bounded transport and output validation tests, and a provider-specific adapter where required. Do not expose arbitrary endpoint overrides or weaken host, schema, timeout, cancellation, budget or secret handling to achieve portability. Any conflict with Edition 1.5 or the selected model lock must be identified for explicit approval before implementation; this clarification does not amend the architecture or unlock the current adapter.

Current source status: the Groq transport, strict parsing, Application validation, persistent daily-budget component, admission policy/decorator and controlled HTTP tests exist. Overall Step 5.2 remains open: production composition, credential/configuration validation, counter provisioning, actual ingress/trust integration, provider-side spending control and live evaluation/activation are not complete. The React AI client/panel/preview/Apply/Clear flow is absent. Existing implementation, local execution and CI evidence remain separately recorded in ImplementationProgress.md; no new execution or live-provider proof is supplied by this documentation clarification.

## Owner-authorized portability implementation decision

On 2026-10-19 the owner explicitly authorized starting AI implementation and required preferred-LLM portability, with Groq for their installation. This supersedes the prior future-only portability scope and the original global Groq-only model/endpoint lock; Edition 1.5 now records the bounded amendment. Groq remains the owner's default and its existing request behavior is retained. No provider call or live quality/account-access evaluation was performed for this decision.

| Required profile target | Fixed model / endpoint | Profile-specific request rules |
| --- | --- | --- |
| Groq (default) | `openai/gpt-oss-20b`; `https://api.groq.com/openai/v1/chat/completions` | Strict JSON Schema; `reasoning_effort=low`, `include_reasoning=false`, `max_completion_tokens=400` |
| OpenAI | `gpt-5.4-mini-2026-03-17`; `https://api.openai.com/v1/chat/completions` | Strict JSON Schema; `reasoning_effort=none`, `max_completion_tokens=400`, `store=false`; omit Groq-specific `include_reasoning` |

Both profiles use one nonstreaming choice, separate fixed instructions and user text, no tools, strict bounded local parsing and Application validation. Response model identity must match the selected profile. Refusal, incomplete generation, invalid output and provider failure do not trigger retries, repair, fallback or weaker validation. Documentation-supported capabilities do not prove real interpretation quality or availability to a particular account.

Official capability sources inspected on 2026-10-19: [Groq strict structured outputs](https://console.groq.com/docs/structured-outputs), [OpenAI GPT-5.4 Mini endpoint/features/reasoning/snapshot](https://developers.openai.com/api/docs/models/gpt-5.4-mini), [OpenAI structured-output failure handling](https://developers.openai.com/api/docs/guides/structured-outputs), and [explicit storage opt-out](https://developers.openai.com/api/docs/guides/migrate-to-responses). These inform the proposed implementation contract; no live result is inferred.

Keep configuration small: `AiQueries:Enabled` (false by default), `Provider` (Groq or OpenAI), backend-only `ApiKey`, and the existing architecture key `BudgetCounterPath`. Model and endpoint are fixed by the selected profile rather than redundant configuration knobs; unsupported legacy Model/BaseUrl overrides fail active validation. Existing limit settings may only match the architecture's fixed supported values. Disabled mode reads no provider secret and creates no storage. Counter configuration can verify a fully qualified lexical path outside the web root; ACLs, symlinks, actual ownership and safe provisioning require separate operational checks and are not proved by string validation.

This delivery must implement both profiles before claiming portable provider support. Other LLMs, local HTTP servers, Azure deployment URLs, or different protocols are not automatically supported; add a reviewed compatible profile or adapter behind `IStructuredQueryGenerator` as needed. No new dependency, registry/framework, browser provider selector or automatic fallback is required. Transport, activation/composition, counter/trusted-identity operations and frontend Submit/preview/Apply/Clear will be independently reviewed in small increments. Existing source remains Groq-pinned and activation remains blocked until those increments are completed and verified.
