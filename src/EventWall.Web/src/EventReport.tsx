import { useEffect, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { API, ApiError, api, postFilterLabels, sourceNames } from './shared'
import type { PostFilterReason, Source } from './shared'

export type ReportCounts = {
  total: number; pending: number; live: number; rejected: number; withdrawn: number
  imagePosts: number; images: number; flaggedPosts: number; everApproved: number; everRejected: number; everWithdrawn: number
  staffActions: number; filterActions: number; sourceActions: number; repeatStaffActions: number
  decisionSamples: number; medianDecisionSeconds: number | null; p95DecisionSeconds: number | null
  oldestPendingSeconds: number | null; approvalRate: number | null
}
export type ReportTimeline = {
  startUtc: string; label: string; intake: Partial<Record<Source, number>>
  approved: number; rejected: number; withdrawn: number; filterActions: number; sourceActions: number
}
export type EventReportData = {
  generatedAtUtc: string; eventName: string
  filters: { fromUtc: string | null; untilUtc: string | null; source: Source | null; timeZone: string }
  eligibleSources: Source[]; excludedSources: Source[]; counts: ReportCounts
  platforms: { source: Source; counts: ReportCounts; blockedAccounts: number }[]
  firstReceivedUtc: string | null; lastReceivedUtc: string | null; granularity: string; timeline: ReportTimeline[]
  findings: { reason: PostFilterReason; count: number }[]
  pendingAges: { label: string; count: number }[]
  reviewers: { name: string; approved: number; rejected: number; withdrawn: number }[]
  operations: {
    submissionsOpen: boolean; activePromos: number; totalPromos: number
    sources: { provider: string; state: string; lastSuccessfulUtc: string | null; simulated: boolean }[]
  }
}
const allSources = Object.keys(sourceNames) as Source[]
const states = ['pending', 'live', 'rejected', 'withdrawn'] as const
const stateNames = { pending: 'Pending', live: 'Live', rejected: 'Rejected', withdrawn: 'Withdrawn' }
const date = (value: string | null) => value ? new Date(value).toLocaleString() : 'Not available'
const number = (value: number) => value.toLocaleString()
const rate = (value: number | null) => value === null ? 'Not available' : `${(value * 100).toFixed(1)}%`
function duration(seconds: number | null) {
  if (seconds === null) return 'Not available'
  if (seconds < 60) return `${Math.round(seconds)}s`
  if (seconds < 3600) return `${Math.round(seconds / 60)}m`
  if (seconds < 86400) return `${(seconds / 3600).toFixed(1)}h`
  return `${(seconds / 86400).toFixed(1)}d`
}
function Metric({ label, children }: { label: string; children: ReactNode }) {
  return <div className="metric-card"><span>{label}</span><strong>{children}</strong></div>
}
function Table({ caption, headings, children }: { caption: string; headings: string[]; children: ReactNode }) {
  return <div className="report-table-scroll" tabIndex={0} role="region" aria-label={caption}>
    <table><caption>{caption}</caption><thead><tr>{headings.map(heading => <th key={heading} scope="col">{heading}</th>)}</tr></thead><tbody>{children}</tbody></table>
  </div>
}
function Stack({ values, max, label }: { values: { key: string; value: number; label: string }[]; max: number; label: string }) {
  return <div className="report-stack" role="img" aria-label={`${label}: ${values.map(item => `${item.label} ${number(item.value)}`).join(', ')}`}>
    {values.map(item => <span key={item.key} className={`report-color-${item.key}`} style={{ width: `${max ? item.value / max * 100 : 0}%` }} />)}
  </div>
}
function Timeline({ report, moderation }: { report: EventReportData; moderation?: boolean }) {
  const series = moderation
    ? [{ key: 'live', label: 'Staff approved' }, { key: 'rejected', label: 'Staff rejected' }, { key: 'withdrawn', label: 'Staff withdrew' }, { key: 'filter', label: 'Automatic filter' }, { key: 'source', label: 'Other source actions' }]
    : report.platforms.map(item => ({ key: item.source, label: sourceNames[item.source] }))
  const values = (row: ReportTimeline) => moderation
    ? [row.approved, row.rejected, row.withdrawn, row.filterActions, row.sourceActions]
    : report.platforms.map(item => row.intake[item.source] ?? 0)
  const peak = report.timeline.reduce((max, row) => Math.max(max, values(row).reduce((sum, count) => sum + count, 0)), 0)
  const max = Math.max(1, peak)
  const caption = moderation ? 'Moderation activity over time' : 'Intake by platform over time'
  return <section className="report-section">
    <h3>{caption}</h3>
    <p className="report-note">One bar per {report.granularity} · Peak: {number(peak)} {moderation ? 'actions' : 'posts'} in a bucket.</p>
    <div className="report-legend">{series.map(item => <span key={item.key}><i className={`report-color-${item.key}`} />{item.label}</span>)}</div>
    {!report.timeline.length ? <p>No recorded activity for this selection.</p> : <>
      <div className="report-timeline" role="img" aria-label={`${caption}, ${report.granularity} buckets. Exact values in the table below.`}>
        {report.timeline.map(row => <div key={row.startUtc} className="report-timeline-column">
          {values(row).map((count, index) => <span key={series[index].key} className={`report-color-${series[index].key}`} style={{ height: `${count / max * 100}%` }} />)}
        </div>)}
      </div>
      <div className="report-axis"><span>{report.timeline[0].label}</span><span>{report.timeline.at(-1)?.label}</span></div>
      <details><summary>View exact timeline values</summary>
        <Table caption={caption} headings={['Local time', ...series.map(item => item.label), 'Total']}>
          {report.timeline.map(row => { const counts = values(row); return <tr key={row.startUtc}>
            <th scope="row">{row.label}</th>{counts.map((count, index) => <td key={series[index].key}>{number(count)}</td>)}<td>{number(counts.reduce((sum, count) => sum + count, 0))}</td>
          </tr> })}
        </Table>
      </details>
    </>}
  </section>
}

export default function EventReport({ active, onError }: { active: boolean; onError: (cause: unknown, fallback: string) => void }) {
  const [report, setReport] = useState<EventReportData | null>(null)
  const [from, setFrom] = useState('')
  const [through, setThrough] = useState('')
  const [source, setSource] = useState('')
  const [pendingRequest, setPendingRequest] = useState<AbortController | null>(null)
  const [error, setError] = useState('')
  const [applied, setApplied] = useState('')
  const request = useRef<AbortController | null>(null)
  const busy = active && pendingRequest !== null && !pendingRequest.signal.aborted
  const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone
  const draft = JSON.stringify([from, through, source, timeZone])
  useEffect(() => {
    if (!active) { request.current?.abort(); request.current = null }
    return () => { request.current?.abort(); request.current = null }
  }, [active])

  async function generate(event: FormEvent) {
    event.preventDefault()
    if (!active || request.current) return
    if (from && through && from > through) { setError('The end date must not precede the start date.'); return }
    const controller = new AbortController()
    request.current = controller
    setPendingRequest(controller)
    setError('')
    const params = new URLSearchParams({ timeZone })
    if (from) params.set('from', from)
    if (through) params.set('through', through)
    if (source) params.set('source', source)
    try {
      const next = await api<EventReportData>(`/api/admin/analytics?${params}`, { signal: controller.signal })
      if (request.current !== controller || controller.signal.aborted) return
      setReport(next)
      setApplied(draft)
    } catch (cause) {
      if (controller.signal.aborted || request.current !== controller) return
      setError(cause instanceof Error ? cause.message : 'Could not generate the report.')
      if (cause instanceof ApiError && cause.status === 401) onError(cause, 'Could not generate the report.')
    } finally {
      if (request.current === controller) { request.current = null; setPendingRequest(null) }
    }
  }
  return <section className="admin-panel event-report" hidden={!active} aria-label="Event report">
    <div className="queue-header"><div><h2>Event report</h2><p>An on-demand view of the conversation. Operational data, not reach or impressions.</p></div>
      <div className="queue-actions">
        <button className="button button-muted" type="button" disabled={!report} onClick={() => window.print()}>Print / Save PDF</button>
        <a className="button button-muted" href={`${API}/api/admin/export.csv`} download>Export CSV</a>
        <a className="button button-muted" href={`${API}/api/admin/export.json`} download>Export JSON</a>
      </div>
    </div>
    <form className="report-filters" onSubmit={event => void generate(event)}>
      <label>Received from<input type="date" value={from} onChange={event => setFrom(event.target.value)} disabled={busy} /></label>
      <label>Received through<input type="date" value={through} onChange={event => setThrough(event.target.value)} disabled={busy} /></label>
      <label>Report platform<select value={source} onChange={event => setSource(event.target.value)} disabled={busy}>
        <option value="">All eligible platforms</option>
        {(report?.eligibleSources ?? allSources).map(item => <option key={item} value={item}>{sourceNames[item]}</option>)}
      </select></label>
      <button className="button button-primary" disabled={busy}>{busy ? 'Generating report...' : report ? 'Refresh report' : 'Generate report'}</button>
    </form>
    <p className="report-note">Dates and timelines use {timeZone}. Filters apply only when you click Generate or Refresh. No automatic refresh.</p>
    <div role="status" aria-live="polite">
      {busy ? 'Generating report. The previous snapshot, if any, remains below.' : report ? `Report generated ${date(report.generatedAtUtc)}.` : 'Click Generate report to load event data.'}
      {report && applied !== draft && <p>Filters changed. The previous snapshot remains displayed until you refresh.</p>}
    </div>
    {error && <p className="notice notice-error" role="alert">{error} {report && 'The previous snapshot is unchanged.'}</p>}
    {report && <div aria-busy={busy}>
      <header className="report-snapshot"><h3>{report.eventName}</h3>
        <p>Applied selection: {report.filters.source ? sourceNames[report.filters.source] : 'All eligible platforms'} · Received {report.filters.fromUtc ? date(report.filters.fromUtc) : 'event start'} to {report.filters.untilUtc ? `${date(report.filters.untilUtc)} (exclusive)` : 'latest'} · {report.filters.timeZone}</p>
        <p>First receipt: {date(report.firstReceivedUtc)} · Last receipt: {date(report.lastReceivedUtc)}</p>
      </header>
      {report.excludedSources.length > 0 && <p className="notice notice-warning">Excluded by analytics policy: {report.excludedSources.map(item => sourceNames[item]).join(', ')}. No counts from those platforms are included.</p>}
      {report.counts.total === 0 && <p className="empty-queue">No posts match this selection.</p>}
      <div className="report-metrics">
        <Metric label="Total items">{number(report.counts.total)}</Metric>
        {states.map(state => <Metric key={state} label={stateNames[state]}>{number(report.counts[state])}</Metric>)}
        <Metric label="Current approval rate">{rate(report.counts.approvalRate)}</Metric>
      </div>
      <p className="report-note">Approval rate = Live / (Live + Rejected), denominator {number(report.counts.live + report.counts.rejected)}. Pending and withdrawn items are not in that denominator.</p>
      <section className="report-section"><h3>Platform comparison</h3>
        <div className="report-legend">{states.map(state => <span key={state}><i className={`report-color-${state}`} />{stateNames[state]}</span>)}</div>
        <Table caption="Current status and moderation speed by platform" headings={['Platform', 'Status mix', 'Total', 'Share', 'Pending', 'Live', 'Rejected', 'Withdrawn', 'Approval rate', 'Median', 'P95', 'Samples', 'Oldest pending']}>
          {report.platforms.map(item => <tr key={item.source}><th scope="row">{sourceNames[item.source]}</th>
            <td><Stack label={sourceNames[item.source]} max={item.counts.total} values={states.map(state => ({ key: state, label: stateNames[state], value: item.counts[state] }))} /></td>
            <td>{number(item.counts.total)}</td><td>{rate(report.counts.total ? item.counts.total / report.counts.total : null)}</td>
            {states.map(state => <td key={state}>{number(item.counts[state])}</td>)}<td>{rate(item.counts.approvalRate)}</td>
            <td>{duration(item.counts.medianDecisionSeconds)}</td><td>{duration(item.counts.p95DecisionSeconds)}</td><td>{number(item.counts.decisionSamples)}</td><td>{duration(item.counts.oldestPendingSeconds)}</td>
          </tr>)}
        </Table>
      </section>
      <Timeline report={report} /><Timeline report={report} moderation />
      <p className="report-note">Intake uses receipt time. Moderation timelines follow the selected posts through generation, including decisions after the intake date range. Empty buckets are included.</p>
      <section className="report-section"><h3>Moderation history</h3>
        <div className="report-metrics">
          <Metric label="Unique posts ever approved">{number(report.counts.everApproved)}</Metric>
          <Metric label="Unique posts ever rejected">{number(report.counts.everRejected)}</Metric>
          <Metric label="Unique posts ever withdrawn">{number(report.counts.everWithdrawn)}</Metric>
          <Metric label="Staff actions">{number(report.counts.staffActions)}</Metric>
          <Metric label="Automatic filter actions">{number(report.counts.filterActions)}</Metric>
          <Metric label="Other source actions">{number(report.counts.sourceActions)}</Metric>
          <Metric label="Repeat staff actions">{number(report.counts.repeatStaffActions)}</Metric>
        </div>
        <p className="report-note">Historical categories overlap. Reapprovals are additional actions, not additional unique posts. Automatic source actions include blocked-account rejection, content updates and removal; they are not staff decisions.</p>
        <Table caption="Recorded moderation history by platform" headings={['Platform', 'Ever approved', 'Ever rejected', 'Ever withdrawn', 'Staff actions', 'Automatic filter actions', 'Other source actions']}>
          {report.platforms.map(item => <tr key={item.source}><th scope="row">{sourceNames[item.source]}</th>
            <td>{number(item.counts.everApproved)}</td><td>{number(item.counts.everRejected)}</td><td>{number(item.counts.everWithdrawn)}</td>
            <td>{number(item.counts.staffActions)}</td><td>{number(item.counts.filterActions)}</td><td>{number(item.counts.sourceActions)}</td>
          </tr>)}
        </Table>
        <Table caption="Recorded staff activity" headings={['Staff', 'Approvals', 'Rejections', 'Withdrawals']}>
          {report.reviewers.map(item => <tr key={item.name}><th scope="row">{item.name}</th><td>{number(item.approved)}</td><td>{number(item.rejected)}</td><td>{number(item.withdrawn)}</td></tr>)}
        </Table>
      </section>
      <section className="report-section"><h3>Queue health</h3>
        <div className="report-metrics">
          <Metric label="Median to first staff decision">{duration(report.counts.medianDecisionSeconds)}</Metric>
          <Metric label="P95 to first staff decision">{duration(report.counts.p95DecisionSeconds)}</Metric>
          <Metric label="Decision samples">{number(report.counts.decisionSamples)}</Metric>
          <Metric label="Oldest pending">{duration(report.counts.oldestPendingSeconds)}</Metric>
        </div>
        <p className="report-note">Receipt to first recorded staff decision, not time spent reviewing. Automatic actions and negative durations are excluded. Pending ages are measured at generation.</p>
        <Table caption="Pending age distribution" headings={['Age at generation', 'Posts']}>
          {report.pendingAges.map(item => <tr key={item.label}><th scope="row">{item.label}</th><td>{number(item.count)}</td></tr>)}
        </Table>
      </section>
      <section className="report-section"><h3>Filtering and safety</h3>
        <p>{number(report.counts.flaggedPosts)} posts have retained filter findings. Findings overlap; this is not a historical ledger of every rejection reason.</p>
        <Table caption="Retained filter findings" headings={['Finding', 'Posts']}>
          {report.findings.map(item => <tr key={item.reason}><th scope="row">{postFilterLabels[item.reason]}</th><td>{number(item.count)}</td></tr>)}
        </Table>
        <Table caption="Blocked accounts by selected eligible platform (current event totals, not date-filtered)" headings={['Platform', 'Blocked accounts']}>
          {report.platforms.map(item => <tr key={item.source}><th scope="row">{sourceNames[item.source]}</th><td>{number(item.blockedAccounts)}</td></tr>)}
        </Table>
      </section>
      <section className="report-section"><h3>Content and operational context</h3>
        <div className="report-metrics">
          <Metric label="Posts with images">{number(report.counts.imagePosts)}</Metric><Metric label="Retained images">{number(report.counts.images)}</Metric>
          <Metric label="Portal intake">{report.operations.submissionsOpen ? 'Open' : 'Paused'}</Metric>
          <Metric label="Active / total promos">{report.operations.activePromos} / {report.operations.totalPromos}</Metric>
        </div>
        <p className="report-note">Images follow the selected post cohort. Portal state, promos and connector health are event-wide context at generation, not date-filtered history or uptime.</p>
        <Table caption="Current connector health (not historical)" headings={['Platform', 'State', 'Last successful collection']}>
          {report.operations.sources.map(item => <tr key={item.provider}><th scope="row">{item.provider}</th><td>{item.simulated ? 'Simulated demo' : item.state}</td><td>{date(item.lastSuccessfulUtc)}</td></tr>)}
        </Table>
      </section>
      <p className="report-note">Only retained event records are available. Unstored traffic, complete historical filter reasons, audience reach, impressions and read-on-air activity cannot be inferred. The report is assembled from read-only queries while the event may continue changing.</p>
    </div>}
  </section>
}
