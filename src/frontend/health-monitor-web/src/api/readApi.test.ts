import { afterEach, describe, expect, it, vi } from 'vitest'
import { createReadApi, ReadApiError } from './readApi'

const summary = { total: 42, healthy: 31, degraded: 6, offline: 3, unknown: 2, averageLatencyMs: 12.5, sampleCount: 31, asOfUtc: '2026-10-18T12:00:00Z' }
afterEach(() => vi.restoreAllMocks())

/** Supplies one JSON response without accessing a server or loading process configuration. */
function json(value: unknown, status = 200, contentType = 'application/json') {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': contentType } })
}

describe('read API boundary', () => {
  it('uses the explicit origin, V1 route and same signal without browser credentials', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(summary))
    const signal = new AbortController().signal
    const api = createReadApi('https://localhost:7080/', fetcher)
    expect(await api.getMetrics(signal)).toEqual(summary)
    expect(fetcher).toHaveBeenCalledExactlyOnceWith('https://localhost:7080/api/v1/metrics/summary', { signal, headers: { Accept: 'application/json' }, credentials: 'omit' })
  })
  it.each([null, 0, 0.125])('preserves null, zero and fractional latency (%s)', async value => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json({ ...summary, averageLatencyMs: value }))
    expect((await createReadApi('https://example.test', fetcher).getMetrics(new AbortController().signal)).averageLatencyMs).toBe(value)
  })
  it.each([undefined, '', ' https://example.test', 'https://example.test/path', 'https://example.test?key=private-canary', 'https://user:private-canary@example.test', 'file:///tmp/data', 'not-an-origin'])('rejects invalid configuration without echoing it (%s)', value => {
    expect(() => createReadApi(value)).toThrow(ReadApiError)
    try { createReadApi(value) } catch (error) { expect((error as Error).message).not.toContain('private-canary') }
  })
  it.each([null, [], {}, { ...summary, total: -1 }, { ...summary, sampleCount: 0.5 }, { ...summary, averageLatencyMs: '12' }, { ...summary, averageLatencyMs: -1 }, { ...summary, asOfUtc: '2026-10-18T12:00:00' }, { ...summary, asOfUtc: 'invalidZ' }, { ...summary, asOfUtc: '1Z' }, { ...summary, asOfUtc: '2026-02-31T12:00:00Z' }])('rejects malformed summary shape %#', async value => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(value))
    await expect(createReadApi('https://example.test', fetcher).getMetrics(new AbortController().signal)).rejects.toThrow('invalid summary')
  })
  it('preserves the server UTC timestamp including .NET tick precision', async () => {
    const value = { ...summary, asOfUtc: '2026-10-18T12:00:00.1234567Z' }
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(value))
    expect((await createReadApi('https://example.test', fetcher).getMetrics(new AbortController().signal)).asOfUtc).toBe(value.asOfUtc)
  })
  it('maps owned ProblemDetails metadata while suppressing arbitrary diagnostics', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json({ code: 'database_unavailable', title: 'private-canary', detail: 'private-canary' }, 503, 'application/problem+json; charset=utf-8'))
    await expect(createReadApi('https://example.test', fetcher).getMetrics(new AbortController().signal)).rejects.toMatchObject({ status: 503, code: 'database_unavailable', message: 'Monitoring data is temporarily unavailable.' })
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
  it.each(['<html>private-canary</html>', '{'])('does not expose error HTML or malformed problem JSON (%s)', async body => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response(body, { status: 500, headers: { 'Content-Type': body === '{' ? 'application/problem+json' : 'text/html' } }))
    await expect(createReadApi('https://example.test', fetcher).getMetrics(new AbortController().signal)).rejects.toMatchObject({ status: 500, code: null, message: 'Fleet metrics could not be loaded.' })
  })
  it('maps network and invalid successful JSON failures without leaking the original error', async () => {
    const fetcher = vi.fn<typeof fetch>().mockRejectedValueOnce(new Error('private-canary')).mockResolvedValueOnce(new Response('<html>private-canary</html>'))
    const api = createReadApi('https://example.test', fetcher)
    await expect(api.getMetrics(new AbortController().signal)).rejects.toThrow('The monitoring API could not be reached.')
    await expect(api.getMetrics(new AbortController().signal)).rejects.toThrow('The monitoring API returned an invalid summary.')
    expect(fetcher).toHaveBeenCalledTimes(2)
  })
  it('makes no request when already cancelled', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const controller = new AbortController(); const reason = new Error('cancelled'); controller.abort(reason)
    await expect(createReadApi('https://example.test', fetcher).getMetrics(controller.signal)).rejects.toBe(reason)
    expect(fetcher).not.toHaveBeenCalled()
  })
  it('does not publish a response completed after caller cancellation', async () => {
    let finish!: (response: Response) => void
    const fetcher = vi.fn<typeof fetch>().mockImplementation(() => new Promise(resolve => { finish = resolve }))
    const controller = new AbortController(); const reason = new Error('cancelled')
    const request = createReadApi('https://example.test', fetcher).getMetrics(controller.signal)
    controller.abort(reason); finish(json(summary))
    await expect(request).rejects.toBe(reason)
  })
})
const device = { id: '00000000-0000-4000-8000-000000000001', assetTag: 'DEV-1', displayName: 'Device 1', deviceType: 'Gateway', siteCode: 'DEN', health: 'Offline', lastObservedAtUtc: '2026-10-18T12:00:00.1234567Z', latencyMs: 0, cpuPercent: 0 }

