// @vitest-environment jsdom
import { useState } from 'react'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider, focusManager, onlineManager } from '@tanstack/react-query'
import type { PaginationState } from '@tanstack/react-table'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createReadApi } from '../../api/readApi'
import { DeviceDataGrid } from './DeviceDataGrid'
import type { DeviceGridState } from './DeviceDataGrid'
import App from '../../App'
import { useDashboardPreferences } from './dashboardPreferences'

const clients: QueryClient[] = []
beforeEach(() => focusManager.setFocused(true))
afterEach(() => {
  cleanup(); clients.splice(0).forEach(client => client.clear())
  useDashboardPreferences.setState(useDashboardPreferences.getInitialState(), true)
  focusManager.setFocused(undefined); onlineManager.setOnline(true); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllEnvs(); vi.unstubAllGlobals()
})

/** Supplies only explicit fictional DTO data, never a database or real service response. */
function row(number: number) {
  return { id: `00000000-0000-4000-8000-${String(number).padStart(12, '0')}`, assetTag: `DEV-${number}`, displayName: `Device ${number}`, deviceType: 'Gateway', siteCode: 'DEN', health: 'Offline', lastObservedAtUtc: '2026-10-18T12:00:00Z', latencyMs: 0, cpuPercent: 0 }
}
/** Returns a fresh single-use JSON body for one server page, with one distinguishable row. */
function pageResponse(page: number, pageSize = 25, totalCount = 60, items: unknown[] = [row(page)]) {
  return new Response(JSON.stringify({ page, pageSize, totalCount, items, asOfUtc: '2026-10-18T12:00:00Z' }), { headers: { 'Content-Type': 'application/json' } })
}
/** Models the dashboard's one applied-query owner without storing response rows in React state. */
function Harness({ fetcher, initial }: { fetcher: typeof fetch; initial: PaginationState }) {
  const [state, setState] = useState<DeviceGridState>({ pagination: initial, q: '', site: '', type: '', status: '' })
  return <DeviceDataGrid api={createReadApi('https://example.test', fetcher)} state={state} onStateChange={setState} />
}
/** Each case gets an isolated real Query cache; a zero GC delay exposes missing observer ownership. */
function mount(fetcher: typeof fetch, initial = { pageIndex: 0, pageSize: 25 }) {
  const client = new QueryClient({ defaultOptions: { queries: { gcTime: 0 } } }); clients.push(client)
  return { ...render(<QueryClientProvider client={client}><Harness fetcher={fetcher} initial={initial} /></QueryClientProvider>), client }
}
/** Reads only the public request URL so assertions cover actual wire coordinates rather than table implementation details. */
function requestedPages(fetcher: ReturnType<typeof vi.fn<typeof fetch>>) {
  return fetcher.mock.calls.map(([url]) => Number(new URL(String(url)).searchParams.get('page')))
}

it('renders semantic server rows, stable inventory identity, zero measurements and UTC without deriving health', async () => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValue(pageResponse(1))
  mount(fetcher)
  const table = await screen.findByRole('table', { name: 'Active device registrations' })
  expect(within(table).getByText('Device 1')).toBeTruthy(); expect(within(table).getByText('DEV-1')).toBeTruthy()
  expect(within(table).getByText('Offline')).toBeTruthy(); expect(within(table).getByText('0 ms')).toBeTruthy(); expect(within(table).getByText('0%')).toBeTruthy()
  expect(within(table).getByRole('columnheader', { name: 'Name' }).getAttribute('aria-sort')).toBe('ascending')
  expect(screen.getByRole('region', { name: /Device table/ }).tabIndex).toBe(0)
  expect(screen.getByText(/60 matching active devices/)).toBeTruthy()
  expect(screen.getByText(/Offline rows can retain older/)).toBeTruthy()
  expect(fetcher).toHaveBeenCalledTimes(1)
})

it('distinguishes absent measurements and observations from measured zero', async () => {
  mount(vi.fn<typeof fetch>().mockResolvedValue(pageResponse(1, 25, 1, [{ ...row(1), health: 'Unknown', latencyMs: null, cpuPercent: null, lastObservedAtUtc: null }])))
  const table = await screen.findByRole('table')
  expect(within(table).getAllByText('—')).toHaveLength(2)
  expect(within(table).getByText('Not observed')).toBeTruthy(); expect(within(table).getByText('Unknown')).toBeTruthy()
  expect(within(table).queryByText('0 ms')).toBeNull()
})

