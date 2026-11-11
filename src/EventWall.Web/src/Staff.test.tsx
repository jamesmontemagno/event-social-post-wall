import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import AdminApp from './AdminApp'
import type { Post, PostFilterSettings, Promo, SourceInfo } from './shared'

beforeEach(() => {
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } })
  Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } })
})
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

function setup(sources: SourceInfo[] = [], isAdministrator = true, postOverrides: Partial<Post> = {}) {
  let open = true
  let postFilters: PostFilterSettings = {
    profanity: true, allCaps: true, singleWord: true, emojiOnly: true, locationOnly: true,
    noSubstantiveText: true, repeatedCharacters: true, duplicateText: true, blockedTerms: [], allowedTerms: [], locationPhrases: [],
  }
  let filterSettingsVersion = 0
  const post: Post = {
    id: 'one', source: 'Twitch', text: 'A useful question', authorName: 'Viewer',
    status: 'Pending', version: 0, receivedAtUtc: new Date().toISOString(), canBlockAuthor: true,
    ...postOverrides,
  }
  const requests: { url: string; body: Record<string, unknown> }[] = []
  let promos: Promo[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'https://test.local')
    const body = init?.body ? JSON.parse(String(init.body)) as Record<string, unknown> : {}
    if (url.pathname === '/api/admin/presence')
      return new Response(JSON.stringify([{ username: isAdministrator ? 'Admin' : 'TestModerator', isAdministrator, lastSeenAtUtc: new Date().toISOString() }]), { status: 200 })
    if (init?.method === 'POST') requests.push({ url: url.pathname, body })
    let data: unknown = {}
    if (url.pathname === '/api/public/event') data = { slug: 'communityevent', name: 'Community Event', submissionsOpen: open, wallSiteUrl: 'https://wall.example.test/', submissionSiteUrl: 'https://questions.example.test/' }
    if (url.pathname.endsWith('/session-status')) data = { configured: true, authenticated: true, username: isAdministrator ? 'Admin' : 'TestModerator', isAdministrator }
    if (url.pathname.endsWith('/csrf')) data = { requestToken: 'token' }
    if (url.pathname.endsWith('/sources')) data = sources
    if (url.pathname.endsWith('/sources/youtube/collection')) {
      sources = sources.map(source => source.provider === 'YouTube'
        ? { ...source, collectionRunning: Boolean(body.running), state: body.running ? 'test-required' : 'paused' }
        : source)
      data = sources
    }
    if (url.pathname.endsWith('/settings')) data = { submissionsOpen: open, postFilters, filterSettingsVersion }
    if (url.pathname.endsWith('/settings/filters')) {
      postFilters = body.postFilters as PostFilterSettings
      filterSettingsVersion++
      data = { submissionsOpen: open, postFilters, filterSettingsVersion }
    }
    if (url.pathname.endsWith('/settings/submissions')) { open = Boolean(body.open); data = { submissionsOpen: open } }
    if (url.pathname.endsWith('/block-author')) { post.authorBlocked = true; data = { blocked: true } }
    if (url.pathname.endsWith('/approve')) { post.status = 'Live'; post.version = 1 }
    if (url.pathname.endsWith('/withdraw')) { post.status = 'Withdrawn'; post.version = 2 }
    if (url.pathname === '/api/admin/manual') data = { ...post, source: 'Manual', status: 'Pending' }
    if (url.pathname.endsWith('/queue')) data = !url.searchParams.has('status') || url.searchParams.get('status') === post.status ? [{ ...post }] : []
    if (url.pathname === '/api/admin/promos') {
      if (init?.method === 'POST') {
        const promo: Promo = { id: String(promos.length + 1), title: String(body.title), description: String(body.description), url: body.url ? String(body.url) : null, active: true, version: 1, createdAtUtc: new Date().toISOString() }
        promos = [promo, ...promos]
        data = promo
      } else data = promos
    }
    const promoId = url.pathname.match(/^\/api\/admin\/promos\/([^/]+)(\/(?:active|delete))?$/)
    if (promoId) {
      if (promoId[2] === '/delete') {
        promos = promos.filter(promo => promo.id !== promoId[1])
        return new Response(null, { status: 204 })
      }
      promos = promos.map(promo => promo.id === promoId[1] ? promoId[2] === '/active'
        ? { ...promo, active: Boolean(body.active), version: promo.version + 1 }
        : { ...promo, title: String(body.title), description: String(body.description), url: body.url ? String(body.url) : null, version: promo.version + 1 } : promo)
      data = promos.find(promo => promo.id === promoId[1])
    }
    return new Response(JSON.stringify(data), { status: 200, headers: { 'Content-Type': 'application/json' } })
  })
  vi.stubGlobal('fetch', fetchMock)
  render(<AdminApp />)
  return { requests, fetchMock, post }
}