/** Builds a response in the requested API coordinates, including legitimate out-of-range empty pages. */
function page(page = 1, pageSize = 25, items: unknown[] = [device], totalCount = items.length) {
  return { items, page, pageSize, totalCount, asOfUtc: summary.asOfUtc }
}

describe('device page boundary', () => {
  it('requests one-based coordinates with fixed server ordering and preserves IDs, units and UTC', async () => {
    const body = page(2, 10, [device], 12)
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(body))
    const signal = new AbortController().signal
    expect(await createReadApi('https://example.test', fetcher).getDevices({ page: 2, pageSize: 10 }, signal)).toEqual(body)
    expect(fetcher).toHaveBeenCalledExactlyOnceWith('https://example.test/api/v1/devices?page=2&pageSize=10&sort=displayName&direction=asc', { signal, headers: { Accept: 'application/json' }, credentials: 'omit' })
  })
  it.each([[1, 1], [2_147_483_647, 100]])('accepts the exact page/capacity boundaries (%i,%i)', async (number, size) => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(page(number, size, [], 0)))
    expect((await createReadApi('https://example.test', fetcher).getDevices({ page: number, pageSize: size }, new AbortController().signal)).items).toEqual([])
  })
  it.each([[0, 25], [-1, 25], [1.5, 25], [NaN, 25], [Infinity, 25], [2_147_483_648, 25], [1, 0], [1, 101], [1, 2.5]])('rejects invalid coordinates before HTTP (%s,%s)', async (number, size) => {
    const fetcher = vi.fn<typeof fetch>()
    await expect(createReadApi('https://example.test', fetcher).getDevices({ page: number, pageSize: size }, new AbortController().signal)).rejects.toThrow('device page request is invalid')
    expect(fetcher).not.toHaveBeenCalled()
  })
  it('rejects a missing request without HTTP', async () => {
    const fetcher = vi.fn<typeof fetch>()
    await expect(createReadApi('https://example.test', fetcher).getDevices(null!, new AbortController().signal)).rejects.toBeInstanceOf(ReadApiError)
    expect(fetcher).not.toHaveBeenCalled()
  })
  it.each([null, [], {}, page(2), page(1, 10), { ...page(), totalCount: -1 }, { ...page(), totalCount: 0 }, { ...page(), asOfUtc: '2026-02-31T12:00:00Z' }, page(1, 25, [{ ...device, id: 'not-a-uuid' }]), page(1, 25, [{ ...device, health: 'healthy' }]), page(1, 25, [{ ...device, latencyMs: 0.5 }]), page(1, 25, [{ ...device, cpuPercent: 101 }]), page(1, 25, [{ ...device, lastObservedAtUtc: '2026-10-18T12:00:00' }])])('rejects invalid response shape or cache identity %#', async body => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(body))
    await expect(createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25 }, new AbortController().signal)).rejects.toThrow('invalid device page')
  })
  it('rejects duplicate UUID identity even with different hexadecimal casing', async () => {
    const row = { ...device, id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa' }
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(page(1, 25, [row, { ...row, id: row.id.toUpperCase() }], 2)))
    await expect(createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25 }, new AbortController().signal)).rejects.toThrow('invalid device page')
  })
  it('rejects more rows than the requested capacity', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(page(1, 1, [device, { ...device, id: '00000000-0000-4000-8000-000000000002' }], 2)))
    await expect(createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 1 }, new AbortController().signal)).rejects.toThrow('invalid device page')
  })
  it.each([0, 100, null])('preserves CPU boundary/null and absent measurements (%s)', async cpu => {
    const row = { ...device, cpuPercent: cpu, latencyMs: null, lastObservedAtUtc: null }
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(page(1, 25, [row])))
    expect((await createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25 }, new AbortController().signal)).items[0]).toEqual(row)
  })
  it('uses safe device-specific HTTP diagnostics without exposing the response', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json({ code: 'unexpected_error', title: 'private-canary', detail: 'private-canary' }, 500, 'application/problem+json'))
    await expect(createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25 }, new AbortController().signal)).rejects.toMatchObject({ message: 'Device registrations could not be loaded.', code: 'unexpected_error', status: 500 })
  })
  it('preserves caller cancellation before and after a device response', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(page()))
    const controller = new AbortController(); const reason = new Error('cancelled'); controller.abort(reason)
    await expect(createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25 }, controller.signal)).rejects.toBe(reason)
    expect(fetcher).not.toHaveBeenCalled()
    const next = new AbortController()
    fetcher.mockImplementationOnce(async () => { next.abort(reason); return json(page()) })
    await expect(createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25 }, next.signal)).rejects.toBe(reason)
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
})