it('paginates at the server and can return to the fresh cached page without a second observer fetch', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => pageResponse(Number(new URL(String(input)).searchParams.get('page'))))
  mount(fetcher); await screen.findByText('Device 1')
  expect((screen.getByRole('button', { name: 'Previous page' }) as HTMLButtonElement).disabled).toBe(true)
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Device 2')
  expect(screen.queryByText('Device 1')).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Previous page' })); await screen.findByText('Device 1')
  expect(requestedPages(fetcher)).toEqual([1, 2])
})

it('keeps the previous cache entry through a failed page change and succeeds on explicit retry', async () => {
  let fail!: (error: Error) => void
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(pageResponse(1))
    .mockImplementationOnce(() => new Promise((_, reject) => { fail = reject })).mockResolvedValueOnce(pageResponse(2))
  mount(fetcher); await screen.findByText('Device 1')
  const oldFetch = screen.getByText(/Device fetch/).querySelector('time')?.getAttribute('datetime')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' }))
  expect(await screen.findByText(/Showing previous page 1/)).toBeTruthy()
  expect((screen.getByRole('button', { name: 'Next page' }) as HTMLButtonElement).disabled).toBe(true)
  expect(screen.getByText('Device 1')).toBeTruthy()
  await act(async () => fail(new Error('private-canary')))
  await screen.findByRole('alert')
  expect(screen.getByText(/Requested page 2 .*could not be loaded/)).toBeTruthy()
  expect(screen.getByText('Device 1')).toBeTruthy(); expect(screen.queryByText(/private-canary/)).toBeNull()
  expect(screen.getByText('Page 1 of 3')).toBeTruthy()
  expect(screen.getByText(/Device fetch/).querySelector('time')?.getAttribute('datetime')).toBe(oldFetch)
  fireEvent.click(screen.getByRole('button', { name: 'Retry devices' }))
  await screen.findByText('Device 2'); expect(screen.queryByText('Device 1')).toBeNull()
  expect(screen.queryByRole('alert')).toBeNull(); expect(requestedPages(fetcher)).toEqual([1, 2, 2])
})

it('can recover to the previous cached page after a page-change failure', async () => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(pageResponse(1)).mockRejectedValueOnce(new Error('failed'))
  mount(fetcher); await screen.findByText('Device 1')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByRole('alert')
  fireEvent.click(screen.getByRole('button', { name: 'Previous page' }))
  await waitFor(() => expect(screen.queryByRole('alert')).toBeNull())
  expect(screen.getByText('Device 1')).toBeTruthy(); expect(requestedPages(fetcher)).toEqual([1, 2])
})

it('resets to page one when capacity changes and never labels previous rows as the new page size', async () => {
  let finish!: (response: Response) => void
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(pageResponse(1)).mockResolvedValueOnce(pageResponse(2))
    .mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
  mount(fetcher); await screen.findByText('Device 1')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Device 2')
  fireEvent.change(screen.getByRole('combobox', { name: 'Devices per page' }), { target: { value: '50' } })
  expect(await screen.findByText(/Showing previous page 2 \(25 per page\).*Requested page 1 \(50 per page\)/)).toBeTruthy()
  expect((screen.getByRole('combobox', { name: 'Devices per page' }) as HTMLSelectElement).disabled).toBe(true)
  expect(screen.getByText('Page 2 of 3')).toBeTruthy()
  expect(screen.queryByText('Page 2 of 2')).toBeNull()
  await act(async () => finish(pageResponse(1, 50)))
  await screen.findByText('Device 1')
  expect(requestedPages(fetcher)).toEqual([1, 2, 1])
  expect(new URL(String(fetcher.mock.calls[2][0])).searchParams.get('pageSize')).toBe('50')
  expect(screen.getByText('Page 1 of 2')).toBeTruthy()
})

it('corrects an out-of-range empty response to the last valid page exactly once', async () => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(pageResponse(4, 25, 30, [])).mockResolvedValueOnce(pageResponse(2, 25, 30))
  mount(fetcher, { pageIndex: 3, pageSize: 25 })
  await screen.findByText('Device 2')
  expect(requestedPages(fetcher)).toEqual([4, 2])
  expect(screen.getByText('Page 2 of 2')).toBeTruthy(); expect(screen.queryByText('No active devices.')).toBeNull()
})

