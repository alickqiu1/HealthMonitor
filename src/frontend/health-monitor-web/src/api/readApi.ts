/** The canonical V1 health tokens; the browser never reclassifies server health. */
export type DeviceHealth = 'Healthy' | 'Degraded' | 'Offline' | 'Unknown'

/** A read projection, not a persistence entity. Offline rows can retain stale successful measurements. */
export type DeviceRow = Readonly<{
  id: string
  assetTag: string
  displayName: string
  deviceType: string
  siteCode: string
  health: DeviceHealth
  lastObservedAtUtc: string | null
  latencyMs: number | null
  cpuPercent: number | null
}>

/** Supported API filter capabilities, not inventory invariants or values inferred from the current page. */
export const deviceSites = ['EDM', 'CGY', 'DEN'] as const
export const deviceTypes = ['Server', 'Database', 'Gateway', 'SCADA'] as const
export const deviceHealthStates = ['Healthy', 'Degraded', 'Offline', 'Unknown'] as const satisfies readonly DeviceHealth[]

/** Exact V1 sort capabilities; the server owns severity, numeric/null ordering and UUID tie-breakers. */
export const deviceSortKeys = ['displayName', 'assetTag', 'siteCode', 'status', 'lastObservedAtUtc', 'latencyMs', 'cpuPercent'] as const
export type DeviceSort = typeof deviceSortKeys[number]

/** API coordinates are one-based; the table translates its zero-based state at the call site. */
export type DevicePageRequest = Readonly<{ page: number; pageSize: number; q?: string; site?: string; type?: string; status?: DeviceHealth | ''; sort?: DeviceSort; direction?: 'asc' | 'desc' }>

/** One server page with a fleet result count; items are never treated as the entire inventory. */
export type DevicePage = Readonly<{
  items: readonly DeviceRow[]
  page: number
  pageSize: number
  totalCount: number
  asOfUtc: string
}>

