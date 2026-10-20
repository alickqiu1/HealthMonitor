// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, expect, it, vi } from 'vitest'
import App from './App'
import { useDashboardPreferences } from './features/dashboard/dashboardPreferences'

const clients: QueryClient[] = []
const initial = { q: null, site: 'DEN', type: 'Server', status: 'Offline', sort: 'status', direction: 'desc' }
const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
afterEach(() => { cleanup(); clients.splice(0).forEach(client => client.clear()); useDashboardPreferences.setState(useDashboardPreferences.getInitialState(), true); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.unstubAllEnvs() })

/** Uses the actual dashboard, grid, metrics, Query and both API clients, with no external service. */
function mount(proposal: Record<string, unknown> = initial, aiStatus = 200) {
  vi.stubEnv('VITE_API_BASE_URL', 'https://example.test')
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async (input, init) => {
    const url = new URL(String(input))
    if (init?.method === 'POST') return json(aiStatus === 200
      ? { contractVersion: '1', outcome: 'Ready', query: proposal, message: 'Review the proposed filters before applying them.' } : { detail: 'private-provider-canary' }, aiStatus)
    if (url.pathname.endsWith('/summary')) return json({ total: 100, healthy: 50, degraded: 20, offline: 20, unknown: 10, averageLatencyMs: 1, sampleCount: 100, asOfUtc: '2026-10-18T12:00:00Z' })
    const page = Number(url.searchParams.get('page')); const pageSize = Number(url.searchParams.get('pageSize'))
    return json({ items: [], page, pageSize, totalCount: 100, asOfUtc: '2026-10-18T12:00:00Z' })
  })
  vi.stubGlobal('fetch', fetcher)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } }); clients.push(client)
  render(<QueryClientProvider client={client}><App /></QueryClientProvider>)
  const posts = () => fetcher.mock.calls.filter(([, init]) => init?.method === 'POST')
  const grids = () => fetcher.mock.calls.map(([input]) => new URL(String(input))).filter(url => url.pathname === '/api/v1/devices')
  const latest = () => grids().at(-1)!.searchParams
  async function preview() {
    fireEvent.change(screen.getByLabelText('Describe the devices to show'), { target: { value: 'offline servers in DEN' } })
    fireEvent.click(screen.getByRole('button', { name: 'Preview filters' })); await screen.findByRole('heading', { name: 'Proposed filters' })
  }
  return { client, fetcher, posts, grids, latest, preview }
}

it('keeps preview separate, replaces all filters atomically, resets page and preserves page size', async () => {
  const host = mount(); await waitFor(() => expect((screen.getByLabelText('Devices per page') as HTMLSelectElement).disabled).toBe(false))
  fireEvent.change(screen.getByLabelText('Devices per page'), { target: { value: '50' } })
  await waitFor(() => expect(host.latest().get('pageSize')).toBe('50'))
  await waitFor(() => expect((screen.getByRole('button', { name: 'Next page' }) as HTMLButtonElement).disabled).toBe(false))
  fireEvent.click(screen.getByRole('button', { name: 'Next page' }))
  await waitFor(() => expect(host.latest().get('page')).toBe('2'))
  fireEvent.change(screen.getByLabelText('Site'), { target: { value: 'CGY' } })
  fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'Gateway' } })
  fireEvent.change(screen.getByLabelText('Health'), { target: { value: 'Healthy' } })
  fireEvent.change(screen.getByLabelText('Search name or asset tag'), { target: { value: 'old applied search' } })
  fireEvent.click(screen.getByRole('button', { name: 'Search' }))
  await waitFor(() => expect(host.latest().get('q')).toBe('old applied search'))
  await waitFor(() => expect((screen.getByRole('button', { name: 'Next page' }) as HTMLButtonElement).disabled).toBe(false))
  fireEvent.click(screen.getByRole('button', { name: 'Next page' })); await waitFor(() => expect(host.latest().get('page')).toBe('2'))
  const before = host.grids().length; await host.preview()
  expect(host.grids()).toHaveLength(before)
  fireEvent.click(screen.getByRole('button', { name: 'Apply proposed filters' }))
  await waitFor(() => expect(Object.fromEntries(host.latest())).toEqual({ page: '1', pageSize: '50', site: 'DEN', type: 'Server', status: 'Offline', sort: 'status', direction: 'desc' }))
  expect((screen.getByLabelText('Search name or asset tag') as HTMLInputElement).value).toBe('')
  expect(host.posts()).toHaveLength(1)
  fireEvent.click(screen.getByLabelText('Automatic updates'))
  await act(async () => { await host.client.invalidateQueries({ queryKey: ['devices'] }) })
  expect(host.posts()).toHaveLength(1)
  fireEvent.change(screen.getByLabelText('Site'), { target: { value: 'EDM' } })
  await waitFor(() => expect(host.latest().get('site')).toBe('EDM'))
  expect(host.posts()).toHaveLength(1)
}, 15_000)

it('Clear and neutral Apply discard even same-q unsubmitted drafts without sending another POST', async () => {
  const host = mount({ ...initial, site: null, type: null, status: null, sort: 'displayName', direction: 'asc' })
  await waitFor(() => expect(host.grids().length).toBeGreaterThan(0))
  fireEvent.change(screen.getByLabelText('Search name or asset tag'), { target: { value: 'unsubmitted' } })
  await host.preview(); fireEvent.click(screen.getByRole('button', { name: 'Apply proposed filters' }))
  expect((screen.getByLabelText('Search name or asset tag') as HTMLInputElement).value).toBe('')
  fireEvent.change(screen.getByLabelText('Search name or asset tag'), { target: { value: 'another unsubmitted' } })
  fireEvent.click(screen.getByRole('button', { name: 'Clear AI filters' }))
  expect((screen.getByLabelText('Search name or asset tag') as HTMLInputElement).value).toBe('')
  expect((screen.getByLabelText('Describe the devices to show') as HTMLTextAreaElement).value).toBe('')
  expect(screen.queryByText('Proposed filters')).toBeNull(); expect(host.posts()).toHaveLength(1)
})

it.each(['displayName', 'assetTag', 'siteCode', 'status', 'lastObservedAtUtc', 'latencyMs', 'cpuPercent'])('preserves API sort %s through UI mapping and ordinary GET', async sort => {
  const host = mount({ ...initial, sort }); await host.preview()
  fireEvent.click(screen.getByRole('button', { name: 'Apply proposed filters' }))
  await waitFor(() => expect(host.latest().get('sort')).toBe(sort)); expect(host.posts()).toHaveLength(1)
})

it('disabled AI never blocks ordinary manual filtering', async () => {
  const host = mount(initial, 404); await waitFor(() => expect(host.grids().length).toBeGreaterThan(0))
  fireEvent.change(screen.getByLabelText('Describe the devices to show'), { target: { value: 'devices' } })
  fireEvent.click(screen.getByRole('button', { name: 'Preview filters' })); await screen.findByText(/AI search is unavailable/)
  fireEvent.change(screen.getByLabelText('Site'), { target: { value: 'DEN' } })
  await waitFor(() => expect(host.latest().get('site')).toBe('DEN'))
  expect(host.posts()).toHaveLength(1); expect(screen.queryByText(/private-provider-canary/)).toBeNull()
})