describe('literal global device search', () => {
  it.each(['  Edmonton & <tag> + %  ', 'é😀', ' ' + '😀'.repeat(50) + ' '])('trims and safely encodes literal search %s', async q => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(page()))
    await createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25, q }, new AbortController().signal)
    const url = new URL(String(fetcher.mock.calls[0][0]))
    expect(url.searchParams.get('q')).toBe(q.trim())
    expect([...url.searchParams.keys()]).toEqual(['page', 'pageSize', 'sort', 'direction', 'q'])
  })
  it('omits neutral whitespace search from the wire request', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(page()))
    await createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25, q: '  ' }, new AbortController().signal)
    expect(new URL(String(fetcher.mock.calls[0][0])).searchParams.has('q')).toBe(false)
  })
  it.each(['x'.repeat(101), '😀'.repeat(50) + 'x', 42, null])('rejects invalid search before HTTP %#', async q => {
    const fetcher = vi.fn<typeof fetch>()
    await expect(createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25, q: q as string }, new AbortController().signal)).rejects.toBeInstanceOf(ReadApiError)
    expect(fetcher).not.toHaveBeenCalled()
  })
})


describe('canonical server device filters', () => {
  it.each([['site', 'EDM'], ['site', 'CGY'], ['site', 'DEN'], ['type', 'Server'], ['type', 'Database'], ['type', 'Gateway'], ['type', 'SCADA'], ['status', 'Healthy'], ['status', 'Degraded'], ['status', 'Offline'], ['status', 'Unknown']])('accepts supported %s=%s', async (key, value) => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(page()))
    await createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25, [key]: value }, new AbortController().signal)
    expect(new URL(String(fetcher.mock.calls[0][0])).searchParams.get(key)).toBe(value)
  })
  it.each([['site', 'edm'], ['site', 'SEA'], ['type', 'server'], ['type', 'Router'], ['status', 'healthy'], ['status', '0'], ['site', null], ['type', 42]])('rejects noncanonical %s=%s before HTTP', async (key, value) => {
    const fetcher = vi.fn<typeof fetch>()
    await expect(createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25, [key]: value }, new AbortController().signal)).rejects.toBeInstanceOf(ReadApiError)
    expect(fetcher).not.toHaveBeenCalled()
  })
  it('omits all neutral options, preserving the existing unfiltered request', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(page()))
    await createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25, site: '', type: '', status: '' }, new AbortController().signal)
    expect([...new URL(String(fetcher.mock.calls[0][0])).searchParams.keys()]).toEqual(['page', 'pageSize', 'sort', 'direction'])
  })
  it('sends search and all AND filters together without changing sort or coordinates', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(page()))
    await createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25, q: 'EDM & 1', site: 'EDM', type: 'Server', status: 'Unknown' }, new AbortController().signal)
    expect(Object.fromEntries(new URL(String(fetcher.mock.calls[0][0])).searchParams)).toEqual({ page: '1', pageSize: '25', sort: 'displayName', direction: 'asc', q: 'EDM & 1', site: 'EDM', type: 'Server', status: 'Unknown' })
  })
})


