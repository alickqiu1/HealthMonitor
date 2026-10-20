// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, expect, it, vi } from 'vitest'
import { AiQueryPanel } from './AiQueryPanel'

const clients: QueryClient[] = []
const ready = { contractVersion: '1', outcome: 'Ready', query: { q: null, site: 'DEN', type: 'Server', status: 'Offline', sort: 'latencyMs', direction: 'desc' }, message: 'Review the proposed filters before applying them.' }
const json = (value: unknown = ready) => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } })
afterEach(() => { cleanup(); clients.splice(0).forEach(client => client.clear()); vi.unstubAllGlobals(); vi.restoreAllMocks() })

/** Uses the actual POST client and mutation without a server, persistent storage or model. */
function mount(fetcher = vi.fn<typeof fetch>().mockImplementation(async () => json())) {
  vi.stubGlobal('fetch', fetcher)
  const client = new QueryClient(); clients.push(client)
  const apply = vi.fn(); const clear = vi.fn()
  const rendered = render(<QueryClientProvider client={client}><AiQueryPanel origin="https://example.test" onApply={apply} onClear={clear} /></QueryClientProvider>)
  const edit = (text: string) => fireEvent.change(screen.getByLabelText('Describe the devices to show'), { target: { value: text } })
  const submit = async () => {
    const before = fetcher.mock.calls.length
    fireEvent.click(screen.getByRole('button', { name: 'Preview filters' }))
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(before + 1))
  }
  return { ...rendered, client, fetcher, apply, clear, edit, submit }
}

/** A transport which deliberately ignores abort, proving that generation ownership is the final guard. */
function deferred() {
  let resolve!: (response: Response) => void; let reject!: (reason: Error) => void
  const promise = new Promise<Response>((ok, bad) => { resolve = ok; reject = bad })
  return { promise, resolve, reject }
}

