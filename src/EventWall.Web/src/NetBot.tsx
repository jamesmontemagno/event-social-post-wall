import { useState } from 'react'
import { useSkin } from './skin'
import botHead from './assets/aurora/bot.svg'

import type { Flight } from './netbotFlight'

export function NetBotHero() {
  const skin = useSkin()
  if (skin === 'classic') return null
  return <span className="netbot-rocket is-cruise netbot-hero" aria-hidden="true" />
}

export function NetBotFlyby({ flight }: { flight: Flight }) {
  // Remounting inside/outside the native dialog must retain the flight's progress.
  const [animationDelay] = useState(() => `${-Math.max(0, Date.now() - flight.startedAt)}ms`)
  return <span className={`netbot-rocket is-${flight.kind} netbot-flyby`} data-testid="netbot-flyby" data-flight={flight.kind} aria-hidden="true" style={{ ...flight.style, animationDelay }} />
}

export function NetBotPeek({ visible }: { visible: boolean }) {
  const skin = useSkin()
  if (skin === 'classic') return null
  return <img className={`netbot-peek${visible ? ' is-visible' : ''}`} src={botHead} alt="" aria-hidden="true" />
}