it.each([true, false])('shows staff presence across console screens (administrator=%s)', async administrator => {
  const { fetchMock } = setup([], administrator)
  const presence = await screen.findByRole('region', { name: 'Staff online presence' })
  expect((await within(presence).findByRole('listitem')).textContent).toContain(administrator ? 'Admin' : 'TestModerator')
  fireEvent.click(screen.getByRole('tab', { name: 'Promos' }))
  expect(screen.getByRole('region', { name: 'Staff online presence' })).toBe(presence)
  const request = fetchMock.mock.calls.find(([url]) => String(url) === '/api/admin/presence')
  expect(request?.[1]?.method).toBe('POST')
  expect(new Headers(request?.[1]?.headers).get('X-CSRF-TOKEN')).toBe('token')
})

it.each([
  { source: 'X', sourceCreatedAtUtc: new Date().toISOString() },
  { source: 'Bluesky', sourceCreatedAtUtc: '2026-10-15T18:42:30Z' },
  { source: 'Portal', sourceCreatedAtUtc: undefined },
  { source: 'Manual', sourceCreatedAtUtc: undefined },
] satisfies Partial<Post>[])('shows the original date and time in both staff queues for $source', async postOverrides => {
  const receivedAtUtc = '2026-11-10T21:24:00Z'
  setup([], true, { ...postOverrides, receivedAtUtc })
  await screen.findByText('A useful question')
  const timestamp = postOverrides.sourceCreatedAtUtc ?? receivedAtUtc
  const label = postOverrides.sourceCreatedAtUtc ? 'Posted' : 'Received'
  const expected = `${label} ${new Date(timestamp).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'long' })}`

  function expectTimestamp() {
    expect(screen.getByText(expected).tagName).toBe('TIME')
    expect(screen.getByText(expected).getAttribute('datetime')).toBe(timestamp)
    expect(screen.queryByText('Just now')).toBeNull()
  }

  expectTimestamp()
  fireEvent.click(screen.getByRole('button', { name: /Approve & send live/ }))
  await screen.findByText('Approved and sent live.')
  fireEvent.click(screen.getByRole('tab', { name: 'Approved' }))
  await screen.findByText('A useful question')
  expectTimestamp()
  fireEvent.click(screen.getByRole('button', { name: 'List' }))
  expectTimestamp()
})

it('explains that Bluesky uses the credential-free Jetstream stream', async () => {
  setup([{
    provider: 'Bluesky', enabled: false, configured: true, ready: false, state: 'disabled',
    summary: 'Uses the public Jetstream stream; no account or API credentials are required.',
    setupUrl: 'https://bsky.network/docs/jetstream/', requiredSettings: [],
  }])
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: /social connections/i }))
  expect(await screen.findByText(/Live public stream; matching posts are filtered locally/)).toBeTruthy()
  expect(screen.queryByText(/Search interval/)).toBeNull()
})

it('removes CSV export from both queue views without removing admin manual entry or refresh', async () => {
  const { fetchMock } = setup()
  await screen.findByText('A useful question')
  expect(screen.queryByRole('link', { name: /Export CSV/ })).toBeNull()
  expect(screen.getByRole('button', { name: /Add manually/ })).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Refresh posts' })).toBeTruthy()
  fireEvent.click(screen.getByRole('tab', { name: 'Approved' }))
  expect(screen.queryByRole('link', { name: /Export CSV/ })).toBeNull()
  expect(screen.getByRole('button', { name: 'Refresh posts' })).toBeTruthy()
  fireEvent.click(screen.getByRole('tab', { name: 'Event report' }))
  expect(screen.getByRole('button', { name: 'Generate report' })).toBeTruthy()
  expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/analytics'))).toBe(false)
})

it('never mounts the report or requests its data for moderators', async () => {
  const { fetchMock } = setup([], false)
  await screen.findByText('A useful question')
  expect(screen.queryByRole('tab', { name: 'Event report' })).toBeNull()
  expect(screen.queryByRole('region', { name: 'Event report', hidden: true })).toBeNull()
  expect(screen.queryByRole('button', { name: 'Generate report', hidden: true })).toBeNull()
  expect(fetchMock.mock.calls.some(([url]) => String(url).includes('/analytics'))).toBe(false)
})

