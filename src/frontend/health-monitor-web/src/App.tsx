import { useState } from 'react'
import type { PaginationState } from '@tanstack/react-table'
import { createReadApi, ReadApiError } from './api/readApi'
import type { ReadApi } from './api/readApi'
import { MetricsOverview } from './features/dashboard/MetricsOverview'
import { DeviceDataGrid } from './features/dashboard/DeviceDataGrid'

/** The first read-only dashboard slice; unavailable AI never gates ordinary monitoring reads. */
function App() {
  // Current grid coordinates have one dashboard owner; Query alone owns response data.
  const [pagination, setPagination] = useState<PaginationState>({ pageIndex: 0, pageSize: 25 })
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
        {api ? <div className="space-y-12"><MetricsOverview api={api} /><DeviceDataGrid api={api} pagination={pagination} onPaginationChange={setPagination} /></div> : <div role="alert" className="rounded-xl border border-amber-300 bg-amber-50 p-6">
          <h2 className="font-semibold">Connect the monitoring API</h2>
          <p className="mt-2 text-sm">{configurationError} See the project README for local setup.</p>
        </div>}
      </main>
      <footer className="mx-auto max-w-6xl px-6 pb-8 text-xs text-slate-500">Read-only fleet monitoring · Fictional infrastructure</footer>
    </div>
  )
}

export default App
