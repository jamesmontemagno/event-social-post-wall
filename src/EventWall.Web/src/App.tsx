import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import './App.css'
import { api, formatAge, sourceNames } from './shared'
import type { EventInfo, Post, Promo } from './shared'
import PostIdentity, { ProfileName, SourceIcon } from './PostIdentity'
import PostImages from './PostImages'
import ThemeToggle from './ThemeToggle'
import SkinPicker from './SkinPicker'
import { NetBotFlyby, NetBotHero, NetBotPeek } from './NetBot'
import { useNetBotFlight } from './netbotFlight'
import type { Flight } from './netbotFlight'
import { initializeSkin } from './skin'
import '@fontsource-variable/space-grotesk'
import './skin.css'
import SiteFooter from './SiteFooter'
import MasonryGrid from './MasonryGrid'
import { formatWallPostText } from './wallPostText'
import WallPromos from './WallPromos'

initializeSkin()

type WallSort = 'post' | 'approved'
type WallLoadMode = 'initial' | 'sort' | 'refresh'
const WALL_CHECK_INTERVAL_MS = 60_000

function compareTimes(a: string | null | undefined, b: string | null | undefined) {
  // ISO UTC timestamps compare chronologically without the suffix, including fractional seconds.
  const aTime = a?.replace(/Z$/, '') ?? ''
  const bTime = b?.replace(/Z$/, '') ?? ''
  return aTime === bTime ? 0 : aTime < bTime ? 1 : -1
}

function compareWallPosts(a: Post, b: Post, sortBy: WallSort) {
  if (sortBy === 'approved') {
    return compareTimes(a.approvedAtUtc, b.approvedAtUtc) ||
      (b.publishedRevision ?? 0) - (a.publishedRevision ?? 0)
  }
  return compareTimes(a.sourceCreatedAtUtc ?? a.receivedAtUtc, b.sourceCreatedAtUtc ?? b.receivedAtUtc) ||
    compareTimes(a.receivedAtUtc, b.receivedAtUtc)
}

function WallPostAge({ post, sortBy, className }: { post: Post; sortBy: WallSort; className?: string }) {
  const approval = sortBy === 'approved' ? post.approvedAtUtc : null
  const age = formatAge(approval ?? post.sourceCreatedAtUtc ?? post.receivedAtUtc)
  return <span className={className} title={`${approval ? 'Approved' : 'Posted'} ${age}`}>{age}</span>
}

function Header({ event }: { event: EventInfo | null }) {
  return (
    <header className="topbar">
      <a className="brand" href={import.meta.env.BASE_URL}>
        <span className="brand-mark">E</span>
        <span><strong>{event?.name ?? 'Community Event'}</strong><small>COMMUNITY WALL</small></span>
      </a>
      {event?.demoMode && <span className="demo-pill" title="Posts are fictional and generated locally. No social provider APIs are called."><span className="demo-dot" />Demo mode · simulated posts</span>}
      <nav aria-label="Main navigation">
        {event?.submissionSiteUrl && <a className="nav-link" href={event.submissionSiteUrl}>Submit a question</a>}
        <SkinPicker />
        <ThemeToggle />
      </nav>
    </header>
  )
}

const EXPANDED_POST_CLOSE_MS = 200

function prefersReducedMotion() {
  return typeof window.matchMedia !== 'function' || !window.matchMedia('(prefers-reduced-motion: no-preference)').matches
}