it('returns a zero-row fleet to page zero without a correction loop', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => pageResponse(Number(new URL(String(input)).searchParams.get('page')), 25, 0, []))
  mount(fetcher, { pageIndex: 2, pageSize: 25 })
  await screen.findByText('No active devices.')
  await waitFor(() => expect(screen.getByText('Page 1 of 1')).toBeTruthy())
  expect(requestedPages(fetcher)).toEqual([3, 1])
  expect((screen.getByRole('button', { name: 'Next page' }) as HTMLButtonElement).disabled).toBe(true)
})

it('retains the current page and its fetch timestamp after a failed background refresh', async () => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(pageResponse(1)).mockResolvedValueOnce(new Response('private-canary', { status: 500 }))
  mount(fetcher); await screen.findByText('Device 1')
  const oldFetch = screen.getByText(/Device fetch/).querySelector('time')?.getAttribute('datetime')
  fireEvent.click(screen.getByRole('button', { name: 'Refresh devices' })); await screen.findByRole('alert')
  expect(screen.getByText('Device 1')).toBeTruthy(); expect(screen.queryByText(/Showing previous page/)).toBeNull()
  expect(screen.getByText(/Displayed rows may be stale/)).toBeTruthy(); expect(screen.queryByText(/private-canary/)).toBeNull()
  expect(screen.getByText(/Device fetch/).querySelector('time')?.getAttribute('datetime')).toBe(oldFetch)
})

it('has a separate initial error state and retry does not silently count the fleet as empty', async () => {
  const fetcher = vi.fn<typeof fetch>().mockRejectedValueOnce(new Error('failed')).mockResolvedValueOnce(pageResponse(1))
  mount(fetcher); await screen.findByRole('alert')
  expect(screen.queryByText('No active devices.')).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Retry devices' })); await screen.findByText('Device 1')
  expect(fetcher).toHaveBeenCalledTimes(2)
})

it('cancels a pending forward page when navigating back to the displayed cached page', async () => {
  let signal: AbortSignal | undefined
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(pageResponse(1)).mockImplementationOnce((_, init) => {
    signal = init?.signal as AbortSignal
    return new Promise((_, reject) => signal!.addEventListener('abort', () => reject(signal!.reason), { once: true }))
  })
  mount(fetcher); await screen.findByText('Device 1')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText(/Showing previous page 1/)
  fireEvent.click(screen.getByRole('button', { name: 'Previous page' }))
  await waitFor(() => expect(signal?.aborted).toBe(true))
  expect(screen.getByText('Device 1')).toBeTruthy(); expect(fetcher).toHaveBeenCalledTimes(2)
})

it('cancels the pending query on unmount without an extra request', async () => {
  let signal: AbortSignal | undefined
  const fetcher = vi.fn<typeof fetch>().mockImplementation((_, init) => {
    signal = init?.signal as AbortSignal
    return new Promise((_, reject) => signal!.addEventListener('abort', () => reject(signal!.reason), { once: true }))
  })
  const view = mount(fetcher); await screen.findByText('Loading devices…')
  view.unmount(); expect(signal?.aborted).toBe(true); expect(fetcher).toHaveBeenCalledTimes(1)
})

it('polls only the current page; the disabled prior-page observer never starts its own fetch', async () => {
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] })
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => pageResponse(Number(new URL(String(input)).searchParams.get('page'))))
  mount(fetcher); await screen.findByText('Device 1')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Device 2')
  await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
  await waitFor(() => expect(requestedPages(fetcher)).toEqual([1, 2, 2]))
})

it('does not refetch global metrics when the dashboard changes the device page', async () => {
  vi.stubEnv('VITE_API_BASE_URL', 'https://example.test')
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => {
    const url = new URL(String(input))
    if (url.pathname.endsWith('/metrics/summary')) return new Response(JSON.stringify({ total: 42, healthy: 31, degraded: 6, offline: 3, unknown: 2, averageLatencyMs: 12.5, sampleCount: 31, asOfUtc: '2026-10-18T12:00:00Z' }), { headers: { 'Content-Type': 'application/json' } })
    return pageResponse(Number(url.searchParams.get('page')), 25, 42)
  })
  vi.stubGlobal('fetch', fetcher)
  const client = new QueryClient(); clients.push(client)
  render(<QueryClientProvider client={client}><App /></QueryClientProvider>)
  await screen.findByText('42'); await screen.findByText('Device 1')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Device 2')
  expect(screen.getByText('42')).toBeTruthy()
  expect(fetcher.mock.calls.filter(([input]) => String(input).endsWith('/metrics/summary'))).toHaveLength(1)
})