it.each([true, false])('lets staff create, edit and delete promos with preview, versions and CSRF (administrator=%s)', async administrator => {
  const { requests, fetchMock } = setup([], administrator)
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: 'Promos' }))
  await screen.findByText('No promos yet. Create one above to get started.')
  fireEvent.change(screen.getByRole('textbox', { name: 'Title' }), { target: { value: 'Developer survey' } })
  fireEvent.change(screen.getByRole('textbox', { name: 'Description' }), { target: { value: 'Help shape .NET' } })
  fireEvent.change(screen.getByRole('textbox', { name: 'URL (optional)' }), { target: { value: 'https://example.test/survey' } })
  expect(within(screen.getByRole('region', { name: 'Promo preview' })).getByRole('img', { name: 'QR code for https://example.test/survey' })).toBeTruthy()
  fireEvent.click(screen.getByRole('button', { name: 'Create promo' }))
  const saved = await screen.findByRole('region', { name: 'Saved promo: Developer survey' })
  expect(requests.at(-1)?.body).toEqual({ title: 'Developer survey', description: 'Help shape .NET', url: 'https://example.test/survey', version: 0 })
  expect((within(saved).getByRole('checkbox', { name: 'Active on wall' }) as HTMLInputElement).checked).toBe(true)
  fireEvent.click(within(saved).getByRole('checkbox', { name: 'Active on wall' }))
  await waitFor(() => expect(requests.at(-1)).toEqual({ url: '/api/admin/promos/1/active', body: { version: 1, active: false } }))
  await waitFor(() => expect((within(saved).getByRole('checkbox', { name: 'Active on wall' }) as HTMLInputElement).checked).toBe(false))
  const request = fetchMock.mock.calls.find(([url, init]) => String(url).endsWith('/promos') && init?.method === 'POST')
  expect(new Headers(request?.[1]?.headers).get('X-CSRF-TOKEN')).toBe('token')
  fireEvent.click(within(saved).getByRole('button', { name: 'Edit promo' }))
  fireEvent.change(screen.getByRole('textbox', { name: 'Title' }), { target: { value: 'Community day' } })
  fireEvent.change(screen.getByRole('textbox', { name: 'Description' }), { target: { value: 'Join us for community sessions' } })
  fireEvent.change(screen.getByRole('textbox', { name: 'URL (optional)' }), { target: { value: '' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save promo' }))
  const updated = await screen.findByRole('region', { name: 'Saved promo: Community day' })
  expect(within(updated).queryByRole('img')).toBeNull()
  expect(requests.at(-1)?.body).toEqual({ title: 'Community day', description: 'Join us for community sessions', url: null, version: 2 })
  fireEvent.click(within(updated).getByRole('button', { name: 'Delete promo' }))
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel' }))
  expect(screen.getByRole('region', { name: 'Saved promo: Community day' })).toBeTruthy()
  fireEvent.click(within(updated).getByRole('button', { name: 'Delete promo' }))
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete promo' }))
  await screen.findByText('No promos yet. Create one above to get started.')
  expect(requests.at(-1)).toEqual({ url: '/api/admin/promos/1/delete', body: { version: 3 } })
})

it('retains the promo draft on a conflict and supports refreshing saved promos', async () => {
  const { fetchMock } = setup()
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: 'Promos' }))
  await screen.findByText('No promos yet. Create one above to get started.')
  fireEvent.change(screen.getByRole('textbox', { name: 'Title' }), { target: { value: 'Draft title' } })
  fireEvent.change(screen.getByRole('textbox', { name: 'Description' }), { target: { value: 'Keep my draft' } })
  fetchMock.mockImplementationOnce(async () => new Response(JSON.stringify({ title: 'This promo changed. Refresh promos before saving again.' }), { status: 409 }))
  fireEvent.click(screen.getByRole('button', { name: 'Create promo' }))
  expect((await screen.findByRole('alert')).textContent).toContain('Refresh promos')
  expect((screen.getByRole('textbox', { name: 'Description' }) as HTMLTextAreaElement).value).toBe('Keep my draft')
  fireEvent.click(screen.getByRole('button', { name: 'Refresh promos' }))
  await waitFor(() => expect((screen.getByRole('textbox', { name: 'Description' }) as HTMLTextAreaElement).value).toBe(''))
})

it('explains Mastodon polling limits', async () => {
  setup([{
    provider: 'Mastodon', enabled: true, configured: true, ready: false, state: 'test-required',
    summary: 'Polls a hashtag timeline.', setupUrl: 'https://docs.joinmastodon.org/methods/timelines/#tag',
    requiredSettings: ['Social:Mastodon:InstanceUrl'],
  }])
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: /social connections/i }))
  expect(await screen.findByText(/Edits and deletions are not detected/)).toBeTruthy()
})

function youtubeSource(overrides: Partial<SourceInfo> = {}): SourceInfo {
  return {
    provider: 'YouTube', enabled: true, configured: true, ready: false, state: 'paused',
    summary: 'Collection is stopped. Start when the live stream begins.',
    setupUrl: 'https://developers.google.com/youtube/v3/live/docs/liveChatMessages/list',
    requiredSettings: [], collectionRunning: false, collectionAvailable: true, ...overrides,
  }
}