function ExpandedPost({ post, pinned, sortBy, onUnpin, onClose, flight }: {
  post: Post
  pinned: boolean
  sortBy: WallSort
  onUnpin: () => void
  onClose: () => void
  flight: Flight | null
}) {
  const dialog = useRef<HTMLDialogElement>(null)
  const closeButton = useRef<HTMLButtonElement>(null)
  const [unpinStatus, setUnpinStatus] = useState('')
  const [closing, setClosing] = useState(false)
  const closed = useRef(false)
  const onCloseRef = useRef(onClose)
  useEffect(() => { onCloseRef.current = onClose })
  const text = formatWallPostText(post.text)
  useEffect(() => {
    const element = dialog.current
    element?.showModal()
    return () => element?.close()
  }, [])

  const finishClose = useCallback(() => {
    if (closed.current) return
    closed.current = true
    dialog.current?.close()
    onCloseRef.current()
  }, [])

  // Close once the fade-out ends; the timer covers browsers or hidden tabs that never fire animationend.
  useEffect(() => {
    if (!closing) return undefined
    const modal = dialog.current?.querySelector('.post-modal')
    const handleEnd = (event: Event) => { if (event.target === modal) finishClose() }
    modal?.addEventListener('animationend', handleEnd)
    const timer = window.setTimeout(finishClose, EXPANDED_POST_CLOSE_MS + 100)
    return () => {
      modal?.removeEventListener('animationend', handleEnd)
      window.clearTimeout(timer)
    }
  }, [closing, finishClose])

  function close() {
    if (closing || closed.current) return
    if (prefersReducedMotion()) finishClose()
    else setClosing(true)
  }

  return (
    <dialog ref={dialog} className={`post-dialog${closing ? ' is-closing' : ''}`} aria-label="Expanded post" aria-describedby={text ? 'expanded-post-text' : undefined}
      onCancel={event => { event.preventDefault(); close() }} onClick={close}>
      {flight && <NetBotFlyby flight={flight} />}
      <section className="post-modal expanded-post-modal" onClick={event => event.stopPropagation()}>
        <header className="modal-actions">
          <div className="modal-author">
            <PostIdentity post={post} />
            <div className="modal-author-details">
              <strong><ProfileName post={post} /></strong>
              <WallPostAge post={post} sortBy={sortBy} />
            </div>
          </div>
          <div className="modal-controls">
            <span className="modal-pin-status" role="status">{unpinStatus}</span>
            {pinned && <button className="button button-muted" onClick={() => {
              onUnpin()
              setUnpinStatus('Returned to feed')
              closeButton.current?.focus()
            }}>Unpin post</button>}
            <button ref={closeButton} className="modal-close" onClick={close} aria-label="Close expanded post" autoFocus>×</button>
          </div>
        </header>
        <div className="post-modal-content">
          {text && <blockquote id="expanded-post-text">{text}</blockquote>}
          <PostImages post={post} expanded />
          {post.sourceUrl && <a className="source-link" href={post.sourceUrl} target="_blank" rel="noreferrer">Open original post ↗</a>}
        </div>
      </section>
    </dialog>
  )
}

