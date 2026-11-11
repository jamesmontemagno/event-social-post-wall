import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, api, postFilterLabels, sourceNames } from './shared'
import type { AdminEventSettings, Post, PostImage, Source, SourceInfo, Status } from './shared'
import PostIdentity, { ProfileName, SourceIcon } from './PostIdentity'
import ConfirmationDialog from './ConfirmationDialog'
import DismissibleNotice from './DismissibleNotice'
import PostImages from './PostImages'
import ImagePreview from './ImagePreview'
import PostFilters from './PostFilters'
import TeamMembers from './TeamMembers'
import Promos from './Promos'
import StaffPresence from './StaffPresence'
import EventReport from './EventReport'

type StaffSession = { authenticated: boolean; username: string | null; isAdministrator: boolean }

function isConnected(source: SourceInfo) {
  return source.ready || source.state === 'connected-partial'
}

function StaffLogin({ onLogin }: { onLogin: () => Promise<void> }) {
  const [passcode, setPasscode] = useState('')
  const [username, setUsername] = useState('admin')
  const [configured, setConfigured] = useState(true)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    void api<{ configured: boolean }>('/api/admin/session-status')
      .then(status => setConfigured(status.configured))
      .catch(() => setError('The staff service is not reachable.'))
  }, [])

  async function login(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError('')
    try {
      await api('/api/admin/login', { method: 'POST', body: JSON.stringify({ username, passcode }) })
      setPasscode('')
      await onLogin()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Sign-in failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="staff-login">
      <span className="eyebrow">STAFF ACCESS</span>
      <h1>Behind the<br /><em>conversation.</em></h1>
      <p>Sign in with your team username and access code. Admin uses the existing server passcode.</p>
      {!configured && <div className="notice notice-warning">Staff access is not configured. Set a strong STAFF_PASSCODE in the server’s protected app settings, then restart the app.</div>}
      <form onSubmit={login}>
        <label htmlFor="staff-username">Username</label>
        <input id="staff-username" autoComplete="username" autoCapitalize="none" spellCheck={false} value={username} onChange={event => setUsername(event.target.value)} maxLength={40} required />
        <label htmlFor="staff-passcode">Access code</label>
        <input id="staff-passcode" type="password" autoComplete="current-password" value={passcode} onChange={event => setPasscode(event.target.value)} minLength={12} required />
        {error && <div className="notice notice-error">{error}</div>}
        <button className="button button-primary button-wide" disabled={busy || !configured}>{busy ? 'Signing in…' : 'Open staff console'} <span>↗</span></button>
      </form>
    </div>
  )
}