it('counts partial connections while retaining their warnings and updating on refresh', async () => {
  const sources: SourceInfo[] = [
    { provider: 'Twitch', enabled: false, configured: false, ready: false, state: 'disabled', summary: 'Disabled.', setupUrl: '', requiredSettings: [] },
    youtubeSource(),
    { provider: 'X', enabled: true, configured: true, ready: false, state: 'connected-partial', summary: 'Recent Search cannot detect deletions.', setupUrl: '', requiredSettings: [] },
    { provider: 'Bluesky', enabled: true, configured: true, ready: true, state: 'connected', summary: 'Connected.', setupUrl: '', requiredSettings: [] },
    { provider: 'Mastodon', enabled: true, configured: true, ready: false, state: 'connected-partial', summary: 'Polling cannot detect deletions.', setupUrl: '', requiredSettings: [] },
  ]
  const { fetchMock } = setup(sources)
  fireEvent.click(await screen.findByRole('tab', { name: 'Social connections 3/5' }))

  const xCard = screen.getByRole('heading', { name: 'X' }).closest('article')!
  expect(within(xCard).getByText('connected partial')).toBeTruthy()
  expect(within(xCard).getByText('Recent Search cannot detect deletions.')).toBeTruthy()
  expect(within(xCard).queryByText(/Not collecting/)).toBeNull()
  expect(screen.getByText('Not collecting. Configure and test before the event.')).toBeTruthy()
  expect(screen.getByText('Collection is stopped. Start it when the live stream begins.')).toBeTruthy()

  await waitFor(() => expect(fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/sources'))).toHaveLength(2))
  sources[2] = { ...sources[2], state: 'error', summary: 'Connection failed.' }
  fireEvent.click(screen.getByRole('button', { name: /Refresh status/ }))
  await screen.findByRole('tab', { name: 'Social connections 2/5' })
  expect(within(xCard).getByText('Connection failed.')).toBeTruthy()
  expect(within(xCard).getByText('Not collecting. Configure and test before the event.')).toBeTruthy()
})

it('starts and stops YouTube collection with explicit state and CSRF without exposing credentials', async () => {
  const { requests, fetchMock } = setup([youtubeSource()])
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: /social connections/i }))
  const start = await screen.findByRole('button', { name: 'Start YouTube collection' })
  expect(start.getAttribute('aria-pressed')).toBe('false')
  fireEvent.click(start)
  const stop = await screen.findByRole('button', { name: 'Stop YouTube collection' })
  expect(stop.getAttribute('aria-pressed')).toBe('true')
  expect(screen.getByRole('status').textContent).toContain('Quota pauses and polling limits still apply')
  fireEvent.click(stop)
  await screen.findByRole('button', { name: 'Start YouTube collection' })
  expect(screen.getByRole('status').textContent).toContain('collection stopped')
  expect(requests).toEqual([
    { url: '/api/admin/sources/youtube/collection', body: { running: true } },
    { url: '/api/admin/sources/youtube/collection', body: { running: false } },
  ])
  for (const [, init] of fetchMock.mock.calls.filter(([url]) => String(url).endsWith('/sources/youtube/collection'))) {
    expect(new Headers(init?.headers).get('X-CSRF-TOKEN')).toBe('token')
  }
})

it('keeps stopped state and announces a failed collection change', async () => {
  const { requests, fetchMock } = setup([youtubeSource()])
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: /social connections/i }))
  const start = await screen.findByRole('button', { name: 'Start YouTube collection' })
  fetchMock.mockImplementationOnce(async () => new Response('{"title":"Policy approval is required."}', { status: 400 }))
  fireEvent.click(start)
  expect((await screen.findByRole('alert')).textContent).toContain('Policy approval is required.')
  expect(screen.getByRole('button', { name: 'Start YouTube collection' }).getAttribute('aria-pressed')).toBe('false')
  expect(requests).toEqual([])
})

it('disables collection controls and refresh while saving to avoid duplicate writes', async () => {
  const { fetchMock } = setup([youtubeSource()])
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: /social connections/i }))
  const start = await screen.findByRole('button', { name: 'Start YouTube collection' })
  let finish: (response: Response) => void = () => { throw new Error('Pending request was not initialized.') }
  const pending = new Promise<Response>(resolve => { finish = resolve })
  fetchMock.mockImplementationOnce(() => pending)
  fireEvent.click(start)
  expect(screen.getByRole('button', { name: 'Saving...' }).hasAttribute('disabled')).toBe(true)
  expect(screen.getByRole('button', { name: /Refresh status/ }).hasAttribute('disabled')).toBe(true)
  finish(new Response(JSON.stringify([youtubeSource({ collectionRunning: true, state: 'test-required' })]),
    { status: 200, headers: { 'Content-Type': 'application/json' } }))
  await screen.findByRole('button', { name: 'Stop YouTube collection' })
})

