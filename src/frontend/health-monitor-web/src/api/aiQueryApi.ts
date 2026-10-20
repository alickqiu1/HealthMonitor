import { createReadApi, deviceHealthStates, deviceSites, deviceSortKeys, deviceTypes } from './readApi'
import type { DeviceHealth, DeviceSort } from './readApi'

/** Validated proposal only; applying it still uses the independently validated ordinary device GET. */
export type AiDeviceQuery = Readonly<{ q: string | null; site: string | null; type: string | null; status: DeviceHealth | null; sort: DeviceSort; direction: 'asc' | 'desc' }>
export type AiTranslation = Readonly<{ outcome: 'Ready'; query: AiDeviceQuery }> | Readonly<{ outcome: 'NeedsClarification' | 'Unsupported'; query: null }>

/** Owned UI diagnostics; never retain response bodies, submitted text, credentials or provider messages. */
export class AiQueryError extends Error {
  constructor(message: string) { super(message); this.name = 'AiQueryError' }
}
const invalid = () => new AiQueryError('The AI service returned an invalid response. Use the manual filters or try a new request.')
const messages = {
  Ready: 'Review the proposed filters before applying them.',
  NeedsClarification: 'Please name Healthy, Degraded, Offline, or Unknown and any desired site or device type.',
  Unsupported: 'Only supported device filter queries can be proposed.',
} as const

/**
 * Creates a manual POST client for the same validated API origin as monitoring reads.
 * No prompt enters a URL, GET cache key, log or persistent storage. Cancellation is best effort and cannot refund provider usage.
 */
export function createAiQueryApi(baseUrl: string, fetcher: typeof fetch = globalThis.fetch) {
  const origin = createReadApi(baseUrl).origin
  return {
    origin,
    /** Validates post-trim UTF-16 length and serialized bytes before exactly one non-retrying submission. */
    async translate(prompt: string, signal: AbortSignal): Promise<AiTranslation> {
      signal.throwIfAborted()
      if (typeof prompt !== 'string' || !prompt.trim() || prompt.trim().length > 500)
        throw new AiQueryError('Enter a request containing 1–500 characters after trimming.')
      const body = JSON.stringify({ prompt: prompt.trim() })
      if (new TextEncoder().encode(body).byteLength > 8192) throw new AiQueryError('The AI request is too large.')
      let response: Response
      try {
        response = await fetcher(`${origin}/api/v1/ai/device-queries`, {
          method: 'POST', body, signal, credentials: 'omit', redirect: 'error', cache: 'no-store',
          headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
        })
      } catch {
        signal.throwIfAborted()
        throw new AiQueryError('The AI service could not be reached. You can continue using manual filters.')
      }
      signal.throwIfAborted()
      if (response.status !== 200) {
        // Never render ProblemDetails or provider headers, and never retry a budget-consuming POST automatically.
        void response.body?.cancel().catch(() => {})
        const errors: Record<number, string> = {
          400: 'Please revise your request and submit it again.', 413: 'The AI request is too large.',
          404: 'AI search is unavailable. You can continue using manual filters.',
          429: 'The AI request limit has been reached. Wait before submitting again; manual filters remain available.',
          502: 'The AI service could not produce valid filters. You can use manual filters.',
          503: 'AI search is temporarily unavailable. You can continue using manual filters.',
          504: 'The AI request timed out. You can continue using manual filters.',
        }
        throw new AiQueryError(errors[response.status] ?? 'The AI request could not be completed. You can use manual filters.')
      }
      try {
        if (!/^application\/json(?:\s*;|$)/i.test(response.headers.get('Content-Type') ?? '') || !response.body) {
          await response.body?.cancel(); throw invalid()
        }
        const reader = response.body.getReader()
        const chunks: Uint8Array[] = []; let size = 0
        try {
          for (;;) {
            signal.throwIfAborted()
            const { done, value } = await reader.read()
            signal.throwIfAborted()
            if (done) break
            size += value.byteLength
            if (size > 8192) throw invalid()
            chunks.push(value)
          }
        } catch (error) {
          // A failed read or ignored transport abort must still release the unread stream; cleanup cannot hide its cause.
          try { await reader.cancel() } catch { /* Preserve the original read/cancellation failure. */ }
          throw error
        } finally { reader.releaseLock() }
        const bytes = new Uint8Array(size); let offset = 0
        for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength }
        const result: unknown = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes))
        return validate(result)
      } catch {
        signal.throwIfAborted()
        throw invalid()
      }
    },
  }
}

/** Requires the exact V1 shape and canonical server vocabulary; never coerces unknown values into an applicable query. */
function validate(value: unknown): AiTranslation {
  if (!record(value) || !keys(value, ['contractVersion', 'outcome', 'query', 'message']) || value.contractVersion !== '1'
    || typeof value.outcome !== 'string' || !Object.hasOwn(messages, value.outcome)
    || value.message !== messages[value.outcome as keyof typeof messages]) throw invalid()
  if (value.outcome === 'NeedsClarification' || value.outcome === 'Unsupported') {
    if (value.query !== null) throw invalid()
    return { outcome: value.outcome, query: null }
  }
  if (value.outcome !== 'Ready' || !record(value.query) || !keys(value.query, ['q', 'site', 'type', 'status', 'sort', 'direction'])) throw invalid()
  const q = value.query
  if (!(q.q === null || typeof q.q === 'string' && q.q.length >= 1 && q.q.length <= 100 && q.q === q.q.trim())
    || !nullableToken(q.site, deviceSites) || !nullableToken(q.type, deviceTypes) || !nullableToken(q.status, deviceHealthStates)
    || typeof q.sort !== 'string' || !(deviceSortKeys as readonly string[]).includes(q.sort)
    || q.direction !== 'asc' && q.direction !== 'desc') throw invalid()
  return { outcome: 'Ready', query: { q: q.q as string | null, site: q.site as string | null, type: q.type as string | null,
    status: q.status as DeviceHealth | null, sort: q.sort as DeviceSort, direction: q.direction } }
}
function record(value: unknown): value is Record<string, unknown> { return typeof value === 'object' && value !== null && !Array.isArray(value) }
function keys(value: Record<string, unknown>, expected: string[]) { return Object.keys(value).length === expected.length && expected.every(key => Object.hasOwn(value, key)) }
function nullableToken(value: unknown, allowed: readonly string[]) { return value === null || typeof value === 'string' && allowed.includes(value) }
