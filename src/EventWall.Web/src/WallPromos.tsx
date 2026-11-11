import { useCallback, useEffect, useRef, useState } from 'react'
import type { Promo } from './shared'
import PromoCard from './PromoCard'

function PromoDialog({ promo, onClose, restoreFocus }: { promo: Promo; onClose: () => void; restoreFocus: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const element = dialog.current
    element?.showModal()
    return () => { element?.close(); restoreFocus() }
  }, [restoreFocus])
  return <dialog ref={dialog} className="post-dialog promo-dialog" aria-label={`Promo: ${promo.title}`}
    onCancel={event => { event.preventDefault(); onClose() }} onClick={onClose}>
    <section className="post-modal promo-modal" onClick={event => event.stopPropagation()}>
      <header className="modal-actions">
        <button className="modal-close" aria-label="Close promo" autoFocus onClick={onClose}>×</button>
      </header>
      <PromoCard promo={promo} />
    </section>
  </dialog>
}

export default function WallPromos({ eventSlug, promos }: { eventSlug: string; promos: Promo[] }) {
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const trigger = useRef<HTMLButtonElement | null>(null)
  const toggleButton = useRef<HTMLButtonElement>(null)
  const restoreFocus = useCallback(() => {
    if (trigger.current?.isConnected) trigger.current.focus()
    else toggleButton.current?.focus()
  }, [])
  const storageKey = `eventwall:promos:${eventSlug}`
  const [preference, setPreference] = useState(() => {
    try { return { visible: localStorage.getItem(storageKey) === 'true', error: '' } }
    catch { return { visible: false, error: 'Could not read the saved promo preference. Use the star toggle to show promos.' } }
  })
  const { visible, error } = preference
  const selected = promos.find(promo => promo.id === selectedId)

  function toggle() {
    const next = !visible
    setSelectedId(null)
    try {
      localStorage.setItem(storageKey, String(next))
      setPreference({ visible: next, error: '' })
    } catch {
      setPreference({ visible: next, error: 'Promos changed for this visit, but the browser could not save the choice for next time.' })
    }
  }

  return <>
    <div className="promo-controls" role="region" aria-label="Promo wall controls">
      <button ref={toggleButton} className={`promo-toggle${visible ? ' is-active' : ''}`} onClick={toggle}
        aria-label="Show promotional blurbs" aria-pressed={visible} aria-controls="wall-promos" title="Show or hide promotional blurbs">
        <span aria-hidden="true">{visible ? '★' : '☆'}</span><span className="promo-toggle-label">Promos</span>
      </button>
    </div>
    {error && <div className="notice notice-warning" role="alert">{error}</div>}
    <section id="wall-promos" className="promos-section" aria-label="Promotional blurbs" hidden={!visible}>
      <div className="section-heading"><span className="section-kicker">IN THE SPOTLIGHT</span><span>EVENT PROMOS</span></div>
      {visible && (promos.length > 0
        ? <div className="promo-grid">{promos.map(promo => <button key={promo.id} className="promo-title-button"
            onClick={event => { trigger.current = event.currentTarget; setSelectedId(promo.id) }} aria-haspopup="dialog">
            <span aria-hidden="true">☆</span><strong>{promo.title}</strong>
          </button>)}</div>
        : <p className="empty-state">No promotional blurbs yet.</p>)}
    </section>
    {visible && selected && <PromoDialog key={selected.id} promo={selected} onClose={() => setSelectedId(null)} restoreFocus={restoreFocus} />}
  </>
}
