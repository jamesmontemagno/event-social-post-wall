import { useCallback, useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, api } from './shared'
import type { Promo } from './shared'
import PromoCard from './PromoCard'
import ConfirmationDialog from './ConfirmationDialog'
import DismissibleNotice from './DismissibleNotice'

export default function Promos({ csrf, onBusyChange, onError }: {
  csrf: string
  onBusyChange: (busy: boolean) => void
  onError: (cause: unknown, fallback: string) => void
}) {
  const [promos, setPromos] = useState<Promo[] | null>(null)
  const [editing, setEditing] = useState<Promo | null>(null)
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [url, setUrl] = useState('')
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const [deleting, setDeleting] = useState<Promo | null>(null)
  const reportError = useCallback((cause: unknown, fallback: string) => {
    if (cause instanceof ApiError && cause.status === 401) onError(cause, fallback)
    else setError(cause instanceof Error ? cause.message : fallback)
  }, [onError])
  const load = useCallback(async (signal?: AbortSignal) => {
    const next = await api<Promo[]>('/api/admin/promos', { signal })
    setPromos(next)
  }, [])
  useEffect(() => {
    const controller = new AbortController()
    void api<Promo[]>('/api/admin/promos', { signal: controller.signal }).then(setPromos).catch(cause => {
      if (!controller.signal.aborted) reportError(cause, 'Could not load promos.')
    })
    return () => controller.abort()
  }, [reportError])

  function changeBusy(value: boolean) { setBusy(value); onBusyChange(value) }
  function reset() { setEditing(null); setTitle(''); setDescription(''); setUrl('') }
  async function save(event: FormEvent) {
    event.preventDefault()
    if (busy) return
    changeBusy(true)
    setNotice('')
    setError('')
    try {
      const saved = await api<Promo>(`/api/admin/promos${editing ? `/${editing.id}` : ''}`, {
        method: 'POST', body: JSON.stringify({ title: title.trim(), description: description.trim(), url: url.trim() || null, version: editing?.version ?? 0 }),
      }, csrf)
      setPromos(current => editing
        ? current?.map(promo => promo.id === saved.id ? saved : promo) ?? [saved]
        : [saved, ...(current ?? [])])
      reset()
      setNotice('Promo saved.')
    } catch (cause) {
      reportError(cause, 'Could not save this promo.')
    } finally { changeBusy(false) }
  }
  async function remove() {
    if (!deleting || busy) return
    changeBusy(true)
    setNotice('')
    setError('')
    try {
      await api(`/api/admin/promos/${deleting.id}/delete`, {
        method: 'POST', body: JSON.stringify({ version: deleting.version }),
      }, csrf)
      setPromos(current => current?.filter(promo => promo.id !== deleting.id) ?? [])
      if (editing?.id === deleting.id) reset()
      setNotice('Promo deleted. It will disappear from connected walls.')
    } catch (cause) {
      reportError(cause, 'Could not delete this promo.')
    } finally { setDeleting(null); changeBusy(false) }
  }
  async function setActive(promo: Promo, active: boolean) {
    if (busy) return
    changeBusy(true)
    setNotice('')
    setError('')
    try {
      const saved = await api<Promo>(`/api/admin/promos/${promo.id}/active`, {
        method: 'POST', body: JSON.stringify({ version: promo.version, active }),
      }, csrf)
      setPromos(current => current?.map(item => item.id === saved.id ? saved : item) ?? [saved])
      setEditing(current => current?.id === saved.id ? saved : current)
      setNotice(`Promo ${active ? 'activated' : 'deactivated'}.`)
    } catch (cause) {
      reportError(cause, 'Could not change this promo’s active state.')
    } finally { changeBusy(false) }
  }
  async function refresh() {
    changeBusy(true)
    setError('')
    try { await load(); reset() }
    catch (cause) { reportError(cause, 'Could not refresh promos.') }
    finally { changeBusy(false) }
  }

  return <section className="admin-panel promos-panel" aria-label="Manage promos">
    <div className="queue-header">
      <div><h2>Promotional blurbs</h2><p>Activate promos to make them available on the wall. Hosts choose locally when to show active promos, below pinned posts.</p></div>
      <button className="button button-muted" disabled={busy} onClick={() => void refresh()}>Refresh promos</button>
    </div>
    {error && <div className="notice notice-error" role="alert">{error}</div>}
    {notice && <DismissibleNotice message={notice} onDismiss={() => setNotice('')} />}
    <div className="promo-editor">
      <form onSubmit={event => void save(event)}>
        <h3>{editing ? 'Edit promo' : 'Create promo'}</h3>
        <label htmlFor="promo-title">Title</label>
        <input id="promo-title" value={title} onChange={event => setTitle(event.target.value)} required maxLength={80}
          disabled={busy} aria-describedby="promo-title-help" />
        <p id="promo-title-help">{title.length}/80 characters. Hosts tap this title to open the details.</p>
        <label htmlFor="promo-description">Description</label>
        <textarea id="promo-description" value={description} onChange={event => setDescription(event.target.value)} required maxLength={500}
          rows={4} disabled={busy} aria-describedby="promo-description-help" />
        <p id="promo-description-help">{description.length}/500 characters. Plain text only.</p>
        <label htmlFor="promo-url">URL (optional)</label>
        <input id="promo-url" type="url" value={url} onChange={event => setUrl(event.target.value)}
          maxLength={2048} disabled={busy} placeholder="https://example.com" aria-describedby="promo-url-help" />
        <p id="promo-url-help">Use an HTTP(S) URL. A QR code is generated automatically, without an external service.</p>
        <div className="queue-actions">
          <button className="button button-primary" disabled={busy || !csrf || promos === null || !title.trim() || !description.trim()}>{busy ? 'Saving...' : editing ? 'Save promo' : 'Create promo'}</button>
          {editing && <button className="button button-muted" type="button" disabled={busy} onClick={reset}>Cancel editing</button>}
        </div>
      </form>
      <section aria-label="Promo preview">
        <h3>Preview</h3>
        <PromoCard promo={{ title: title || 'Your promo title', description: description || 'Your description will appear here.', url: /^https?:\/\/\S+$/i.test(url.trim()) ? url.trim() : null }} />
      </section>
    </div>
    <h3>Saved promos</h3>
    {promos === null ? <p role="status">Promos not loaded yet. Use Refresh promos to retry if loading fails.</p> :
      promos.length === 0 ? <p>No promos yet. Create one above to get started.</p> :
        <div className="promo-grid">{promos.map(promo => <section key={promo.id} aria-label={`Saved promo: ${promo.title}`}>
          <PromoCard promo={promo} />
          <label className="promo-active-control">
            <input type="checkbox" checked={promo.active} disabled={busy || !csrf}
              onChange={event => void setActive(promo, event.target.checked)} />
            Active on wall
          </label>
          <div className="promo-actions">
            <button className="button button-muted" disabled={busy} onClick={() => { setEditing(promo); setTitle(promo.title); setDescription(promo.description); setUrl(promo.url ?? '') }}>Edit promo</button>
            <button className="button button-warning" disabled={busy || !csrf} onClick={() => setDeleting(promo)}>Delete promo</button>
          </div>
        </section>)}</div>}
    {deleting && <ConfirmationDialog title="Delete this promo?" description={`"${deleting.title}" will be removed from the promo library and connected walls.`}
      confirmLabel="Delete promo" busy={busy} onConfirm={() => void remove()} onCancel={() => setDeleting(null)} />}
  </section>
}
