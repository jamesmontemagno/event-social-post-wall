import { useEffect, useState } from 'react'
import Staff from './Staff'
import { api } from './shared'
import type { EventInfo } from './shared'
import './App.css'
import './Admin.css'
import ThemeToggle from './ThemeToggle'
import SiteFooter from './SiteFooter'

function SiteLink({ href, label }: { href?: string | null; label: string }) {
  if (!href) return <button className="nav-link" type="button" disabled>{label}</button>
  return <a className="nav-link" href={href} target="_blank" rel="noopener noreferrer"
    aria-label={`${label} (opens in a new tab)`}>{label}<span aria-hidden="true">↗</span></a>
}

export default function AdminApp() {
  const [event, setEvent] = useState<EventInfo | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    void api<EventInfo>('/api/public/event').then(setEvent)
      .catch(cause => setError(cause instanceof Error ? cause.message : 'Could not load event settings.'))
  }, [])

  const missingSiteSettings = event
    ? [
        !event.wallSiteUrl && 'Public__WallSiteUrl',
        !event.submissionSiteUrl && 'Public__SubmissionSiteUrl',
      ].filter((setting): setting is string => Boolean(setting))
    : []

  return (
    <div className="app-shell admin-site">
      <header className="topbar">
        <a className="brand" href="/">
          <span className="brand-mark">E</span>
          <span><strong>{event?.name ?? 'Community Event'}</strong><small>PRIVATE CONTROL ROOM</small></span>
        </a>
        {event?.demoMode && <span className="demo-pill"><span className="demo-dot" />Demo mode · simulated posts</span>}
        <nav aria-label="Main navigation">
          <span className="nav-link active">Staff console</span>
          <SiteLink href={event?.wallSiteUrl} label="Open wall" />
          <SiteLink href={event?.submissionSiteUrl} label="Open questions portal" />
          <ThemeToggle />
        </nav>
      </header>
      {error && <div className="notice notice-error" role="alert">{error}</div>}
      {missingSiteSettings.length > 0 && <div className="notice notice-warning" role="status">
        Set {missingSiteSettings.join(' and ')} to valid site URLs on the admin deployment to enable the links.
      </div>}
      <Staff onLogout={() => undefined} />
      <SiteFooter />
    </div>
  )
}
