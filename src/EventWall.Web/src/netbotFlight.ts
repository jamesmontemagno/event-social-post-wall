import type { CSSProperties } from 'react'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useSkin } from './skin'

export const firstFlybyDelayMs = 20_000
export const minFlybyGapMs = 60_000
export const maxFlybyGapMs = 120_000
export const flybyDurationMs = 12_000

export type Flight = { startedAt: number } & ReturnType<typeof planFlight>

function prefersReducedMotion() {
  return typeof window.matchMedia === 'function' && window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

export function useNetBotFlight() {
  const skin = useSkin()
  const [flight, setFlight] = useState<Flight | null>(null)
  const active = useRef(false)
  const landing = useRef<number | undefined>(undefined)
  const [reducedMotion, setReducedMotion] = useState(prefersReducedMotion)
  const enabled = skin !== 'classic' && !reducedMotion
  const launch = useCallback(() => {
    if (!enabled || active.current || document.visibilityState === 'hidden') return
    active.current = true
    setFlight({ startedAt: Date.now(), ...planFlight() })
    landing.current = window.setTimeout(() => {
      active.current = false
      setFlight(null)
    }, flybyDurationMs)
  }, [enabled])

  useEffect(() => {
    if (typeof window.matchMedia !== 'function') return
    const query = window.matchMedia('(prefers-reduced-motion: reduce)')
    const update = () => setReducedMotion(query.matches)
    query.addEventListener?.('change', update)
    return () => query.removeEventListener?.('change', update)
  }, [])

  useEffect(() => {
    if (!enabled) return
    let next: number | undefined
    const scheduledLaunch = () => {
      launch()
      next = window.setTimeout(scheduledLaunch, nextFlybyGap())
    }
    next = window.setTimeout(scheduledLaunch, firstFlybyDelayMs)
    return () => {
      window.clearTimeout(next)
      window.clearTimeout(landing.current)
      active.current = false
      setFlight(null)
    }
  }, [enabled, launch])

  return { flight: enabled ? flight : null, launch }
}

type Random = () => number

export function nextFlybyGap(random: Random = Math.random) {
  return Math.round(minFlybyGapMs + random() * (maxFlybyGapMs - minFlybyGapMs))
}

export type FlightKind = 'cruise-left' | 'cruise' | 'launch'
export const flightKinds: FlightKind[] = ['cruise-left', 'cruise', 'launch']

const between = (random: Random, min: number, max: number) => Math.round(min + random() * (max - min))

/**
 * Picks one of three rocket poses plus a random height, tilt and drift so consecutive flights differ.
 * Each pose always travels the way its art faces (cruise-left: right to left, cruise: left to right,
 * launch: bottom to top) and is never mirrored, which would reverse the "11" on the hull.
 */
export function planFlight(random: Random = Math.random): { kind: FlightKind; style: CSSProperties } {
  const kind = flightKinds[Math.min(flightKinds.length - 1, Math.floor(random() * flightKinds.length))]
  const style: Record<string, string> = { animationDuration: `${flybyDurationMs}ms` }
  if (kind === 'cruise-left') {
    style.top = `${between(random, 8, 72)}vh`
    style['--fly-tilt'] = `${between(random, -4, 6)}deg`
    style['--fly-rise'] = `${between(random, -14, 10)}vh`
  } else if (kind === 'cruise') {
    style.top = `${between(random, 8, 72)}vh`
    style['--fly-tilt'] = `${between(random, -6, 4)}deg`
    style['--fly-rise'] = `${between(random, -14, 10)}vh`
  } else {
    style.left = `${between(random, 6, 78)}vw`
    style['--fly-tilt'] = `${between(random, -6, 6)}deg`
    style['--fly-drift'] = `${between(random, -10, 10)}vw`
  }
  return { kind, style: style as CSSProperties }
}
