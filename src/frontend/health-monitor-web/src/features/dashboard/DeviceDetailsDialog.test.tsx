// @vitest-environment jsdom
import { StrictMode } from 'react'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider, focusManager } from '@tanstack/react-query'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import App from '../../App'
import { useDashboardPreferences } from './dashboardPreferences'

const firstId = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee'
const secondId = 'bbbbbbbb-bbbb-4ccc-8ddd-eeeeeeeeeeee'
const now = '2026-10-19T17:00:00Z'
const originalShow = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal')
const originalClose = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'close')
const clients: QueryClient[] = []
let showModal: ReturnType<typeof vi.fn>
let closeModal: ReturnType<typeof vi.fn>

beforeEach(() => {
  focusManager.setFocused(true)
  useDashboardPreferences.setState(useDashboardPreferences.getInitialState(), true)
  vi.stubEnv('VITE_API_BASE_URL', 'https://example.test')
  // jsdom does not supply native modality/focus containment. This shim proves lifecycle calls only, not Tab/Escape behavior.
  showModal = vi.fn(function (this: HTMLDialogElement) { this.setAttribute('open', '') })
  closeModal = vi.fn(function (this: HTMLDialogElement) { this.removeAttribute('open') })
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: showModal })
  Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value: closeModal })
})
afterEach(() => {
  cleanup(); clients.splice(0).forEach(client => client.clear())
  useDashboardPreferences.setState(useDashboardPreferences.getInitialState(), true)
  if (originalShow) Object.defineProperty(HTMLDialogElement.prototype, 'showModal', originalShow); else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal')
  if (originalClose) Object.defineProperty(HTMLDialogElement.prototype, 'close', originalClose); else Reflect.deleteProperty(HTMLDialogElement.prototype, 'close')
  document.body.style.overflow = ''; focusManager.setFocused(undefined); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.unstubAllEnvs()
})
/** Fictional DTO fixtures exercise the real client, Query cache and App composition without a live service. */
function row(id = firstId) { return { id, assetTag: id === firstId ? 'EDM-1' : 'DEN-2', displayName: id === firstId ? 'First device' : 'Second device', deviceType: 'Server', siteCode: 'EDM', health: 'Unknown', latencyMs: null, cpuPercent: null, lastObservedAtUtc: null } }
function detail(id = firstId) { return { ...row(id), createdAtUtc: '2026-10-18T12:00:00Z', retiredAtUtc: null, lastSuccessfulAtUtc: null, asOfUtc: now } }
function telemetry(id = firstId) { return { id: 'cccccccc-bbbb-4ccc-8ddd-eeeeeeeeeeee', deviceId: id, observedAtUtc: '2026-10-19T16:59:00Z', outcome: 'Success', latencyMs: 40, cpuPercent: 30 } }
function incident(id = firstId) { return { id: 'dddddddd-bbbb-4ccc-8ddd-eeeeeeeeeeee', deviceId: id, kind: 'Cpu', openedAtUtc: '2026-10-19T16:58:00Z', resolvedAtUtc: null, summary: 'Synthetic CPU incident.' } }
function json(body: unknown, status = 200) { return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }) }
function page(items: unknown[] = [], totalCount = items.length) { return { items, page: 1, pageSize: 10, totalCount, asOfUtc: now } }
/** Route-driven responses preserve actual API contracts; only individual tests override an owned route. */
function respond(input: RequestInfo | URL): Response {
  const url = new URL(String(input)); const id = url.pathname.split('/')[4] ?? firstId
  if (url.pathname.endsWith('/metrics/summary')) return json({ total: 2, healthy: 0, degraded: 0, offline: 0, unknown: 2, averageLatencyMs: null, sampleCount: 0, asOfUtc: now })
  if (url.pathname === '/api/v1/devices') return json({ ...page([row(), row(secondId)], 2), pageSize: Number(url.searchParams.get('pageSize')) })
  if (url.pathname.endsWith('/telemetry')) return json(page([telemetry(id)], 14))
  if (url.pathname.endsWith('/incidents')) return json(page([incident(id)], 12))
  return json(detail(id))
}
/** Every case has its own Query cache; App owns grid state and the real store owns selection only. */
function mount(fetcher = vi.fn<typeof fetch>().mockImplementation(async input => respond(input)), strict = false) {
  vi.stubGlobal('fetch', fetcher)
  const client = new QueryClient({ defaultOptions: { queries: { gcTime: 0 } } }); clients.push(client)
  const app = <QueryClientProvider client={client}><App /></QueryClientProvider>
  return { ...render(strict ? <StrictMode>{app}</StrictMode> : app), fetcher, client }
}
async function openFirst() {
  const opener = await screen.findByRole('button', { name: 'View details for First device' })
  fireEvent.click(opener)
  await screen.findByRole('dialog', { name: 'Device details' })
  return opener
}
function section(name: string) { return screen.getByRole('region', { name }) }

