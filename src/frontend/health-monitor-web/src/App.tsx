import { useDashboardPreferences } from './features/dashboard/dashboardPreferences'
import { useState } from 'react'
import { createReadApi, ReadApiError } from './api/readApi'
import type { ReadApi } from './api/readApi'
import { MetricsOverview } from './features/dashboard/MetricsOverview'
import { DeviceDataGrid } from './features/dashboard/DeviceDataGrid'
import type { DeviceGridState } from './features/dashboard/DeviceDataGrid'

/** The first read-only dashboard slice; unavailable AI never gates ordinary monitoring reads. */
function App() {
  const pollingEnabled = useDashboardPreferences(state => state.pollingEnabled)
  const setPollingEnabled = useDashboardPreferences(state => state.setPollingEnabled)
  // Applied search and paging change atomically with one dashboard owner; Query alone owns response data.
  const [grid, setGrid] = useState<DeviceGridState>({ pagination: { pageIndex: 0, pageSize: 25 }, q: '', site: '', type: '', status: '', sorting: [{ id: 'displayName', desc: false }] })
  let api: ReadApi | undefined
  let configurationError = 'The monitoring API connection is unavailable.'
  // Only configuration parsing is caught here. Rendering happens outside this boundary.
  try { api = createReadApi(import.meta.env.VITE_API_BASE_URL) }
  catch (error) { if (error instanceof ReadApiError) configurationError = error.message }
  return (
    <div className="min-h-screen bg-slate-50 text-slate-900">
      <header className="border-b border-slate-200 bg-white">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-4 px-6 py-5">
          <div><h1 className="text-xl font-bold tracking-tight">HealthMonitor</h1><p className="mt-1 text-sm text-slate-500">Infrastructure health, at a glance.</p></div>
          <span className="rounded-full border border-sky-200 bg-sky-50 px-3 py-1 text-xs font-semibold text-sky-800">Synthetic data</span>
        </div>
      </header>
      <main className="mx-auto max-w-6xl px-6 py-10">
        {api ? <div className="space-y-12">
          <div className="rounded-xl border border-slate-200 bg-white p-4">
            <div className="flex flex-wrap items-center gap-3">
              <label className="flex items-center gap-2 text-sm font-semibold">
                <input type="checkbox" checked={pollingEnabled} onChange={event => setPollingEnabled(event.target.checked)} aria-describedby="automatic-updates-note"
                  className="size-4 accent-sky-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-700" />
                Automatic updates
              </label>
              <span aria-live="polite" className="text-xs text-slate-500">{pollingEnabled ? 'Every 5 seconds' : 'Paused'}</span>
            </div>
            <p id="automatic-updates-note" className="mt-2 text-xs text-slate-600">Pauses background refresh only. Opening the dashboard, manual refresh and page changes still load data. Requests already running may finish.</p>
          </div>
          <MetricsOverview api={api} /><DeviceDataGrid api={api} state={grid} onStateChange={setGrid} /></div> : <div role="alert" className="rounded-xl border border-amber-300 bg-amber-50 p-6">
          <h2 className="font-semibold">Connect the monitoring API</h2>
          <p className="mt-2 text-sm">{configurationError} See the project README for local setup.</p>
        </div>}
      </main>
      <footer className="mx-auto max-w-6xl px-6 pb-8 text-xs text-slate-500">Read-only fleet monitoring · Fictional infrastructure</footer>
    </div>
  )
}

export default App
