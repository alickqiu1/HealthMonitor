import { create } from 'zustand'

const storageKey = 'healthmonitor-polling-enabled'
type DashboardPreferences = {
  pollingEnabled: boolean
  setPollingEnabled: (enabled: boolean) => void
}

/** Reads only this application's nonsecret choice. Missing, malformed or inaccessible storage preserves the enabled default. */
function readPollingChoice(): boolean {
  try { return globalThis.localStorage?.getItem(storageKey) !== 'false' }
  catch { return true }
}

/**
 * Shared UI preference only: Query retains every response, page and freshness value.
 * Persistence is best-effort and reload-based; this does not implement cross-tab synchronization.
 * Pausing changes background refresh policy, not current requests, server health or backend telemetry production.
 */
export const useDashboardPreferences = create<DashboardPreferences>(set => ({
  pollingEnabled: readPollingChoice(),
  setPollingEnabled: enabled => {
    set({ pollingEnabled: enabled })
    // Blocked/full storage must not prevent the in-memory control or ordinary monitoring reads from working.
    try { globalThis.localStorage?.setItem(storageKey, enabled ? 'true' : 'false') }
    catch { /* The current session still uses the selected preference; no other storage is read or cleared. */ }
  },
}))
