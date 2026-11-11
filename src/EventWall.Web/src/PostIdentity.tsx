import { useState } from 'react'
import type { ReactNode } from 'react'
import { sourceNames } from './shared'
import type { Post, Source } from './shared'
import originalBot from './assets/portal-bots/original.png'
import bikeHelmetBot from './assets/portal-bots/bike-helmet.png'
import raspberryBeretBot from './assets/portal-bots/raspberry-beret.png'
import hardHatBot from './assets/portal-bots/hard-hat.png'
import cowboyBot from './assets/portal-bots/cowboy.png'
import wizardBot from './assets/portal-bots/wizard.png'
import mohawkBot from './assets/portal-bots/mohawk.png'
import partyHatBot from './assets/portal-bots/party-hat.png'
import bowlerBot from './assets/portal-bots/bowler.png'
import winterCapBot from './assets/portal-bots/winter-cap.png'
import alienBot from './assets/portal-bots/alien.png'
import crownBot from './assets/portal-bots/crown.png'

const portalBots = [originalBot, bikeHelmetBot, raspberryBeretBot, hardHatBot, cowboyBot, wizardBot,
  mohawkBot, partyHatBot, bowlerBot, winterCapBot, alienBot, crownBot]

function questionBot(id: string) {
  let hash = 0
  for (const character of id) hash = (hash * 31 + character.charCodeAt(0)) >>> 0
  return portalBots[hash % portalBots.length]
}

export function SourceIcon({ source }: { source: Source }) {
  const paths: Record<Source, ReactNode> = {
    YouTube: <><rect x="2" y="5" width="20" height="14" rx="4" fill="currentColor" /><path d="m10 9 6 3-6 3Z" fill="var(--surface)" /></>,
    Twitch: <><path d="M4 3h17v12l-6 6h-5v-4H4Z" fill="none" stroke="currentColor" strokeWidth="2" /><path d="M10 7v5m5-5v5" stroke="currentColor" strokeWidth="2" /></>,
    X: <path d="M5 3h4l10 18h-4L5 3Zm14 0L5 21" fill="none" stroke="currentColor" strokeWidth="2" />,
    Bluesky: <path d="M12 11C9 6 3 2 3 6c0 6 2 8 6 7-6 1-4 7 0 6l3-4 3 4c4 1 6-5 0-6 4 1 6-1 6-7 0-4-6 0-9 5Z" fill="currentColor" />,
    Mastodon: <><path d="M6 3h12a3 3 0 0 1 3 3v8a5 5 0 0 1-5 5H9l-4 2v-4a3 3 0 0 1-2-3V6a3 3 0 0 1 3-3Z" fill="currentColor" /><path d="M8 14V9a2 2 0 0 1 4 0v3m0 0V9a2 2 0 0 1 4 0v5" fill="none" stroke="var(--surface)" strokeWidth="1.8" strokeLinecap="round" /></>,
    Portal: <path d="M4 4h16v12H9l-5 4V4Zm4 5h8M8 12h5" fill="none" stroke="currentColor" strokeWidth="2" strokeLinejoin="round" />,
    Manual: <path d="m5 15 11-11 4 4L9 19l-5 1 1-5Zm8-8 4 4" fill="none" stroke="currentColor" strokeWidth="2" />,
  }
  return <span className={`source-mark source-${source.toLowerCase()}`} role="img" aria-label={sourceNames[source]}>
    <svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">{paths[source]}</svg>
  </span>
}

export default function PostIdentity({ post }: { post: Post }) {
  const [failedUrl, setFailedUrl] = useState<string>()
  const portal = post.source === 'Portal'
  const avatarUrl = portal ? questionBot(post.id) : post.authorAvatarUrl?.startsWith('https://') ? post.authorAvatarUrl : undefined
  const showAvatar = Boolean(avatarUrl && failedUrl !== avatarUrl)
  return <span className={`post-identity${showAvatar ? ' has-avatar' : ''}${portal ? ' portal-identity' : ''}`}>
    {showAvatar && <img className={`author-avatar${portal ? ' portal-bot-avatar' : ''}`} src={avatarUrl}
      alt={portal ? '.NET bot question icon' : `Avatar of ${post.authorName}`}
      loading="lazy" referrerPolicy="no-referrer" onError={() => setFailedUrl(avatarUrl)} />}
    <SourceIcon source={post.source} />
  </span>
}

export function ProfileName({ post }: { post: Post }) {
  return post.authorProfileUrl?.startsWith('https://')
    ? <a href={post.authorProfileUrl} target="_blank" rel="noopener noreferrer">{post.authorName}</a>
    : <span>{post.authorName}</span>
}
