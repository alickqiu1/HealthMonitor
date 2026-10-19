import { useEffect, useRef } from 'react'
import type { ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import type { UseQueryResult } from '@tanstack/react-query'
import { ReadApiError } from '../../api/readApi'
import type { ReadApi } from '../../api/readApi'
import { useDashboardPreferences } from './dashboardPreferences'

/**
 * One native modal session survives selection changes; only Query identities change with the selected UUID.
 * The browser owns modality/focus containment. Query owns responses and cooperative cancellation, not a network-draining guarantee.
 */
export function DeviceDetailsDialog({ api, deviceId }: { api: ReadApi; deviceId: string }) {
  const dialogRef = useRef<HTMLDialogElement>(null)
  const headingRef = useRef<HTMLHeadingElement>(null)
  const closeDrawer = useDashboardPreferences(state => state.closeDrawer)
  const pollingEnabled = useDashboardPreferences(state => state.pollingEnabled)
  // These policies match the dashboard; explicit retry still works when background updates are paused.
  const policy = { staleTime: 2_000, retry: false, refetchInterval: pollingEnabled ? 5_000 : false as const,
    refetchOnWindowFocus: pollingEnabled, refetchOnReconnect: pollingEnabled, refetchIntervalInBackground: false }
  const identity = deviceId.toLowerCase()
  const detail = useQuery({ ...policy, queryKey: ['device', api.origin, identity], queryFn: ({ signal }) => api.getDevice(deviceId, signal) })
  const telemetry = useQuery({ ...policy, queryKey: ['telemetry', api.origin, identity, { page: 1, pageSize: 10, window: 'server-default-24h' }], queryFn: ({ signal }) => api.getRecentTelemetry(deviceId, signal) })
  const incidents = useQuery({ ...policy, queryKey: ['incidents', api.origin, identity, { page: 1, pageSize: 10, state: 'all' }], queryFn: ({ signal }) => api.getRecentIncidents(deviceId, signal) })

  useEffect(() => {
    const dialog = dialogRef.current
    if (!dialog) return
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const previousOverflow = document.body.style.overflow
    const ownsOverflow = previousOverflow !== 'hidden'
    dialog.showModal()
    if (ownsOverflow) document.body.style.overflow = 'hidden'
    headingRef.current?.focus()
    // No selection/query dependencies: polling and UUID switches must not close or refocus this modal session.
    return () => {
      if (dialog.open) dialog.close()
      const anotherModal = document.querySelector('dialog[open]') !== null
      if (ownsOverflow && document.body.style.overflow === 'hidden' && !anotherModal) document.body.style.overflow = previousOverflow
      if (anotherModal) return
      if (opener?.isConnected && opener !== document.body && opener !== document.documentElement && !opener.matches(':disabled')) opener.focus()
      else document.getElementById('devices-title')?.focus()
    }
  }, [])

  return <dialog ref={dialogRef} aria-labelledby="device-details-title" onCancel={event => { event.preventDefault(); closeDrawer() }}
    className="fixed inset-y-0 right-0 left-auto m-0 h-dvh max-h-none w-full max-w-xl overflow-y-auto overscroll-contain border-0 bg-white p-0 text-slate-900 shadow-xl backdrop:bg-slate-900/40">
    <div className="space-y-7 p-5 sm:p-7">
      <header className="flex items-start justify-between gap-4">
        <div><h2 id="device-details-title" ref={headingRef} tabIndex={-1} className="text-xl font-bold focus-visible:outline-2 focus-visible:outline-sky-700">Device details</h2>
          <p className="mt-1 break-all text-xs text-slate-500">{deviceId}</p></div>
        <button type="button" onClick={closeDrawer} className="rounded border border-slate-300 px-3 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-700">Close details</button>
      </header>
      <p className="text-xs text-slate-600">Synthetic observations · UTC times. Each section is evaluated independently. Press Escape to close.</p>
      <ReadSection title="Device overview" query={detail}>
        {detail.data && <>
          <h3 className="text-lg font-semibold">{detail.data.displayName}</h3>
          <dl className="grid grid-cols-2 gap-3 text-sm">
            <Fact label="Asset tag">{detail.data.assetTag}</Fact><Fact label="Site">{detail.data.siteCode}</Fact>
            <Fact label="Type">{detail.data.deviceType}</Fact><Fact label="Health">{detail.data.health}</Fact>
            <Fact label="Latency">{detail.data.latencyMs === null ? 'Not available' : `${detail.data.latencyMs} ms`}</Fact>
            <Fact label="CPU">{detail.data.cpuPercent === null ? 'Not available' : `${detail.data.cpuPercent}%`}</Fact>
            <Fact label="Last observed">{detail.data.lastObservedAtUtc ? <UtcTime value={detail.data.lastObservedAtUtc} /> : 'Not observed'}</Fact>
            <Fact label="Last success">{detail.data.lastSuccessfulAtUtc ? <UtcTime value={detail.data.lastSuccessfulAtUtc} /> : 'No successful observation'}</Fact>
            <Fact label="Registered"><UtcTime value={detail.data.createdAtUtc} /></Fact>
            <Fact label="Registration">{detail.data.retiredAtUtc ? <>Retired <UtcTime value={detail.data.retiredAtUtc} /></> : 'Active'}</Fact>
          </dl>
          <p className="text-xs text-slate-500">Health comes from the server. Offline devices can retain older successful measurements.</p>
          <Evaluation value={detail.data.asOfUtc} />
        </>}
      </ReadSection>
      <ReadSection title="Recent measurements" query={telemetry}>
        <p className="text-xs text-slate-600">Up to 10 most recent observations from the 24 hours before the evaluation time shown below.</p>
        {telemetry.data && <>
          <p className="text-sm">Showing {telemetry.data.items.length} of {telemetry.data.totalCount} observations in this window.</p>
          {telemetry.data.items.length === 0 ? <p>No observations in this window.</p> : <div className="overflow-x-auto"><table className="w-full text-left text-sm">
            <caption className="sr-only">Recent synthetic measurements</caption><thead><tr>{['Observed (UTC)', 'Outcome', 'Latency', 'CPU'].map(label => <th key={label} scope="col" className="px-2 py-2">{label}</th>)}</tr></thead>
            <tbody>{telemetry.data.items.map(item => <tr key={item.id} className="border-t border-slate-200"><td className="px-2 py-2"><UtcTime value={item.observedAtUtc} /></td><td className="px-2 py-2">{item.outcome}</td><td className="px-2 py-2">{item.latencyMs === null ? '—' : `${item.latencyMs} ms`}</td><td className="px-2 py-2">{item.cpuPercent === null ? '—' : `${item.cpuPercent}%`}</td></tr>)}</tbody>
          </table></div>}
          <Evaluation value={telemetry.data.asOfUtc} />
        </>}
      </ReadSection>
      <ReadSection title="Recent incidents" query={incidents}>
        <p className="text-xs text-slate-600">Up to 10 most recent retained incidents, all lifecycle states. This is not a complete lifetime history.</p>
        {incidents.data && <>
          <p className="text-sm">Showing {incidents.data.items.length} of {incidents.data.totalCount} retained incidents.</p>
          {incidents.data.items.length === 0 ? <p>No retained incidents.</p> : <ul className="space-y-3">{incidents.data.items.map(item => <li key={item.id} className="rounded border border-slate-200 p-3 text-sm">
            <p className="font-semibold">{item.kind} · {item.resolvedAtUtc ? 'Resolved' : 'Open'}</p>
            <p className="mt-1 break-words">{item.summary}</p><p className="mt-2">Opened <UtcTime value={item.openedAtUtc} /></p>
            {item.resolvedAtUtc && <p>Resolved <UtcTime value={item.resolvedAtUtc} /></p>}
          </li>)}</ul>}
          <Evaluation value={incidents.data.asOfUtc} />
        </>}
      </ReadSection>
    </div>
  </dialog>
}

type SectionQuery = Pick<UseQueryResult<unknown, Error>, 'isPending' | 'isError' | 'isFetching' | 'data' | 'error' | 'dataUpdatedAt' | 'refetch'>
/** All three owned read sections use the same independent failure/stale/retry treatment, not a shared snapshot. */
function ReadSection({ title, query, children }: { title: string; query: SectionQuery; children: ReactNode }) {
  return <section aria-label={title} className="space-y-3 border-t border-slate-200 pt-5">
    <div className="flex flex-wrap items-center justify-between gap-3"><h3 className="font-bold">{title}</h3>
      <button type="button" disabled={query.isFetching} onClick={() => void query.refetch({ cancelRefetch: false })}
        className="rounded border border-slate-300 px-3 py-1 text-sm focus-visible:outline-2 focus-visible:outline-sky-700 disabled:opacity-50">
        {query.isError ? 'Retry' : 'Refresh'} {title.toLowerCase()}
      </button></div>
    {query.isPending && <p role="status">Loading {title.toLowerCase()}…</p>}
    {query.isError && <div role="alert" className="rounded bg-amber-50 p-3 text-sm text-amber-950">
      <p>{query.error instanceof ReadApiError ? query.error.message : 'This section could not be loaded.'}</p>
      {query.data !== undefined && <p>Previous {title.toLowerCase()} may be stale.</p>}
    </div>}
    {children}
    {query.isFetching && query.data !== undefined && <p role="status" className="text-xs">Updating {title.toLowerCase()}…</p>}
    {query.data !== undefined && query.dataUpdatedAt > 0 && <p className="text-xs text-slate-500">Last successful fetch (UTC): <UtcTime value={new Date(query.dataUpdatedAt).toISOString()} /></p>}
  </section>
}
function Fact({ label, children }: { label: string; children: ReactNode }) { return <div className="min-w-0 break-words"><dt className="text-slate-500">{label}</dt><dd>{children}</dd></div> }
/** Display original UTC timestamps; the browser never infers domain health or lifecycle transitions. */
function UtcTime({ value }: { value: string }) { return <time dateTime={value}>{new Date(value).toLocaleString('en-CA', { timeZone: 'UTC', hour12: false })}</time> }
function Evaluation({ value }: { value: string }) { return <p className="text-xs text-slate-500">Server evaluation (UTC): <UtcTime value={value} /></p> }
