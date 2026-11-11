import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import EventReport from './EventReport'
import type { EventReportData, ReportCounts } from './EventReport'

afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers() })
const counts: ReportCounts = {
  total: 4, pending: 1, live: 2, rejected: 1, withdrawn: 0, imagePosts: 1, images: 2, flaggedPosts: 1,
  everApproved: 3, everRejected: 2, everWithdrawn: 1, staffActions: 5, filterActions: 2, sourceActions: 1,
  repeatStaffActions: 2, decisionSamples: 2, medianDecisionSeconds: 30, p95DecisionSeconds: 90,
  oldestPendingSeconds: 600, approvalRate: 2 / 3,
}
function report(): EventReportData {
  return {
    generatedAtUtc: '2026-11-10T18:00:00Z', eventName: 'Reporting test event',
    filters: { fromUtc: null, untilUtc: null, source: null, timeZone: 'UTC' },
    eligibleSources: ['Portal', 'Manual'], excludedSources: ['X'], counts,
    platforms: [{ source: 'Portal', counts, blockedAccounts: 0 }],
    firstReceivedUtc: '2026-11-10T17:00:00Z', lastReceivedUtc: '2026-11-10T18:00:00Z', granularity: 'hour',
    timeline: [
      { startUtc: '2026-11-10T17:00:00Z', label: '2026-11-10 17:00 +00:00', intake: { Portal: 4 }, approved: 2, rejected: 1, withdrawn: 0, filterActions: 2, sourceActions: 1 },
      { startUtc: '2026-11-10T18:00:00Z', label: '2026-11-10 18:00 +00:00', intake: {}, approved: 0, rejected: 0, withdrawn: 0, filterActions: 0, sourceActions: 0 },
    ],
    findings: [{ reason: 'Profanity', count: 1 }], pendingAges: [{ label: '5-30 minutes', count: 1 }],
    reviewers: [{ name: 'Admin', approved: 2, rejected: 1, withdrawn: 0 }],
    operations: { submissionsOpen: true, activePromos: 1, totalPromos: 2, sources: [{ provider: 'X', state: 'disabled', lastSuccessfulUtc: null, simulated: false }] },
  }
}
const json = (value: EventReportData) => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } })

it('loads only on explicit clicks, displays exact chart data and keeps the snapshot across tab changes', async () => {
  const fetch = vi.fn(async (_input: RequestInfo | URL) => json(report()))
  const print = vi.fn()
  vi.stubGlobal('fetch', fetch)
  vi.stubGlobal('print', print)
  const onError = vi.fn()
  const { rerender } = render(<EventReport active onError={onError} />)
  expect(fetch).not.toHaveBeenCalled()
  expect((screen.getByRole('button', { name: 'Print / Save PDF' }) as HTMLButtonElement).disabled).toBe(true)
  expect(screen.getByRole('link', { name: 'Export CSV' }).getAttribute('href')).toBe('/api/admin/export.csv')
  expect(screen.getByRole('link', { name: 'Export JSON' }).getAttribute('href')).toBe('/api/admin/export.json')
  fireEvent.change(screen.getByLabelText('Received from'), { target: { value: '2026-11-01' } })
  expect(fetch).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('button', { name: 'Generate report' }))
  await screen.findByText('Reporting test event')
  fireEvent.click(screen.getByRole('button', { name: 'Print / Save PDF' }))
  expect(print).toHaveBeenCalledOnce()
  expect(fetch).toHaveBeenCalledTimes(1)
  expect(String(fetch.mock.calls[0]?.[0])).toContain('from=2026-11-01')
  expect(screen.getAllByText('66.7%')).toHaveLength(2)
  expect(screen.getByText(/Excluded by analytics policy: X/)).toBeTruthy()
  const timeline = screen.getByRole('table', { name: 'Intake by platform over time', hidden: true })
  expect(within(timeline).getAllByRole('row', { hidden: true })).toHaveLength(3)
  expect(screen.getByRole('table', { name: 'Recorded staff activity' })).toBeTruthy()
  fireEvent.change(screen.getByLabelText('Report platform'), { target: { value: 'Manual' } })
  expect(screen.getByText(/Filters changed/)).toBeTruthy()
  expect(fetch).toHaveBeenCalledTimes(1)
  rerender(<EventReport active={false} onError={onError} />)
  rerender(<EventReport active onError={onError} />)
  vi.useFakeTimers()
  act(() => { vi.advanceTimersByTime(60000) })
  expect(fetch).toHaveBeenCalledTimes(1)
  vi.useRealTimers()
  fireEvent.click(screen.getByRole('button', { name: 'Refresh report' }))
  await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2))
})