/** Flat V1 detail projection; retired registrations remain addressable by explicit UUID. */
export type DeviceDetail = DeviceRow & Readonly<{
  createdAtUtc: string
  retiredAtUtc: string | null
  lastSuccessfulAtUtc: string | null
  asOfUtc: string
}>
/** Persisted observation facts; null measurements remain null rather than becoming zero. */
export type TelemetryObservation = Readonly<{
  id: string; deviceId: string; observedAtUtc: string; outcome: 'Success' | 'Timeout'
  latencyMs: number | null; cpuPercent: number | null
}>
/** Plain-text incident facts; an absent resolution means open, not a missing response field. */
export type DeviceIncident = Readonly<{
  id: string; deviceId: string; kind: 'Latency' | 'Cpu' | 'Connectivity'
  openedAtUtc: string; resolvedAtUtc: string | null; summary: string
}>
/** Fixed first-page preview, not a complete history. Total count belongs to the endpoint's effective scope. */
export type RecentHistory<T> = Readonly<{
  items: readonly T[]; page: 1; pageSize: 10; totalCount: number; asOfUtc: string
}>
type ReadResource = 'summary' | 'devices' | 'details' | 'telemetry' | 'incidents'

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

  /** Shared transport for the owned GET routes; no cache, retry policy or general HTTP framework is added. */
  async function readJson(path: string, signal: AbortSignal, resource: ReadResource): Promise<unknown> {
    signal.throwIfAborted()
    let response: Response
    try {
      response = await fetcher(`${origin}${path}`, { signal, headers: { Accept: 'application/json' }, credentials: 'omit' })
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
      throw new ReadApiError(response.status === 503 ? 'Monitoring data is temporarily unavailable.'
        : response.status === 404 && resource !== 'summary' && resource !== 'devices' ? 'This device is no longer available.'
          : resource === 'summary' ? 'Fleet metrics could not be loaded.'
            : resource === 'devices' ? 'Device registrations could not be loaded.' : 'Device details or history could not be loaded.', response.status, code)
    }
    let body: unknown
    try { body = await response.json() }
    catch {
      signal.throwIfAborted()
      throw invalidPayload(resource)
    }
    signal.throwIfAborted()
    return body
  }

  return {
    origin,
    /**
     * Gets fleet-wide metrics without changing pagination or filters.
     * @param signal Query-owned cancellation, forwarded unchanged and checked before publication.
     * @returns A checked projection preserving nulls and the original server timestamp.
     * @throws ReadApiError for transport/HTTP/shape failures; cancellation preserves the caller's reason.
     */
    async getMetrics(signal: AbortSignal): Promise<MetricsSummary> {
      const body = await readJson('/api/v1/metrics/summary', signal, 'summary')
      if (!isMetrics(body)) throw invalidPayload('summary')
      return body
    },
    /**
     * Gets one globally filtered/sorted page; the server owns ordering and no local row processing occurs.
     * @param request One-based page, capacity 1..100, optional literal name/tag search (100 UTF-16 units after trimming), and canonical AND filters. Empty filter values are neutral; sort defaults to displayName/asc.
     * @param signal Query-owned cancellation; no deadline or retry is added here.
     * @returns A checked page matching the requested coordinates, with stable catalog IDs and explicit nulls.
     * @throws ReadApiError for invalid coordinates/filter/sort input or invalid transport/response data, without echoing raw diagnostics.
     */
    async getDevices(request: DevicePageRequest, signal: AbortSignal): Promise<DevicePage> {
      signal.throwIfAborted()
      if (!request || !Number.isInteger(request.page) || request.page < 1 || request.page > 2_147_483_647
        || !Number.isInteger(request.pageSize) || request.pageSize < 1 || request.pageSize > 100)
        throw new ReadApiError('The device page request is invalid.')
      if (request.q !== undefined && typeof request.q !== 'string') throw new ReadApiError('The device search is invalid.')
      const q = request.q?.trim() ?? ''
      if (q.length > 100) throw new ReadApiError('The device search must contain at most 100 characters after trimming.')
      const sort = request.sort === undefined ? 'displayName' : request.sort; const direction = request.direction === undefined ? 'asc' : request.direction
      if (!(deviceSortKeys as readonly string[]).includes(sort) || !['asc', 'desc'].includes(direction)) throw new ReadApiError('The device sort is invalid.')
      const search = new URLSearchParams({ page: String(request.page), pageSize: String(request.pageSize), sort, direction })
      if (q) search.set('q', q)
      // Validate even typed callers at the public transport boundary; neutral options are omitted, not blank tokens.
      for (const [key, value, supported] of [
        ['site', request.site, deviceSites], ['type', request.type, deviceTypes], ['status', request.status, deviceHealthStates],
      ] as const) {
        if (value === undefined || value === '') continue
        if (typeof value !== 'string' || !(supported as readonly string[]).includes(value)) throw new ReadApiError('The device filter is invalid.')
        search.set(key, value)
      }
      const body = await readJson(`/api/v1/devices?${search}`, signal, 'devices')
      if (!isDevicePage(body, request)) throw invalidPayload('devices')
      return body
    },
    /**
     * Reads one known active or retired device without trusting grid cache rows as detail.
     * @param deviceId Nonempty D-format UUID; equivalent response casing is permitted.
     * @param signal Query-owned cancellation, forwarded and checked before publication.
     * @returns Checked flat detail with original UTC precision; no health or retirement inference.
     * @throws ReadApiError for invalid identity, sanitized HTTP failure, or invalid/mismatched response shape.
     */
    async getDevice(deviceId: string, signal: AbortSignal): Promise<DeviceDetail> {
      signal.throwIfAborted(); requireDeviceId(deviceId)
      const body = await readJson(`/api/v1/devices/${encodeURIComponent(deviceId)}`, signal, 'details')
      if (!isDetail(body, deviceId)) throw invalidPayload('details')
      return body
    },
    /**
     * Reads up to ten newest observations in the server's default trailing 24-hour half-open UTC window.
     * @param deviceId Explicit active or retired device UUID.
     * @param signal Query-owned cancellation; no clock, local filtering, pagination or retry is added.
     * @returns First page and count within that effective window, evaluated at its own asOfUtc.
     * @throws ReadApiError for invalid identity, sanitized HTTP failure, or invalid/mismatched history.
     */
    async getRecentTelemetry(deviceId: string, signal: AbortSignal): Promise<RecentHistory<TelemetryObservation>> {
      signal.throwIfAborted(); requireDeviceId(deviceId)
      const body = await readJson(`/api/v1/devices/${encodeURIComponent(deviceId)}/telemetry?page=1&pageSize=10`, signal, 'telemetry')
      if (!isRecentHistory(body, deviceId, isObservation)) throw invalidPayload('telemetry')
      return body
    },
    /**
     * Reads up to ten newest retained incidents, all lifecycle states; no lifetime completeness is implied.
     * @param deviceId Explicit active or retired device UUID.
     * @param signal Query-owned cancellation, forwarded unchanged.
     * @returns First page and retained-record count, evaluated independently of detail/telemetry reads.
     * @throws ReadApiError for invalid identity, sanitized HTTP failure, or invalid/mismatched history.
     */
    async getRecentIncidents(deviceId: string, signal: AbortSignal): Promise<RecentHistory<DeviceIncident>> {
      signal.throwIfAborted(); requireDeviceId(deviceId)
      const body = await readJson(`/api/v1/devices/${encodeURIComponent(deviceId)}/incidents?page=1&pageSize=10&state=all`, signal, 'incidents')
      if (!isRecentHistory(body, deviceId, isIncident)) throw invalidPayload('incidents')
      return body
    },
  }
}