it('shows the server total as three pages for 24 devices at ten per page, including the final page', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => pageResponse(Number(new URL(String(input)).searchParams.get('page')), 10, 24))
  mount(fetcher, { pageIndex: 0, pageSize: 10 })
  await screen.findByText('Page 1 of 3')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' }))
  await screen.findByText('Page 2 of 3')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' }))
  await screen.findByText('Page 3 of 3')
  expect((screen.getByRole('button', { name: 'Next page' }) as HTMLButtonElement).disabled).toBe(true)
  expect((screen.getByRole('button', { name: 'Previous page' }) as HTMLButtonElement).disabled).toBe(false)
  expect(requestedPages(fetcher)).toEqual([1, 2, 3])
})

it('does not invent a page total before the first response and uses page one of one for an empty result', async () => {
  let finish!: (response: Response) => void
  const fetcher = vi.fn<typeof fetch>().mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
  mount(fetcher)
  expect(screen.getByText('Requested page 1')).toBeTruthy()
  expect(screen.queryByText(/^Page \d+ of \d+$/)).toBeNull()
  await act(async () => finish(pageResponse(1, 25, 0, [])))
  await screen.findByText('Page 1 of 1')
  expect((screen.getByRole('button', { name: 'Next page' }) as HTMLButtonElement).disabled).toBe(true)
  expect((screen.getByRole('button', { name: 'Previous page' }) as HTMLButtonElement).disabled).toBe(true)
})

it('supports ordinary cache invalidation without refetching the preserved inactive page', async () => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(pageResponse(1))
    .mockResolvedValueOnce(pageResponse(1, 25, 60, [{ ...row(1), displayName: 'Refreshed device' }]))
    .mockResolvedValueOnce(pageResponse(2))
    .mockResolvedValueOnce(pageResponse(2, 25, 60, [{ ...row(2), displayName: 'Refreshed second page' }]))
  const view = mount(fetcher)
  await screen.findByText('Device 1')
  await act(async () => { await view.client.invalidateQueries({ queryKey: ['devices'] }) })
  await screen.findByText('Refreshed device')
  expect(fetcher).toHaveBeenCalledTimes(2)
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Device 2')
  await act(async () => { await view.client.invalidateQueries({ queryKey: ['devices'] }) })
  await screen.findByText('Refreshed second page')
  expect(requestedPages(fetcher)).toEqual([1, 1, 2, 2])
})
/** Supplies both owned GET responses for shared-policy tests; every body is fresh and no service is contacted. */
function dashboardResponse(input: RequestInfo | URL) {
  const url = new URL(String(input))
  if (url.pathname.endsWith('/metrics/summary')) return new Response(JSON.stringify({ total: 42, healthy: 31, degraded: 6, offline: 3, unknown: 2, averageLatencyMs: 12.5, sampleCount: 31, asOfUtc: '2026-10-18T12:00:00Z' }), { headers: { 'Content-Type': 'application/json' } })
  return pageResponse(Number(url.searchParams.get('page')), Number(url.searchParams.get('pageSize')), 42)
}

it('pauses both panels across interval, focus and reconnect while manual actions and resume still work', async () => {
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] })
  vi.stubEnv('VITE_API_BASE_URL', 'https://example.test')
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => dashboardResponse(input))
  vi.stubGlobal('fetch', fetcher)
  const client = new QueryClient(); clients.push(client)
  render(<QueryClientProvider client={client}><App /></QueryClientProvider>)
  await screen.findByText('42'); await screen.findByText('Device 1')
  const counts = () => ({ metrics: fetcher.mock.calls.filter(([input]) => String(input).includes('/metrics/summary')).length, devices: fetcher.mock.calls.filter(([input]) => String(input).includes('/devices?')).length })
  expect(counts()).toEqual({ metrics: 1, devices: 1 })
  fireEvent.click(screen.getByRole('checkbox', { name: 'Automatic updates' }))
  expect(screen.getByText('Paused')).toBeTruthy()
  await act(async () => {
    await vi.advanceTimersByTimeAsync(15_000)
    focusManager.setFocused(false); focusManager.setFocused(true)
    onlineManager.setOnline(false); onlineManager.setOnline(true)
  })
  expect(counts()).toEqual({ metrics: 1, devices: 1 })
  expect(screen.getByText('42')).toBeTruthy(); expect(screen.getByText('Device 1')).toBeTruthy()
  fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
  await waitFor(() => expect(counts()).toEqual({ metrics: 2, devices: 1 }))
  fireEvent.click(screen.getByRole('button', { name: 'Refresh devices' }))
  await waitFor(() => expect(counts()).toEqual({ metrics: 2, devices: 2 }))
  await waitFor(() => expect((screen.getByRole('button', { name: 'Next page' }) as HTMLButtonElement).disabled).toBe(false))
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Device 2')
  expect(counts()).toEqual({ metrics: 2, devices: 3 })
  await act(async () => { await vi.advanceTimersByTimeAsync(10_000) })
  expect(counts()).toEqual({ metrics: 2, devices: 3 })
  fireEvent.click(screen.getByRole('checkbox', { name: 'Automatic updates' }))
  await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
  await waitFor(() => expect(counts()).toEqual({ metrics: 3, devices: 4 }))
  expect(screen.getByText('Every 5 seconds')).toBeTruthy()
})

