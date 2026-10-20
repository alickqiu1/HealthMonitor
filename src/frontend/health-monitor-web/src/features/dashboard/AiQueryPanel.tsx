import { useEffect, useMemo, useRef, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { AiQueryError, createAiQueryApi } from '../../api/aiQueryApi'
import type { AiDeviceQuery, AiTranslation } from '../../api/aiQueryApi'

type View = { generation: number; origin: string; pending: boolean; error: string; result: AiTranslation | null; notice: string }
const sortLabels: Readonly<Record<string, string>> = { displayName: 'Name', assetTag: 'Asset tag', siteCode: 'Site', status: 'Health', lastObservedAtUtc: 'Last observed', latencyMs: 'Latency', cpuPercent: 'CPU' }
const idle = (origin: string): View => ({ generation: 0, origin, pending: false, error: '', result: null, notice: '' })

/**
 * Owns an explicit translation submission and unapplied preview, independently of monitoring polling.
 * Generation ownership, rather than abort success, decides which response may enable Apply.
 */
export function AiQueryPanel({ origin, onApply, onClear }: {
  origin: string; onApply: (query: AiDeviceQuery) => void; onClear: () => void
}) {
  const api = useMemo(() => createAiQueryApi(origin), [origin])
  const [prompt, setPrompt] = useState('')
  const [view, setView] = useState<View>(() => idle(origin))
  const generation = useRef(0)
  const active = useRef<{ id: number; controller: AbortController } | null>(null)
  const mutation = useMutation({
    mutationKey: ['ai-device-query', 'v1'], retry: false, gcTime: 0,
    mutationFn: ({ text, signal }: { text: string; signal: AbortSignal }) => api.translate(text, signal),
  })
  const reset = mutation.reset
  useEffect(() => () => {
    generation.current++; active.current?.controller.abort(); active.current = null; reset()
  }, [api, reset])
  // Mutation variables can retain an in-flight prompt until settlement. Nothing is persisted or used as a GET cache key.
  // Reset does not cancel a request or prove that its server/provider stopped; abort remains best effort.
  const current = view.origin === origin ? view : idle(origin)
  function invalidate() {
    generation.current++; active.current?.controller.abort(); active.current = null; reset(); setView(idle(origin))
  }
  async function submit() {
    if (active.current || !prompt.trim()) return
    const id = ++generation.current; const controller = new AbortController()
    active.current = { id, controller }; setView({ ...idle(origin), pending: true })
    try {
      const result = await mutation.mutateAsync({ text: prompt, signal: controller.signal })
      if (generation.current === id && !controller.signal.aborted) setView({ ...idle(origin), generation: id, result })
    } catch (error) {
      if (generation.current === id && !controller.signal.aborted)
        setView({ ...idle(origin), error: error instanceof AiQueryError ? error.message : 'AI search is unavailable. You can use manual filters.' })
    } finally {
      // An older request's finally must never clear a newer request's pending ownership.
      if (generation.current === id) { active.current = null; reset() }
    }
  }
  return <section aria-labelledby="ai-search-heading" className="rounded-xl border border-slate-200 bg-white p-5">
    <h2 id="ai-search-heading" className="text-lg font-semibold">AI filter assistant</h2>
    <p id="ai-search-help" className="mt-2 text-sm text-slate-600">Describe device filters or sorting. Review the proposal before applying it. Manual filters always remain available. For example: “Show offline servers in Calgary”.</p>
    <form className="mt-4 space-y-3" onSubmit={event => { event.preventDefault(); void submit() }}>
      <label htmlFor="ai-search-prompt" className="block text-sm font-semibold">Describe the devices to show</label>
      <textarea id="ai-search-prompt" value={prompt} aria-describedby="ai-search-help ai-search-count" rows={3}
        className="w-full rounded border border-slate-300 p-3 focus-visible:outline-2 focus-visible:outline-sky-700"
        onChange={event => { invalidate(); setPrompt(event.target.value) }} />
      <p id="ai-search-count" className="text-xs text-slate-600">{prompt.trim().length}/500 characters after trimming</p>
      <div className="flex gap-3">
        <button type="submit" disabled={current.pending || !prompt.trim()} className="rounded bg-sky-700 px-4 py-2 font-semibold text-white disabled:opacity-50">{current.pending ? 'Translating…' : 'Preview filters'}</button>
        <button type="button" onClick={() => { invalidate(); setPrompt(''); onClear() }} className="rounded border border-slate-300 px-4 py-2 font-semibold">Clear AI filters</button>
      </div>
    </form>
    <div role="status" aria-live="polite" className="mt-3 text-sm">
      {current.pending && 'Creating a filter proposal. Your current grid is unchanged.'}
      {current.error || current.notice}
      {current.result?.outcome === 'NeedsClarification' && 'Please name Healthy, Degraded, Offline, or Unknown and any desired site or device type.'}
      {current.result?.outcome === 'Unsupported' && 'Only supported device filter queries can be proposed.'}
    </div>
    {current.result?.outcome === 'Ready' && <div className="mt-4 rounded border border-sky-200 bg-sky-50 p-4">
      <h3 className="font-semibold">Proposed filters</h3>
      <dl className="mt-2 grid gap-2 text-sm sm:grid-cols-2">
        {Object.entries(current.result.query).map(([key, value]) => <div key={key}><dt className="font-semibold">{({ q: 'Search', site: 'Site', type: 'Device type', status: 'Health', sort: 'Sort', direction: 'Direction' } as Record<string, string>)[key]}</dt><dd>{key === 'sort' ? sortLabels[value!] : key === 'direction' ? value === 'asc' ? 'Ascending' : 'Descending' : value ?? 'Any'}</dd></div>)}
      </dl>
      <p className="mt-3 text-xs text-slate-600">This proposal changes filters only. Health is determined by the monitoring rules; the assistant does not diagnose devices.</p>
      {/* These fixed rules mirror Domain TelemetryThresholds; server health remains authoritative. */}
      {(current.result.query.status === 'Degraded' || current.result.query.status === 'Offline') && <p className="mt-2 text-xs text-slate-600">
        Monitoring rules: a fresh successful observation is Degraded when latency is greater than 250 ms or CPU is greater than 80%. A timeout or an observation older than 90 seconds is Offline. Exactly 90 seconds is fresh; exactly 250 ms and 80% remain within their healthy thresholds. Devices without observations are Unknown.
      </p>}
      <button type="button" className="mt-3 rounded bg-sky-700 px-4 py-2 font-semibold text-white" onClick={() => {
        if (current.result?.outcome !== 'Ready' || current.generation !== generation.current) return
        onApply(current.result.query)
        setView({ ...current, notice: 'The proposed filters were applied. Manual changes remain available.' })
      }}>Apply proposed filters</button>
    </div>}
  </section>
}