it('requires configuration to start but permits stopping a misconfigured or quota-paused collector', async () => {
  setup([youtubeSource({ collectionAvailable: false, state: 'needs-configuration' })])
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: /social connections/i }))
  expect((await screen.findByRole('button', { name: 'Start YouTube collection' })).hasAttribute('disabled')).toBe(true)
  cleanup()
  setup([youtubeSource({ collectionRunning: true, collectionAvailable: false, state: 'budget-exhausted' })])
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: /social connections/i }))
  expect((await screen.findByRole('button', { name: 'Stop YouTube collection' })).hasAttribute('disabled')).toBe(false)
})

it('does not offer real collection controls on simulated YouTube feeds', async () => {
  setup([youtubeSource({ simulated: true, collectionRunning: null, collectionAvailable: false })])
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: /social connections/i }))
  await screen.findByText('YouTube', { selector: 'h3' })
  expect(screen.queryByRole('button', { name: /YouTube collection/ })).toBeNull()
})

it('gives a named moderator only moderation views and never requests owner settings', async () => {
  const { fetchMock, requests } = setup([], false)
  await screen.findByText('A useful question')
  expect(screen.getByText('TestModerator', { selector: 'strong' })).toBeTruthy()
  for (const name of ['Social connections', 'Post filters', 'Event report', 'Team']) expect(screen.queryByRole('tab', { name })).toBeNull()
  expect(screen.queryByRole('button', { name: /Portal/ })).toBeNull()
  expect(screen.getByRole('button', { name: /Add manually/ })).toBeTruthy()
  expect(screen.queryByRole('link', { name: /Export/ })).toBeNull()
  expect(fetchMock.mock.calls.some(([url]) => /\/(sources|settings|analytics|team)/.test(String(url)))).toBe(false)
  fireEvent.click(screen.getByRole('button', { name: /Approve & send live/ }))
  await screen.findByText('Approved and sent live.')
  expect(requests).toEqual([{ url: '/api/admin/posts/one/approve', body: { version: 0 } }])
})

it('lets a moderator add a manual post to the review queue with CSRF', async () => {
  const { requests, fetchMock } = setup([], false)
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('button', { name: /Add manually/ }))
  fireEvent.change(screen.getByRole('textbox', { name: 'Post text' }), { target: { value: 'A message from the event' } })
  fireEvent.change(screen.getByRole('textbox', { name: 'Author attribution (optional)' }), { target: { value: 'Event staff' } })
  fireEvent.change(screen.getByRole('textbox', { name: 'HTTPS source link (optional)' }), { target: { value: 'https://example.test/post' } })
  fireEvent.click(screen.getByRole('button', { name: 'Add post' }))
  await screen.findByText('Manual post added to the review queue as unverified.')
  expect(requests).toEqual([{ url: '/api/admin/manual', body: {
    text: 'A message from the event', authorName: 'Event staff', sourceUrl: 'https://example.test/post',
  } }])
  const request = fetchMock.mock.calls.find(([url]) => String(url) === '/api/admin/manual')
  expect(new Headers(request?.[1]?.headers).get('X-CSRF-TOKEN')).toBe('token')
  expect(screen.queryByRole('textbox', { name: 'Post text' })).toBeNull()
})

it('returns to named sign-in when a moderator session is revoked', async () => {
  const { fetchMock } = setup([], false)
  await screen.findByText('A useful question')
  fetchMock.mockImplementationOnce(async () => new Response('{"title":"Session revoked"}', { status: 401 }))
  fireEvent.click(screen.getByRole('button', { name: /Approve & send live/ }))
  expect(await screen.findByLabelText('Username')).toBeTruthy()
  expect(screen.queryByText('TestModerator')).toBeNull()
})

it('blocks only after confirmation and preserves the existing post', async () => {
  const { requests } = setup()
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('button', { name: 'Block user' }))
  const cancelled = screen.getByRole('dialog', { name: 'Block this user?' })
  expect(within(cancelled).getByText(/Existing posts will not change/)).toBeTruthy()
  expect(requests).toHaveLength(0)
  fireEvent.click(within(cancelled).getByRole('button', { name: 'Cancel' }))
  expect(requests).toHaveLength(0)
  fireEvent.click(screen.getByRole('button', { name: 'Block user' }))
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Block user' }))
  await screen.findByText('Account blocked')
  expect(requests).toEqual([{ url: '/api/admin/posts/one/block-author', body: { version: 0 } }])
  expect(screen.getByText('A useful question')).toBeTruthy()
  expect(screen.getByRole('button', { name: 'User blocked' }).hasAttribute('disabled')).toBe(true)
})

