// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider, focusManager } from '@tanstack/react-query'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createReadApi } from '../../api/readApi'
import { MetricsOverview } from './MetricsOverview'
import App from '../../App'
import { useDashboardPreferences } from './dashboardPreferences'

const summary = { total: 42, healthy: 31, degraded: 6, offline: 3, unknown: 2, averageLatencyMs: 12.5, sampleCount: 31, asOfUtc: '2026-10-18T12:00:00Z' }
const clients: QueryClient[] = []
beforeEach(() => focusManager.setFocused(true))
afterEach(() => {
  cleanup(); clients.splice(0).forEach(client => client.clear())
  useDashboardPreferences.setState(useDashboardPreferences.getInitialState(), true)
  focusManager.setFocused(undefined); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllEnvs(); vi.unstubAllGlobals()
})

/** Gives each render its own real Query cache so cancellation and stale-state assertions cannot borrow another test's data. */
function mount(fetcher: typeof fetch) {
  const client = new QueryClient({ defaultOptions: { queries: { gcTime: 0 } } }); clients.push(client)
  return render(<QueryClientProvider client={client}><MetricsOverview api={createReadApi('https://example.test', fetcher)} /></QueryClientProvider>)
}
/** Returns a fresh response each time: a fetch body is a single-use stream. */
function response(value: unknown = summary) { return new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } }) }

it('shows real metrics, nullable samples and distinct server/fetch timestamps', async () => {
  mount(vi.fn<typeof fetch>().mockResolvedValue(response()))
  expect(await screen.findByText('42')).toBeTruthy()
  expect(screen.getByText('12.5 ms')).toBeTruthy()
  expect(screen.getByText('Fresh latency samples: 31')).toBeTruthy()
  const server = screen.getByText(/Server evaluation/).querySelector('time')
  const fetched = screen.getByText(/Last successful fetch/).querySelector('time')
  expect(server?.getAttribute('datetime')).toBe(summary.asOfUtc)
  expect(fetched?.getAttribute('datetime')).not.toBe(summary.asOfUtc)
})

it('distinguishes an empty fleet and absent latency from loading or zero latency', async () => {
  mount(vi.fn<typeof fetch>().mockResolvedValue(response({ total: 0, healthy: 0, degraded: 0, offline: 0, unknown: 0, averageLatencyMs: null, sampleCount: 0, asOfUtc: summary.asOfUtc })))
  expect(await screen.findByText(/No active devices/)).toBeTruthy()
  expect(screen.getByText('—')).toBeTruthy()
  expect(screen.queryByText('0 ms')).toBeNull()
  expect(screen.getByText('Fresh latency samples: 0')).toBeTruthy()
  expect(screen.queryByText(/Loading fleet/)).toBeNull()
})

it('has a separate initial failure state and recovers through explicit retry', async () => {
  const fetcher = vi.fn<typeof fetch>().mockRejectedValueOnce(new Error('private-canary')).mockResolvedValueOnce(response())
  mount(fetcher)
  expect(await screen.findByRole('alert')).toBeTruthy()
  expect(screen.getByText('Fleet metrics are unavailable.')).toBeTruthy()
  expect(screen.queryByText(/private-canary/)).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Retry' }))
  expect(await screen.findByText('42')).toBeTruthy()
  expect(screen.queryByRole('alert')).toBeNull(); expect(fetcher).toHaveBeenCalledTimes(2)
})

it('keeps last successful values and freshness while clearly marking a failed refresh', async () => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(response()).mockResolvedValueOnce(new Response('<html>private-canary</html>', { status: 500 }))
  mount(fetcher)
  await screen.findByText('42')
  const fetchedAt = screen.getByText(/Last successful fetch/).querySelector('time')?.getAttribute('datetime')
  fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
  await screen.findByRole('alert')
  expect(screen.getByText('42')).toBeTruthy(); expect(screen.getByText('12.5 ms')).toBeTruthy()
  expect(screen.getByText(/showing the last successful summary/)).toBeTruthy()
  expect(screen.getByText(/Summary may be stale/)).toBeTruthy()
  expect(screen.queryByText(/private-canary/)).toBeNull()
  expect(screen.getByText(/Last successful fetch/).querySelector('time')?.getAttribute('datetime')).toBe(fetchedAt)
})

it('consumes Query cancellation when the view unmounts', async () => {
  let signal: AbortSignal | undefined
  const fetcher = vi.fn<typeof fetch>().mockImplementation((_, init) => {
    signal = init?.signal as AbortSignal
    return new Promise((_, reject) => signal!.addEventListener('abort', () => reject(signal!.reason), { once: true }))
  })
  const view = mount(fetcher)
  await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(1))
  view.unmount()
  expect(signal?.aborted).toBe(true)
})

it('does not overlap a pending fetch and polls again after completion', async () => {
  // Keep notification/waitFor timeouts real; advance only the polling interval and wall clock.
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] })
  let finish!: (value: Response) => void
  const fetcher = vi.fn<typeof fetch>().mockImplementationOnce(() => new Promise(resolve => { finish = resolve })).mockResolvedValueOnce(response())
  mount(fetcher)
  expect(screen.getByRole('status').textContent).toContain('Loading fleet')
  await act(async () => { await vi.advanceTimersByTimeAsync(15_000) })
  expect(fetcher).toHaveBeenCalledTimes(1)
  await act(async () => finish(response()))
  await screen.findByText('42')
  await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
  await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2))
})

it('suspends interval fetching when the page is not focused', async () => {
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] })
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => response())
  mount(fetcher); await screen.findByText('42')
  focusManager.setFocused(false)
  await act(async () => { await vi.advanceTimersByTimeAsync(15_000) })
  expect(fetcher).toHaveBeenCalledTimes(1)
})
it('shows the synthetic app shell and a safe missing-configuration state without fetching', () => {
  vi.stubEnv('VITE_API_BASE_URL', '')
  const fetcher = vi.fn<typeof fetch>(); vi.stubGlobal('fetch', fetcher)
  const client = new QueryClient(); clients.push(client)
  render(<QueryClientProvider client={client}><App /></QueryClientProvider>)
  expect(screen.getByRole('heading', { level: 1, name: 'HealthMonitor' })).toBeTruthy(); expect(screen.getByText('Synthetic data')).toBeTruthy()
  expect(screen.getByRole('alert').textContent).toContain('not configured correctly')
  expect(fetcher).not.toHaveBeenCalled()
})