it.each([false, true])('loads while already paused or after pausing a running initial request (pause after start: %s)', async pauseAfterStart => {
  vi.stubEnv('VITE_API_BASE_URL', 'https://example.test')
  useDashboardPreferences.setState({ pollingEnabled: pauseAfterStart })
  const release: Array<() => void> = []
  const signals: AbortSignal[] = []
  const fetcher = vi.fn<typeof fetch>().mockImplementation((input, init) => new Promise((resolve, reject) => {
    const signal = init?.signal as AbortSignal; signals.push(signal)
    signal.addEventListener('abort', () => reject(signal.reason), { once: true })
    release.push(() => resolve(dashboardResponse(input)))
  }))
  vi.stubGlobal('fetch', fetcher)
  const client = new QueryClient(); clients.push(client)
  render(<QueryClientProvider client={client}><App /></QueryClientProvider>)
  await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2))
  if (pauseAfterStart) fireEvent.click(screen.getByRole('checkbox', { name: 'Automatic updates' }))
  expect((screen.getByRole('checkbox', { name: 'Automatic updates' }) as HTMLInputElement).checked).toBe(false)
  expect(signals.every(signal => !signal.aborted)).toBe(true)
  await act(async () => { release.forEach(finish => finish()) })
  await screen.findByText('42'); await screen.findByText('Device 1')
  expect(screen.getByText('Paused')).toBeTruthy(); expect(fetcher).toHaveBeenCalledTimes(2)
})


/** Submits through the actual labelled form; draft editing alone must never change the server query. */
function search(value: string) {
  fireEvent.change(screen.getByRole('searchbox', { name: 'Search name or asset tag' }), { target: { value } })
  fireEvent.submit(screen.getByRole('form', { name: 'Search active devices' }))
}
/** Captures actual request identity, including neutral searches omitted from the wire. */
function searches(fetcher: ReturnType<typeof vi.fn<typeof fetch>>) {
  return fetcher.mock.calls.map(([input]) => {
    const query = new URL(String(input)).searchParams
    return { q: query.get('q') ?? '', page: Number(query.get('page')), pageSize: Number(query.get('pageSize')) }
  })
}

it('searches the whole server fleet, resets paging atomically and preserves page size without filtering loaded rows', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => {
    const query = new URL(String(input)).searchParams
    return query.get('q') ? pageResponse(1, 10, 1, [{ ...row(24), displayName: 'Remote match' }]) : pageResponse(Number(query.get('page')), 10, 24)
  })
  mount(fetcher, { pageIndex: 1, pageSize: 10 }); await screen.findByText('Device 2')
  fireEvent.change(screen.getByRole('searchbox'), { target: { value: '  DEV-24  ' } })
  expect(fetcher).toHaveBeenCalledTimes(1)
  fireEvent.submit(screen.getByRole('form', { name: 'Search active devices' }))
  await screen.findByText('Remote match')
  expect(screen.queryByText('Device 2')).toBeNull()
  expect(screen.getByText(/1 matching active devices/)).toBeTruthy()
  expect(screen.getByText('Page 1 of 1')).toBeTruthy()
  expect(screen.getByText('Displayed results: search “DEV-24”')).toBeTruthy()
  expect(searches(fetcher)).toEqual([{ q: '', page: 2, pageSize: 10 }, { q: 'DEV-24', page: 1, pageSize: 10 }])
  fireEvent.click(screen.getByRole('button', { name: 'Clear search' })); await screen.findByText('Device 1')
  expect(searches(fetcher).at(-1)).toEqual({ q: '', page: 1, pageSize: 10 })
  expect((screen.getByRole('searchbox') as HTMLInputElement).value).toBe('')
})