it('confirms both opening and pausing portal intake and cancellation performs no write', async () => {
  const { requests } = setup()
  const portal = await screen.findByRole('button', { name: 'Portal open' })
  await waitFor(() => expect(portal.hasAttribute('disabled')).toBe(false))
  fireEvent.click(portal)
  fireEvent.click(within(screen.getByRole('dialog', { name: 'Pause portal submissions?' })).getByRole('button', { name: 'Cancel' }))
  expect(requests).toHaveLength(0)
  fireEvent.click(portal)
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Pause submissions' }))
  fireEvent.click(await screen.findByRole('button', { name: 'Portal paused' }))
  fireEvent.click(within(screen.getByRole('dialog', { name: 'Open portal submissions?' })).getByRole('button', { name: 'Open submissions' }))
  await screen.findByRole('button', { name: 'Portal open' })
  expect(requests.map(request => request.body)).toEqual([{ open: false }, { open: true }])
})

it('shows approved posts in their own tab, supports grid layout and withdraws accidental approvals', async () => {
  const { requests, fetchMock } = setup()
  await screen.findByText('A useful question')
  expect(screen.getByRole('button', { name: 'Grid' }).getAttribute('aria-pressed')).toBe('true')
  expect(document.querySelector('.queue-grid')).toBeTruthy()
  fireEvent.click(screen.getByRole('button', { name: 'List' }))
  expect(document.querySelector('.queue-grid')).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: 'Grid' }))
  fireEvent.click(screen.getByRole('button', { name: /Approve & send live/ }))
  await screen.findByText('Approved and sent live.')
  fireEvent.click(screen.getByRole('tab', { name: 'Approved' }))
  expect(await screen.findByText('A useful question')).toBeTruthy()
  expect(screen.queryByLabelText('Filter by status')).toBeNull()
  expect(fetchMock.mock.calls.some(([url]) => String(url).includes('status=Live'))).toBe(true)
  fireEvent.click(screen.getByRole('button', { name: 'Withdraw' }))
  await screen.findByText('Post withdrawn from the live wall.')
  expect(screen.queryByText('A useful question')).toBeNull()
  expect(requests.at(-1)).toEqual({ url: '/api/admin/posts/one/withdraw', body: { version: 1 } })
})

it('keeps the question bot through list/grid switching and approval into the approved tab', async () => {
  const { post } = setup()
  post.source = 'Portal'
  post.canBlockAuthor = false
  await screen.findByText('A useful question')
  const url = screen.getByRole('img', { name: '.NET bot question icon' }).getAttribute('src')
  fireEvent.click(screen.getByRole('button', { name: 'Grid' }))
  expect(screen.getByRole('img', { name: '.NET bot question icon' }).getAttribute('src')).toBe(url)
  fireEvent.click(screen.getByRole('button', { name: 'List' }))
  expect(screen.getByRole('img', { name: '.NET bot question icon' }).getAttribute('src')).toBe(url)
  fireEvent.click(screen.getByRole('button', { name: /Approve & send live/ }))
  await screen.findByText('Approved and sent live.')
  fireEvent.click(screen.getByRole('tab', { name: 'Approved' }))
  expect(await screen.findByRole('img', { name: '.NET bot question icon' })).toBeTruthy()
  expect(screen.getByRole('img', { name: '.NET bot question icon' }).getAttribute('src')).toBe(url)
})

it('disables account blocking when a post has no verified social identity', async () => {
  const { post } = setup()
  post.canBlockAuthor = false
  await screen.findByText('A useful question')
  expect(screen.getByRole('button', { name: 'Block user' }).hasAttribute('disabled')).toBe(true)
})

it('preserves attached photos, accessible fallbacks and post context when switching list and grid layouts', async () => {
  const { post } = setup()
  post.images = Array.from({ length: 4 }, (_, index) => ({ url: `/preview-${index}.jpg`, alt: `Attached photo ${index + 1}` }))
  post.sourceUrl = 'https://example.test/original'
  post.filterReasons = ['AllCaps']
  const gallery = await screen.findByRole('group', { name: '4 attached images' })
  expect(screen.getByRole('button', { name: 'Grid' }).getAttribute('aria-pressed')).toBe('true')
  expect(within(gallery).getAllByRole('img')).toHaveLength(4)
  expect(within(gallery).getByAltText('Attached photo 1').getAttribute('referrerpolicy')).toBe('no-referrer')
  fireEvent.error(within(gallery).getByAltText('Attached photo 4'))
  expect(within(gallery).getByText('Image unavailable')).toBeTruthy()
  for (const layout of ['Grid', 'List']) {
    fireEvent.click(screen.getByRole('button', { name: layout }))
    expect(screen.getByRole('button', { name: layout }).getAttribute('aria-pressed')).toBe('true')
    const switched = screen.getByRole('group', { name: '4 attached images' })
    expect(within(switched).getAllByRole('img')).toHaveLength(3)
    expect(within(switched).getByText('Image unavailable')).toBeTruthy()
    expect(screen.getByRole('list', { name: 'Post filter matches' })).toBeTruthy()
    expect(screen.getByRole('link', { name: /Original post/ }).getAttribute('href')).toBe(post.sourceUrl)
    expect(screen.getByRole('button', { name: /Approve & send live/ })).toBeTruthy()
    expect(screen.getByText('A useful question')).toBeTruthy()
  }
})