it('opens only on explicit action, focuses heading, labels scopes and shows independent overview/history without changing grid state', async () => {
  const view = mount(); const opener = await screen.findByRole('button', { name: 'View details for First device' })
  expect(view.fetcher).toHaveBeenCalledTimes(2)
  fireEvent.click(opener); const dialog = await screen.findByRole('dialog', { name: 'Device details' })
  await within(dialog).findByText('First device')
  expect(document.activeElement).toBe(within(dialog).getByRole('heading', { name: 'Device details' }))
  expect(showModal).toHaveBeenCalledTimes(1); expect(document.body.style.overflow).toBe('hidden')
  expect(within(section('Device overview')).getByText('Not observed')).toBeTruthy()
  expect(within(section('Recent measurements')).getByText('Showing 1 of 14 observations in this window.')).toBeTruthy()
  expect(within(section('Recent measurements')).getByText(/24 hours before/)).toBeTruthy()
  expect(within(section('Recent incidents')).getByText('Showing 1 of 12 retained incidents.')).toBeTruthy()
  expect(within(section('Recent incidents')).getByText(/not a complete lifetime history/)).toBeTruthy()
  expect(within(section('Recent incidents')).getByText('Cpu · Open')).toBeTruthy()
  expect(within(section('Recent measurements')).getByText('40 ms')).toBeTruthy()
  expect(within(section('Recent measurements')).getByText('30%')).toBeTruthy()
  fireEvent.click(within(dialog).getByRole('button', { name: 'Close details' }))
  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  expect(document.activeElement).toBe(opener); expect(document.body.style.overflow).toBe('')
  expect(useDashboardPreferences.getState()).toMatchObject({ selectedDeviceId: null, drawerOpen: false })
  expect(screen.getByText('Page 1 of 1')).toBeTruthy()
})

it('maps native cancel to close and restores previous overflow instead of assuming the empty default', async () => {
  document.body.style.overflow = 'auto'
  mount(); const opener = await openFirst(); const dialog = screen.getByRole('dialog')
  fireEvent(dialog, new Event('cancel', { cancelable: true }))
  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
  expect(document.activeElement).toBe(opener); expect(document.body.style.overflow).toBe('auto')
  expect(closeModal).toHaveBeenCalledTimes(1)
})

it('retains explicit selection after its row disappears and falls back to the stable heading when closing', async () => {
  let includeFirst = true
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => new URL(String(input)).pathname === '/api/v1/devices' && !includeFirst
    ? json({ ...page([row(secondId)], 1), pageSize: 25 }) : respond(input))
  const view = mount(fetcher); const opener = await openFirst(); await within(section('Device overview')).findByText('First device')
  includeFirst = false
  await act(async () => { await view.client.invalidateQueries({ queryKey: ['devices'] }) })
  await waitFor(() => expect(opener.isConnected).toBe(false))
  expect(screen.getByRole('dialog')).toBeTruthy(); expect(useDashboardPreferences.getState().selectedDeviceId).toBe(firstId)
  fireEvent.click(screen.getByRole('button', { name: 'Close details' }))
  expect(document.activeElement).toBe(screen.getByRole('heading', { name: 'Devices' }))
})

