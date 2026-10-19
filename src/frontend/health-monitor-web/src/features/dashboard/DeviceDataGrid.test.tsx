// @vitest-environment jsdom
import { useState } from 'react'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider, focusManager } from '@tanstack/react-query'
import type { PaginationState } from '@tanstack/react-table'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createReadApi } from '../../api/readApi'
import { DeviceDataGrid } from './DeviceDataGrid'
import App from '../../App'

const clients: QueryClient[] = []
beforeEach(() => focusManager.setFocused(true))
afterEach(() => {
  cleanup(); clients.splice(0).forEach(client => client.clear())
  focusManager.setFocused(undefined); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllEnvs(); vi.unstubAllGlobals()
})

/** Supplies only explicit fictional DTO data, never a database or real service response. */
function row(number: number) {
  return { id: `00000000-0000-4000-8000-${String(number).padStart(12, '0')}`, assetTag: `DEV-${number}`, displayName: `Device ${number}`, deviceType: 'Gateway', siteCode: 'DEN', health: 'Offline', lastObservedAtUtc: '2026-10-18T12:00:00Z', latencyMs: 0, cpuPercent: 0 }
}
/** Returns a fresh single-use JSON body for one server page, with one distinguishable row. */
function pageResponse(page: number, pageSize = 25, totalCount = 60, items: unknown[] = [row(page)]) {
  return new Response(JSON.stringify({ page, pageSize, totalCount, items, asOfUtc: '2026-10-18T12:00:00Z' }), { headers: { 'Content-Type': 'application/json' } })
}
/** Models the dashboard's one pagination owner without storing response rows in React state. */
function Harness({ fetcher, initial }: { fetcher: typeof fetch; initial: PaginationState }) {
  const [pagination, setPagination] = useState(initial)
  return <DeviceDataGrid api={createReadApi('https://example.test', fetcher)} pagination={pagination} onPaginationChange={setPagination} />
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
  expect((screen.getByRole('combobox') as HTMLSelectElement).disabled).toBe(true)
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