describe('allowlisted server device sorting', () => {
  it.each(['displayName', 'assetTag', 'siteCode', 'status', 'lastObservedAtUtc', 'latencyMs', 'cpuPercent'] as const)('passes both directions of %s without processing rows', async sort => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => json(page()))
    const api = createReadApi('https://example.test', fetcher)
    for (const direction of ['asc', 'desc'] as const) {
      await api.getDevices({ page: 1, pageSize: 25, sort, direction }, new AbortController().signal)
      expect(Object.fromEntries(new URL(String(fetcher.mock.calls.at(-1)![0])).searchParams)).toMatchObject({ sort, direction })
    }
  })
  it.each([{ sort: 'deviceType' }, { sort: 'health' }, { sort: 'Name' }, { sort: null }, { direction: 'ASC' }, { direction: '' }, { direction: null }])('rejects unsupported sort input %# before HTTP', async invalid => {
    const fetcher = vi.fn<typeof fetch>()
    await expect(createReadApi('https://example.test', fetcher).getDevices({ page: 1, pageSize: 25, ...invalid } as never, new AbortController().signal)).rejects.toBeInstanceOf(ReadApiError)
    expect(fetcher).not.toHaveBeenCalled()
  })
})


const detailIdentity = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee'
/** V1 wire facts are fictional and independent of any running API/database. */
function detailBody() {
  return { ...device, id: detailIdentity, createdAtUtc: '2026-10-17T12:00:00Z', retiredAtUtc: null, lastSuccessfulAtUtc: null, asOfUtc: summary.asOfUtc }
}
function observationBody() {
  return { id: 'bbbbbbbb-bbbb-4ccc-8ddd-eeeeeeeeeeee', deviceId: detailIdentity, observedAtUtc: summary.asOfUtc, outcome: 'Success', latencyMs: 0, cpuPercent: 0 }
}
function incidentBody() {
  return { id: 'cccccccc-bbbb-4ccc-8ddd-eeeeeeeeeeee', deviceId: detailIdentity, kind: 'Cpu', openedAtUtc: summary.asOfUtc, resolvedAtUtc: null, summary: 'Synthetic CPU condition.' }
}
/** History totals describe the server scope; one preview never implies a complete lifetime history. */
function recentBody(items: unknown[] = [], totalCount = items.length) { return { items, page: 1, pageSize: 10, totalCount, asOfUtc: summary.asOfUtc } }
const detailReads = ['getDevice', 'getRecentTelemetry', 'getRecentIncidents'] as const