it.each(['List', 'Grid'])('previews the selected attachment in %s review and approved views without moderation writes', async layout => {
  const { post, requests } = setup()
  post.images = [{ url: '/first.jpg', alt: 'First photo' }, { url: '/second.jpg', alt: 'Second photo' }]
  await screen.findByRole('group', { name: '2 attached images' })
  fireEvent.click(screen.getByRole('button', { name: layout }))
  for (const view of ['queue', 'approved']) {
    if (view === 'approved') {
      post.status = 'Live'
      fireEvent.click(screen.getByRole('tab', { name: 'Approved' }))
    }
    const trigger = await screen.findByRole('button', { name: 'Preview image 2 of 2 by Viewer' })
    for (const closeWith of ['close', 'escape', 'backdrop']) {
      fireEvent.click(trigger)
      const dialog = screen.getByRole('dialog', { name: 'Image preview' })
      expect(dialog.tagName).toBe('DIALOG')
      expect(within(dialog).getAllByRole('img')).toHaveLength(1)
      const image = within(dialog).getByAltText('Second photo')
      expect(image.getAttribute('src')).toBe('/second.jpg')
      expect(image.getAttribute('referrerpolicy')).toBe('no-referrer')
      expect(within(dialog).getByText('Second photo')).toBeTruthy()
      fireEvent.click(image)
      expect(screen.getByRole('dialog', { name: 'Image preview' })).toBe(dialog)
      if (closeWith === 'close') fireEvent.click(within(dialog).getByRole('button', { name: 'Close image preview' }))
      else if (closeWith === 'escape') fireEvent(dialog, new Event('cancel', { cancelable: true }))
      else fireEvent.click(dialog)
      expect(screen.queryByRole('dialog', { name: 'Image preview' })).toBeNull()
      expect(requests).toHaveLength(0)
    }
  }
})

it.each(['List', 'Grid'])('retains the original shortened links for moderation in %s layout', async layout => {
  const { post, requests } = setup()
  post.text = 'Can Aspire deploy to Kubernetes? https://t.co/PB6cDv0XLE See https://learn.microsoft.com/dotnet.'
  await screen.findByText(post.text)
  fireEvent.click(screen.getByRole('button', { name: layout }))
  expect(screen.getByText(post.text)).toBeTruthy()
  expect(requests).toHaveLength(0)
})

it('keeps image previews closable on load failure and supplies a missing-caption fallback', async () => {
  const { post, requests } = setup()
  post.images = [{ url: '/photo.jpg', alt: null }]
  const trigger = await screen.findByRole('button', { name: 'Preview image 1 of 1 by Viewer' })
  fireEvent.click(trigger)
  const dialog = screen.getByRole('dialog', { name: 'Image preview' })
  expect(within(dialog).getByText('Image attached to a post by Viewer')).toBeTruthy()
  fireEvent.error(within(dialog).getByAltText('Image attached to this post'))
  expect(within(dialog).getByText('Image unavailable')).toBeTruthy()
  fireEvent.click(within(dialog).getByRole('button', { name: 'Close image preview' }))
  expect(screen.queryByRole('dialog')).toBeNull()
  expect(screen.getByRole('button', { name: /Approve & send live/ })).toBeTruthy()
  expect(requests).toHaveLength(0)
})

