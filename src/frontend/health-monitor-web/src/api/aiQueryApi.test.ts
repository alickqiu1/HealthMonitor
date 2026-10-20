import { describe, expect, it, vi } from 'vitest'
import { createAiQueryApi } from './aiQueryApi'

const query = { q: null, site: 'DEN', type: 'Server', status: null, sort: 'displayName', direction: 'asc' }
const ready = { contractVersion: '1', outcome: 'Ready', query, message: 'Review the proposed filters before applying them.' }
const response = (value: unknown) => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } })
const signal = () => new AbortController().signal

describe('explicit AI POST boundary', () => {
  it('submits once to the read origin with trimmed JSON, original signal and no credentials, cache or redirects', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(response(ready)); const token = signal()
    expect(await createAiQueryApi('https://example.test/', fetcher).translate('  devices  ', token)).toEqual({ outcome: 'Ready', query })
    expect(fetcher).toHaveBeenCalledExactlyOnceWith('https://example.test/api/v1/ai/device-queries', {
      method: 'POST', body: JSON.stringify({ prompt: 'devices' }), signal: token, credentials: 'omit', redirect: 'error', cache: 'no-store',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    })
  })
  it.each(['', '   ', 'x'.repeat(501), '😀'.repeat(250) + 'x'])('rejects invalid UTF-16 input before fetching (%s)', async prompt => {
    const fetcher = vi.fn<typeof fetch>(); await expect(createAiQueryApi('https://example.test', fetcher).translate(prompt, signal())).rejects.toThrow('1–500')
    expect(fetcher).not.toHaveBeenCalled()
  })
  it.each(['x'.repeat(500), '😀'.repeat(250), '\ud800'.repeat(500), '\n'.repeat(499) + 'x'])('accepts exact normalized boundaries and bounded serialized bytes (%s)', async prompt => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(response(ready))
    await createAiQueryApi('https://example.test', fetcher).translate(prompt, signal())
    expect(new TextEncoder().encode(fetcher.mock.calls[0][1]!.body as string).byteLength).toBeLessThanOrEqual(8192)
  })
  it('does not submit a precancelled operation', async () => {
    const controller = new AbortController(); controller.abort(); const fetcher = vi.fn<typeof fetch>()
    await expect(createAiQueryApi('https://example.test', fetcher).translate('devices', controller.signal)).rejects.toThrow()
    expect(fetcher).not.toHaveBeenCalled()
  })
  it.each([
    { contractVersion: '1', outcome: 'NeedsClarification', query: null, message: 'Please name Healthy, Degraded, Offline, or Unknown and any desired site or device type.' },
    { contractVersion: '1', outcome: 'Unsupported', query: null, message: 'Only supported device filter queries can be proposed.' },
  ])('accepts the nonapplicable $outcome decision', async value => {
    const api = createAiQueryApi('https://example.test', vi.fn<typeof fetch>().mockResolvedValue(response(value)))
    expect(await api.translate('devices', signal())).toEqual({ outcome: value.outcome, query: null })
  })
  it.each([null, { ...ready, extra: true }, { ...ready, contractVersion: 1 }, { ...ready, message: 'private-provider-canary' },
    { ...ready, query: null }, { ...ready, query: { ...query, site: 'den' } }, { ...ready, query: { ...query, type: 'server' } },
    { ...ready, query: { ...query, status: 'Broken' } }, { ...ready, query: { ...query, sort: 'type' } },
    { ...ready, query: { ...query, direction: 'ASC' } }, { ...ready, query: { ...query, q: ' padded ' } },
    { ...ready, query: { ...query, q: 'x'.repeat(101) } }, { ...ready, query: { ...query, page: 1 } },
    { ...ready, outcome: 'Unsupported' }, { ...ready, outcome: 'toString' }])('rejects malformed or noncanonical proposals without echoing payload (%j)', async value => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(response(value))
    await expect(createAiQueryApi('https://example.test', fetcher).translate('devices', signal())).rejects.toThrow('invalid response')
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
  it.each([400, 404, 413, 429, 500, 502, 503, 504])('maps HTTP %s without reading raw diagnostics or retrying', async status => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response('private-provider-canary', { status }))
    await expect(createAiQueryApi('https://example.test', fetcher).translate('devices', signal())).rejects.not.toThrow('private-provider-canary')
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
  it('bounds actual streamed bytes and cancels overflow even without Content-Length', async () => {
    const cancel = vi.fn(); const body = new ReadableStream<Uint8Array>({ start(controller) { controller.enqueue(new Uint8Array(8193)) }, cancel })
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response(body, { headers: { 'Content-Type': 'application/json' } }))
    await expect(createAiQueryApi('https://example.test', fetcher).translate('devices', signal())).rejects.toThrow('invalid response')
    expect(cancel).toHaveBeenCalledTimes(1)
  })
  it.each([new Response('<html>private-canary</html>'), new Response('{', { headers: { 'Content-Type': 'application/json' } }),
    new Response(new Uint8Array([0xff]), { headers: { 'Content-Type': 'application/json' } })])('rejects media, JSON or UTF-8 failures safely', async result => {
    await expect(createAiQueryApi('https://example.test', vi.fn<typeof fetch>().mockResolvedValue(result)).translate('devices', signal())).rejects.toThrow('invalid response')
  })
  it('owns network errors without echoing transport text', async () => {
    const fetcher = vi.fn<typeof fetch>().mockRejectedValue(new Error('private-provider-canary'))
    await expect(createAiQueryApi('https://example.test', fetcher).translate('devices', signal())).rejects.toThrow('could not be reached')
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
})


it('accepts exactly 8192 response bytes and a normalized 100-character literal search', async () => {
  const payload = JSON.stringify({ ...ready, query: { ...query, q: 'x'.repeat(100) } })
  const body = payload + ' '.repeat(8192 - new TextEncoder().encode(payload).length)
  const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response(body, { headers: { 'Content-Type': 'application/json' } }))
  expect((await createAiQueryApi('https://example.test', fetcher).translate('devices', signal())).query?.q).toBe('x'.repeat(100))
})


it.each(['error', 'abort'])('cancels and releases a reader after %s without echoing failures', async mode => {
  const controller = new AbortController()
  const reader = { read: vi.fn().mockImplementation(async () => {
    if (mode === 'error') throw new Error('private-read-canary')
    controller.abort(); return { done: false, value: new Uint8Array([120]) }
  }), cancel: vi.fn().mockResolvedValue(undefined), releaseLock: vi.fn() }
  const result = { status: 200, headers: new Headers({ 'Content-Type': 'application/json' }), body: { getReader: () => reader } } as unknown as Response
  const api = createAiQueryApi('https://example.test', vi.fn<typeof fetch>().mockResolvedValue(result))
  await expect(api.translate('devices', controller.signal)).rejects.not.toThrow('private-read-canary')
  expect(reader.cancel).toHaveBeenCalledTimes(1); expect(reader.releaseLock).toHaveBeenCalledTimes(1)
})
