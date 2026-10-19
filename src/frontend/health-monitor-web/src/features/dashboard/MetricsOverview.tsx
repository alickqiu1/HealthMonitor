import { useDashboardPreferences } from './dashboardPreferences'
import { useQuery } from '@tanstack/react-query'
import { ReadApiError } from '../../api/readApi'
import type { ReadApi } from '../../api/readApi'

/** Shows global active-fleet metrics. Query owns cancellation, cache and polling; UI never mirrors that data in another store. */
export function MetricsOverview({ api }: { api: ReadApi }) {
  const pollingEnabled = useDashboardPreferences(state => state.pollingEnabled)
  const query = useQuery({
    // Never reuse a cached summary from a different configured API origin.
    queryKey: ['fleet-metrics', api.origin],
    queryFn: ({ signal }) => api.getMetrics(signal),
    refetchInterval: pollingEnabled ? 5_000 : false,
    refetchOnWindowFocus: pollingEnabled,
    refetchOnReconnect: pollingEnabled,
    refetchIntervalInBackground: false,
    retry: false,
  })
  const data = query.data
  const cards = data ? [
    ['Active devices', data.total, 'text-slate-900'],
    ['Healthy', data.healthy, 'text-emerald-700'],
    ['Degraded', data.degraded, 'text-amber-700'],
    ['Offline', data.offline, 'text-red-700'],
    ['Unknown', data.unknown, 'text-slate-600'],
    ['Average latency', data.averageLatencyMs === null ? '—' : `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(data.averageLatencyMs)} ms`, 'text-slate-900'],
  ] : []
  return (
    <section aria-labelledby="fleet-title" className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div><h2 id="fleet-title" className="text-2xl font-semibold tracking-tight">Fleet overview</h2>
          <p className="mt-1 text-sm text-slate-600">All active devices. Summary values are fleet-wide.</p></div>
        <button type="button" disabled={query.isFetching} onClick={() => void query.refetch({ cancelRefetch: false })}
          className="rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold hover:bg-slate-100 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-700 disabled:opacity-50">
          {query.isError ? 'Retry' : 'Refresh'}
        </button>
      </div>
      {query.isPending && <p role="status" className="rounded-xl border border-slate-200 bg-white p-6">Loading fleet metrics…</p>}
      {query.isError && <div role="alert" className="rounded-xl border border-amber-300 bg-amber-50 p-4 text-amber-950">
        <p className="font-semibold">{data ? 'Refresh failed — showing the last successful summary.' : 'Fleet metrics are unavailable.'}</p>
        <p className="mt-1 text-sm">{query.error instanceof ReadApiError ? query.error.message : 'Fleet metrics could not be loaded.'}</p>
      </div>}
      {data && <>
        {data.total === 0 && <p role="status" className="rounded-xl border border-slate-200 bg-white p-5">No active devices. Metrics will update when device registrations are available.</p>}
        <dl className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {cards.map(([label, value, color]) => <div key={label} className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm">
            <dt className="text-sm font-medium text-slate-600">{label}</dt>
            <dd className={`mt-3 text-3xl font-semibold tabular-nums ${color}`}>{value}</dd>
          </div>)}
        </dl>
        <div className="flex flex-wrap gap-x-8 gap-y-2 text-xs text-slate-600">
          <p>Server evaluation (UTC): <UtcTime value={data.asOfUtc} /></p>
          <p>Last successful fetch (UTC): <UtcTime value={new Date(query.dataUpdatedAt).toISOString()} /></p>
          <p>Fresh latency samples: {data.sampleCount}</p>
        </div>
        {/* A failed refresh retains data, so freshness must remain visible rather than presenting cached values as current. */}
        <p className="text-sm text-slate-500">{query.isFetching ? 'Refreshing…' : query.isError ? 'Summary may be stale. Retry to update.' : pollingEnabled ? 'Refreshes every 5 seconds while this page is active.' : 'Automatic updates are paused.'}</p>
      </>}
    </section>
  )
}

/** Keeps the server evaluation and client fetch instants separate, both explicitly shown in UTC. */
function UtcTime({ value }: { value: string }) {
  return <time dateTime={value}>{new Date(value).toLocaleString('en-CA', { timeZone: 'UTC', hour12: false })}</time>
}