it('saves every filter control and custom list with the settings version and CSRF token', async () => {
  const { requests, fetchMock } = setup()
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: 'Post filters' }))
  const toggles = screen.getAllByRole('checkbox')
  expect(toggles).toHaveLength(8)
  expect((screen.getByRole('checkbox', { name: /Exact duplicate text/ }) as HTMLInputElement).checked).toBe(true)
  for (const toggle of toggles) {
    expect((toggle as HTMLInputElement).checked).toBe(true)
    fireEvent.click(toggle)
  }
  fireEvent.change(screen.getByRole('textbox', { name: /^Blocked terms/ }), { target: { value: 'spoiler\npromotion' } })
  fireEvent.change(screen.getByRole('textbox', { name: /^Allowed profanity terms/ }), { target: { value: 'bastard' } })
  fireEvent.change(screen.getByRole('textbox', { name: /^Location-only phrases/ }), { target: { value: 'New York' } })
  fireEvent.click(screen.getByRole('button', { name: 'Save post filters' }))
  await screen.findByRole('status')
  expect(requests.at(-1)).toEqual({
    url: '/api/admin/settings/filters',
    body: {
      version: 0,
      postFilters: {
        profanity: false, allCaps: false, singleWord: false, emojiOnly: false, locationOnly: false,
        noSubstantiveText: false, repeatedCharacters: false, duplicateText: false, blockedTerms: ['spoiler', 'promotion'],
        allowedTerms: ['bastard'], locationPhrases: ['New York'],
      },
    },
  })
  const request = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/settings/filters'))
  expect(new Headers(request?.[1]?.headers).get('X-CSRF-TOKEN')).toBe('token')
  fireEvent.click(screen.getByRole('button', { name: 'Save post filters' }))
  await waitFor(() => expect(requests.at(-1)?.body.version).toBe(1))
})

it('preserves unsaved filter edits across tabs and explicit server conflicts', async () => {
  const { fetchMock } = setup()
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: 'Post filters' }))
  fireEvent.change(screen.getByRole('textbox', { name: /^Blocked terms/ }), { target: { value: 'spoiler' } })
  fireEvent.click(screen.getByRole('tab', { name: 'Review queue' }))
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('tab', { name: 'Post filters' }))
  expect((screen.getByRole('textbox', { name: /^Blocked terms/ }) as HTMLTextAreaElement).value).toBe('spoiler')
  fetchMock.mockImplementationOnce(async () => new Response(JSON.stringify({
    title: 'Post filters changed in another staff session. Reload settings before saving again.',
  }), { status: 409, headers: { 'Content-Type': 'application/json' } }))
  fireEvent.click(screen.getByRole('button', { name: 'Save post filters' }))
  expect(await screen.findByRole('alert')).toHaveProperty('textContent', expect.stringContaining('Reload settings'))
  expect((screen.getByRole('textbox', { name: /^Blocked terms/ }) as HTMLTextAreaElement).value).toBe('spoiler')
  fireEvent.click(screen.getByRole('button', { name: 'Reload saved settings (discard edits)' }))
  await screen.findByText('Saved post filters reloaded; local edits discarded.')
  expect((screen.getByRole('textbox', { name: /^Blocked terms/ }) as HTMLTextAreaElement).value).toBe('')
})

it('shows rejected reasons and lets staff approve false positives without a misleading rejection label', async () => {
  const { post } = setup()
  post.status = 'Rejected'
  post.filterReasons = ['SingleWord', 'LocationOnly', 'DuplicateText']
  await screen.findByRole('combobox', { name: 'Filter by status' })
  fireEvent.change(screen.getByRole('combobox', { name: 'Filter by status' }), { target: { value: 'Rejected' } })
  await screen.findByText('Automatically rejected')
  expect(screen.getByRole('list', { name: 'Post filter matches' }).textContent).toContain('Location/check-in only')
  expect(screen.getByRole('list', { name: 'Post filter matches' }).textContent).toContain('Exact duplicate text')
  fireEvent.click(screen.getByRole('button', { name: /Approve & send live/ }))
  await screen.findByText('Approved and sent live.')
  fireEvent.click(screen.getByRole('tab', { name: 'Approved' }))
  await screen.findByText('Filter matches (staff decision retained)')
  expect(screen.queryByText('Automatically rejected')).toBeNull()
})

it('never shows pending posts while the Approved tab is loading', async () => {
  const { fetchMock } = setup()
  await screen.findByText('A useful question')
  let release: ((response: Response) => void) | undefined
  fetchMock.mockImplementationOnce(() => new Promise<Response>(resolve => { release = resolve }))
  fireEvent.click(screen.getByRole('tab', { name: 'Approved' }))
  expect(screen.queryByText('A useful question')).toBeNull()
  expect(screen.getByText('Loading posts...')).toBeTruthy()
  release?.(new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }))
  await screen.findByText('Nothing waiting in this view.')
  expect(screen.queryByText('A useful question')).toBeNull()
})

it('shows an explicit error when a confirmed write fails', async () => {
  const { fetchMock } = setup()
  await screen.findByText('A useful question')
  fireEvent.click(screen.getByRole('button', { name: 'Block user' }))
  fetchMock.mockImplementationOnce(async () => new Response(JSON.stringify({ title: 'This item changed. Reload it.' }), { status: 409 }))
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Block user' }))
  expect(await screen.findByText('This item changed. Reload it.')).toBeTruthy()
  expect(screen.getByRole('button', { name: 'Block user' }).hasAttribute('disabled')).toBe(false)
})
