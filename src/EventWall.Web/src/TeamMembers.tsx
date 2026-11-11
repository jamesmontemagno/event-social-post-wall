import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { api } from './shared'
import ConfirmationDialog from './ConfirmationDialog'
import DismissibleNotice from './DismissibleNotice'

type Member = {
  id: string
  username: string
  enabled: boolean
  version: number
  createdAtUtc: string
  codeRotatedAtUtc: string
  lastSignedInAtUtc: string | null
  moderationActions: number
}
type Invitation = { member: Member; accessCode: string }

function InvitationDialog({ invitation, onClose }: { invitation: Invitation; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  useEffect(() => {
    const element = dialog.current
    element?.showModal()
    return () => element?.close()
  }, [])
  async function copy() {
    setError('')
    setNotice('')
    try {
      await navigator.clipboard.writeText(`EventWall moderation access\n${window.location.origin}/\nUsername: ${invitation.member.username}\nAccess code: ${invitation.accessCode}`)
      setNotice('Invitation copied. Share it privately with this team member.')
    } catch {
      setError('Clipboard access failed. Select and copy the username and code below instead.')
    }
  }
  return <dialog ref={dialog} className="confirmation-dialog invitation-dialog" aria-labelledby="invitation-title"
    aria-describedby="invitation-description" onCancel={event => { event.preventDefault(); onClose() }}>
    <h2 id="invitation-title">Invitation ready</h2>
    <p id="invitation-description">Share privately. This reusable code is shown only now; create a new code if it is lost. Members can moderate posts and manage promos, not change event settings.</p>
    <label htmlFor="invited-username">Invited username</label>
    <input id="invited-username" value={invitation.member.username} readOnly />
    <label htmlFor="invitation-code">Invitation code</label>
    <input id="invitation-code" className="invitation-code" value={invitation.accessCode} readOnly autoComplete="off" spellCheck={false} />
    {error && <div className="notice notice-error" role="alert">{error}</div>}
    {notice && <div className="notice notice-success" role="status">{notice}</div>}
    <div className="confirmation-actions">
      <button className="button button-muted" onClick={onClose}>Done</button>
      <button className="button button-primary" autoFocus onClick={() => void copy()}>Copy invitation</button>
    </div>
  </dialog>
}

export default function TeamMembers({ csrf, onBusyChange }: { csrf: string; onBusyChange?: (busy: boolean) => void }) {
  const [members, setMembers] = useState<Member[] | null>(null)
  const [username, setUsername] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [invitation, setInvitation] = useState<Invitation | null>(null)
  const [confirmation, setConfirmation] = useState<{ member: Member; action: 'code' | 'status' } | null>(null)
  const load = useCallback(async () => {
    const next = await api<Member[]>('/api/admin/team')
    setMembers(next)
    setError('')
  }, [])
  useEffect(() => {
    const controller = new AbortController()
    void api<Member[]>('/api/admin/team', { signal: controller.signal }).then(setMembers)
      .catch(cause => { if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : 'Could not load the team.') })
    return () => controller.abort()
  }, [])
  function changeBusy(value: boolean) { setBusy(value); onBusyChange?.(value) }

  async function invite(event: FormEvent) {
    event.preventDefault()
    if (busy) return
    changeBusy(true)
    setError('')
    setNotice('')
    try {
      const next = await api<Invitation>('/api/admin/team', { method: 'POST', body: JSON.stringify({ username: username.trim() }) }, csrf)
      setMembers(current => [...(current ?? []), next.member].sort((a, b) => a.username.localeCompare(b.username)))
      setUsername('')
      setInvitation(next)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not invite this member.')
    } finally { changeBusy(false) }
  }

  async function changeAccess() {
    if (!confirmation || busy) return
    changeBusy(true)
    setError('')
    setNotice('')
    const { member, action } = confirmation
    try {
      const result = await api<Member | Invitation>(`/api/admin/team/${member.id}/${action}`, {
        method: 'POST', body: JSON.stringify(action === 'code' ? { version: member.version } : { version: member.version, enabled: false }),
      }, csrf)
      const updated = 'member' in result ? result.member : result
      setMembers(current => current?.map(item => item.id === updated.id ? updated : item) ?? null)
      setConfirmation(null)
      if ('accessCode' in result) setInvitation(result)
      else setNotice(`${updated.username} disabled. Their access code and active sessions no longer work.`)
    } catch (cause) {
      setConfirmation(null)
      setError(cause instanceof Error ? cause.message : 'Could not change member access.')
    } finally { changeBusy(false) }
  }

  return <section className="admin-panel team-panel">
    <div className="queue-header">
      <div><h2>Your moderation team</h2><p>Named access. Clear accountability. You keep control of sources, settings, exports and the team.</p></div>
      <button className="button button-muted" disabled={busy} onClick={() => void load().catch(cause => setError(cause instanceof Error ? cause.message : 'Could not refresh the team.'))}>Refresh team</button>
    </div>
    <p className="team-owner"><strong>Admin - Administrator</strong><span>The existing owner signs in with the protected server passcode and cannot be disabled here.</span></p>
    {error && <div className="notice notice-error" role="alert">{error}</div>}
    {notice && <DismissibleNotice message={notice} onDismiss={() => setNotice('')} />}
    <form className="team-invite-form" onSubmit={event => void invite(event)}>
      <label htmlFor="team-username">New member username</label>
      <div><input id="team-username" value={username} onChange={event => setUsername(event.target.value)} required minLength={3} maxLength={40}
        pattern="[A-Za-z0-9][A-Za-z0-9._\-]{2,39}" autoComplete="off" autoCapitalize="none" spellCheck={false} aria-describedby="team-username-help" disabled={busy} />
        <button className="button button-primary" disabled={busy || !csrf || members === null}>{busy ? 'Saving...' : 'Invite team member'}</button></div>
      <p id="team-username-help">3-40 letters, numbers, dots, hyphens or underscores. Usernames are unique, case-insensitive and permanent. Admin is reserved.</p>
    </form>
    {members === null ? <p role="status">{error ? 'Team unavailable. Refresh to try again.' : 'Loading team...'}</p> :
      members.length === 0 ? <div className="empty-queue"><h3>No team members yet.</h3><p>Invite someone to help keep the conversation moving.</p></div> :
        <div className="team-list">{members.map(member => <article className="team-member" key={member.id}>
          <div className="team-member-heading"><h3>{member.username}</h3><span className={`state-badge state-${member.enabled ? 'live' : 'withdrawn'}`}>{member.enabled ? 'Active moderator' : 'Disabled'}</span></div>
          <dl className="team-member-details">
            <div><dt>Invited</dt><dd>{new Date(member.createdAtUtc).toLocaleDateString()}</dd></div>
            <div><dt>Last sign-in</dt><dd>{member.lastSignedInAtUtc ? new Date(member.lastSignedInAtUtc).toLocaleString() : 'Not signed in yet'}</dd></div>
            <div><dt>Post decisions</dt><dd>{member.moderationActions}</dd></div>
            <div><dt>Code issued</dt><dd>{new Date(member.codeRotatedAtUtc).toLocaleString()}</dd></div>
          </dl>
          <div className="team-member-actions">
            <button className="button button-muted" disabled={busy || !csrf} onClick={() => setConfirmation({ member, action: 'code' })}>{member.enabled ? 'Rotate code' : 'Invite again'}</button>
            {member.enabled && <button className="button button-warning" disabled={busy || !csrf} onClick={() => setConfirmation({ member, action: 'status' })}>Disable access</button>}
          </div>
        </article>)}</div>}
    {confirmation && <ConfirmationDialog title={confirmation.action === 'code' ? 'Create a new access code?' : 'Disable this member?'}
      description={confirmation.action === 'code'
        ? `The old code and active sessions for ${confirmation.member.username} will stop working immediately. A new private invitation will restore moderation access.`
        : `${confirmation.member.username} will lose access immediately. Their named moderation history will remain. Invite again with a new code to restore access.`}
      confirmLabel={confirmation.action === 'code' ? 'Create new code' : 'Disable access'} busy={busy}
      onConfirm={() => void changeAccess()} onCancel={() => setConfirmation(null)} />}
    {invitation && <InvitationDialog invitation={invitation} onClose={() => setInvitation(null)} />}
  </section>
}
