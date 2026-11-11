import { useEffect, useRef } from 'react'

export default function ConfirmationDialog({ title, description, confirmLabel, busy, onConfirm, onCancel }: {
  title: string
  description: string
  confirmLabel: string
  busy: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const element = dialog.current
    element?.showModal()
    return () => element?.close()
  }, [])
  return <dialog ref={dialog} className="confirmation-dialog" aria-labelledby="confirmation-title"
    aria-describedby="confirmation-description" onCancel={event => { event.preventDefault(); if (!busy) onCancel() }}>
    <h2 id="confirmation-title">{title}</h2>
    <p id="confirmation-description">{description}</p>
    <div className="confirmation-actions">
      <button className="button button-muted" autoFocus disabled={busy} onClick={onCancel}>Cancel</button>
      <button className="button button-warning" disabled={busy} onClick={onConfirm}>{busy ? 'Saving...' : confirmLabel}</button>
    </div>
  </dialog>
}