describe('UUID-specific detail and recent-history contracts', () => {
  it.each(detailReads)('requests the exact owned %s route and preserves caller cancellation identity', async method => {
    const body = method === 'getDevice' ? detailBody() : recentBody(method === 'getRecentTelemetry' ? [observationBody()] : [incidentBody()])
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(body)); const signal = new AbortController().signal
    expect(await createReadApi('https://example.test', fetcher)[method](detailIdentity.toUpperCase(), signal)).toEqual(body)
    const suffix = method === 'getDevice' ? '' : method === 'getRecentTelemetry' ? '/telemetry?page=1&pageSize=10' : '/incidents?page=1&pageSize=10&state=all'
    expect(fetcher).toHaveBeenCalledExactlyOnceWith('https://example.test/api/v1/devices/' + detailIdentity.toUpperCase() + suffix, { signal, headers: { Accept: 'application/json' }, credentials: 'omit' })
  })
  it.each(detailReads)('rejects malformed/empty identities without HTTP for %s', async method => {
    const fetcher = vi.fn<typeof fetch>(); const api = createReadApi('https://example.test', fetcher)
    for (const identity of ['', 'private-invalid-id', '../devices', '00000000-0000-0000-0000-000000000000', null, 42]) {
      await expect(api[method](identity as string, new AbortController().signal)).rejects.toMatchObject({ message: 'The device identity is invalid.' })
    }
    expect(fetcher).not.toHaveBeenCalled()
  })
  it('preserves retired registration, unknown health, null measurements and original UTC tick precision', async () => {
    const body = { ...detailBody(), health: 'Unknown', latencyMs: null, cpuPercent: null, lastObservedAtUtc: null, retiredAtUtc: summary.asOfUtc, lastSuccessfulAtUtc: '2026-10-18T11:00:00.1234567Z' }
    expect(await createReadApi('https://example.test', vi.fn<typeof fetch>().mockResolvedValue(json(body))).getDevice(detailIdentity, new AbortController().signal)).toEqual(body)
  })
  it.each([null, {}, { ...detailBody(), id: device.id }, { ...detailBody(), createdAtUtc: null }, { ...detailBody(), retiredAtUtc: undefined }, { ...detailBody(), lastSuccessfulAtUtc: undefined }, { ...detailBody(), asOfUtc: '2026-02-31T12:00:00Z' }])('rejects wrong-device or incomplete detail %#', async body => {
    await expect(createReadApi('https://example.test', vi.fn<typeof fetch>().mockResolvedValue(json(body))).getDevice(detailIdentity, new AbortController().signal)).rejects.toThrow('invalid device detail')
  })
  it.each(['getRecentTelemetry', 'getRecentIncidents'] as const)('accepts empty and exact-ten previews with true larger totals for %s', async method => {
    const item = method === 'getRecentTelemetry' ? observationBody() : incidentBody()
    const items = Array.from({ length: 10 }, (_, index) => ({ ...item, id: `00000000-0000-4000-8000-${String(index + 1).padStart(12, '0')}` }))
    const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(json(recentBody())).mockResolvedValueOnce(json(recentBody(items, 31)))
    const api = createReadApi('https://example.test', fetcher)
    expect((await api[method](detailIdentity, new AbortController().signal)).items).toEqual([])
    expect(await api[method](detailIdentity, new AbortController().signal)).toEqual(recentBody(items, 31))
  })
  it.each(['getRecentTelemetry', 'getRecentIncidents'] as const)('validates page identity, limits, device membership and event UUID uniqueness for %s', async method => {
    const item = method === 'getRecentTelemetry' ? observationBody() : incidentBody()
    const body = recentBody([item])
    const invalid = [null, {}, { ...body, page: 2 }, { ...body, pageSize: 25 }, { ...body, totalCount: -1 }, { ...body, totalCount: 0 }, { ...body, asOfUtc: 'not-UTC' }, { ...body, items: Array(11).fill(item), totalCount: 11 }, recentBody([{ ...item, deviceId: device.id }]), recentBody([{ ...item, id: 'not-an-id' }]), recentBody([item, { ...item, id: item.id.toUpperCase() }])]
    for (const value of invalid) await expect(createReadApi('https://example.test', vi.fn<typeof fetch>().mockResolvedValue(json(value)))[method](detailIdentity, new AbortController().signal)).rejects.toThrow('invalid')
    // Two distinct events may share a timestamp. Uniqueness must not drop one of them.
    const valid = recentBody([item, { ...item, id: 'dddddddd-bbbb-4ccc-8ddd-eeeeeeeeeeee', deviceId: detailIdentity.toUpperCase() }])
    expect(await createReadApi('https://example.test', vi.fn<typeof fetch>().mockResolvedValue(json(valid)))[method](detailIdentity, new AbortController().signal)).toEqual(valid)
  })
  it.each([{ outcome: 'Timeout', latencyMs: null, cpuPercent: null }, { outcome: 'Success', latencyMs: 2_147_483_647, cpuPercent: 100 }])('preserves legitimate observation measurements %#', async fields => {
    const body = recentBody([{ ...observationBody(), ...fields }])
    expect(await createReadApi('https://example.test', vi.fn<typeof fetch>().mockResolvedValue(json(body))).getRecentTelemetry(detailIdentity, new AbortController().signal)).toEqual(body)
  })
  it.each([{ outcome: 'success' }, { observedAtUtc: null }, { latencyMs: -1 }, { latencyMs: 1.5 }, { latencyMs: 2_147_483_648 }, { cpuPercent: 101 }, { cpuPercent: undefined }])('rejects invalid observation wire fields %#', async fields => {
    const body = recentBody([{ ...observationBody(), ...fields }])
    await expect(createReadApi('https://example.test', vi.fn<typeof fetch>().mockResolvedValue(json(body))).getRecentTelemetry(detailIdentity, new AbortController().signal)).rejects.toThrow('invalid telemetry history')
  })
  it.each(['Latency', 'Cpu', 'Connectivity'])('preserves plain-text %s incident summary and nullable/resolved lifecycle fields', async kind => {
    const body = recentBody([{ ...incidentBody(), kind, resolvedAtUtc: summary.asOfUtc, summary: '<script>not executed</script>' }])
    expect(await createReadApi('https://example.test', vi.fn<typeof fetch>().mockResolvedValue(json(body))).getRecentIncidents(detailIdentity, new AbortController().signal)).toEqual(body)
  })
  it.each([{ kind: 'CPU' }, { openedAtUtc: '2026-10-18' }, { resolvedAtUtc: undefined }, { summary: null }])('rejects invalid incident wire fields %#', async fields => {
    await expect(createReadApi('https://example.test', vi.fn<typeof fetch>().mockResolvedValue(json(recentBody([{ ...incidentBody(), ...fields }])))).getRecentIncidents(detailIdentity, new AbortController().signal)).rejects.toThrow('invalid incident history')
  })
  it.each(detailReads)('sanitizes missing/unavailable/error responses for %s without raw details', async method => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(json({ title: 'private-canary', detail: '<html>secret</html>' }, 404, 'application/problem+json')).mockResolvedValueOnce(json({}, 503)).mockResolvedValueOnce(new Response('<html>private-canary</html>', { status: 500 }))
    const api = createReadApi('https://example.test', fetcher)
    await expect(api[method](detailIdentity, new AbortController().signal)).rejects.toMatchObject({ status: 404, message: 'This device is no longer available.' })
    await expect(api[method](detailIdentity, new AbortController().signal)).rejects.toMatchObject({ status: 503, message: 'Monitoring data is temporarily unavailable.' })
    await expect(api[method](detailIdentity, new AbortController().signal)).rejects.toMatchObject({ status: 500, message: 'Device details or history could not be loaded.' })
    expect(fetcher).toHaveBeenCalledTimes(3)
  })
  it.each(detailReads)('preserves pre-start and pending-body cancellation for %s', async method => {
    const reason = new Error('owned cancellation'); const before = new AbortController(); before.abort(reason)
    const fetcher = vi.fn<typeof fetch>(); const api = createReadApi('https://example.test', fetcher)
    await expect(api[method](detailIdentity, before.signal)).rejects.toBe(reason)
    expect(fetcher).not.toHaveBeenCalled()
    const controller = new AbortController()
    const body = method === 'getDevice' ? detailBody() : recentBody()
    fetcher.mockResolvedValue({ ok: true, json: async () => { controller.abort(reason); return body } } as Response)
    await expect(api[method](detailIdentity, controller.signal)).rejects.toBe(reason)
  })
})