it('prevents duplicate generation and retains prior timestamp and data on refresh failure', async () => {
  let resolve: (value: Response) => void = () => { throw new Error('Request not started') }
  const fetch = vi.fn()
    .mockImplementationOnce(() => new Promise<Response>(done => { resolve = done }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ title: 'Database is unavailable' }), { status: 503 }))
  vi.stubGlobal('fetch', fetch)
  const onError = vi.fn()
  render(<EventReport active onError={onError} />)
  fireEvent.click(screen.getByRole('button', { name: 'Generate report' }))
  fireEvent.click(screen.getByRole('button', { name: 'Generating report...' }))
  expect(fetch).toHaveBeenCalledTimes(1)
  await act(async () => { resolve(json(report())) })
  const timestamp = screen.getByRole('status').textContent
  fireEvent.click(screen.getByRole('button', { name: 'Refresh report' }))
  await screen.findByRole('alert')
  expect(screen.getByRole('alert').textContent).toContain('previous snapshot is unchanged')
  expect(screen.getByRole('status').textContent).toBe(timestamp)
  expect(screen.getByText('Reporting test event')).toBeTruthy()
  expect(onError).not.toHaveBeenCalled()
  expect((screen.getByRole('button', { name: 'Refresh report' }) as HTMLButtonElement).disabled).toBe(false)
})

it('cancels on departure and ignores stale responses after a new generation', async () => {
  const pending: { resolve: (response: Response) => void; signal: AbortSignal }[] = []
  const fetch = vi.fn((_url: string, init: RequestInit) => new Promise<Response>(resolve => { pending.push({ resolve, signal: init.signal as AbortSignal }) }))
  vi.stubGlobal('fetch', fetch)
  const onError = vi.fn()
  const { rerender } = render(<EventReport active onError={onError} />)
  fireEvent.click(screen.getByRole('button', { name: 'Generate report' }))
  rerender(<EventReport active={false} onError={onError} />)
  expect(pending[0].signal.aborted).toBe(true)
  rerender(<EventReport active onError={onError} />)
  fireEvent.click(screen.getByRole('button', { name: 'Generate report' }))
  await act(async () => { pending[1].resolve(json({ ...report(), eventName: 'New snapshot' })) })
  await act(async () => { pending[0].resolve(json({ ...report(), eventName: 'Stale snapshot' })) })
  expect(screen.queryByText('Stale snapshot')).toBeNull()
  expect(screen.getByText('New snapshot')).toBeTruthy()
  expect(onError).not.toHaveBeenCalled()
})

it('surfaces invalid date ranges without a request and forwards session expiration', async () => {
  const fetch = vi.fn(async () => new Response(JSON.stringify({ title: 'Sign in again' }), { status: 401 }))
  vi.stubGlobal('fetch', fetch)
  const onError = vi.fn()
  render(<EventReport active onError={onError} />)
  fireEvent.change(screen.getByLabelText('Received from'), { target: { value: '2026-11-10' } })
  fireEvent.change(screen.getByLabelText('Received through'), { target: { value: '2026-11-01' } })
  fireEvent.click(screen.getByRole('button', { name: 'Generate report' }))
  expect(screen.getByRole('alert').textContent).toContain('end date must not precede')
  expect(fetch).not.toHaveBeenCalled()
  fireEvent.change(screen.getByLabelText('Received through'), { target: { value: '' } })
  fireEvent.click(screen.getByRole('button', { name: 'Generate report' }))
  await waitFor(() => expect(onError).toHaveBeenCalledWith(expect.objectContaining({ status: 401 }), expect.any(String)))
})
