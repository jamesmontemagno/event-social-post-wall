export type Source = 'Portal' | 'Manual' | 'Twitch' | 'YouTube' | 'X' | 'Bluesky' | 'Mastodon'
export type Status = 'Pending' | 'Live' | 'Rejected' | 'Withdrawn'
export type PostFilterReason = 'Profanity' | 'AllCaps' | 'SingleWord' | 'EmojiOnly' | 'LocationOnly' | 'NoSubstantiveText' | 'RepeatedCharacters' | 'DuplicateText'
export const postFilterLabels: Record<PostFilterReason, string> = {
  Profanity: 'Profanity',
  AllCaps: 'All caps',
  SingleWord: 'Single word',
  EmojiOnly: 'Emoji only',
  LocationOnly: 'Location/check-in only',
  NoSubstantiveText: 'No substantive text',
  RepeatedCharacters: 'Repeated-character spam',
  DuplicateText: 'Exact duplicate text',
}
export type PostFilterSettings = {
  profanity: boolean
  allCaps: boolean
  singleWord: boolean
  emojiOnly: boolean
  locationOnly: boolean
  noSubstantiveText: boolean
  repeatedCharacters: boolean
  duplicateText: boolean
  blockedTerms: string[]
  allowedTerms: string[]
  locationPhrases: string[]
}
export type AdminEventSettings = {
  submissionsOpen: boolean
  postFilters: PostFilterSettings
  filterSettingsVersion: number
}
export type PostImage = { url: string; alt?: string | null }
export type Promo = { id: string; title: string; description: string; url: string | null; createdAtUtc: string; active: boolean; version: number }
export type Post = {
  id: string
  source: Source
  text: string
  authorName: string
  sourceUrl?: string
  receivedAtUtc: string
  sourceCreatedAtUtc?: string
  publishedRevision?: number
  approvedAtUtc?: string | null
  status?: Status
  externalId?: string
  version?: number
  authorProfileUrl?: string
  authorAvatarUrl?: string
  canBlockAuthor?: boolean
  authorBlocked?: boolean
  moderatedBy?: string | null
  images?: PostImage[]
  filterReasons?: PostFilterReason[]
}
export type EventInfo = { slug: string; name: string; submissionsOpen: boolean; demoMode?: boolean; questionHashtag?: string; submissionSiteUrl?: string | null; wallSiteUrl?: string | null }
export type SourceInfo = {
  provider: string
  enabled: boolean
  configured: boolean
  ready: boolean
  state: string
  summary: string
  setupUrl: string
  requiredSettings: string[]
  costNote?: string
  simulated?: boolean
  collectionRunning?: boolean | null
  collectionAvailable?: boolean
}

export const API = import.meta.env.VITE_API_BASE_URL ?? ''
export const sourceNames: Record<Source, string> = {
  Portal: 'Event portal',
  Manual: 'Staff added',
  Twitch: 'Twitch',
  YouTube: 'YouTube',
  X: 'X',
  Bluesky: 'Bluesky',
  Mastodon: 'Mastodon',
}

export class ApiError extends Error {
  readonly status: number
  readonly code?: string
  readonly retryAfterSeconds?: number
  constructor(message: string, status: number, code?: string, retryAfterSeconds?: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.retryAfterSeconds = retryAfterSeconds
  }
}

export async function api<T>(path: string, init?: RequestInit, csrf?: string): Promise<T> {
  const headers = new Headers(init?.headers)
  if (init?.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (csrf) headers.set('X-CSRF-TOKEN', csrf)
  const response = await fetch(`${API}${path}`, { ...init, headers, credentials: 'same-origin' })
  if (!response.ok) {
    const detail = await response.json().catch(() => undefined) as { detail?: string; title?: string; code?: string; retryAfterSeconds?: number } | undefined
    const retry = Number(response.headers.get('Retry-After') ?? detail?.retryAfterSeconds)
    throw new ApiError(detail?.detail ?? detail?.title ?? `Request failed (${response.status})`,
      response.status, detail?.code, Number.isFinite(retry) && retry > 0 ? retry : undefined)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export function formatAge(date: string) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(date).getTime()) / 1000))
  if (seconds < 60) return 'Just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return new Date(date).toLocaleDateString()
}