it('validates the post-trim UTF-16 boundary without changing applied state or issuing invalid requests', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => pageResponse(1))
  mount(fetcher); await screen.findByText('Device 1')
  search('😀'.repeat(50) + 'x')
  expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'Search must contain at most 100 characters after trimming.')
  expect(screen.getByRole('searchbox').getAttribute('aria-invalid')).toBe('true')
  expect(fetcher).toHaveBeenCalledTimes(1)
  search('  ' + '😀'.repeat(50) + '  ')
  await screen.findByText('Displayed results: search “' + '😀'.repeat(50) + '”')
  expect(screen.queryByRole('alert')).toBeNull()
  expect(searches(fetcher).at(-1)?.q.length).toBe(100)
  search('   ')
  await screen.findByText('Displayed results: all active devices')
})

it('retains previous search identity, count, total and timestamp after a same-page search fails; retries requested search', async () => {
  let fail!: (error: Error) => void
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(pageResponse(1))
    .mockResolvedValueOnce(pageResponse(1, 25, 30, [{ ...row(10), displayName: 'First search result' }]))
    .mockImplementationOnce(() => new Promise((_, reject) => { fail = reject }))
    .mockResolvedValueOnce(pageResponse(1, 25, 1, [{ ...row(11), displayName: 'Second search result' }]))
  mount(fetcher); await screen.findByText('Device 1')
  search('first'); await screen.findByText('First search result')
  const fetchTime = screen.getByText(/Device fetch/).querySelector('time')?.dateTime
  search('second')
  expect(await screen.findByText(/Showing previous page 1 .*search “first”.*Requested page 1 .*search “second”/)).toBeTruthy()
  await act(async () => fail(new Error('private transport detail')))
  await screen.findByRole('alert')
  expect(screen.getByText('First search result')).toBeTruthy()
  expect(screen.getByText('Displayed results: search “first”')).toBeTruthy()
  expect(screen.getByText(/30 matching active devices/)).toBeTruthy()
  expect(screen.getByText('Page 1 of 2')).toBeTruthy()
  expect(screen.getByText(/Device fetch/).querySelector('time')?.dateTime).toBe(fetchTime)
  fireEvent.click(screen.getByRole('button', { name: 'Retry devices' })); await screen.findByText('Second search result')
  expect(searches(fetcher).map(query => query.q)).toEqual(['', 'first', 'second', 'second'])
  expect(screen.queryByText(/private transport detail/)).toBeNull()
})

it('distinguishes no search matches from an empty fleet and keeps literal text safe', async () => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(pageResponse(1)).mockResolvedValueOnce(pageResponse(1, 25, 0, []))
  mount(fetcher); await screen.findByText('Device 1')
  search('<img src=x onerror=alert(1)>')
  await screen.findByText('No active devices match these filters.')
  expect(screen.queryByText('No active devices.')).toBeNull()
  expect(screen.getByText('Page 1 of 1')).toBeTruthy()
  expect(screen.queryByRole('img')).toBeNull()
})

it('ignores late superseded searches at equal coordinates and prevents stale range correction from moving current paging', async () => {
  let finishFirst!: (response: Response) => void
  let finishOldPage!: (response: Response) => void
  const signals: AbortSignal[] = []
  const fetcher = vi.fn<typeof fetch>().mockImplementation((input, init) => {
    const query = new URL(String(input)).searchParams
    const q = query.get('q') ?? ''; const page = Number(query.get('page'))
    if (q === 'old' && page === 1) return new Promise(resolve => { finishFirst = resolve; signals.push(init?.signal as AbortSignal) })
    if (q === 'old' && page === 2) return new Promise(resolve => { finishOldPage = resolve; signals.push(init?.signal as AbortSignal) })
    return Promise.resolve(pageResponse(page, 25, 60, [{ ...row(q === 'new' ? 9 : 1), displayName: q === 'new' ? 'Current search' : 'Initial row' }]))
  })
  mount(fetcher); await screen.findByText('Initial row')
  search('old'); await waitFor(() => expect(finishFirst).toBeTypeOf('function'))
  search('new'); await screen.findByText('Current search')
  await act(async () => finishFirst(pageResponse(1, 25, 1, [{ ...row(8), displayName: 'Obsolete row' }])))
  expect(screen.queryByText('Obsolete row')).toBeNull()
  expect(screen.getByText('Displayed results: search “new”')).toBeTruthy()
  // Revisit old search, then supersede its page-two request with a different search's page two.
  search('old'); await waitFor(() => expect(fetcher.mock.calls.filter(([url]) => new URL(String(url)).searchParams.get('q') === 'old')).toHaveLength(2))
  await act(async () => finishFirst(pageResponse(1)))
  await screen.findByText('Device 1')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await waitFor(() => expect(finishOldPage).toBeTypeOf('function'))
  search('new'); await screen.findByText('Current search')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' }))
  await screen.findByText('Page 2 of 3')
  await act(async () => finishOldPage(pageResponse(2, 25, 0, [])))
  expect(screen.getByText('Page 2 of 3')).toBeTruthy()
  expect(screen.queryByText('No active devices match these filters.')).toBeNull()
  expect(signals[0].aborted).toBe(true); expect(signals.at(-1)?.aborted).toBe(true)
  expect(searches(fetcher).at(-1)).toEqual({ q: 'new', page: 2, pageSize: 25 })
})

