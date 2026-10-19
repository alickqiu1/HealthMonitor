/** The existing V1 fleet-wide summary. Missing latency is distinct from a measured zero. */
export type MetricsSummary = Readonly<{
  total: number
  healthy: number
  degraded: number
  offline: number
  unknown: number
  averageLatencyMs: number | null
  sampleCount: number
  asOfUtc: string
}>

/** An owned display message; raw ProblemDetails titles, details and HTML are never shown or logged. */
export class ReadApiError extends Error {
  readonly status: number | null
  readonly code: string | null
  constructor(message: string, status: number | null = null, code: string | null = null) {
    super(message)
    this.name = 'ReadApiError'
    this.status = status
    this.code = code
  }
}

/**
 * Creates the read client for one explicit public API origin, without cookies, storage or guessed ports.
 * @param baseUrl Public HTTP(S) origin from configuration; credentials, path prefixes, queries and fragments are rejected.
 * @param fetcher Browser fetch in the application, or a controlled transport for tests.
 * @returns An origin-scoped client; the origin also identifies its Query cache entries.
 * @throws ReadApiError when configuration cannot safely identify a single API origin.
 */
export function createReadApi(baseUrl: string | undefined, fetcher: typeof fetch = globalThis.fetch) {
  let url: URL
  try {
    if (!baseUrl || baseUrl !== baseUrl.trim()) throw new Error()
    url = new URL(baseUrl)
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.pathname !== '/' || url.search || url.hash)
      throw new Error()
  } catch {
    throw new ReadApiError('The monitoring API connection is not configured correctly.')
  }
  const origin = url.origin
  return {
    origin,
    /**
     * Consumes Query's cancellation signal so inactive requests cannot publish a later result.
     * @param signal Query-owned cancellation, forwarded unchanged to fetch and checked before publishing parsed data.
     * @returns The shape-checked summary, preserving explicit nulls and the server UTC timestamp.
     * @throws ReadApiError for transport/HTTP/shape failures; cancellation preserves the caller's reason instead.
     * No retry, request deadline or second cache is introduced at this boundary.
     */
    async getMetrics(signal: AbortSignal): Promise<MetricsSummary> {
      signal.throwIfAborted()
      let response: Response
      try {
        response = await fetcher(`${origin}/api/v1/metrics/summary`, { signal, headers: { Accept: 'application/json' }, credentials: 'omit' })
      } catch {
        signal.throwIfAborted()
        throw new ReadApiError('The monitoring API could not be reached.')
      }
      signal.throwIfAborted()
      if (!response.ok) {
        let code: string | null = null
        if (response.headers.get('content-type')?.split(';')[0].trim().toLowerCase() === 'application/problem+json') {
          try {
            const problem: unknown = await response.json()
            if (isRecord(problem) && typeof problem.code === 'string'
              && ['validation_failed', 'fleet_capacity_exceeded', 'database_unavailable', 'unexpected_error'].includes(problem.code)) code = problem.code
          } catch { signal.throwIfAborted() }
        }
        signal.throwIfAborted()
        throw new ReadApiError(response.status === 503 ? 'Monitoring data is temporarily unavailable.' : 'Fleet metrics could not be loaded.', response.status, code)
      }
      let body: unknown
      try { body = await response.json() }
      catch {
        signal.throwIfAborted()
        throw new ReadApiError('The monitoring API returned an invalid summary.')
      }
      signal.throwIfAborted()
      if (!isMetrics(body)) throw new ReadApiError('The monitoring API returned an invalid summary.')
      return body
    },
  }
}

export type ReadApi = ReturnType<typeof createReadApi>

/** Guards transport shape, not domain rules: the backend remains responsible for fleet calculations. */
function isMetrics(value: unknown): value is MetricsSummary {
  if (!isRecord(value)) return false
  const counts = ['total', 'healthy', 'degraded', 'offline', 'unknown', 'sampleCount']
  return counts.every(key => Number.isSafeInteger(value[key]) && (value[key] as number) >= 0)
    && (value.averageLatencyMs === null || (typeof value.averageLatencyMs === 'number' && Number.isFinite(value.averageLatencyMs) && value.averageLatencyMs >= 0))
    && isUtcInstant(value.asOfUtc)
}

/** Narrows parsed JSON without trusting arrays as named-field responses. */
function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}
/** Requires a real UTC ISO instant; Date.parse alone also accepts ambiguous shorthand and normalizes nonexistent calendar dates. */
function isUtcInstant(value: unknown): value is string {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(value)) return false
  const instant = Date.parse(value)
  // Preserve the original .NET tick precision while checking the calendar fields at JavaScript's millisecond resolution.
  return Number.isFinite(instant) && new Date(instant).toISOString().slice(0, 19) === value.slice(0, 19)
}
