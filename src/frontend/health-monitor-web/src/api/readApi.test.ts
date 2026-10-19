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