it('searches and paginates while paused without refetching or filtering global metrics; invalidates only current search', async () => {
  useDashboardPreferences.setState({ pollingEnabled: false })
  vi.stubEnv('VITE_API_BASE_URL', 'https://example.test')
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => dashboardResponse(input))
  vi.stubGlobal('fetch', fetcher)
  const client = new QueryClient(); clients.push(client)
  render(<QueryClientProvider client={client}><App /></QueryClientProvider>)
  await screen.findByText('42'); await screen.findByText('Device 1')
  search('DEV'); await screen.findByText('Displayed results: search “DEV”')
  filter('Site', 'EDM'); await screen.findByText('Displayed results: search “DEV” · site EDM')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Device 2')
  await act(async () => { await client.invalidateQueries({ queryKey: ['devices'] }) })
  const calls = fetcher.mock.calls.map(([input]) => new URL(String(input)))
  expect(calls.filter(url => url.pathname.endsWith('/metrics/summary'))).toHaveLength(1)
  expect(calls.filter(url => url.pathname.endsWith('/devices')).map(url => [url.searchParams.get('q'), url.searchParams.get('page')])).toEqual([[null, '1'], ['DEV', '1'], ['DEV', '1'], ['DEV', '2'], ['DEV', '2']])
  expect(calls.at(-1)?.searchParams.get('site')).toBe('EDM')
  expect(screen.getByText('42')).toBeTruthy(); expect(screen.getByText('Paused')).toBeTruthy()
})


/** Selects use the visible controls and apply immediately; no form submission is synthesized. */
function filter(name: 'Site' | 'Type' | 'Health', value: string) {
  fireEvent.change(screen.getByRole('combobox', { name }), { target: { value } })
}
/** Reads complete actual wire inputs; tests never substitute current-page client filtering. */
function filterRequests(fetcher: ReturnType<typeof vi.fn<typeof fetch>>) {
  return fetcher.mock.calls.map(([input]) => Object.fromEntries(new URL(String(input)).searchParams))
}

it('combines applied search and selects, preserves page size, and distinguishes Clear search from Clear all filters', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => {
    const query = new URL(String(input)).searchParams
    return pageResponse(Number(query.get('page')), 10, 24, [{ ...row(query.has('site') ? 24 : 1), displayName: query.has('site') ? 'Off-page filter match' : 'Unfiltered row' }])
  })
  mount(fetcher, { pageIndex: 1, pageSize: 10 }); await screen.findByText('Unfiltered row')
  search('DEV'); await screen.findByText('Displayed results: search “DEV”')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Page 2 of 3')
  fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'unapplied draft' } })
  filter('Site', 'EDM'); await screen.findByText('Off-page filter match')
  expect(filterRequests(fetcher).at(-1)).toMatchObject({ q: 'DEV', site: 'EDM', page: '1', pageSize: '10' })
  expect((screen.getByRole('searchbox') as HTMLInputElement).value).toBe('unapplied draft')
  filter('Type', 'Server'); await screen.findByText('Displayed results: search “DEV” · site EDM · type Server')
  filter('Health', 'Unknown'); await screen.findByText('Displayed results: search “DEV” · site EDM · type Server · health Unknown')
  expect(filterRequests(fetcher).at(-1)).toMatchObject({ q: 'DEV', site: 'EDM', type: 'Server', status: 'Unknown', page: '1', pageSize: '10' })
  fireEvent.click(screen.getByRole('button', { name: 'Clear search' }))
  await screen.findByText('Displayed results: site EDM · type Server · health Unknown')
  expect(filterRequests(fetcher).at(-1)?.q).toBeUndefined()
  expect(filterRequests(fetcher).at(-1)).toMatchObject({ site: 'EDM', type: 'Server', status: 'Unknown' })
  expect((screen.getByRole('searchbox') as HTMLInputElement).value).toBe('')
  fireEvent.click(screen.getByRole('button', { name: 'Clear all filters' }))
  await screen.findByText('Displayed results: all active devices')
  for (const name of ['Site', 'Type', 'Health']) expect((screen.getByRole('combobox', { name }) as HTMLSelectElement).value).toBe('')
  expect(screen.getByText('Page 1 of 3')).toBeTruthy()
})