/** Keeps invalid JSON/shape messages owned and consistent without exposing the rejected response. */
function invalidPayload(resource: ReadResource) {
  const messages = { summary: 'summary', devices: 'device page', details: 'device detail', telemetry: 'telemetry history', incidents: 'incident history' }
  return new ReadApiError(`The monitoring API returned an invalid ${messages[resource]}.`)
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
/** Checks page identity before caching: a response for another page must never be displayed under the requested key. */
function isDevicePage(value: unknown, request: DevicePageRequest): value is DevicePage {
  if (!isRecord(value) || value.page !== request.page || value.pageSize !== request.pageSize
    || !Number.isSafeInteger(value.totalCount) || (value.totalCount as number) < 0 || !isUtcInstant(value.asOfUtc)
    || !Array.isArray(value.items) || value.items.length > request.pageSize || value.items.length > (value.totalCount as number)
    || !value.items.every(isDeviceRow)) return false
  // Stable row identity requires unique UUIDs, including equivalent hexadecimal casing.
  return new Set(value.items.map(row => row.id.toLowerCase())).size === value.items.length
}

/** Validates wire types and measurement units without deriving health or changing server-owned inventory facts. */
function isDeviceRow(value: unknown): value is DeviceRow {
  return isRecord(value) && typeof value.id === 'string' && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value.id)
    && ['assetTag', 'displayName', 'deviceType', 'siteCode'].every(key => typeof value[key] === 'string')
    && typeof value.health === 'string' && ['Healthy', 'Degraded', 'Offline', 'Unknown'].includes(value.health)
    && (value.lastObservedAtUtc === null || isUtcInstant(value.lastObservedAtUtc))
    && (value.latencyMs === null || (Number.isInteger(value.latencyMs) && (value.latencyMs as number) >= 0 && (value.latencyMs as number) <= 2_147_483_647))
    && (value.cpuPercent === null || (typeof value.cpuPercent === 'number' && Number.isFinite(value.cpuPercent) && value.cpuPercent >= 0 && value.cpuPercent <= 100))
}

/** Checks flat V1 extension fields before narrowing the common row projection; retirement remains server-owned. */
function isDetail(value: unknown, deviceId: string): value is DeviceDetail {
  return isRecord(value) && isUtcInstant(value.createdAtUtc) && nullableUtc(value.retiredAtUtc)
    && nullableUtc(value.lastSuccessfulAtUtc) && isUtcInstant(value.asOfUtc) && isDeviceRow(value) && sameUuid(value.id, deviceId)
}

/** New identity-specific reads reject empty UUIDs before HTTP without echoing caller input. */
function requireDeviceId(value: unknown): asserts value is string {
  if (!isUuid(value)) throw new ReadApiError('The device identity is invalid.')
}
function isUuid(value: unknown): value is string {
  return typeof value === 'string' && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value)
    && value !== '00000000-0000-0000-0000-000000000000'
}
/** PostgreSQL UUID identity is case-insensitive; lexical case must not create a false device mismatch. */
function sameUuid(value: unknown, expected: string): boolean {
  return isUuid(value) && value.toLowerCase() === expected.toLowerCase()
}
function nullableUtc(value: unknown): boolean { return value === null || isUtcInstant(value) }
function nullableLatency(value: unknown): boolean { return value === null || (Number.isInteger(value) && (value as number) >= 0 && (value as number) <= 2_147_483_647) }
function nullableCpu(value: unknown): boolean { return value === null || (typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 100) }
/** Validate wire shape and route membership, without recomputing health, thresholds or lifecycle rules. */
function isObservation(value: unknown): value is TelemetryObservation {
  return isRecord(value) && isUuid(value.id) && isUuid(value.deviceId) && isUtcInstant(value.observedAtUtc)
    && (value.outcome === 'Success' || value.outcome === 'Timeout') && nullableLatency(value.latencyMs) && nullableCpu(value.cpuPercent)
}
function isIncident(value: unknown): value is DeviceIncident {
  return isRecord(value) && isUuid(value.id) && isUuid(value.deviceId)
    && (value.kind === 'Latency' || value.kind === 'Cpu' || value.kind === 'Connectivity')
    && isUtcInstant(value.openedAtUtc) && nullableUtc(value.resolvedAtUtc) && typeof value.summary === 'string'
}
/** Both fixed previews share the existing page envelope; uniqueness is by record UUID, never timestamp. */
function isRecentHistory<T extends { id: string; deviceId: string }>(value: unknown, deviceId: string, validItem: (item: unknown) => item is T): value is RecentHistory<T> {
  if (!isRecord(value) || value.page !== 1 || value.pageSize !== 10 || !Number.isSafeInteger(value.totalCount)
    || (value.totalCount as number) < 0 || !isUtcInstant(value.asOfUtc) || !Array.isArray(value.items)
    || value.items.length > 10 || value.items.length > (value.totalCount as number)
    || !value.items.every(item => validItem(item) && sameUuid(item.deviceId, deviceId))) return false
  return new Set(value.items.map(item => item.id.toLowerCase())).size === value.items.length
}