it('switches query identity without reopening/refocusing and ignores cancelled old-device completion', async () => {
  const release: Array<() => void> = []; const signals: AbortSignal[] = []
  const fetcher = vi.fn<typeof fetch>().mockImplementation((input, init) => {
    if (new URL(String(input)).pathname.includes('/' + firstId)) return new Promise(resolve => { signals.push(init?.signal as AbortSignal); release.push(() => resolve(respond(input))) })
    return Promise.resolve(respond(input))
  })
  mount(fetcher); await openFirst(); await waitFor(() => expect(release).toHaveLength(3))
  const close = screen.getByRole('button', { name: 'Close details' }); close.focus()
  act(() => useDashboardPreferences.getState().openDevice(secondId))
  await within(section('Device overview')).findByText('Second device')
  expect(signals.every(signal => signal.aborted)).toBe(true)
  expect(showModal).toHaveBeenCalledTimes(1); expect(closeModal).not.toHaveBeenCalled()
  expect(document.activeElement).toBe(close)
  await act(async () => { release.forEach(finish => finish()) })
  expect(within(section('Device overview')).queryByText('First device')).toBeNull()
  expect(within(section('Recent measurements')).getByText('40 ms')).toBeTruthy()
})

it.each([false, true])('cancels dialog reads on close/unmount without cancelling an unrelated pending read (unmount: %s)', async unmount => {
  const release: Array<() => void> = []; const signals: AbortSignal[] = []
  const fetcher = vi.fn<typeof fetch>().mockImplementation((input, init) => {
    if (new URL(String(input)).pathname.includes('/' + firstId)) return new Promise(resolve => { signals.push(init?.signal as AbortSignal); release.push(() => resolve(respond(input))) })
    return Promise.resolve(respond(input))
  })
  const view = mount(fetcher); await openFirst(); await waitFor(() => expect(release).toHaveLength(3))
  let unrelatedSignal!: AbortSignal; let finishUnrelated!: () => void
  const unrelated = view.client.fetchQuery({ queryKey: ['unrelated-read'], queryFn: ({ signal }) => {
    unrelatedSignal = signal
    return new Promise<number>(resolve => { finishUnrelated = () => resolve(1) })
  } })
  await waitFor(() => expect(unrelatedSignal).toBeDefined())
  if (unmount) view.unmount()
  else fireEvent.click(screen.getByRole('button', { name: 'Close details' }))
  expect(unrelatedSignal.aborted).toBe(false)
  finishUnrelated(); await unrelated
  expect(signals.every(signal => signal.aborted)).toBe(true)
  await act(async () => { release.forEach(finish => finish()) })
  expect(screen.queryByRole('dialog')).toBeNull()
  if (!unmount) expect(view.client.getQueryData(['fleet-metrics', 'https://example.test'])).toBeDefined()
})

it('separates unavailable-device 404 from retired detail and keeps history failures independent with safe retry', async () => {
  let detailStatus = 404; let failTelemetry = true
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => {
    const path = new URL(String(input)).pathname
    if (path === '/api/v1/devices/' + firstId) return detailStatus === 404 ? json({ detail: 'private-canary' }, 404) : json({ ...detail(), retiredAtUtc: now })
    if (path.endsWith('/telemetry') && failTelemetry) return json({ title: 'private-canary' }, 503)
    return respond(input)
  })
  mount(fetcher); await openFirst()
  await within(section('Device overview')).findByText('This device is no longer available.')
  await within(section('Recent measurements')).findByText('Monitoring data is temporarily unavailable.')
  expect(within(section('Recent incidents')).getByText('Synthetic CPU incident.')).toBeTruthy()
  detailStatus = 200; failTelemetry = false
  fireEvent.click(within(section('Device overview')).getByRole('button', { name: 'Retry device overview' }))
  fireEvent.click(within(section('Recent measurements')).getByRole('button', { name: 'Retry recent measurements' }))
  await within(section('Device overview')).findByText(/Retired/)
  await within(section('Recent measurements')).findByText('40 ms')
  expect(screen.queryByText(/private-canary/)).toBeNull()
})

