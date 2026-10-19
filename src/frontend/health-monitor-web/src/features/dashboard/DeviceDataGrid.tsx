import { useDashboardPreferences } from './dashboardPreferences'
import { useEffect, useMemo, useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { flexRender, getCoreRowModel, useReactTable } from '@tanstack/react-table'
import type { ColumnDef, OnChangeFn, PaginationState, SortingState, Updater } from '@tanstack/react-table'
import { deviceSites, deviceTypes, deviceHealthStates, ReadApiError } from '../../api/readApi'
import type { DeviceHealth, DeviceSort, DevicePage, DeviceRow, ReadApi } from '../../api/readApi'

const columns: ColumnDef<DeviceRow>[] = [
  { accessorKey: 'displayName', header: 'Name' },
  { accessorKey: 'assetTag', header: 'Asset tag' },
  { accessorKey: 'siteCode', header: 'Site' },
  { accessorKey: 'deviceType', header: 'Type', enableSorting: false },
  { accessorKey: 'health', header: 'Health' },
  { accessorKey: 'latencyMs', header: 'Latency', cell: context => context.row.original.latencyMs === null ? '—' : `${context.row.original.latencyMs} ms` },
  { accessorKey: 'cpuPercent', header: 'CPU', cell: context => context.row.original.cpuPercent === null ? '—' : `${context.row.original.cpuPercent}%` },
  { accessorKey: 'lastObservedAtUtc', header: 'Last observed (UTC)', cell: context => context.row.original.lastObservedAtUtc === null ? 'Not observed' : <UtcTime value={context.row.original.lastObservedAtUtc} /> },
]

/** Applied search and paging change atomically; server response data belongs only to Query. */
export type DeviceGridState = Readonly<{ pagination: PaginationState; q: string; site: string; type: string; status: DeviceHealth | ''; sorting: SortingState }>
type GridRequest = Omit<DeviceGridState, 'pagination' | 'sorting'> & { page: number; pageSize: number; sort: DeviceSort; direction: 'asc' | 'desc' }

/** UI field names never become unchecked API sort keys; Type deliberately has no mapping. */
const sortColumns: Readonly<Record<string, DeviceSort | undefined>> = {
  displayName: 'displayName', assetTag: 'assetTag', siteCode: 'siteCode', health: 'status',
  lastObservedAtUtc: 'lastObservedAtUtc', latencyMs: 'latencyMs', cpuPercent: 'cpuPercent',
}
/** Maps the dashboard's single controlled sort to wire identity, also used to reject stale corrections. */
function requestFor({ pagination, q, site, type, status, sorting }: DeviceGridState): GridRequest {
  const sort = sorting[0] ?? { id: 'displayName', desc: false }
  return { page: pagination.pageIndex + 1, pageSize: pagination.pageSize, q, site, type, status,
    sort: sortColumns[sort.id] ?? 'displayName', direction: sort.desc ? 'desc' : 'asc' }
}

/** Equal page coordinates do not imply equal results: every input also identifies preserved responses. */
function pageKey(origin: string, request: GridRequest) {
  return ['devices', origin, request] as const
}

/**
 * Presents one server-owned page, with one applied search/pagination state supplied by the dashboard.
 * Only a prior request identity is retained locally; response rows remain exclusively in Query's cache.
 */
export function DeviceDataGrid({ api, state, onStateChange }: {
  api: ReadApi
  state: DeviceGridState
  onStateChange: OnChangeFn<DeviceGridState>
}) {
  const { pagination, q, site, type, status, sorting } = state
  // Draft editing never starts requests; only explicit submission changes the applied query.
  const [draft, setDraft] = useState(q)
  const [searchError, setSearchError] = useState('')
  const pollingEnabled = useDashboardPreferences(state => state.pollingEnabled)
  const request = useMemo(() => requestFor(state), [state])
  const [preservedIdentity, setPreservedIdentity] = useState(request)
  const query = useQuery({
    queryKey: pageKey(api.origin, request),
    queryFn: ({ signal }) => api.getDevices(request, signal),
    placeholderData: keepPreviousData,
    staleTime: 2_000,
    refetchInterval: pollingEnabled ? 5_000 : false,
    refetchOnWindowFocus: pollingEnabled,
    refetchOnReconnect: pollingEnabled,
    refetchIntervalInBackground: false,
    retry: false,
  })
  // Query v5 drops placeholder data on error. A disabled observer preserves the prior cache entry's lifetime,
  // without copying rows, starting a second request, or maintaining a second cache/polling loop.
  const preserved = useQuery<DevicePage>({
    queryKey: pageKey(api.origin, preservedIdentity),
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
  // aria-sort describes the retained rows; header actions describe the requested sort until new rows arrive.
  const displayedIdentity = current && !query.isPlaceholderData && !currentIsPastLastPage ? request : preservedIdentity
  const showingPrevious = displayed !== undefined && !sameRequest(displayedIdentity, request)
  const busy = query.isFetching || correctingRange

  useEffect(() => {
    if (!query.isSuccess || query.isPlaceholderData || !current || current.items.length !== 0 || pagination.pageIndex <= lastValidIndex) return
    // A shrinking result set gets one correction to the last valid page, never an unbounded refetch loop.
    // The updater also rejects a stale correction if the user has already changed the request.
    onStateChange(previous => sameRequest(requestFor(previous), request)
      ? { ...previous, pagination: { ...previous.pagination, pageIndex: lastValidIndex } } : previous)
  }, [current, query.isSuccess, query.isPlaceholderData, pagination.pageIndex, lastValidIndex, request, onStateChange])

  /** Retains exact displayed identity, including searches sharing the same page and size. */
  function changeState(updater: Updater<DeviceGridState>) {
    if (displayed) setPreservedIdentity({ ...displayedIdentity, page: displayed.page, pageSize: displayed.pageSize })
    onStateChange(updater)
  }
  /** Navigation retains applied search; submission resets search and page together. */
  function changePagination(updater: Updater<PaginationState>) {
    changeState(previous => ({ ...previous, pagination: typeof updater === 'function' ? updater(previous.pagination) : updater }))
  }
  /** Applies literal intent only after validation; page reset and search publication are one dashboard update. */
  function applySearch(value: string) {
    const next = value.trim()
    // Match the API's post-trim UTF-16 limit without truncating literal user intent.
    if (next.length > 100) { setSearchError('Search must contain at most 100 characters after trimming.'); return }
    setSearchError(''); setDraft(next)
    changeState(previous => ({ ...previous, q: next, pagination: { ...previous.pagination, pageIndex: 0 } }))
  }
  /** Selects apply immediately to the applied search, never silently submitting draft text. */
  function applyFilter(key: 'site' | 'type' | 'status', value: string) {
    changeState(previous => ({ ...previous, [key]: value, pagination: { ...previous.pagination, pageIndex: 0 } }))
  }
  /** Resets both draft and applied intent so clearing cannot leave hidden search or filter state. */
  function clearAllFilters() {
    setDraft(''); setSearchError('')
    changeState(previous => ({ q: '', site: '', type: '', status: '', sorting: [{ id: 'displayName', desc: false }], pagination: { ...previous.pagination, pageIndex: 0 } }))
  }
  /** A single allowlisted sort changes atomically with the page reset; no client sorting row model runs. */
  function changeSorting(updater: Updater<SortingState>) {
    changeState(previous => {
      const next = typeof updater === 'function' ? updater(previous.sorting) : updater
      if (next.length !== 1 || !sortColumns[next[0].id]) return previous
      return { ...previous, sorting: next, pagination: { ...previous.pagination, pageIndex: 0 } }
    })
  }
  // Table requires a mutable array container; keep a stable adapter copy, not another server-state store.
  const rows = useMemo(() => displayed ? Array.from(displayed.items) : [], [displayed])
  // React Compiler is not configured. Keep Table v8's imperative API local; revisit before enabling compilation.
  // eslint-disable-next-line react-hooks/incompatible-library
  const table = useReactTable({
    columns, data: rows, getCoreRowModel: getCoreRowModel(), getRowId: row => row.id,
    manualPagination: true, manualSorting: true, manualFiltering: true,
    enableSorting: true, enableMultiSort: false, maxMultiSortColCount: 1, enableSortingRemoval: false, sortDescFirst: false, enableColumnFilters: false,
    state: { pagination, sorting, globalFilter: q, columnFilters: [{ id: 'siteCode', value: site }, { id: 'deviceType', value: type }, { id: 'health', value: status }].filter(filter => filter.value) }, onPaginationChange: changePagination, onSortingChange: changeSorting,
    rowCount: displayed?.totalCount ?? 0,
  })
  const fetchedAt = displayed && !query.isPlaceholderData && !correctingRange && !showingPrevious ? query.dataUpdatedAt : preserved.dataUpdatedAt
  // The total belongs to the displayed response, not a pending request with a different page size.
  const displayedPageCount = displayed ? Math.max(1, Math.ceil(displayed.totalCount / displayed.pageSize)) : undefined

  return <section aria-labelledby="devices-title" className="space-y-4">
    <div className="flex flex-wrap items-end justify-between gap-4">
      <div><h2 id="devices-title" className="text-2xl font-semibold tracking-tight">Devices</h2>
        <p className="mt-1 text-sm text-slate-600">Active registrations · Order requested: {sortLabel(request)}</p></div>
      <button type="button" disabled={busy} onClick={() => void query.refetch({ cancelRefetch: false })}
        className="rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold hover:bg-slate-100 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-700 disabled:opacity-50">
        {query.isError ? 'Retry devices' : 'Refresh devices'}
      </button>
    </div>
    <form aria-label="Search active devices" onSubmit={event => { event.preventDefault(); applySearch(draft) }} className="flex flex-wrap items-end gap-3">
      <div className="flex-1"><label htmlFor="device-search" className="block text-sm font-semibold">Search name or asset tag</label>
        <input id="device-search" type="search" value={draft} onChange={event => { setDraft(event.target.value); setSearchError('') }}
          aria-describedby="device-search-help" aria-invalid={searchError ? true : undefined}
          className="mt-1 w-full rounded border border-slate-300 bg-white p-2 focus-visible:outline-2 focus-visible:outline-sky-700" />
        <p id="device-search-help" className="mt-1 text-xs text-slate-500">Searches all active devices, up to 100 characters. Fleet metrics stay global.</p></div>
      <button type="submit" className="rounded border border-slate-300 bg-white px-4 py-2 font-semibold focus-visible:outline-2 focus-visible:outline-sky-700">Search</button>
      <button type="button" onClick={() => applySearch('')} className="rounded border border-slate-300 bg-white px-4 py-2 focus-visible:outline-2 focus-visible:outline-sky-700">Clear search</button>
    </form>
    {searchError && <p role="alert">{searchError}</p>}
    <div className="flex flex-wrap items-end gap-3">
      <label className="text-sm font-semibold">Site <select aria-label="Site" value={site} onChange={event => applyFilter('site', event.target.value)} className="ml-2 rounded border border-slate-300 bg-white p-2 focus-visible:outline-2 focus-visible:outline-sky-700">
        <option value="">All sites</option>{deviceSites.map(value => <option key={value} value={value}>{value}</option>)}
      </select></label>
      <label className="text-sm font-semibold">Type <select aria-label="Type" value={type} onChange={event => applyFilter('type', event.target.value)} className="ml-2 rounded border border-slate-300 bg-white p-2 focus-visible:outline-2 focus-visible:outline-sky-700">
        <option value="">All types</option>{deviceTypes.map(value => <option key={value} value={value}>{value}</option>)}
      </select></label>
      <label className="text-sm font-semibold">Health <select aria-label="Health" value={status} onChange={event => applyFilter('status', event.target.value)} className="ml-2 rounded border border-slate-300 bg-white p-2 focus-visible:outline-2 focus-visible:outline-sky-700">
        <option value="">All health states</option>{deviceHealthStates.map(value => <option key={value} value={value}>{value}</option>)}
      </select></label>
      <button type="button" onClick={clearAllFilters} className="rounded border border-slate-300 bg-white px-4 py-2 focus-visible:outline-2 focus-visible:outline-sky-700" aria-describedby="filter-reset-note">Clear all filters</button>
      <p className="text-xs text-slate-500">Filters combine with AND across all active devices. Fleet metrics stay global.</p>
      <p id="filter-reset-note" className="text-xs text-slate-500">Clear all filters also restores Name ascending.</p>
      <p className="text-xs text-slate-500">Header arrows show the requested order. Rows keep their previous order until the new results load.</p>
      <p className="text-xs text-slate-500">Health order: Offline, Degraded, Unknown, Healthy (ascending); missing values stay last in either direction.</p>
    </div>
    {(query.isPending || currentIsPastLastPage) && !displayed && <p role="status">Loading devices…</p>}
    {correctingRange && <p role="status">This page no longer exists. Loading the last available page…</p>}
    {showingPrevious && <p role="status" className="rounded-lg bg-sky-50 p-3 text-sm text-sky-950">
      Showing previous page {displayed.page} ({displayed.pageSize} per page), {resultLabel(displayedIdentity)}, {sortLabel(displayedIdentity)}. Requested page {request.page} ({request.pageSize} per page), {resultLabel(request)}, {sortLabel(request)} {query.isError ? 'could not be loaded.' : 'is loading.'}
    </p>}
    {query.isError && <div role="alert" className="rounded-lg border border-amber-300 bg-amber-50 p-4 text-sm text-amber-950">
      <p className="font-semibold">{displayed ? 'Device refresh failed. Displayed rows may be stale.' : 'Devices are unavailable.'}</p>
      <p>{query.error instanceof ReadApiError ? query.error.message : 'Device registrations could not be loaded.'}</p>
    </div>}
    {displayed && <>
      <p className="text-sm text-slate-600">Displayed results: {resultLabel(displayedIdentity)}</p>
      <p className="text-sm text-slate-600">{displayed.totalCount} matching active devices · {displayed.items.length} displayed on page {displayed.page}</p>
      {displayed.totalCount === 0 ? <p role="status" className="rounded-xl border border-slate-200 bg-white p-5">{(displayedIdentity.q || displayedIdentity.site || displayedIdentity.type || displayedIdentity.status) ? 'No active devices match these filters.' : 'No active devices.'}</p>
        : <div role="region" aria-label="Device table, scroll horizontally if needed" tabIndex={0} className="overflow-x-auto rounded-xl border border-slate-200 bg-white focus-visible:outline-2 focus-visible:outline-sky-700">
          <table className="w-full min-w-240 text-left text-sm">
            <caption className="sr-only">Active device registrations</caption>
            <thead className="border-b border-slate-200 bg-slate-100 text-xs text-slate-600">
              {table.getHeaderGroups().map(group => <tr key={group.id}>{group.headers.map(header => <th key={header.id} scope="col" aria-sort={sortColumns[header.column.id] === displayedIdentity.sort ? (displayedIdentity.direction === 'asc' ? 'ascending' : 'descending') : undefined} className="whitespace-nowrap px-4 py-3 font-semibold">
                {header.isPlaceholder ? null : header.column.getCanSort() ? <button type="button" onClick={header.column.getToggleSortingHandler()}
                  aria-label={`Sort by ${header.column.columnDef.header} ${header.column.getIsSorted() === 'asc' ? 'descending' : 'ascending'}`}
                  className="rounded focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-700">
                  {flexRender(header.column.columnDef.header, header.getContext())}<span aria-hidden="true">{header.column.getIsSorted() === 'asc' ? ' ↑' : header.column.getIsSorted() === 'desc' ? ' ↓' : ''}</span>
                </button> : flexRender(header.column.columnDef.header, header.getContext())}
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

/** Literal user text stays React text; it is never interpreted as markup or a filter expression. */
function resultLabel({ q, site, type, status }: GridRequest): string {
  const filters = [q ? `search “${q}”` : '', site ? `site ${site}` : '', type ? `type ${type}` : '', status ? `health ${status}` : ''].filter(Boolean)
  return filters.length ? filters.join(' · ') : 'all active devices'
}

/** A stale response may affect neither navigation nor labels of another applied query at equal coordinates. */
function sameRequest(left: GridRequest, right: GridRequest): boolean {
  return left.page === right.page && left.pageSize === right.pageSize && left.q === right.q
    && left.site === right.site && left.type === right.type && left.status === right.status
    && left.sort === right.sort && left.direction === right.direction
}

/** Sort vocabulary describes server behavior without reimplementing comparisons in the browser. */
function sortLabel({ sort, direction }: GridRequest): string {
  const names = { displayName: 'Name', assetTag: 'Asset tag', siteCode: 'Site', status: 'Health', lastObservedAtUtc: 'Last observed', latencyMs: 'Latency', cpuPercent: 'CPU' }
  return `${names[sort]} ${direction === 'asc' ? 'ascending' : 'descending'}`
}
