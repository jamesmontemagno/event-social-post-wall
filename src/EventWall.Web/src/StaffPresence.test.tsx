import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import StaffPresence from './StaffPresence'

const staff = [
  { username: 'Admin', isAdministrator: true, lastSeenAtUtc: new Date().toISOString() },
  { username: 'james', isAdministrator: false, lastSeenAtUtc: new Date().toISOString() },
]

beforeEach(() => vi.useFakeTimers())
afterEach(() => { cleanup(); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

it('waits for CSRF, updates every 3 minutes and stops when unmounted', async () => {
  const fetchMock = vi.fn().mockResolvedValue(new Response(JSON.stringify(staff)))
  vi.stubGlobal('fetch', fetchMock)
  const onLogout = vi.fn()
  const view = render(<StaffPresence csrf="" onLogout={onLogout} />)
  expect(fetchMock).not.toHaveBeenCalled()
  await act(async () => view.rerender(<StaffPresence csrf="token" onLogout={onLogout} />))
  expect(screen.getAllByRole('listitem')).toHaveLength(2)
  fetchMock.mockResolvedValue(new Response(JSON.stringify([staff[1]])))
  await act(async () => vi.advanceTimersByTimeAsync(179999))
  expect(fetchMock).toHaveBeenCalledTimes(1)
  await act(async () => vi.advanceTimersByTimeAsync(1))
  expect(screen.getAllByRole('listitem')).toHaveLength(1)
  expect(screen.queryByText('Admin')).toBeNull()
  expect(new Headers(fetchMock.mock.calls[0][1].headers).get('X-CSRF-TOKEN')).toBe('token')
  view.unmount()
  await act(async () => vi.advanceTimersByTimeAsync(360000))
  expect(fetchMock).toHaveBeenCalledTimes(2)
})

it('clears stale names on failure, recovers and refreshes when returning to the tab', async () => {
  const fetchMock = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify(staff)))
    .mockRejectedValueOnce(new TypeError('Network unavailable'))
    .mockResolvedValue(new Response(JSON.stringify([staff[1]])))
  vi.stubGlobal('fetch', fetchMock)
  await act(async () => { render(<StaffPresence csrf="token" onLogout={vi.fn()} />) })
  await act(async () => vi.advanceTimersByTimeAsync(180000))
  expect(screen.queryByRole('list')).toBeNull()
  expect(screen.getByRole('alert').textContent).toContain('Online status unavailable')
  Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
  await act(async () => fireEvent(document, new Event('visibilitychange')))
  expect(screen.queryByRole('alert')).toBeNull()
  expect(screen.getAllByRole('listitem')).toHaveLength(1)
})

it('signs out an expired session and does not overlap pending heartbeats', async () => {
  let finish: (response: Response) => void = () => { throw new Error('Heartbeat was not started.') }
  const fetchMock = vi.fn(() => new Promise<Response>(resolve => { finish = resolve }))
  vi.stubGlobal('fetch', fetchMock)
  const onLogout = vi.fn()
  await act(async () => { render(<StaffPresence csrf="token" onLogout={onLogout} />) })
  await act(async () => vi.advanceTimersByTimeAsync(180000))
  expect(fetchMock).toHaveBeenCalledTimes(1)
  await act(async () => finish(new Response('{}', { status: 401 })))
  expect(onLogout).toHaveBeenCalledOnce()
  expect(screen.queryByRole('list')).toBeNull()
})
