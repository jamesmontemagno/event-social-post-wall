import { useEffect, useRef, useState } from 'react'

export const NOTICE_TIMEOUT_MS = 5000

export default function DismissibleNotice({ message, onDismiss }: { message: string; onDismiss: () => void }) {
  const [hovered, setHovered] = useState(false)
  const [focused, setFocused] = useState(false)
  const dismiss = useRef(onDismiss)
  useEffect(() => { dismiss.current = onDismiss })
  const paused = hovered || focused

  // Pause while staff hover or focus the notice; restart the full countdown afterward or when the message changes.
  useEffect(() => {
    if (paused) return undefined
    const timer = window.setTimeout(() => dismiss.current(), NOTICE_TIMEOUT_MS)
    return () => window.clearTimeout(timer)
  }, [message, paused])

  return (
    <div className="notice notice-success notice-dismissible"
      onMouseEnter={() => setHovered(true)} onMouseLeave={() => setHovered(false)}
      onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}>
      <span role="status">{message}</span>
      <button type="button" className="notice-close" aria-label="Dismiss notification" onClick={() => dismiss.current()}>
        <span aria-hidden="true">×</span>
      </button>
    </div>
  )
}