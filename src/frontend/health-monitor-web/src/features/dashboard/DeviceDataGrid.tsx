import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { flexRender, getCoreRowModel, useReactTable } from '@tanstack/react-table'
import type { ColumnDef, OnChangeFn, PaginationState, Updater } from '@tanstack/react-table'
import { ReadApiError } from '../../api/readApi'
import type { DevicePage, DeviceRow, ReadApi } from '../../api/readApi'

const columns: ColumnDef<DeviceRow>[] = [
  { accessorKey: 'displayName', header: 'Name' },
  { accessorKey: 'assetTag', header: 'Asset tag' },
  { accessorKey: 'siteCode', header: 'Site' },
  { accessorKey: 'deviceType', header: 'Type' },
  { accessorKey: 'health', header: 'Health' },
  { accessorKey: 'latencyMs', header: 'Latency', cell: context => context.row.original.latencyMs === null ? '—' : `${context.row.original.latencyMs} ms` },
  { accessorKey: 'cpuPercent', header: 'CPU', cell: context => context.row.original.cpuPercent === null ? '—' : `${context.row.original.cpuPercent}%` },
  { accessorKey: 'lastObservedAtUtc', header: 'Last observed (UTC)', cell: context => context.row.original.lastObservedAtUtc === null ? 'Not observed' : <UtcTime value={context.row.original.lastObservedAtUtc} /> },
]

/** Builds the single cache identity for the current fixed-order request; future filters must join this same key. */
function pageKey(origin: string, page: number, pageSize: number) {
  return ['devices', origin, { page, pageSize, sort: 'displayName', direction: 'asc' }] as const
}

/**
 * Presents one server-owned page, with one authoritative pagination state supplied by the dashboard.
 * Only a prior request identity is retained locally; response rows remain exclusively in Query's cache.
 */
