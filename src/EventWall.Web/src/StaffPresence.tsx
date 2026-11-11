import { useEffect, useState } from 'react'
import { ApiError, api } from './shared'

type OnlineStaff = { username: string; isAdministrator: boolean; lastSeenAtUtc: string }

export default function StaffPresence({ csrf, onLogout }: { csrf: string; onLogout: () => void }) {
  const [members, setMembers] = useState<OnlineStaff[] | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    if (!csrf) return
    const controller = new AbortController()
    let pending = false
    async function heartbeat() {
      if (pending) return
      pending = true
      try {
        const next = await api<OnlineStaff[]>('/api/admin/presence', {
          method: 'POST',
          signal: AbortSignal.any([controller.signal, AbortSignal.timeout(10000)]),
        }, csrf)
        if (!controller.signal.aborted) {
          setMembers(next)
          setError('')
        }
      } catch (cause) {
        if (!controller.signal.aborted) {
          setMembers(null)
          setError('Online status unavailable. Retrying automatically.')
          if (cause instanceof ApiError && cause.status === 401) onLogout()
        }
      } finally {
        pending = false
      }
    }
    function visibilityChanged() {
      setMembers(null)
      if (document.visibilityState === 'visible') void heartbeat()
    }
    void heartbeat()
    const timer = window.setInterval(() => void heartbeat(), 180000)
    document.addEventListener('visibilitychange', visibilityChanged)
    return () => {
      controller.abort()
      window.clearInterval(timer)
      document.removeEventListener('visibilitychange', visibilityChanged)
    }
  }, [csrf, onLogout])

  return <section className="staff-presence" aria-label="Staff online presence">
    <strong>Online recently</strong>
    <div aria-live="polite">
      {error ? <p role="alert">{error}</p> : members === null ? <p>Checking online staff...</p> :
        <ul aria-label="Online staff">{members.map(member => <li key={member.username}>
          <span className="status-dot" aria-hidden="true" />
          <span>{member.username} <span className="staff-presence-role">{member.isAdministrator ? 'Administrator' : 'Moderator'}</span></span>
        </li>)}</ul>}
    </div>
    <small>Console connected within the last 5 minutes; not necessarily reviewing posts.</small>
  </section>
}
