import { afterEach, beforeEach, expect, it, vi } from 'vitest'

const key = 'healthmonitor-polling-enabled'
beforeEach(() => vi.resetModules())
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks() })

/** Models a fresh application import with only the owned preference key; no browser/user storage is inspected. */
async function load(raw: string | null) {
  const storage = { getItem: vi.fn(() => raw), setItem: vi.fn() }
  vi.stubGlobal('localStorage', storage)
  const { useDashboardPreferences } = await import('./dashboardPreferences')
  return { store: useDashboardPreferences, storage }
}

it.each([[null, true], ['true', true], ['false', false], ['', true], ['FALSE', true], ['false ', true], ['{"pollingEnabled":false}', true]] as const)('reads an exact boolean choice or the enabled default (%s)', async (raw, expected) => {
  const { store, storage } = await load(raw)
  expect(store.getState().pollingEnabled).toBe(expected)
  expect(storage.getItem).toHaveBeenCalledExactlyOnceWith(key)
  expect(storage.setItem).not.toHaveBeenCalled()
})

it('persists only the boolean choice and restores it on the next application import', async () => {
  let saved: string | null = null
  const storage = { getItem: vi.fn(() => saved), setItem: vi.fn((_: string, value: string) => { saved = value }) }
  vi.stubGlobal('localStorage', storage)
  const first = (await import('./dashboardPreferences')).useDashboardPreferences
  first.getState().setPollingEnabled(false)
  expect(first.getState().pollingEnabled).toBe(false)
  expect(storage.setItem).toHaveBeenCalledExactlyOnceWith(key, 'false')
  vi.resetModules()
  const second = (await import('./dashboardPreferences')).useDashboardPreferences
  expect(second.getState().pollingEnabled).toBe(false)
  second.getState().setPollingEnabled(true)
  expect(storage.setItem).toHaveBeenLastCalledWith(key, 'true')
})

it('uses the enabled default if reading storage is denied without logging the stored value', async () => {
  const log = vi.spyOn(console, 'warn').mockImplementation(() => {})
  vi.stubGlobal('localStorage', { getItem: () => { throw new Error('private-canary') } })
  const store = (await import('./dashboardPreferences')).useDashboardPreferences
  expect(store.getState().pollingEnabled).toBe(true); expect(log).not.toHaveBeenCalled()
})

it('keeps the memory choice when writing storage fails', async () => {
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => { throw new Error('private-canary') } })
  const store = (await import('./dashboardPreferences')).useDashboardPreferences
  expect(() => store.getState().setPollingEnabled(false)).not.toThrow()
  expect(store.getState().pollingEnabled).toBe(false)
})

it('works without storage and does not introduce a persistence dependency for monitoring', async () => {
  vi.stubGlobal('localStorage', undefined)
  const store = (await import('./dashboardPreferences')).useDashboardPreferences
  expect(store.getState().pollingEnabled).toBe(true)
  expect(() => store.getState().setPollingEnabled(false)).not.toThrow()
  expect(store.getState().pollingEnabled).toBe(false)
})


it('keeps selection/open coherent without persisting it and clears selection on close', async () => {
  const { store, storage } = await load(null)
  expect(store.getState()).toMatchObject({ selectedDeviceId: null, drawerOpen: false })
  store.getState().openDevice('aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee')
  expect(store.getState()).toMatchObject({ selectedDeviceId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', drawerOpen: true })
  store.getState().openDevice('bbbbbbbb-bbbb-4ccc-8ddd-eeeeeeeeeeee')
  expect(store.getState().selectedDeviceId).toBe('bbbbbbbb-bbbb-4ccc-8ddd-eeeeeeeeeeee')
  store.getState().closeDrawer()
  expect(store.getState()).toMatchObject({ selectedDeviceId: null, drawerOpen: false })
  expect(storage.setItem).not.toHaveBeenCalled()
  expect(() => store.getState().openDevice('  ')).toThrow('A device identity is required.')
  expect(store.getState().drawerOpen).toBe(false)
})

it('does not restore selection on reload while preserving the one polling preference', async () => {
  const { store, storage } = await load('false')
  store.getState().openDevice('aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee')
  store.getState().setPollingEnabled(true)
  expect(storage.setItem).toHaveBeenCalledExactlyOnceWith(key, 'true')
  vi.resetModules()
  const reloaded = (await import('./dashboardPreferences')).useDashboardPreferences
  expect(reloaded.getState()).toMatchObject({ selectedDeviceId: null, drawerOpen: false })
})
