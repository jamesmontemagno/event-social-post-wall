import { useLayoutEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'

const rowUnit = 2

// Pinterest-style waterfall: each card spans as many tiny grid rows as its measured height,
// so cards stack under shorter neighbours while DOM and reading order stay newest-first.
export default function MasonryGrid({ className, children }: { className: string; children: ReactNode }) {
  const grid = useRef<HTMLDivElement>(null)
  const [enabled, setEnabled] = useState(false)

  useLayoutEffect(() => {
    const container = grid.current
    if (!container || typeof ResizeObserver === 'undefined') return
    const layout = () => {
      const gap = parseFloat(getComputedStyle(container).columnGap) || 0
      for (const child of Array.from(container.children) as HTMLElement[]) {
        const height = child.getBoundingClientRect().height
        if (height > 0) child.style.gridRowEnd = `span ${Math.ceil((height + gap) / rowUnit)}`
      }
    }
    const observer = new ResizeObserver(() => window.requestAnimationFrame(layout))
    Array.from(container.children).forEach(child => observer.observe(child))
    observer.observe(container)
    layout()
    setEnabled(true)
    return () => observer.disconnect()
  }, [children])

  return <div ref={grid} className={`${className}${enabled ? ' is-masonry' : ''}`}>{children}</div>
}