function StaffConsole({ onLogout, session }: { onLogout: () => void; session: StaffSession }) {
  const { isAdministrator } = session
  const [csrf, setCsrf] = useState('')
  const [teamBusy, setTeamBusy] = useState(false)
  const [posts, setPosts] = useState<Post[]>([])
  const [sources, setSources] = useState<SourceInfo[]>([])
  const [collectionBusy, setCollectionBusy] = useState(false)
  const [submissionsOpen, setSubmissionsOpen] = useState(true)
  const [tab, setTab] = useState<'queue' | 'approved' | 'sources' | 'filters' | 'analytics' | 'team' | 'promos'>('queue')
  const [filterSettings, setFilterSettings] = useState<AdminEventSettings | null>(null)
  const [layout, setLayout] = useState<'list' | 'grid'>('grid')
  const [imagePreview, setImagePreview] = useState<{ post: Post; image: PostImage } | null>(null)
  const [confirmation, setConfirmation] = useState<{ kind: 'block'; post: Post } | { kind: 'portal'; open: boolean } | null>(null)
  const [confirmBusy, setConfirmBusy] = useState(false)
  const [settingsLoaded, setSettingsLoaded] = useState(false)
  const [status, setStatus] = useState<Status | 'All'>('Pending')
  const [sourceFilter, setSourceFilter] = useState<Source | 'All'>('All')
  const [search, setSearch] = useState('')
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [busyId, setBusyId] = useState('')
  const [manualOpen, setManualOpen] = useState(false)
  const [incoming, setIncoming] = useState<Post[] | null>(null)
  const queueKey = JSON.stringify([tab, status, sourceFilter, search.trim()])
  const [loadedQueueKey, setLoadedQueueKey] = useState('')
  const reportError = useCallback((cause: unknown, fallback: string) => {
    if (cause instanceof ApiError && cause.status === 401) { onLogout(); return }
    setError(cause instanceof Error ? cause.message : fallback)
  }, [onLogout])

  const loadStaff = useCallback(async () => {
    if (!isAdministrator) {
      const token = await api<{ requestToken: string }>('/api/admin/csrf')
      setCsrf(token.requestToken)
      return
    }
    const [token, sourceData, settings] = await Promise.all([
      api<{ requestToken: string }>('/api/admin/csrf'),
      api<SourceInfo[]>('/api/admin/sources'),
      api<AdminEventSettings>('/api/admin/settings'),
    ])
    setCsrf(token.requestToken)
    setSources(sourceData)
    setSubmissionsOpen(settings.submissionsOpen)
    setFilterSettings(settings)
    setSettingsLoaded(true)
  }, [isAdministrator])

  const fetchQueue = useCallback(async () => {
    const params = new URLSearchParams()
    if (tab === 'approved') params.set('status', 'Live')
    else if (status !== 'All') params.set('status', status)
    if (sourceFilter !== 'All') params.set('source', sourceFilter)
    if (search.trim()) params.set('search', search.trim())
    return api<Post[]>(`/api/admin/queue?${params}`)
  }, [search, sourceFilter, status, tab])

  const loadQueue = useCallback(async () => {
    const next = await fetchQueue()
    setPosts(next)
    setLoadedQueueKey(queueKey)
    setIncoming(null)
  }, [fetchQueue, queueKey])

  useEffect(() => {
    if (tab !== 'queue' && tab !== 'approved') return undefined
    let active = true
    const timer = window.setInterval(() => {
      void fetchQueue().then(next => { if (active) setIncoming(next) })
        .catch(cause => { if (active) reportError(cause, 'Could not check for new posts.') })
    }, 5000)
    return () => { active = false; window.clearInterval(timer) }
  }, [fetchQueue, reportError, tab])

  const waitingCount = useMemo(() => {
    if (!incoming) return 0
    const shown = new Set(posts.map(post => post.id))
    return incoming.filter(post => !shown.has(post.id)).length
  }, [incoming, posts])

  function showWaiting() {
    if (incoming) setPosts(incoming)
    setIncoming(null)
  }

  useEffect(() => {
    void loadStaff().catch(cause => reportError(cause, 'Could not initialize the staff console.'))
  }, [loadStaff, reportError])

  useEffect(() => {
    let active = true
    if (tab === 'queue' || tab === 'approved') {
      setPosts([])
      setIncoming(null)
      void fetchQueue().then(next => { if (active) { setPosts(next); setLoadedQueueKey(queueKey) } })
        .catch(cause => { if (active) reportError(cause, 'Could not load posts.') })
    }
    if (tab === 'sources') void api<SourceInfo[]>('/api/admin/sources').then(setSources).catch(cause => setError(cause instanceof Error ? cause.message : 'Could not load sources.'))
    return () => { active = false }
  }, [fetchQueue, queueKey, reportError, tab])

  async function act(post: Post, action: 'approve' | 'reject' | 'withdraw') {
    setBusyId(post.id)
    setError('')
    setNotice('')
    try {
      await api(`/api/admin/posts/${post.id}/${action}`, {
        method: 'POST',
        body: JSON.stringify({ version: post.version ?? 0 }),
      }, csrf)
      setNotice(action === 'approve' ? 'Approved and sent live.' : action === 'withdraw' ? 'Post withdrawn from the live wall.' : 'Post rejected.')
      await loadQueue()
    } catch (cause) {
      reportError(cause, 'The moderation action failed.')
    } finally {
      setBusyId('')
    }
  }

  async function confirmAction() {
    if (!confirmation || confirmBusy) return
    setConfirmBusy(true)
    setError('')
    setNotice('')
    try {
      if (confirmation.kind === 'portal') {
        const settings = await api<{ submissionsOpen: boolean }>('/api/admin/settings/submissions', {
          method: 'POST',
          body: JSON.stringify({ open: confirmation.open }),
        }, csrf)
        setSubmissionsOpen(settings.submissionsOpen)
        setNotice(settings.submissionsOpen ? 'Portal submissions opened.' : 'Portal submissions paused.')
      } else {
        await api(`/api/admin/posts/${confirmation.post.id}/block-author`, {
          method: 'POST', body: JSON.stringify({ version: confirmation.post.version ?? 0 }),
        }, csrf)
        setNotice('Account blocked for this event. Future posts will be auto-rejected; existing posts are unchanged.')
        await loadQueue()
      }
      setConfirmation(null)
    } catch (cause) {
      setConfirmation(null)
      reportError(cause, 'Could not save this change.')
    } finally {
      setConfirmBusy(false)
    }
  }

  async function addManual(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const data = new FormData(event.currentTarget)
    try {
      const post = await api<Post>('/api/admin/manual', {
        method: 'POST',
        body: JSON.stringify({
          text: data.get('manualText'),
          authorName: data.get('manualAuthor'),
          sourceUrl: data.get('manualUrl') || null,
        }),
      }, csrf)
      setManualOpen(false)
      setNotice(post.status === 'Rejected'
        ? 'Manual post automatically rejected. Select Rejected in the review queue to see reasons or approve it.'
        : 'Manual post added to the review queue as unverified.')
      await loadQueue()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not add the manual post.')
    }
  }

  async function setYouTubeCollection(running: boolean) {
    if (collectionBusy) return
    setCollectionBusy(true)
    setError('')
    setNotice('')
    try {
      const next = await api<SourceInfo[]>('/api/admin/sources/youtube/collection', {
        method: 'POST', body: JSON.stringify({ running }),
      }, csrf)
      setSources(next)
      setNotice(running
        ? 'YouTube collection started. Quota pauses and polling limits still apply.'
        : 'YouTube collection stopped. An in-flight request may finish; no further polls will start.')
    } catch (cause) {
      reportError(cause, 'Could not change YouTube collection.')
    } finally {
      setCollectionBusy(false)
    }
  }

  async function logout() {
    try {
      await api('/api/admin/logout', { method: 'POST' }, csrf)
      onLogout()
    } catch (cause) {
      reportError(cause, 'Could not sign out.')
    }
  }

  return (
    <main className="staff-page">
      <div className="staff-heading">
        <div><span className="eyebrow">EVENT CONTROL ROOM</span><h1>Keep the signal <em>good.</em></h1><p>Review every post before it reaches the live wall.</p><p className="staff-identity">Signed in as <strong>{session.username}</strong> · {isAdministrator ? 'Administrator' : 'Moderator'}</p>
          <StaffPresence csrf={csrf} onLogout={onLogout} />
        </div>
        <button className="button button-muted" disabled={!csrf || teamBusy} onClick={() => void logout()}>Sign out</button>
      </div>
      <div className="staff-toolbar">
        <div className="staff-tabs" role="tablist">
          <button role="tab" aria-selected={tab === 'queue'} className={tab === 'queue' ? 'selected' : ''} disabled={!!busyId || teamBusy} onClick={() => setTab('queue')}>Review queue</button>
          <button role="tab" aria-selected={tab === 'approved'} className={tab === 'approved' ? 'selected' : ''} disabled={!!busyId || teamBusy} onClick={() => setTab('approved')}>Approved</button>
          <button role="tab" aria-selected={tab === 'promos'} className={tab === 'promos' ? 'selected' : ''} disabled={!!busyId || teamBusy} onClick={() => setTab('promos')}>Promos</button>
          {isAdministrator && <><button role="tab" aria-selected={tab === 'sources'} className={tab === 'sources' ? 'selected' : ''} disabled={!!busyId || teamBusy} onClick={() => setTab('sources')}>Social connections <span className="tab-count">{sources.filter(isConnected).length}/{sources.length}</span></button>
          <button role="tab" aria-selected={tab === 'filters'} className={tab === 'filters' ? 'selected' : ''} disabled={!!busyId || teamBusy} onClick={() => setTab('filters')}>Post filters</button>
          <button role="tab" aria-selected={tab === 'analytics'} className={tab === 'analytics' ? 'selected' : ''} disabled={!!busyId || teamBusy} onClick={() => setTab('analytics')}>Event report</button>
          <button role="tab" aria-selected={tab === 'team'} className={tab === 'team' ? 'selected' : ''} disabled={!!busyId || teamBusy} onClick={() => setTab('team')}>Team</button></>}
        </div>
        {isAdministrator && <button className={`button ${submissionsOpen ? 'button-muted' : 'button-primary'}`} disabled={!settingsLoaded || !!busyId} onClick={() => setConfirmation({ kind: 'portal', open: !submissionsOpen })}>
          <span className={`status-dot ${submissionsOpen ? '' : 'closed-dot'}`} /> Portal {submissionsOpen ? 'open' : 'paused'}
        </button>}
      </div>

      {error && <div className="notice notice-error" role="alert">{error}</div>}
      {notice && <DismissibleNotice message={notice} onDismiss={() => setNotice('')} />}

      {(tab === 'queue' || tab === 'approved') && (
        <section className="admin-panel">
          <div className="queue-header">
            <div><h2>{tab === 'approved' ? 'Approved and live' : 'Incoming conversation'}</h2><p>{tab === 'approved' ? 'Review published posts. Withdraw anything approved by mistake.' : 'Nothing reaches the wall without your say.'}</p></div>
            <div className="queue-actions">
              <button className="button button-muted" onClick={() => setManualOpen(value => !value)}>＋ Add manually</button>
              <button className="button button-muted" onClick={() => void loadQueue().catch(cause => setError(cause instanceof Error ? cause.message : 'Could not refresh posts.'))}>Refresh posts</button>
            </div>
          </div>
          {manualOpen && <form className="manual-form" onSubmit={event => void addManual(event)}>
            <strong>Add an unverified post</strong>
            <textarea name="manualText" aria-label="Post text" required maxLength={2000} rows={3} placeholder="Paste the text exactly as provided" />
            <input name="manualAuthor" aria-label="Author attribution (optional)" maxLength={80} placeholder="Author attribution (optional)" />
            <input name="manualUrl" aria-label="HTTPS source link (optional)" type="url" placeholder="HTTPS source link (optional)" />
            <div className="form-actions"><span>Manual entries are filtered and still need staff approval.</span><button className="button button-primary">Add post</button></div>
          </form>}
          <div className="queue-filters">
            <input aria-label="Search queue" value={search} onChange={event => setSearch(event.target.value)} placeholder="Search messages or names…" />
            {tab === 'queue' && <select aria-label="Filter by status" value={status} onChange={event => setStatus(event.target.value as Status | 'All')}>
              <option value="Pending">Pending</option><option value="Live">Live</option><option value="Rejected">Rejected</option><option value="Withdrawn">Withdrawn</option><option value="All">All statuses</option>
            </select>}
            <select aria-label="Filter by source" value={sourceFilter} onChange={event => setSourceFilter(event.target.value as Source | 'All')}>
              <option value="All">All sources</option>{Object.entries(sourceNames).map(([key, label]) => <option value={key} key={key}>{label}</option>)}
            </select>
            <div className="layout-switch" role="group" aria-label="Post layout">
              <button className="button button-muted" aria-pressed={layout === 'list'} onClick={() => setLayout('list')}>List</button>
              <button className="button button-muted" aria-pressed={layout === 'grid'} onClick={() => setLayout('grid')}>Grid</button>
            </div>
          </div>
          {loadedQueueKey === queueKey && waitingCount > 0 && <div className="queue-waiting" role="status">
            <span><span className="live-dot" /> {waitingCount} new {waitingCount === 1 ? 'post' : 'posts'} waiting for review</span>
            <button className="button button-new" onClick={showWaiting}>Show</button>
          </div>}
          {loadedQueueKey !== queueKey ? <div className="empty-queue" role="status">Loading posts...</div> : posts.length === 0 ? <div className="empty-queue"><span>✳</span><h3>Nothing waiting in this view.</h3><p>When a new post arrives, it’ll be ready here for review.</p></div> :
            <div className={`queue-list ${layout === 'grid' ? 'queue-grid' : ''}`}>{posts.map(post => (
              <article className="queue-item" key={post.id}>
                <div className="queue-item-main">
                  <div className="queue-item-meta"><PostIdentity post={post} /><span>{sourceNames[post.source]}</span><span>·</span><time dateTime={post.sourceCreatedAtUtc ?? post.receivedAtUtc}>{post.sourceCreatedAtUtc ? 'Posted' : 'Received'} {new Date(post.sourceCreatedAtUtc ?? post.receivedAtUtc).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'long' })}</time><span className={`state-badge state-${post.status?.toLowerCase()}`}>{post.status === 'Live' ? 'Approved' : post.status}</span>{post.authorBlocked && <span className="state-badge state-rejected">Account blocked</span>}</div>
                  <p>{post.text}</p>
                  {!!post.filterReasons?.length && <div className="post-filter-findings">
                    <strong>{post.status === 'Rejected' ? 'Automatically rejected' : 'Filter matches (staff decision retained)'}</strong>
                    <ul aria-label="Post filter matches">{post.filterReasons.map(reason => <li key={reason}>{postFilterLabels[reason]}</li>)}</ul>
                  </div>}
                  <PostImages post={post} onPreview={image => setImagePreview({ post, image })} />
                  <div className="queue-author"><ProfileName post={post} />{post.sourceUrl && <> · <a href={post.sourceUrl} target="_blank" rel="noreferrer">Original post ↗</a></>}</div>
                  {post.moderatedBy && <div className="queue-reviewer">Last moderated by {post.moderatedBy}</div>}
                </div>
                <div className="queue-item-actions">
                  {post.status !== 'Live' && <button className="button button-primary" disabled={!!busyId || !csrf} onClick={() => void act(post, 'approve')}>{busyId === post.id ? 'Saving…' : '✓ Approve & send live'}</button>}
                  {post.status === 'Live' && <button className="button button-warning" disabled={!!busyId || !csrf} onClick={() => void act(post, 'withdraw')}>Withdraw</button>}
                  {post.status === 'Pending' && <button className="text-button" disabled={!!busyId || !csrf} onClick={() => void act(post, 'reject')}>Reject</button>}
                  <button className="text-button" disabled={!!busyId || !csrf || !post.canBlockAuthor || post.authorBlocked}
                    title={!post.canBlockAuthor ? 'A stable social account ID is required; anonymous/manual names cannot be blocked.' : undefined}
                    onClick={() => setConfirmation({ kind: 'block', post })}>{post.authorBlocked ? 'User blocked' : 'Block user'}</button>
                </div>
              </article>
            ))}</div>}
        </section>
      )}

      {isAdministrator && filterSettings && <div hidden={tab !== 'filters'}><PostFilters initial={filterSettings} csrf={csrf} /></div>}

      {isAdministrator && tab === 'sources' && <section className="admin-panel">
        <div className="queue-header"><div><h2>Social connections</h2><p>Use official APIs. Credentials stay on the server, never in the browser.</p></div><button className="button button-muted" disabled={collectionBusy} onClick={() => void api<SourceInfo[]>('/api/admin/sources').then(setSources).catch(cause => setError(cause instanceof Error ? cause.message : 'Could not refresh source status.'))}>↻ Refresh status</button></div>
        <div className="source-setup-grid">{sources.map(source => (
          <article className="source-setup-card" key={source.provider}>
            <div className="setup-card-top"><SourceIcon source={source.provider as Source} /><span className={`state-badge state-${source.state}`}>{source.state.replaceAll('-', ' ')}</span></div>
            <h3>{source.provider}</h3><p>{source.summary}</p>
            <a className="source-doc-link" href={source.setupUrl} target="_blank" rel="noreferrer">Official setup docs ↗</a>
            {source.costNote && <p className="cost-note">{source.costNote}</p>}
            {source.provider === 'YouTube' && !source.simulated && source.collectionRunning != null && <>
              <button className={`button ${source.collectionRunning ? 'button-warning' : 'button-primary'}`}
                aria-pressed={source.collectionRunning} aria-busy={collectionBusy}
                disabled={!csrf || collectionBusy || (!source.collectionRunning && !source.collectionAvailable)}
                onClick={() => void setYouTubeCollection(!source.collectionRunning)}>
                {collectionBusy ? 'Saving...' : source.collectionRunning ? 'Stop YouTube collection' : 'Start YouTube collection'}
              </button>
              <p className="setup-callout">Start when the live stream begins and stop when it ends to conserve quota. The choice is saved across admin restarts. An in-flight request may finish after stopping.</p>
            </>}
            <details><summary>Server settings required</summary><ul>{source.requiredSettings.length
              ? source.requiredSettings.map(setting => <li key={setting}><code>{setting.replaceAll(':', '__').toUpperCase()}</code></li>)
              : <li>Set SOCIAL__BLUESKY__ENABLED and SOCIAL__BLUESKY__POLICYAPPROVED after review.</li>}</ul></details>
            {source.simulated && <span className="setup-callout demo-callout">Simulated feed for demos. Real collection stays off until configured.</span>}
            {source.provider === 'Bluesky' && !source.simulated && <span className="setup-callout">Live public stream; matching posts are filtered locally. Reconnects resume from the saved cursor.</span>}
            {source.provider === 'Mastodon' && !source.simulated && <span className="setup-callout">Polls one server's public hashtag timeline, with or without an access token. Edits and deletions are not detected.</span>}
            {!isConnected(source) && source.collectionRunning !== true && <span className="setup-callout">{source.state === 'paused' ? 'Collection is stopped. Start it when the live stream begins.' : 'Not collecting. Configure and test before the event.'}</span>}
          </article>
        ))}</div>
        <div className="source-safety-note"><strong>Keep credentials private.</strong> Set these values in Azure App Service configuration or a protected local environment file. Do not paste tokens into this page, commit them, or place them in GitHub Pages settings.</div>
      </section>}

      {isAdministrator && <EventReport active={tab === 'analytics'} onError={reportError} />}
      {isAdministrator && tab === 'team' && <TeamMembers csrf={csrf} onBusyChange={setTeamBusy} />}
      {tab === 'promos' && <Promos csrf={csrf} onBusyChange={setTeamBusy} onError={reportError} />}
      {imagePreview && <ImagePreview post={imagePreview.post} image={imagePreview.image} onClose={() => setImagePreview(null)} />}
      {confirmation && <ConfirmationDialog
        title={confirmation.kind === 'block' ? 'Block this user?' : confirmation.open ? 'Open portal submissions?' : 'Pause portal submissions?'}
        description={confirmation.kind === 'block'
          ? `Future posts from ${confirmation.post.authorName} on ${sourceNames[confirmation.post.source]} will be automatically rejected for this event. Existing posts will not change. This does not block them on the social platform.`
          : confirmation.open ? 'Anyone with the public portal link will be able to submit a question. Every submission still requires staff approval.'
            : 'New portal submissions will stop. Existing questions and the live wall will remain unchanged.'}
        confirmLabel={confirmation.kind === 'block' ? 'Block user' : confirmation.open ? 'Open submissions' : 'Pause submissions'}
        busy={confirmBusy} onConfirm={() => void confirmAction()} onCancel={() => setConfirmation(null)} />}
    </main>
  )
}

function Staff({ onLogout }: { onLogout: () => void }) {
  const [session, setSession] = useState<StaffSession | null>(null)
  const [checked, setChecked] = useState(false)
  const refresh = useCallback(async () => {
    const status = await api<StaffSession>('/api/admin/session-status')
    setSession(status)
    setChecked(true)
  }, [])
  useEffect(() => { void refresh().catch(() => setChecked(true)) }, [refresh])
  const logout = useCallback(() => { setSession(null); onLogout() }, [onLogout])
  if (!checked) return <main className="staff-page"><div className="empty-state">Checking staff access…</div></main>
  return session?.authenticated
    ? <StaffConsole session={session} onLogout={logout} />
    : <StaffLogin onLogin={refresh} />
}


export default Staff
