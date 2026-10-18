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

This decision completes the selection prerequisite for the reviewed preparatory Step 4 scope. Step 5.1 transport, Step 5.2 admission controls, live evaluation, and production activation remain planned and unverified. Runtime configuration, production code, and dependency files are unchanged by this decision record.