it('previews all six fields after explicit Submit and applies only on a separate click', async () => {
  const host = mount(); expect(screen.getByText(/Show offline servers in Calgary/)).toBeTruthy(); host.edit('offline DEN servers'); expect(host.fetcher).not.toHaveBeenCalled()
  await host.submit(); await screen.findByRole('heading', { name: 'Proposed filters' })
  for (const field of ['Search', 'Site', 'Device type', 'Health', 'Sort', 'Direction']) expect(screen.getByText(field)).toBeTruthy()
  expect(screen.getByText('Descending')).toBeTruthy(); expect(screen.getByText('Latency')).toBeTruthy()
  expect(screen.getByText(/greater than 250 ms/)).toBeTruthy(); expect(screen.getByText(/older than 90 seconds/)).toBeTruthy()
  expect(host.apply).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('button', { name: 'Apply proposed filters' }))
  expect(host.apply).toHaveBeenCalledExactlyOnceWith(ready.query); expect(host.fetcher).toHaveBeenCalledTimes(1)
})
it('invalidates on editing and ignores late transport completion despite ignored abort', async () => {
  const pending = deferred(); const host = mount(vi.fn<typeof fetch>().mockReturnValue(pending.promise))
  host.edit('old'); await host.submit(); host.edit('new')
  expect(host.fetcher.mock.calls[0][1]!.signal!.aborted).toBe(true)
  await act(async () => { pending.resolve(json()) })
  expect(screen.queryByRole('button', { name: 'Apply proposed filters' })).toBeNull(); expect(host.apply).not.toHaveBeenCalled()
})
it('prevents an older failure/finally from clearing newer pending ownership', async () => {
  const old = deferred(); const newer = deferred()
  const host = mount(vi.fn<typeof fetch>().mockReturnValueOnce(old.promise).mockReturnValueOnce(newer.promise))
  host.edit('old'); await host.submit(); host.edit('new'); await host.submit()
  await act(async () => { old.reject(new Error('private-old-canary')) })
  expect(screen.getByRole('button', { name: 'Translating…' })).toBeTruthy(); expect(screen.queryByText(/private-old-canary/)).toBeNull()
  await act(async () => { newer.resolve(json()) }); expect(screen.getByRole('button', { name: 'Apply proposed filters' })).toBeTruthy()
})
it('replaces a previous preview before a new submission', async () => {
  const host = mount(); host.edit('first'); await host.submit(); await screen.findByText('Proposed filters')
  host.edit('second'); expect(screen.queryByText('Proposed filters')).toBeNull(); await host.submit()
  await screen.findByText('Proposed filters'); expect(host.apply).not.toHaveBeenCalled(); expect(host.fetcher).toHaveBeenCalledTimes(2)
})
it('clears input, preview and pending ownership without another POST', async () => {
  const pending = deferred(); const host = mount(vi.fn<typeof fetch>().mockReturnValue(pending.promise))
  host.edit('devices'); await host.submit(); fireEvent.click(screen.getByRole('button', { name: 'Clear AI filters' }))
  expect(host.clear).toHaveBeenCalledTimes(1); expect((screen.getByLabelText('Describe the devices to show') as HTMLTextAreaElement).value).toBe('')
  await act(async () => { pending.resolve(json()) }); expect(screen.queryByText('Proposed filters')).toBeNull(); expect(host.fetcher).toHaveBeenCalledTimes(1)
})
it('aborts on unmount and ignores later completion', async () => {
  const pending = deferred(); const host = mount(vi.fn<typeof fetch>().mockReturnValue(pending.promise))
  host.edit('devices'); await host.submit(); host.unmount(); expect(host.fetcher.mock.calls[0][1]!.signal!.aborted).toBe(true)
  await act(async () => { pending.resolve(json()) }); expect(host.apply).not.toHaveBeenCalled()
})
it.each([
  ['NeedsClarification', 'Please name Healthy, Degraded, Offline, or Unknown and any desired site or device type.'],
  ['Unsupported', 'Only supported device filter queries can be proposed.'],
])('renders owned %s guidance without Apply', async (outcome, message) => {
  const host = mount(vi.fn<typeof fetch>().mockResolvedValue(json({ contractVersion: '1', outcome, query: null, message })))
  host.edit('devices'); await host.submit(); await screen.findByText(message)
  expect(screen.queryByRole('button', { name: 'Apply proposed filters' })).toBeNull(); expect(host.apply).not.toHaveBeenCalled()
})
it.each([404, 429, 503, 504])('keeps manual fallback available on HTTP %s without raw diagnostics', async status => {
  const host = mount(vi.fn<typeof fetch>().mockResolvedValue(new Response('private-error-canary', { status })))
  host.edit('devices'); await host.submit(); await waitFor(() => expect(screen.getByRole('status').textContent).toMatch(/manual filters/))
  expect(screen.queryByText(/private-error-canary/)).toBeNull(); expect(host.apply).not.toHaveBeenCalled(); expect(host.fetcher).toHaveBeenCalledTimes(1)
})
it('prevents duplicate submission and removes completed mutation variables', async () => {
  const pending = deferred(); const host = mount(vi.fn<typeof fetch>().mockReturnValue(pending.promise))
  host.edit('devices'); await host.submit(); fireEvent.submit(screen.getByRole('button', { name: 'Translating…' }).closest('form')!)
  expect(host.fetcher).toHaveBeenCalledTimes(1)
  await act(async () => { pending.resolve(json()) }); await waitFor(() => expect(host.client.getMutationCache().getAll()).toHaveLength(0))
})


it.each([null, 'Degraded'])('shows threshold guidance only for relevant health proposals (%s)', async status => {
  const host = mount(vi.fn<typeof fetch>().mockResolvedValue(json({ ...ready, query: { ...ready.query, status } })))
  host.edit('devices'); await host.submit(); await screen.findByText('Proposed filters')
  expect(screen.queryByText(/greater than 250 ms/) !== null).toBe(status === 'Degraded')
})