function Wall({ event }: { event: EventInfo | null }) {
  const wall = useRef<HTMLElement>(null)
  const [immersive, setImmersive] = useState(false)
  const [fullscreenError, setFullscreenError] = useState('')
  const [items, setItems] = useState<Post[]>([])
  const [promos, setPromos] = useState<Promo[]>([])
  const [pins, setPins] = useState<string[]>([])
  const [pinsLoadedFor, setPinsLoadedFor] = useState('')
  const [sortBy, setSortBy] = useState<WallSort>('post')
  const [highlighted, setHighlighted] = useState<Set<string>>(() => new Set())
  const [revision, setRevision] = useState(0)
  const [newCount, setNewCount] = useState(0)
  const [expanded, setExpanded] = useState<Post | null>(null)
  const { flight, launch } = useNetBotFlight()
  const expandedTrigger = useRef<HTMLButtonElement>(null)
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)
  const loadRequest = useRef(0)
  const snapshotVersion = useRef(0)
  const storageKey = `eventwall:pins:${event?.slug ?? 'communityevent'}`
  const offline = Boolean(error) && !loading
  const hasNewPosts = !offline && newCount > 0
  const itemById = useMemo(() => new Map(items.map(item => [item.id, item])), [items])
  const pinnedItems = useMemo(() => pins.map(id => itemById.get(id)).filter((item): item is Post => Boolean(item)), [pins, itemById])
  const unpinnedItems = useMemo(() => items.filter(item => !pins.includes(item.id))
    .sort((a, b) => compareWallPosts(a, b, sortBy)), [items, pins, sortBy])

  useEffect(() => {
    const handleFullscreen = () => {
      if (!document.fullscreenElement) setImmersive(false)
    }
    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !document.fullscreenElement && !wall.current?.querySelector('dialog[open]')) setImmersive(false)
    }
    document.addEventListener('fullscreenchange', handleFullscreen)
    document.addEventListener('keydown', handleEscape)
    return () => {
      document.removeEventListener('fullscreenchange', handleFullscreen)
      document.removeEventListener('keydown', handleEscape)
    }
  }, [])

  async function toggleImmersive() {
    setFullscreenError('')
    if (immersive) {
      try {
        if (document.fullscreenElement === wall.current) await document.exitFullscreen()
        setImmersive(false)
      } catch {
        setFullscreenError('Could not exit fullscreen. Press Escape to return to the wall.')
      }
    } else {
      setImmersive(true)
      try {
        if (wall.current?.requestFullscreen) await wall.current.requestFullscreen()
      } catch {
        setFullscreenError('Browser fullscreen is unavailable. Immersive mode is still on; use Exit immersive mode or Escape to leave.')
      }
    }
  }

  const loadWall = useCallback(async (
    mode: WallLoadMode,
    selectedSort: WallSort,
    previousRevision = 0,
    previousItems: Post[] = [],
    previousHighlights: ReadonlySet<string> = new Set(),
  ) => {
    const request = ++loadRequest.current
    try {
      const params = new URLSearchParams({ sort: selectedSort })
      if (mode === 'sort') params.set('throughRevision', String(previousRevision))
      if (mode === 'refresh') params.set('afterRevision', String(previousRevision))
      const [snapshot, savedPins] = await Promise.all([
        api<{ revision: number; items: Post[]; newCount: number; promos: Promo[] }>(`/api/public/wall?${params}`),
        Promise.resolve<string[]>(JSON.parse(localStorage.getItem(storageKey) ?? '[]')),
      ])
      const nextItems = snapshot.items
      const nextIds = new Set(nextItems.map(post => post.id))
      const retained = mode === 'sort'
        ? previousItems.filter(post => previousHighlights.has(post.id) && !nextIds.has(post.id))
        : []
      const [pinnedOnly, retainedStatus] = await Promise.all([
        Promise.all(savedPins
          .filter(id => !nextIds.has(id))
          .map(id => api<Post>(`/api/public/wall/items/${id}`).catch(() => null))),
        retained.length ? api<{ liveKnownIds: string[] }>('/api/public/wall/changes', {
          method: 'POST',
          body: JSON.stringify({ sinceRevision: previousRevision, knownIds: retained.map(post => post.id), knownThroughRevision: previousRevision }),
        }) : Promise.resolve(null),
      ])
      if (request !== loadRequest.current) return
      snapshotVersion.current++
      const validPinned = pinnedOnly.filter((post): post is Post => post !== null &&
        (mode !== 'sort' || (post.publishedRevision ?? 0) <= previousRevision))
      const all = [...nextItems, ...validPinned]
      if (retainedStatus) {
        const included = new Set(all.map(post => post.id))
        const live = new Set(retainedStatus.liveKnownIds)
        all.push(...retained.filter(post => live.has(post.id) && !included.has(post.id)))
      }
      const validPins = savedPins.filter(id => all.some(post => post.id === id))
      setItems(all)
      setPromos(snapshot.promos)
      setPins(validPins)
      setPinsLoadedFor(storageKey)
      if (mode !== 'sort') {
        setRevision(snapshot.revision)
        setNewCount(0)
      }
      if (mode === 'initial') setHighlighted(new Set())
      if (mode === 'sort') {
        const visible = new Set(all.map(post => post.id))
        setHighlighted(current => new Set([...current].filter(id => visible.has(id))))
      }
      if (mode === 'refresh') {
        const known = new Set(previousItems.map(post => post.id))
        setHighlighted(new Set(nextItems.filter(post =>
          (post.publishedRevision ?? 0) > previousRevision && !known.has(post.id)).map(post => post.id)))
      }
      setExpanded(current => current && all.some(post => post.id === current.id) ? all.find(post => post.id === current.id) ?? null : null)
      setError('')
    } catch (cause) {
      if (request === loadRequest.current) setError(cause instanceof Error ? cause.message : 'The wall could not be loaded.')
    } finally {
      if (request === loadRequest.current) setLoading(false)
    }
  }, [storageKey])

  const checkWall = useCallback(async () => {
    const request = loadRequest.current
    const snapshot = snapshotVersion.current
    try {
      const ids = [...new Set([...items.map(item => item.id), ...pins, ...(expanded ? [expanded.id] : [])])]
      const live = new Set<string>()
      let count = 0
      const response = await api<{ revision: number; newCount: number; liveKnownIds: string[]; promos: Promo[] }>('/api/public/wall/changes', {
        method: 'POST', body: JSON.stringify({ sinceRevision: revision, knownIds: ids }),
      })
      if (request !== loadRequest.current || snapshot !== snapshotVersion.current) return
      response.liveKnownIds.forEach(id => live.add(id))
      count = response.newCount
      setItems(current => current.filter(item => live.has(item.id) || !ids.includes(item.id) && !pins.includes(item.id)))
      setPins(current => current.filter(id => live.has(id)))
      setHighlighted(current => new Set([...current].filter(id => live.has(id))))
      setExpanded(current => current && !live.has(current.id) ? null : current)
      setNewCount(count)
      setPromos(response.promos)
      setError('')
    } catch (cause) {
      if (request === loadRequest.current && snapshot === snapshotVersion.current) {
        setError(cause instanceof Error ? cause.message : 'The wall connection is unavailable.')
      }
    }
  }, [expanded, items, pins, revision])

  useEffect(() => {
    if (pinsLoadedFor === storageKey) localStorage.setItem(storageKey, JSON.stringify(pins))
  }, [pins, pinsLoadedFor, storageKey])

  useEffect(() => {
    const pending = loadRequest
    void loadWall('initial', 'post')
    return () => { pending.current++ }
  }, [loadWall])

  useEffect(() => {
    const timer = window.setInterval(() => void checkWall(), WALL_CHECK_INTERVAL_MS)
    const recheckOnFocus = () => { if (document.visibilityState === 'visible') void checkWall() }
    document.addEventListener('visibilitychange', recheckOnFocus)
    return () => {
      window.clearInterval(timer)
      document.removeEventListener('visibilitychange', recheckOnFocus)
    }
  }, [checkWall])

  const togglePin = (id: string) => {
    setPins(current => current.includes(id) ? current.filter(value => value !== id) : [id, ...current].slice(0, 50))
  }

  const refreshWall = () => void loadWall('refresh', sortBy, revision, items, highlighted)
  const changeSort = (next: WallSort) => {
    setSortBy(next)
    void loadWall('sort', next, revision, items, highlighted)
  }

  const renderCard = (post: Post, pinned: boolean) => {
    const text = formatWallPostText(post.text)
    const isNew = highlighted.has(post.id)
    return <article className={`post-card${pinned ? ' pinned-card' : ''}${isNew ? ' is-new' : ''}`} key={post.id}>
      <button ref={post.id === expanded?.id ? expandedTrigger : undefined} className="card-open" onClick={() => { launch(); setExpanded(post) }}
        aria-label={`Expand post by ${post.authorName}`} aria-description={`Source: ${sourceNames[post.source]}${isNew ? '; Newly approved' : ''}`}>
        <span className="card-header">
          <span className="card-person">
            <PostIdentity post={post} />
            <span className="card-person-details">
              <span className="post-author">{post.authorName}</span>
              {pinned && <WallPostAge post={post} sortBy={sortBy} className="card-age" />}
            </span>
          </span>
          {!pinned && <WallPostAge post={post} sortBy={sortBy} className="card-age" />}
        </span>
        {text && <span className="post-text">{text}</span>}
        {!pinned && <PostImages post={post} />}
      </button>
      <button className={`pin-button ${pinned ? 'is-pinned' : ''}`} onClick={() => togglePin(post.id)} aria-pressed={pinned} aria-label={pinned ? 'Unpin post' : 'Pin post'} title={pinned ? 'Unpin' : 'Pin to top'}>
        {pinned ? '◆' : '◇'}
      </button>
    </article>
  }

  return (
    <main ref={wall} className={`wall-page${immersive ? ' immersive-wall' : ''}`}>
      {!immersive && <section className="wall-hero">
        <NetBotHero />
        <div>
          <span className="eyebrow"><span className="live-dot" /> LIVE CONVERSATION</span>
          <h1>The room is <em>yours.</em></h1>
          <p>Approved questions and moments from {event?.name ?? 'your event'}.</p>
        </div>
        <div className="wall-actions">
          <span className={offline ? 'wall-offline' : 'wall-online'}><span className={`status-dot${offline ? ' closed-dot' : ''}`} /> {offline ? 'Wall disconnected' : 'On stage'}</span>
          <button className="button button-refresh" onClick={refreshWall} aria-label="Refresh live posts">
            {offline ? 'Reconnect' : '↻ Refresh'}
          </button>
          <button className="button button-muted" onClick={() => void toggleImmersive()} aria-pressed={immersive}>Immerse mode</button>
        </div>
      </section>}

      {immersive && (
        <div className={`immersive-controls${hasNewPosts ? ' has-new' : ''}`} role="region" aria-label="Immersive wall controls">
          <span className="immersive-controls-hint" aria-hidden="true">
            {hasNewPosts && <span className="new-posts-dot" data-testid="new-posts-indicator" />}
            {hasNewPosts ? `${newCount} new · Wall controls` : 'Wall controls'}
          </span>
          <div className="immersive-controls-panel">
            <span className="new-count" role="status">{newCount} new {newCount === 1 ? 'post' : 'posts'} ready</span>
            <button className="button button-refresh" onClick={refreshWall} aria-label="Refresh live posts">
              {hasNewPosts && <span className="new-posts-dot" aria-hidden="true" />}
              {offline ? 'Reconnect' : '↻ Refresh'}
            </button>
            <SkinPicker />
            <ThemeToggle />
            <button className="button button-muted" onClick={() => void toggleImmersive()}>Exit immersive mode</button>
          </div>
        </div>
      )}
      {immersive && <NetBotPeek visible={hasNewPosts} />}
      {!expanded && flight && <NetBotFlyby flight={flight} />}
      {!immersive && hasNewPosts && (
        <div className="new-posts-row">
          <span className="new-count">{newCount} new {newCount === 1 ? 'post' : 'posts'} ready</span>
          <button className="button button-new" onClick={refreshWall}>Show new posts <span>↓</span></button>
        </div>
      )}

      {fullscreenError && <div className="notice notice-warning" role="alert">{fullscreenError}</div>}
      <div className="connection-banner-slot" aria-live="polite">
        {offline && <span className="connection-banner" title={error}>
          <span className="status-dot closed-dot" aria-hidden="true" /> {items.length ? 'Offline · showing last loaded posts · retrying' : 'Offline · retrying'}
        </span>}
      </div>

      <aside className="question-prompt" aria-label="Join the live conversation">
        {event?.submissionSiteUrl ? <a className="question-link" href={event.submissionSiteUrl}>
          <strong>Submit a question</strong>{' '}<span>{new URL(event.submissionSiteUrl).host}</span>
        </a> : <span role="status">Question website address unavailable.</span>}
        <div className="question-instructions">
          <p>Ask your question live:</p>
          <span className="question-platforms" role="group" aria-label="Twitch and YouTube questions">
            <SourceIcon source="Twitch" /><SourceIcon source="YouTube" />
            <span className="question-platform-label"><strong>#question</strong></span>
          </span>
          <span className="question-platforms" role="group" aria-label="X, Bluesky, and Mastodon questions">
            <SourceIcon source="X" /><SourceIcon source="Bluesky" /><SourceIcon source="Mastodon" />
            <span className="question-platform-label"><strong>{event?.questionHashtag ?? '#communityevent'}</strong> or <strong>{event?.name ?? 'Community Event'}</strong></span>
          </span>
        </div>
      </aside>

      {pinnedItems.length > 0 && (
        <section className="pinned-section" aria-label="Pinned posts">
          <div className="section-heading"><span className="section-kicker">YOUR PICKS</span><span>PINNED TO THE TOP</span></div>
          <div className="pinned-grid">{pinnedItems.map(post => renderCard(post, true))}</div>
        </section>
      )}

      {event && <WallPromos key={event.slug} eventSlug={event.slug} promos={promos} />}

      <section className="feed-section" aria-label="Approved live posts">
        <div className="section-heading feed-heading">
          <span className="section-kicker">THE FEED</span>
          <div className="feed-heading-controls">
            <span>{items.length} APPROVED MOMENTS</span>
            <button type="button" className="wall-sort" disabled={loading}
              aria-label="Sort by newest approved" aria-pressed={sortBy === 'approved'}
              aria-description={`Currently sorted by ${sortBy === 'post' ? 'newest post date' : 'newest approval'}.`}
              title={sortBy === 'post'
                ? 'Sorted by newest post date. Click to sort by newest approved.'
                : 'Sorted by newest approved. Click to sort by newest post date.'}
              onClick={() => changeSort(sortBy === 'post' ? 'approved' : 'post')}>
              <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor"
                strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                <path d="M4 4v15m-3-3 3 3 3-3M9 5h11M9 9h8" />
                {sortBy === 'post'
                  ? <><circle cx="16" cy="16" r="4" /><path d="M16 14v2l1.5 1" /></>
                  : <path d="m11 16 3 3 6-7" />}
              </svg>
            </button>
          </div>
        </div>
        {loading ? <div className="empty-state">Loading the conversation…</div> :
          unpinnedItems.length === 0 && pinnedItems.length === 0
            ? <div className="empty-state"><span className="empty-orbit">✳</span><h2>Room for a great question.</h2><p>As the community joins in, approved posts will appear here.</p></div>
            : <MasonryGrid className="post-waterfall">{unpinnedItems.map(post => renderCard(post, false))}</MasonryGrid>}
      </section>

      {!immersive && <footer className="wall-footer"><span>Curated with care by the event team.</span><span>Refreshing never moves the conversation until you’re ready.</span></footer>}

      {expanded && (
        <ExpandedPost post={expanded} pinned={pins.includes(expanded.id)} sortBy={sortBy} flight={flight} onUnpin={() => togglePin(expanded.id)} onClose={() => {
          expandedTrigger.current?.focus()
          setExpanded(null)
        }} />
      )}
    </main>
  )
}

function App() {
  const [event, setEvent] = useState<EventInfo | null>(null)
  const [error, setError] = useState('')
  const legacySubmit = new URLSearchParams(window.location.search).get('view') === 'submit'
  useEffect(() => {
    void api<EventInfo>('/api/public/event').then(info => {
      setEvent(info)
      if (legacySubmit) {
        if (info.submissionSiteUrl) window.location.replace(info.submissionSiteUrl)
        else setError('The question website address is unavailable. Please contact the event team.')
      }
    }).catch(cause => setError(cause instanceof Error ? cause.message : 'The event could not be loaded.'))
  }, [legacySubmit])
  return (
    <div className="app-shell stage-shell">
      <Header event={event} />
      {error && <div className="notice notice-error" role="alert">{error}</div>}
      {legacySubmit ? <main className="empty-state" role="status">Opening the question website...</main> : <Wall key={event?.slug ?? 'communityevent'} event={event} />}
      <SiteFooter />
    </div>
  )
}

export default App