it('shows empty retained histories, timeout nulls and plain-text incident summaries without HTML interpretation', async () => {
  let empty = true
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => {
    const path = new URL(String(input)).pathname
    if (path.endsWith('/telemetry')) return json(empty ? page() : page([{ ...telemetry(), outcome: 'Timeout', latencyMs: null, cpuPercent: null }]))
    if (path.endsWith('/incidents')) return json(empty ? page() : page([{ ...incident(), summary: '<img src=x onerror=alert(1)>', resolvedAtUtc: now }]))
    return respond(input)
  })
  mount(fetcher); await openFirst()
  await within(section('Recent measurements')).findByText('No observations in this window.')
  await within(section('Recent incidents')).findByText('No retained incidents.')
  empty = false
  fireEvent.click(within(section('Recent measurements')).getByRole('button', { name: 'Refresh recent measurements' }))
  fireEvent.click(within(section('Recent incidents')).getByRole('button', { name: 'Refresh recent incidents' }))
  await within(section('Recent measurements')).findByText('Timeout')
  expect(within(section('Recent measurements')).getAllByText('—')).toHaveLength(2)
  await within(section('Recent incidents')).findByText('<img src=x onerror=alert(1)>')
  expect(within(section('Recent incidents')).getByText('Cpu · Resolved')).toBeTruthy()
  expect(screen.queryByRole('img')).toBeNull()
})

it('keeps stale data and its fetch timestamp after section failure; retries do not refetch unrelated sections', async () => {
  let fail = false
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async input => fail && new URL(String(input)).pathname.endsWith('/incidents') ? json({}, 503) : respond(input))
  mount(fetcher); await openFirst(); await within(section('Recent incidents')).findByText('Synthetic CPU incident.')
  const originalStamp = within(section('Recent incidents')).getByText(/Last successful fetch/).querySelector('time')?.dateTime
  fail = true
  fireEvent.click(within(section('Recent incidents')).getByRole('button', { name: 'Refresh recent incidents' }))
  await within(section('Recent incidents')).findByText('Previous recent incidents may be stale.')
  expect(within(section('Recent incidents')).getByText('Synthetic CPU incident.')).toBeTruthy()
  expect(within(section('Recent incidents')).getByText(/Last successful fetch/).querySelector('time')?.dateTime).toBe(originalStamp)
  expect(fetcher.mock.calls.filter(([input]) => new URL(String(input)).pathname.endsWith('/telemetry'))).toHaveLength(1)
})

it('pauses interval updates but still loads and explicitly refreshes all three sections', async () => {
  vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] })
  useDashboardPreferences.setState({ pollingEnabled: false })
  const view = mount(); await openFirst(); await within(section('Recent incidents')).findByText('Synthetic CPU incident.')
  const before = view.fetcher.mock.calls.length
  await act(async () => { await vi.advanceTimersByTimeAsync(15_000) })
  expect(view.fetcher).toHaveBeenCalledTimes(before)
  fireEvent.click(within(section('Device overview')).getByRole('button', { name: 'Refresh device overview' }))
  await waitFor(() => expect(view.fetcher).toHaveBeenCalledTimes(before + 1))
  act(() => useDashboardPreferences.getState().setPollingEnabled(true))
  await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
  await waitFor(() => expect(view.fetcher.mock.calls.filter(([input]) => new URL(String(input)).pathname.includes('/' + firstId))).toHaveLength(7))
})

it('does not overwrite another observable overflow owner or move focus behind another modal', async () => {
  mount(); await openFirst()
  const other = document.createElement('dialog'); other.setAttribute('open', ''); other.tabIndex = -1; document.body.append(other); other.focus()
  document.body.style.overflow = 'clip'
  act(() => useDashboardPreferences.getState().closeDrawer())
  expect(document.body.style.overflow).toBe('clip'); expect(document.activeElement).toBe(other)
  other.remove()
})


it('keeps selection and modal lifecycle coherent under the application StrictMode effect replay', async () => {
  document.body.style.overflow = 'auto'
  mount(undefined, true)
  const opener = await openFirst()
  await within(section('Device overview')).findByText('First device')
  expect(useDashboardPreferences.getState()).toMatchObject({ drawerOpen: true, selectedDeviceId: firstId })
  expect((screen.getByRole('dialog') as HTMLDialogElement).open).toBe(true)
  expect(document.body.style.overflow).toBe('hidden')
  fireEvent.click(screen.getByRole('button', { name: 'Close details' }))
  expect(useDashboardPreferences.getState()).toMatchObject({ drawerOpen: false, selectedDeviceId: null })
  expect(document.activeElement).toBe(opener)
  expect(document.body.style.overflow).toBe('auto')
})