export function DeviceDataGrid({ api, pagination, onPaginationChange }: {
  api: ReadApi
  pagination: PaginationState
  onPaginationChange: OnChangeFn<PaginationState>
}) {
  const request = useMemo(() => ({ page: pagination.pageIndex + 1, pageSize: pagination.pageSize }), [pagination.pageIndex, pagination.pageSize])
  const [preservedIdentity, setPreservedIdentity] = useState(request)
  const query = useQuery({
    queryKey: pageKey(api.origin, request.page, request.pageSize),
    queryFn: ({ signal }) => api.getDevices(request, signal),
    placeholderData: keepPreviousData,
    staleTime: 2_000,
    refetchInterval: 5_000,
    refetchIntervalInBackground: false,
    retry: false,
  })
  // Query v5 drops placeholder data on error. A disabled observer preserves the prior cache entry's lifetime,
  // without copying rows, starting a second request, or maintaining a second cache/polling loop.
  const preserved = useQuery<DevicePage>({
    queryKey: pageKey(api.origin, preservedIdentity.page, preservedIdentity.pageSize),
    // A skipToken here overwrites the shared query function and breaks ordinary cache invalidation.
    queryFn: ({ signal }) => api.getDevices(preservedIdentity, signal),
    enabled: false,
    retry: false,
  })
  const current = query.data
  const lastValidIndex = current ? Math.max(0, Math.ceil(current.totalCount / current.pageSize) - 1) : 0
  const currentIsPastLastPage = pastLastPage(current)
  const correctingRange = !query.isPlaceholderData && currentIsPastLastPage
  const displayed = (currentIsPastLastPage ? undefined : current) ?? (pastLastPage(preserved.data) ? undefined : preserved.data)
  const showingPrevious = displayed !== undefined && (displayed.page !== request.page || displayed.pageSize !== request.pageSize)
  const busy = query.isFetching || correctingRange

  useEffect(() => {
    if (!query.isSuccess || query.isPlaceholderData || !current || current.items.length !== 0 || pagination.pageIndex <= lastValidIndex) return
    // A shrinking result set gets one correction to the last valid page, never an unbounded refetch loop.
    // The updater also rejects a stale correction if the user has already changed the request.
    onPaginationChange(previous => previous.pageIndex === current.page - 1 && previous.pageSize === current.pageSize
      ? { ...previous, pageIndex: lastValidIndex } : previous)
  }, [current, query.isSuccess, query.isPlaceholderData, pagination.pageIndex, lastValidIndex, onPaginationChange])

  /** Retains only the displayed request identity before navigation; Query remains the sole owner of its rows. */
  function changePagination(updater: Updater<PaginationState>) {
    if (displayed) setPreservedIdentity(previous => previous.page === displayed.page && previous.pageSize === displayed.pageSize
      ? previous : { page: displayed.page, pageSize: displayed.pageSize })
    onPaginationChange(updater)
  }
  // Table requires a mutable array container; keep a stable adapter copy, not another server-state store.
  const rows = useMemo(() => displayed ? Array.from(displayed.items) : [], [displayed])
  // React Compiler is not configured. Keep Table v8's imperative API local; revisit before enabling compilation.
  // eslint-disable-next-line react-hooks/incompatible-library
  const table = useReactTable({
    columns, data: rows, getCoreRowModel: getCoreRowModel(), getRowId: row => row.id,
    manualPagination: true, manualSorting: true, manualFiltering: true,
    enableSorting: false, enableColumnFilters: false,
    state: { pagination }, onPaginationChange: changePagination,
    rowCount: displayed?.totalCount ?? 0,
  })
  const fetchedAt = displayed && !query.isPlaceholderData && !correctingRange && !showingPrevious ? query.dataUpdatedAt : preserved.dataUpdatedAt
  // The total belongs to the displayed response, not a pending request with a different page size.
  const displayedPageCount = displayed ? Math.max(1, Math.ceil(displayed.totalCount / displayed.pageSize)) : undefined

  return <section aria-labelledby="devices-title" className="space-y-4">
    <div className="flex flex-wrap items-end justify-between gap-4">
      <div><h2 id="devices-title" className="text-2xl font-semibold tracking-tight">Devices</h2>
        <p className="mt-1 text-sm text-slate-600">Active registrations · Server order: name ascending</p></div>
      <button type="button" disabled={busy} onClick={() => void query.refetch({ cancelRefetch: false })}
        className="rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold hover:bg-slate-100 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-700 disabled:opacity-50">
        {query.isError ? 'Retry devices' : 'Refresh devices'}
      </button>
    </div>
    {(query.isPending || currentIsPastLastPage) && !displayed && <p role="status">Loading devices…</p>}
    {correctingRange && <p role="status">This page no longer exists. Loading the last available page…</p>}
    {showingPrevious && <p role="status" className="rounded-lg bg-sky-50 p-3 text-sm text-sky-950">
      Showing previous page {displayed.page} ({displayed.pageSize} per page). Requested page {request.page} ({request.pageSize} per page) {query.isError ? 'could not be loaded.' : 'is loading.'}
    </p>}
    {query.isError && <div role="alert" className="rounded-lg border border-amber-300 bg-amber-50 p-4 text-sm text-amber-950">
      <p className="font-semibold">{displayed ? 'Device refresh failed. Displayed rows may be stale.' : 'Devices are unavailable.'}</p>
      <p>{query.error instanceof ReadApiError ? query.error.message : 'Device registrations could not be loaded.'}</p>
    </div>}
    {displayed && <>
      <p className="text-sm text-slate-600">{displayed.totalCount} matching active devices · {displayed.items.length} displayed on page {displayed.page}</p>
      {displayed.totalCount === 0 ? <p role="status" className="rounded-xl border border-slate-200 bg-white p-5">No active devices.</p>
        : <div role="region" aria-label="Device table, scroll horizontally if needed" tabIndex={0} className="overflow-x-auto rounded-xl border border-slate-200 bg-white focus-visible:outline-2 focus-visible:outline-sky-700">
          <table className="w-full min-w-240 text-left text-sm">
            <caption className="sr-only">Active device registrations</caption>
            <thead className="border-b border-slate-200 bg-slate-100 text-xs text-slate-600">
              {table.getHeaderGroups().map(group => <tr key={group.id}>{group.headers.map(header => <th key={header.id} scope="col" aria-sort={header.column.id === 'displayName' ? 'ascending' : undefined} className="whitespace-nowrap px-4 py-3 font-semibold">
                {header.isPlaceholder ? null : flexRender(header.column.columnDef.header, header.getContext())}
              </th>)}</tr>)}
            </thead>
            <tbody>{table.getRowModel().rows.map(row => <tr key={row.id} className="border-b border-slate-100 last:border-0">
              {row.getVisibleCells().map(cell => <td key={cell.id} className="whitespace-nowrap px-4 py-3">{flexRender(cell.column.columnDef.cell, cell.getContext())}</td>)}
            </tr>)}</tbody>
          </table>
        </div>}
      <p className="text-xs text-slate-500">Health is evaluated by the server. Offline rows can retain older successful measurements; check the observation time.</p>
      <div className="flex flex-wrap gap-x-8 gap-y-2 text-xs text-slate-600">
        <p>Device evaluation (UTC): <UtcTime value={displayed.asOfUtc} /></p>
        {fetchedAt > 0 && <p>Device fetch (UTC): <UtcTime value={new Date(fetchedAt).toISOString()} /></p>}
      </div>
    </>}
    <div className="flex flex-wrap items-center justify-between gap-4">
      <label className="text-sm">Devices per page <select aria-label="Devices per page" value={pagination.pageSize} disabled={busy}
        onChange={event => changePagination({ pageIndex: 0, pageSize: Number(event.target.value) })}
        className="ml-2 rounded border border-slate-300 bg-white p-2 focus-visible:outline-2 focus-visible:outline-sky-700">
        {[10, 25, 50, 100].map(size => <option key={size} value={size}>{size}</option>)}
      </select></label>
      <div className="flex items-center gap-3 text-sm">
        <button type="button" disabled={correctingRange || (query.isFetching && !query.isPlaceholderData) || !table.getCanPreviousPage()} onClick={() => table.previousPage()} className="rounded border border-slate-300 bg-white px-3 py-2 focus-visible:outline-2 focus-visible:outline-sky-700 disabled:opacity-40">Previous page</button>
        <span>{displayed ? `Page ${displayed.page} of ${displayedPageCount}` : `Requested page ${request.page}`}</span>
        <button type="button" disabled={busy || showingPrevious || !table.getCanNextPage()} onClick={() => table.nextPage()} className="rounded border border-slate-300 bg-white px-3 py-2 focus-visible:outline-2 focus-visible:outline-sky-700 disabled:opacity-40">Next page</button>
      </div>
    </div>
  </section>
}

/** Both the table and measurement descriptions explicitly preserve UTC rather than silently switching time zones. */
function UtcTime({ value }: { value: string }) {
  return <time dateTime={value}>{new Date(value).toLocaleString('en-CA', { timeZone: 'UTC', hour12: false })}</time>
}
/** An empty page beyond the current result set is a navigation correction, not an empty fleet. */
function pastLastPage(page: DevicePage | undefined): boolean {
  return page !== undefined && page.items.length === 0 && page.page > Math.max(1, Math.ceil(page.totalCount / page.pageSize))
}