it.each([['Site', 'EDM', 'CGY', 'site'], ['Type', 'Server', 'Gateway', 'type'], ['Health', 'Unknown', 'Offline', 'status']] as const)('retains distinct same-page %s filter identity, count and time during failure and retries the requested value', async (name, first, second, key) => {
  let fail!: (error: Error) => void
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(pageResponse(1))
    .mockResolvedValueOnce(pageResponse(1, 25, 30, [{ ...row(5), displayName: 'Previous filter result' }]))
    .mockImplementationOnce(() => new Promise((_, reject) => { fail = reject }))
    .mockResolvedValueOnce(pageResponse(1, 25, 1, [{ ...row(6), displayName: 'Requested filter result' }]))
  mount(fetcher); await screen.findByText('Device 1')
  filter(name, first); await screen.findByText('Previous filter result')
  const stamp = screen.getByText(/Device fetch/).querySelector('time')?.dateTime
  filter(name, second)
  expect(await screen.findByText(new RegExp('Showing previous page 1 .*' + first + '.*Requested page 1 .*' + second))).toBeTruthy()
  await act(async () => fail(new Error('private canary'))); await screen.findByRole('alert')
  expect(screen.getByText('Previous filter result')).toBeTruthy()
  expect(screen.getByText('Page 1 of 2')).toBeTruthy()
  expect(screen.getByText(/30 matching active devices/)).toBeTruthy()
  expect(screen.getByText(/Device fetch/).querySelector('time')?.dateTime).toBe(stamp)
  fireEvent.click(screen.getByRole('button', { name: 'Retry devices' })); await screen.findByText('Requested filter result')
  expect(filterRequests(fetcher).map(request => request[key])).toEqual([undefined, first, second, second])
  expect(screen.queryByText(/private canary/)).toBeNull()
})

it('offers every supported capability even when no current row matches, with explicit neutral choices', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => pageResponse(Number(new URL(String(input)).searchParams.get('page')), 25, 0, []))
  mount(fetcher); await screen.findByText('No active devices.')
  for (const [name, label, values] of [['Site', 'All sites', ['EDM', 'CGY', 'DEN']], ['Type', 'All types', ['Server', 'Database', 'Gateway', 'SCADA']], ['Health', 'All health states', ['Healthy', 'Degraded', 'Offline', 'Unknown']]] as const) {
    const select = screen.getByRole('combobox', { name })
    expect(within(select).getAllByRole('option').map(option => option.textContent)).toEqual([label, ...values])
    for (const value of values) {
      filter(name, value)
      await screen.findByText('No active devices match these filters.')
      await waitFor(() => expect(filterRequests(fetcher).at(-1)?.[name === 'Site' ? 'site' : name === 'Type' ? 'type' : 'status']).toBe(value))
    }
    filter(name, ''); await screen.findByText('No active devices.')
  }
})

it('ignores a superseded filter page completion without moving a different filter at equal coordinates', async () => {
  let finish!: (response: Response) => void
  let oldSignal!: AbortSignal
  const fetcher = vi.fn<typeof fetch>().mockImplementation((input, init) => {
    const query = new URL(String(input)).searchParams; const page = Number(query.get('page'))
    if (query.get('site') === 'EDM' && page === 2) return new Promise(resolve => { finish = resolve; oldSignal = init?.signal as AbortSignal })
    return Promise.resolve(pageResponse(page, 25, 60))
  })
  mount(fetcher); await screen.findByText('Device 1')
  filter('Site', 'EDM'); await screen.findByText('Displayed results: site EDM')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await waitFor(() => expect(finish).toBeTypeOf('function'))
  filter('Site', 'CGY'); await screen.findByText('Displayed results: site CGY')
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await screen.findByText('Page 2 of 3')
  await act(async () => finish(pageResponse(2, 25, 0, [])))
  expect(screen.getByText('Page 2 of 3')).toBeTruthy()
  expect(screen.getByText('Displayed results: site CGY')).toBeTruthy()
  expect(oldSignal.aborted).toBe(true)
  expect(filterRequests(fetcher).at(-1)).toMatchObject({ site: 'CGY', page: '2' })
})
