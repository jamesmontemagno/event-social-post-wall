import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import type { EventInfo, Post, Promo } from './shared'
import { setSkin } from './skin'
import { firstFlybyDelayMs, flybyDurationMs } from './netbotFlight'

function mockResponse(data: unknown) {
  const response = typeof data === 'object' && data !== null && 'revision' in data ? { promos: [], ...data } : data
  return new Response(JSON.stringify(response), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

const post: Post = {
  id: 'one', source: 'Bluesky', text: 'How can I get started with .NET?',
  authorName: 'Community member', receivedAtUtc: new Date().toISOString(),
  sourceUrl: 'https://bsky.app/profile/example.test/post/one',
}

function setupWall(event: Partial<EventInfo> = {}, posts: Post[] = [post]) {
  const state = {
    posts,
    revision: 1,
    changes: { revision: 1, newCount: 0, liveKnownIds: posts.map(post => post.id) },
    disconnected: false,
    promos: [] as Promo[],
  }
  const fetchMock = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => {
    const url = String(input)
    if (url.includes('/api/public/event')) {
      return mockResponse({ slug: 'communityevent', name: 'Community Event', submissionsOpen: true, submissionSiteUrl: 'https://questions.example.test/', ...event })
    }
    if (url.includes('/api/public/wall/changes')) {
      if (state.disconnected) throw new Error('Connection unavailable')
      return mockResponse({ ...state.changes, promos: state.promos })
    }
    return mockResponse({ revision: state.revision, newCount: 0, items: state.posts, promos: state.promos })
  })
  vi.stubGlobal('fetch', fetchMock)
  return { state, fetchMock }
}

describe('event wall', () => {
  let fullscreenElement: Element | null = null
  beforeEach(() => {
    fullscreenElement = null
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } })
    Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } })
    Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => fullscreenElement })
    Object.defineProperty(HTMLElement.prototype, 'requestFullscreen', { configurable: true, value: vi.fn(async () => {
      fullscreenElement = screen.getByRole('main')
      document.dispatchEvent(new Event('fullscreenchange'))
    }) })
    Object.defineProperty(document, 'exitFullscreen', { configurable: true, value: vi.fn(async () => {
      fullscreenElement = null
      document.dispatchEvent(new Event('fullscreenchange'))
    }) })
  })

  afterEach(() => {
    cleanup()
    vi.useRealTimers()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    localStorage.clear()
    Reflect.deleteProperty(HTMLElement.prototype, 'requestFullscreen')
    Reflect.deleteProperty(document, 'fullscreenElement')
    Reflect.deleteProperty(document, 'exitFullscreen')
    window.history.replaceState({}, '', '/')
  })

  it('shows only the public event and live wall when the API is available', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/api/public/event')) {
        return mockResponse({ slug: 'communityevent', name: 'Community Event', submissionsOpen: true })
      }
      if (url.includes('/api/public/wall/changes')) {
        return mockResponse({ revision: 1, newCount: 0, liveKnownIds: [] })
      }
      return mockResponse({ revision: 1, newCount: 0, items: [] })
    }))

    render(<App />)

    expect(await screen.findByRole('heading', { name: /the room is/i })).toBeTruthy()
    expect(await screen.findByText('Approved questions and moments from Community Event.')).toBeTruthy()
    expect(screen.getByText('Room for a great question.')).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Staff' })).toBeNull()
    for (const name of ['ASP.NET Core', 'Blazor Server', 'React', 'TypeScript', 'Aspire', 'GitHub Copilot']) {
      expect(screen.getByRole('link', { name }).getAttribute('href')).toMatch(/^https:\/\//)
    }
    expect(screen.getByRole('link', { name: 'Third-party licenses and notices' }).getAttribute('href')).toBe('/THIRD-PARTY-NOTICES.txt')
  })

  it.each([
    { mode: 'normal', pinned: false },
    { mode: 'normal', pinned: true },
    { mode: 'immersive', pinned: false },
    { mode: 'immersive', pinned: true },
  ])('flies on opening a post without restarting across dialog changes ($mode, pinned: $pinned)', async ({ mode, pinned }) => {
    vi.useFakeTimers()
    setSkin('aurora')
    setupWall()
    if (pinned) localStorage.setItem('eventwall:pins:communityevent', JSON.stringify([post.id]))
    await act(async () => { render(<App />) })
    if (mode === 'immersive') fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    const open = () => fireEvent.click(screen.getByRole('button', { name: 'Expand post by Community member' }))
    const close = () => fireEvent.click(screen.getByRole('button', { name: 'Close expanded post' }))

    expect(screen.queryByTestId('netbot-flyby')).toBeNull()
    open()
    const initial = within(screen.getByRole('dialog')).getByTestId('netbot-flyby')
    expect(initial.style.animationDelay).toBe('0ms')
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
    close()
    expect(screen.getAllByTestId('netbot-flyby')).toHaveLength(1)
    expect(screen.getByTestId('netbot-flyby').style.animationDelay).toBe('-5000ms')
    open()
    expect(within(screen.getByRole('dialog')).getByTestId('netbot-flyby').style.animationDelay).toBe('-5000ms')
    await act(async () => { await vi.advanceTimersByTimeAsync(flybyDurationMs - 5_000) })
    expect(screen.queryByTestId('netbot-flyby')).toBeNull()
    close()
    expect(screen.queryByTestId('netbot-flyby')).toBeNull()
    open()
    expect(within(screen.getByRole('dialog')).getByTestId('netbot-flyby').style.animationDelay).toBe('0ms')
  })

  it('does not restart a click-triggered flight at an automatic tick or postpone the next tick', async () => {
    vi.useFakeTimers()
    vi.spyOn(Math, 'random').mockReturnValue(0)
    setSkin('aurora')
    setupWall()
    await act(async () => { render(<App />) })
    await act(async () => { await vi.advanceTimersByTimeAsync(firstFlybyDelayMs - 5_000) })
    fireEvent.click(screen.getByRole('button', { name: 'Expand post by Community member' }))
    const flight = screen.getByTestId('netbot-flyby')
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000) })
    expect(screen.getByTestId('netbot-flyby')).toBe(flight)
    await act(async () => { await vi.advanceTimersByTimeAsync(flybyDurationMs - 5_000) })
    expect(screen.queryByTestId('netbot-flyby')).toBeNull()
    await act(async () => { await vi.advanceTimersByTimeAsync(53_000) })
    expect(within(screen.getByRole('dialog')).getByTestId('netbot-flyby')).toBeTruthy()
  })

  it.each(['classic', 'reduced motion', 'hidden tab'])('does not launch on post opening with %s', async guard => {
    vi.useFakeTimers()
    setSkin(guard === 'classic' ? 'classic' : 'aurora')
    if (guard === 'reduced motion') {
      vi.stubGlobal('matchMedia', () => ({ matches: true, addEventListener() {}, removeEventListener() {} }))
    }
    if (guard === 'hidden tab') vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
    setupWall()
    await act(async () => { render(<App />) })
    fireEvent.click(screen.getByRole('button', { name: 'Expand post by Community member' }))
    expect(screen.getByRole('dialog')).toBeTruthy()
    expect(screen.queryByTestId('netbot-flyby')).toBeNull()
    await act(async () => { await vi.advanceTimersByTimeAsync(firstFlybyDelayMs) })
    expect(screen.queryByTestId('netbot-flyby')).toBeNull()
    act(() => { setSkin('aurora') })
  })

  it('keeps restored pins in approval order when returned to the feed', async () => {
    const recent = { ...post, id: 'recent', authorName: 'Recent', approvedAtUtc: '2026-11-02T12:00:00Z', publishedRevision: 4 }
    const older = { ...post, id: 'older', authorName: 'Older', approvedAtUtc: '2026-11-02T11:00:00Z', publishedRevision: 50 }
    const newer = { ...post, id: 'newer', authorName: 'Newer', approvedAtUtc: '2026-11-02T11:00:00.0000001Z', publishedRevision: 1 }
    const unknown = { ...post, id: 'unknown', authorName: 'Unknown', approvedAtUtc: null, publishedRevision: 100 }
    const saved = [older, newer, unknown]
    localStorage.setItem('eventwall:pins:communityevent', JSON.stringify(saved.map(item => item.id)))
    const { state, fetchMock } = setupWall({}, [recent])
    state.revision = 100
    const defaultFetch = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async input => {
      const url = String(input)
      if (url.startsWith('/api/public/wall/items/')) {
        const found = saved.find(item => url.endsWith(`/${item.id}`))
        return found ? mockResponse(found) : new Response(null, { status: 404 })
      }
      return defaultFetch(input)
    })

    render(<App />)
    await screen.findByRole('region', { name: 'Pinned posts' })
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Sort by newest approved' })) })
    for (const item of saved) {
      const pinned = screen.getByRole('region', { name: 'Pinned posts' })
      const card = within(pinned).getByRole('button', { name: `Expand post by ${item.authorName}` }).closest('article')!
      fireEvent.click(within(card).getByRole('button', { name: 'Unpin post' }))
    }
    const feed = screen.getByRole('region', { name: 'Approved live posts' })
    expect(within(feed).getAllByRole('button', { name: /^Expand post by / }).map(button => button.getAttribute('aria-label')))
      .toEqual(['Expand post by Recent', 'Expand post by Newer', 'Expand post by Older', 'Expand post by Unknown'])
  })

  it('shows the age of the selected date on cards and expanded posts', async () => {
    const now = Date.now()
    const sourceCreatedAtUtc = new Date(now - 2 * 60 * 60 * 1000).toISOString()
    const approvedAtUtc = new Date(now - 90 * 1000).toISOString()
    setupWall({}, [
      { ...post, sourceCreatedAtUtc, approvedAtUtc },
      { ...post, id: 'legacy', authorName: 'Legacy', sourceCreatedAtUtc },
    ])
    render(<App />)
    let approved = await screen.findByRole('button', { name: 'Expand post by Community member' })
    expect(within(approved).getByText('2h ago').getAttribute('title')).toBe('Posted 2h ago')
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Sort by newest approved' })) })
    approved = screen.getByRole('button', { name: 'Expand post by Community member' })
    expect(within(approved).getByText('1m ago').getAttribute('title')).toBe('Approved 1m ago')
    const legacy = screen.getByRole('button', { name: 'Expand post by Legacy' })
    expect(within(legacy).getByText('2h ago').getAttribute('title')).toBe('Posted 2h ago')
    fireEvent.click(approved)
    expect(within(screen.getByRole('dialog')).getByText('1m ago').getAttribute('title')).toBe('Approved 1m ago')
  })

  it.each(['normal', 'immersive'])('defaults to post date and can sort by approval in %s mode', async mode => {
    const older = { ...post, id: 'older', authorName: 'Older post', sourceCreatedAtUtc: '2026-11-01T10:00:00Z', approvedAtUtc: '2026-11-04T10:00:00Z', publishedRevision: 3 }
    const recent = { ...post, id: 'recent', authorName: 'Recent post', sourceCreatedAtUtc: '2026-11-03T10:00:00Z', approvedAtUtc: '2026-11-03T10:00:00Z', publishedRevision: 2 }
    const portal = { ...post, id: 'portal', authorName: 'Portal post', source: 'Portal' as const, receivedAtUtc: '2026-11-02T10:00:00Z', approvedAtUtc: '2026-11-02T10:00:00Z', publishedRevision: 1 }
    const { fetchMock } = setupWall({}, [older, recent, portal])
    render(<App />)
    const feed = await screen.findByRole('region', { name: 'Approved live posts' })
    await within(feed).findByRole('button', { name: 'Expand post by Recent post' })
    if (mode === 'immersive') fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    const sort = screen.getByRole('button', { name: 'Sort by newest approved', pressed: false })
    const order = () => within(feed).getAllByRole('button', { name: /^Expand post by / }).map(button => button.getAttribute('aria-label'))
    expect(screen.queryByRole('combobox', { name: 'Sort by' })).toBeNull()
    expect(sort.getAttribute('aria-description')).toBe('Currently sorted by newest post date.')
    expect(sort.getAttribute('title')).toContain('Click to sort by newest approved')
    expect(order()).toEqual(['Expand post by Recent post', 'Expand post by Portal post', 'Expand post by Older post'])

    await act(async () => { fireEvent.click(sort) })
    expect(screen.getByRole('button', { name: 'Sort by newest approved', pressed: true })).toBe(sort)
    expect(sort.getAttribute('aria-description')).toBe('Currently sorted by newest approval.')
    expect(sort.getAttribute('title')).toContain('Click to sort by newest post date')
    expect(order()).toEqual(['Expand post by Older post', 'Expand post by Recent post', 'Expand post by Portal post'])
    expect(fetchMock.mock.calls.some(([input]) => {
      const url = new URL(String(input), 'http://localhost')
      return url.pathname === '/api/public/wall' && url.searchParams.get('sort') === 'approved' && url.searchParams.get('throughRevision') === '1'
    })).toBe(true)
    await act(async () => { fireEvent.click(sort) })
    expect(screen.getByRole('button', { name: 'Sort by newest approved', pressed: false })).toBe(sort)
    expect(order()).toEqual(['Expand post by Recent post', 'Expand post by Portal post', 'Expand post by Older post'])
  })

  it.each(['normal', 'immersive'])('keeps waiting posts hidden when sorting and highlights revealed posts in %s mode', async mode => {
    const existing = { ...post, sourceCreatedAtUtc: '2026-11-03T10:00:00Z', approvedAtUtc: '2026-11-03T10:01:00Z', publishedRevision: 1 }
    const added = { ...post, id: 'two', authorName: 'New attendee', sourceCreatedAtUtc: '2026-11-01T10:00:00Z', approvedAtUtc: '2026-11-04T10:00:00Z', publishedRevision: 2 }
    const { state, fetchMock } = setupWall({}, [existing])
    const defaultFetch = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost')
      const through = url.searchParams.get('throughRevision')
      if (url.pathname === '/api/public/wall' && through !== null) {
        return mockResponse({ revision: state.revision, newCount: 0,
          items: state.posts.filter(item => (item.publishedRevision ?? 0) <= Number(through)) })
      }
      return defaultFetch(input, init)
    })
    vi.useFakeTimers()
    await act(async () => { render(<App />) })
    if (mode === 'immersive') fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    state.posts = [existing, added]
    state.revision = 2
    state.changes = { revision: 2, newCount: 1, liveKnownIds: [existing.id] }
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(screen.queryByRole('button', { name: 'Expand post by New attendee' })).toBeNull()
    expect(screen.getByText('1 new post ready')).toBeTruthy()
    const changesCall = fetchMock.mock.calls.find(([input]) => String(input) === '/api/public/wall/changes')
    expect(changesCall?.[1]?.method).toBe('POST')
    expect(JSON.parse(String(changesCall?.[1]?.body))).toEqual({ sinceRevision: 1, knownIds: [existing.id] })

    const sort = screen.getByRole('button', { name: 'Sort by newest approved' })
    await act(async () => { fireEvent.click(sort) })
    expect(screen.queryByRole('button', { name: 'Expand post by New attendee' })).toBeNull()
    expect(screen.getByText('1 new post ready')).toBeTruthy()
    await act(async () => {
      fireEvent.click(mode === 'immersive'
        ? screen.getByRole('button', { name: 'Refresh live posts' })
        : screen.getByRole('button', { name: /Show new posts/ }))
    })
    const newCard = screen.getByRole('button', { name: 'Expand post by New attendee' })
    expect(newCard.closest('article')?.classList.contains('is-new')).toBe(true)
    expect(newCard.getAttribute('aria-description')).toContain('Newly approved')
    expect(screen.getByRole('button', { name: 'Expand post by Community member' }).closest('article')?.classList.contains('is-new')).toBe(false)
    await act(async () => { fireEvent.click(sort) })
    expect(screen.getByRole('button', { name: 'Expand post by New attendee' }).closest('article')?.classList.contains('is-new')).toBe(true)
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Refresh live posts' })) })
    expect(screen.getByRole('button', { name: 'Expand post by New attendee' }).closest('article')?.classList.contains('is-new')).toBe(false)
  })

  it('does not let a validation during refresh clear the new highlights', async () => {
    const existing = { ...post, publishedRevision: 1 }
    const added = { ...post, id: 'two', authorName: 'New attendee', publishedRevision: 2 }
    const { state, fetchMock } = setupWall({}, [existing])
    const defaultFetch = fetchMock.getMockImplementation()!
    let finishRefresh: ((response: Response) => void) | undefined
    let finishValidation: ((response: Response) => void) | undefined
    let delayValidation = false
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost')
      if (url.pathname === '/api/public/wall' && url.searchParams.has('afterRevision')) {
        return new Promise<Response>(resolve => { finishRefresh = resolve })
      }
      if (url.pathname === '/api/public/wall/changes' && delayValidation) {
        return new Promise<Response>(resolve => { finishValidation = resolve })
      }
      return defaultFetch(input, init)
    })
    vi.useFakeTimers()
    await act(async () => { render(<App />) })
    state.posts = [existing, added]
    state.revision = 2
    state.changes = { revision: 2, newCount: 1, liveKnownIds: [existing.id] }
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /Show new posts/ })) })
    expect(finishRefresh).toBeTypeOf('function')

    delayValidation = true
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(finishValidation).toBeTypeOf('function')
    await act(async () => { finishRefresh!(mockResponse({ revision: 2, newCount: 1, items: state.posts })) })
    await act(async () => { finishValidation!(mockResponse({ revision: 2, newCount: 1, liveKnownIds: [existing.id] })) })
    expect(screen.getByRole('button', { name: 'Expand post by New attendee' }).closest('article')?.classList.contains('is-new')).toBe(true)
    expect(screen.queryByRole('button', { name: /Show new posts/ })).toBeNull()
  })

  it.each([true, false])('only retains a highlighted post outside the sorted snapshot when it is still live: %s', async stillLive => {
    const existing = { ...post, publishedRevision: 1 }
    const added = { ...post, id: 'two', authorName: 'New attendee', publishedRevision: 2 }
    const { state, fetchMock } = setupWall({}, [existing])
    const defaultFetch = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost')
      if (url.pathname === '/api/public/wall' && url.searchParams.has('throughRevision')) {
        return mockResponse({ revision: state.revision, newCount: 0, items: [existing] })
      }
      return defaultFetch(input, init)
    })
    vi.useFakeTimers()
    await act(async () => { render(<App />) })
    state.posts = [existing, added]
    state.revision = 2
    state.changes = { revision: 2, newCount: 1, liveKnownIds: [existing.id] }
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /Show new posts/ })) })
    state.changes = { revision: 2, newCount: 0, liveKnownIds: stillLive ? [added.id] : [] }
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Sort by newest approved' })) })

    const retained = screen.queryByRole('button', { name: 'Expand post by New attendee' })
    expect(Boolean(retained)).toBe(stillLive)
    if (retained) expect(retained.closest('article')?.classList.contains('is-new')).toBe(true)
    const validation = [...fetchMock.mock.calls].reverse().find(([input, init]) =>
      String(input) === '/api/public/wall/changes' && JSON.parse(String(init?.body)).knownThroughRevision === 2)
    expect(JSON.parse(String(validation?.[1]?.body)).knownIds).toEqual([added.id])
  })

  it('does not reveal a reapproved saved pin when changing sort before refresh', async () => {
    const original = { ...post, publishedRevision: 1 }
    const reapproved = { ...original, publishedRevision: 2, text: 'Updated and reapproved' }
    localStorage.setItem('eventwall:pins:communityevent', JSON.stringify([original.id]))
    const { state, fetchMock } = setupWall({}, [original])
    const defaultFetch = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (input, init) => {
      const url = new URL(String(input), 'http://localhost')
      if (url.pathname === `/api/public/wall/items/${original.id}`) return mockResponse(reapproved)
      if (url.pathname === '/api/public/wall' && url.searchParams.has('throughRevision')) {
        return mockResponse({ revision: 2, newCount: 0, items: [] })
      }
      return defaultFetch(input, init)
    })
    render(<App />)
    await screen.findByRole('region', { name: 'Pinned posts' })
    state.revision = 2
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Sort by newest approved' })) })
    expect(screen.queryByRole('button', { name: 'Expand post by Community member' })).toBeNull()
  })

  it('does not expose staff UI through a public query string', async () => {
    window.history.replaceState({}, '', '/?view=staff')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) =>
      String(input).includes('/api/public/event')
        ? mockResponse({ slug: 'communityevent', name: 'Community Event', submissionsOpen: true })
        : mockResponse({ revision: 0, newCount: 0, items: [] })))
    render(<App />)
    expect(await screen.findByRole('heading', { name: /the room is/i })).toBeTruthy()
    expect(screen.queryByLabelText('Shared staff passcode')).toBeNull()
    expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).includes('/api/admin'))).toBe(false)
  })

  it('links to the standalone question site and never embeds its form', async () => {
    const { fetchMock } = setupWall()
    render(<App />)
    const links = await screen.findAllByRole('link', { name: /submit a question/i })
    for (const link of links) expect(link.getAttribute('href')).toBe('https://questions.example.test/')
    expect(screen.queryByRole('textbox')).toBeNull()
    expect(fetchMock.mock.calls.every(([url]) => !String(url).includes('/submissions'))).toBe(true)
    expect(screen.getByRole('contentinfo', { name: 'Technology and acknowledgements' })).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Third-party licenses and notices' })).toBeTruthy()
  })

  it.each(['normal', 'immersive'])('displays and links the configured question domain in %s mode', async mode => {
    setupWall()
    render(<App />)
    const prompt = await screen.findByRole('complementary', { name: 'Join the live conversation' })
    if (mode === 'immersive') fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    const link = await within(prompt).findByRole('link', { name: 'Submit a question questions.example.test' })
    expect(link.getAttribute('href')).toBe('https://questions.example.test/')
    expect(within(link).getByText('questions.example.test')).toBeTruthy()
    expect(screen.queryByRole('textbox')).toBeNull()
    expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(mode === 'immersive')
  })

  it.each(['pending', 'failed'])('never invents question links when event metadata is %s', async state => {
    const { fetchMock } = setupWall()
    const defaultFetch = fetchMock.getMockImplementation()!
    fetchMock.mockImplementation(async (input, init) => {
      if (String(input) === '/api/public/event') {
        if (state === 'failed') throw new Error('Event metadata unavailable')
        return new Promise<Response>(() => {})
      }
      return defaultFetch(input, init)
    })
    render(<App />)
    expect(screen.queryAllByRole('link', { name: /submit a question/i })).toHaveLength(0)
    await screen.findByRole('button', { name: 'Expand post by Community member' })
    if (state === 'failed') await screen.findByRole('alert')
    expect(screen.queryAllByRole('link', { name: /submit a question/i })).toHaveLength(0)
    expect(fetchMock.mock.calls.every(([url]) => String(url).startsWith('/api/public/'))).toBe(true)
  })

  it('does not mount or poll the wall while resolving a legacy submit link', async () => {
    window.history.replaceState({}, '', '/?view=submit')
    const { fetchMock } = setupWall({ submissionSiteUrl: undefined })
    render(<App />)
    await screen.findByRole('alert')
    expect(screen.queryByRole('heading', { name: /the room is/i })).toBeNull()
    expect(fetchMock.mock.calls.every(([url]) => String(url).endsWith('/api/public/event'))).toBe(true)
  })

  it.each([['normal', undefined], ['normal', '#my_event'], ['immersive', undefined], ['immersive', '#my_event']])('shows each platform tag in %s mode with event hashtag %s', async (mode, questionHashtag) => {
    setupWall({ questionHashtag })
    render(<App />)
    const prompt = await screen.findByRole('complementary', { name: 'Join the live conversation' })
    if (mode === 'immersive') fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    const chat = within(prompt).getByRole('group', { name: 'Twitch and YouTube questions' })
    const social = within(prompt).getByRole('group', { name: 'X, Bluesky, and Mastodon questions' })
    expect(within(chat).getByText('#question')).toBeTruthy()
    expect(within(chat).queryByText(questionHashtag ?? '#communityevent')).toBeNull()
    await waitFor(() => expect(within(social).getByText(questionHashtag ?? '#communityevent')).toBeTruthy())
    expect(within(social).getByText('Community Event')).toBeTruthy()
    expect(within(social).queryByText('#question')).toBeNull()
    expect(within(prompt).getByText(/ask your question live/i)).toBeTruthy()
    for (const name of ['X', 'Twitch', 'Bluesky', 'Mastodon', 'YouTube']) {
      expect(within(name === 'Twitch' || name === 'YouTube' ? chat : social).getByRole('img', { name })).toBeTruthy()
    }
    expect(within(prompt).queryByText(/Twitch & YouTube|X, Bluesky|X & Bluesky/)).toBeNull()
  })

  it('shows popup unpinning while preserving feed pinning and all close paths', async () => {
    setupWall()
    render(<App />)
    await screen.findByRole('button', { name: 'Expand post by Community member' })
    fireEvent.click(screen.getByRole('button', { name: 'Pin post' }))
    expect(screen.getByRole('region', { name: 'Pinned posts' })).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Expand post by Community member' }))
    let dialog = screen.getByRole('dialog', { name: 'Expanded post' })
    expect(dialog.tagName).toBe('DIALOG')
    expect(within(dialog).getByText(post.text)).toBeTruthy()
    expect(within(dialog).getByRole('link', { name: /open original post/i }).getAttribute('href')).toBe(post.sourceUrl)
    expect(within(dialog).queryByText(/approved for the wall/i)).toBeNull()
    expect(within(dialog).getByRole('button', { name: 'Unpin post' })).toBeTruthy()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close expanded post' }))
    expect(screen.queryByRole('dialog')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Expand post by Community member' }))
    dialog = screen.getByRole('dialog')
    fireEvent(dialog, new Event('cancel', { cancelable: true }))
    expect(screen.queryByRole('dialog')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Expand post by Community member' }))
    fireEvent.click(screen.getByRole('dialog'))
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.getByRole('button', { name: 'Unpin post' })).toBeTruthy()
  })

  it('fades the popup out before closing, ignores repeat closes, and skips the fade for reduced motion', async () => {
    let reduce = false
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query.includes('no-preference') ? !reduce : reduce, addEventListener() {}, removeEventListener() {} }))
    setupWall()
    render(<App />)
    const trigger = await screen.findByRole('button', { name: 'Expand post by Community member' })
    fireEvent.click(trigger)
    let dialog = screen.getByRole('dialog', { name: 'Expanded post' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close expanded post' }))
    expect(dialog.classList.contains('is-closing')).toBe(true)
    expect(screen.getByRole('dialog')).toBe(dialog)
    fireEvent(dialog, new Event('cancel', { cancelable: true }))
    fireEvent.animationEnd(dialog.querySelector('.post-modal')!)
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(document.activeElement).toBe(trigger)

    fireEvent.click(trigger)
    dialog = screen.getByRole('dialog')
    fireEvent(dialog, new Event('cancel', { cancelable: true }))
    expect(dialog.classList.contains('is-closing')).toBe(true)
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())

    reduce = true
    fireEvent.click(trigger)
    fireEvent.click(screen.getByRole('dialog'))
    expect(screen.queryByRole('dialog')).toBeNull()
  })

  it.each(['normal', 'immersive'])('places the linked profile and avatar above the expanded content in %s mode', async mode => {
    const profile = {
      ...post, authorProfileUrl: 'https://bsky.app/profile/example.test',
      authorAvatarUrl: 'https://cdn.bsky.app/avatar.jpg',
      images: [{ url: '/photo.jpg', alt: 'Event photo' }],
    }
    setupWall({}, [profile])
    render(<App />)
    const trigger = await screen.findByRole('button', { name: 'Expand post by Community member' })
    if (mode === 'immersive') fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    fireEvent.click(trigger)
    const dialog = screen.getByRole('dialog', { name: 'Expanded post' })
    const name = within(dialog).getByRole('link', { name: post.authorName })
    expect(name.getAttribute('href')).toBe(profile.authorProfileUrl)
    const header = dialog.querySelector('header')
    expect(header?.contains(name)).toBe(true)
    expect(header?.contains(within(dialog).getByRole('img', { name: /avatar/i }))).toBe(true)
    expect(name.compareDocumentPosition(within(dialog).getByText(post.text)) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(within(dialog).getByRole('img', { name: 'Event photo' })).toBeTruthy()
    expect(within(dialog).getByRole('link', { name: /open original post/i })).toBeTruthy()
    expect(within(dialog).queryByRole('button', { name: /preview image/i })).toBeNull()
  })

  it.each(['normal', 'immersive'])('shows authors beside their identities without platform text in %s mode', async mode => {
    const profile: Post = { ...post, authorAvatarUrl: 'https://cdn.bsky.app/avatar.jpg' }
    const noAvatar: Post = { ...post, id: 'two', source: 'YouTube', authorName: 'Another community member' }
    setupWall({}, [profile, noAvatar])
    render(<App />)
    const feed = await screen.findByRole('region', { name: 'Approved live posts' })
    if (mode === 'immersive') fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))

    function checkCard(region: HTMLElement, entry: Post) {
      const card = within(region).getByRole('button', { name: `Expand post by ${entry.authorName}` })
      const author = within(card).getByText(entry.authorName)
      const source = within(card).getByRole('img', { name: entry.source })
      expect(author.closest('.card-person')?.contains(source)).toBe(true)
      expect(author.compareDocumentPosition(within(card).getByText(entry.text)) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
      expect(within(card).queryByText(entry.source)).toBeNull()
      expect(card.getAttribute('aria-description')).toBe(`Source: ${entry.source}`)
      return card
    }

    const card = checkCard(feed, profile)
    expect(within(card).getByRole('img', { name: `Avatar of ${profile.authorName}` })).toBeTruthy()
    const fallbackCard = checkCard(feed, noAvatar)
    expect(within(fallbackCard).queryByRole('img', { name: /avatar/i })).toBeNull()

    fireEvent.click(within(card.closest('article')!).getByRole('button', { name: 'Pin post' }))
    const pinned = screen.getByRole('region', { name: 'Pinned posts' })
    const pinnedCard = checkCard(pinned, profile)
    fireEvent.error(within(pinnedCard).getByRole('img', { name: `Avatar of ${profile.authorName}` }))
    expect(within(pinnedCard).queryByRole('img', { name: /avatar/i })).toBeNull()
    expect(within(pinnedCard).getByRole('img', { name: 'Bluesky' })).toBeTruthy()

    fireEvent.click(pinnedCard)
    const dialog = screen.getByRole('dialog', { name: 'Expanded post' })
    expect(within(dialog).getByRole('img', { name: 'Bluesky' })).toBeTruthy()
    expect(within(dialog).queryByText('Bluesky')).toBeNull()
  })

  it.each(['normal', 'immersive'])('hides only shortened links across feed, pinned and expanded posts in %s mode', async mode => {
    const text = 'Can Aspire deploy to Kubernetes? https://t.co/PB6cDv0XLE See https://learn.microsoft.com/dotnet.'
    const displayed = 'Can Aspire deploy to Kubernetes? See https://learn.microsoft.com/dotnet.'
    const { fetchMock } = setupWall({}, [{ ...post, text }])
    render(<App />)
    const feed = await screen.findByRole('region', { name: 'Approved live posts' })
    await within(feed).findByText(displayed)
    if (mode === 'immersive') fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    fireEvent.click(within(feed).getByRole('button', { name: 'Pin post' }))
    const pinned = screen.getByRole('region', { name: 'Pinned posts' })
    expect(within(pinned).getByText(displayed)).toBeTruthy()
    fireEvent.click(within(pinned).getByRole('button', { name: 'Expand post by Community member' }))
    const dialog = screen.getByRole('dialog', { name: 'Expanded post' })
    expect(within(dialog).getByText(displayed)).toBeTruthy()
    expect(within(dialog).getByRole('link', { name: /open original post/i }).getAttribute('href')).toBe(post.sourceUrl)
    expect(screen.queryByText(/t\.co\/PB6cDv0XLE/)).toBeNull()
    expect(fetchMock.mock.calls.every(([input]) => String(input).startsWith('/api/public/'))).toBe(true)
  })

  it('keeps photos and attribution without inventing post text when only a shortened link remains', async () => {
    setupWall({}, [{ ...post, text: 'https://t.co/PB6cDv0XLE', images: [{ url: '/photo.jpg', alt: 'Shared photo' }] }])
    render(<App />)
    fireEvent.click(await screen.findByRole('button', { name: 'Expand post by Community member' }))
    const dialog = screen.getByRole('dialog', { name: 'Expanded post' })
    expect(dialog.hasAttribute('aria-describedby')).toBe(false)
    expect(dialog.querySelector('blockquote')).toBeNull()
    expect(within(dialog).getByRole('img', { name: 'Shared photo' })).toBeTruthy()
    expect(within(dialog).getByText(post.authorName)).toBeTruthy()
    expect(within(dialog).getByRole('link', { name: /open original post/i })).toBeTruthy()
  })

  it('uses text-only pinned previews while preserving full popup content and feed images', async () => {
    const illustratedPost: Post = {
      ...post, text: 'A longer question about building reliable applications. '.repeat(20).trim(),
      images: [{ url: 'https://cdn.bsky.app/example.png', alt: 'An event illustration' }],
    }
    setupWall({}, [illustratedPost, { ...illustratedPost, id: 'two', authorName: 'Another member' }])
    render(<App />)
    await screen.findByRole('button', { name: 'Expand post by Community member' })
    fireEvent.click(within(screen.getByRole('region', { name: 'Approved live posts' }))
      .getAllByRole('button', { name: 'Pin post' })[0])

    const pinned = screen.getByRole('region', { name: 'Pinned posts' })
    expect(within(pinned).getByText(illustratedPost.text)).toBeTruthy()
    expect(within(pinned).getByText(post.authorName)).toBeTruthy()
    expect(within(pinned).getByRole('img', { name: 'Bluesky' })).toBeTruthy()
    expect(within(pinned).queryByText('Bluesky')).toBeNull()
    expect(within(pinned).queryByRole('group', { name: '1 attached image' })).toBeNull()
    expect(within(pinned).getByRole('button', { name: 'Unpin post' }).getAttribute('aria-pressed')).toBe('true')
    expect(within(screen.getByRole('region', { name: 'Approved live posts' }))
      .getByRole('img', { name: 'An event illustration' })).toBeTruthy()

    fireEvent.click(within(pinned).getByRole('button', { name: 'Expand post by Community member' }))
    const dialog = screen.getByRole('dialog', { name: 'Expanded post' })
    expect(within(dialog).getByText(illustratedPost.text)).toBeTruthy()
    expect(within(dialog).getByRole('img', { name: 'An event illustration' })).toBeTruthy()
    expect(within(dialog).getByRole('link', { name: /open original post/i }).getAttribute('href')).toBe(post.sourceUrl)
  })

  it.each(['normal', 'immersive'])('keeps a portal question bot consistent across feed, pinned, expanded and reloaded views in %s mode', async mode => {
    const question: Post = { ...post, source: 'Portal', sourceUrl: undefined }
    setupWall({}, [question, { ...post, id: 'another' }])
    render(<App />)
    const feed = await screen.findByRole('region', { name: 'Approved live posts' })
    const url = within(feed).getByRole('img', { name: '.NET bot question icon' }).getAttribute('src')
    if (mode === 'immersive') fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    fireEvent.click(within(feed).getAllByRole('button', { name: 'Pin post' })[0])
    const pinned = screen.getByRole('region', { name: 'Pinned posts' })
    expect(within(pinned).getByRole('img', { name: '.NET bot question icon' }).getAttribute('src')).toBe(url)
    fireEvent.click(within(pinned).getByRole('button', { name: 'Expand post by Community member' }))
    const dialog = screen.getByRole('dialog', { name: 'Expanded post' })
    expect(within(dialog).getByRole('img', { name: '.NET bot question icon' }).getAttribute('src')).toBe(url)
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close expanded post' }))
    cleanup()
    render(<App />)
    const reloaded = await screen.findByRole('region', { name: 'Pinned posts' })
    expect(within(reloaded).getByRole('img', { name: '.NET bot question icon' }).getAttribute('src')).toBe(url)
  })

  it.each(['normal', 'immersive'])('unpins in the popup without closing it and persists changes in %s mode', async mode => {
    const { fetchMock } = setupWall({}, [post, { ...post, id: 'two', authorName: 'Another member' }])
    localStorage.setItem('eventwall:pins:communityevent', JSON.stringify(['one', 'two']))
    render(<App />)
    await screen.findByRole('region', { name: 'Pinned posts' })
    if (mode === 'immersive') fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    fireEvent.click(screen.getByRole('button', { name: 'Expand post by Community member' }))

    const dialog = screen.getByRole('dialog', { name: 'Expanded post' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Unpin post' }))
    expect(screen.getByRole('dialog')).toBe(dialog)
    expect(within(dialog).getByRole('status').textContent).toBe('Returned to feed')
    expect(within(dialog).queryByRole('button', { name: 'Unpin post' })).toBeNull()
    expect(document.activeElement).toBe(within(dialog).getByRole('button', { name: 'Close expanded post' }))
    expect(within(screen.getByRole('region', { name: 'Pinned posts' }))
      .queryByRole('button', { name: 'Expand post by Community member' })).toBeNull()
    const returnedPost = within(screen.getByRole('region', { name: 'Approved live posts' }))
      .getByRole('button', { name: 'Expand post by Community member' })
    expect(returnedPost).toBeTruthy()
    expect(JSON.parse(localStorage.getItem('eventwall:pins:communityevent') ?? '[]')).toEqual(['two'])
    expect(fetchMock.mock.calls.every(([url]) => !String(url).includes('/api/admin'))).toBe(true)
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close expanded post' }))
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(document.activeElement).toBe(returnedPost)
    expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(mode === 'immersive')

    cleanup()
    render(<App />)
    const remaining = await screen.findByRole('region', { name: 'Pinned posts' })
    expect(within(remaining).getByRole('button', { name: 'Expand post by Another member' })).toBeTruthy()
    expect(within(remaining).queryByRole('button', { name: 'Expand post by Community member' })).toBeNull()
    fireEvent.click(within(remaining).getByRole('button', { name: 'Expand post by Another member' }))
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Unpin post' }))
    expect(screen.getByRole('dialog')).toBeTruthy()
    expect(screen.queryByRole('region', { name: 'Pinned posts' })).toBeNull()
    expect(JSON.parse(localStorage.getItem('eventwall:pins:communityevent') ?? '[]')).toEqual([])

    cleanup()
    render(<App />)
    await screen.findByRole('button', { name: 'Expand post by Another member' })
    expect(screen.queryByRole('region', { name: 'Pinned posts' })).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Expand post by Another member' }))
    expect(within(screen.getByRole('dialog')).queryByRole('button', { name: 'Unpin post' })).toBeNull()
  })

  it('loads saved pins for the event without overwriting another event pin list', async () => {
    setupWall({ slug: 'another-event' }, [post, { ...post, id: 'two', authorName: 'Another member' }])
    localStorage.setItem('eventwall:pins:communityevent', JSON.stringify(['one']))
    localStorage.setItem('eventwall:pins:another-event', JSON.stringify(['two']))
    render(<App />)
    await waitFor(() => {
      const pinned = screen.getByRole('region', { name: 'Pinned posts' })
      expect(within(pinned).getByRole('button', { name: 'Expand post by Another member' })).toBeTruthy()
      expect(within(pinned).queryByRole('button', { name: 'Expand post by Community member' })).toBeNull()
    })
    expect(JSON.parse(localStorage.getItem('eventwall:pins:communityevent') ?? '[]')).toEqual(['one'])
    expect(JSON.parse(localStorage.getItem('eventwall:pins:another-event') ?? '[]')).toEqual(['two'])
  })

  it('enters fullscreen with only the wall and exits through its control or browser Escape', async () => {
    setupWall()
    render(<App />)
    await screen.findByRole('button', { name: 'Expand post by Community member' })
    fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    await waitFor(() => expect(document.fullscreenElement).toBe(screen.getByRole('main')))
    expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(true)
    expect(screen.queryByRole('heading', { name: /the room is/i })).toBeNull()
    expect(screen.getByRole('region', { name: 'Immersive wall controls' })).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Exit immersive mode' }))
    await waitFor(() => expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(false))
    expect(document.exitFullscreen).toHaveBeenCalledOnce()

    fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    await act(async () => { await document.exitFullscreen() })
    expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(false)
  })

  it.each(['unsupported', 'denied'])('keeps immersive controls usable when fullscreen is %s', async reason => {
    setupWall()
    if (reason === 'unsupported') Object.defineProperty(HTMLElement.prototype, 'requestFullscreen', { value: undefined })
    else vi.mocked(HTMLElement.prototype.requestFullscreen).mockRejectedValueOnce(new Error('Denied'))
    render(<App />)
    await screen.findByRole('button', { name: 'Expand post by Community member' })
    fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    await waitFor(() => expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(true))
    if (reason === 'denied') expect(await screen.findByRole('alert')).toHaveProperty('textContent', expect.stringContaining('Browser fullscreen is unavailable'))
    fireEvent.click(screen.getByRole('button', { name: 'Expand post by Community member' }))
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(true)
    fireEvent(screen.getByRole('dialog'), new Event('cancel', { cancelable: true }))
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(false)
  })

  it('checks for wall changes once a minute and keeps posts visible between checks', async () => {
    const { fetchMock } = setupWall()
    vi.useFakeTimers()
    await act(async () => { render(<App />) })
    const changeChecks = () => fetchMock.mock.calls.filter(([url]) => String(url) === '/api/public/wall/changes').length
    await act(async () => { await vi.advanceTimersByTimeAsync(59_999) })
    expect(changeChecks()).toBe(0)
    await act(async () => { await vi.advanceTimersByTimeAsync(1) })
    expect(changeChecks()).toBe(1)
    await act(async () => { await vi.advanceTimersByTimeAsync(59_000) })
    expect(screen.getByRole('button', { name: 'Expand post by Community member' })).toBeTruthy()
  })

  it('keeps cached posts visible with a small offline banner and clears it on reconnect', async () => {
    const { state } = setupWall()
    vi.useFakeTimers()
    await act(async () => { render(<App />) })
    expect(screen.queryByText(/offline/i)).toBeNull()
    state.disconnected = true
    await act(async () => { await vi.advanceTimersByTimeAsync(600_000) })
    expect(screen.getByRole('button', { name: 'Expand post by Community member' })).toBeTruthy()
    expect(screen.getByText(/offline · showing last loaded posts/i)).toBeTruthy()
    expect(screen.queryByRole('alert')).toBeNull()
    expect(screen.getByText('Wall disconnected')).toBeTruthy()
    state.disconnected = false
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(screen.queryByText(/offline/i)).toBeNull()
    expect(screen.getByText('On stage')).toBeTruthy()
  })

  it('counts waiting posts in immersive controls without loading them until refresh', async () => {
    const { state, fetchMock } = setupWall()
    vi.useFakeTimers()
    await act(async () => { render(<App />) })
    fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    expect(screen.queryByTestId('new-posts-indicator')).toBeNull()
    state.changes.newCount = 2
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(screen.getByRole('status').textContent).toBe('2 new posts ready')
    expect(screen.getByTestId('new-posts-indicator')).toBeTruthy()
    expect(screen.getByRole('region', { name: 'Immersive wall controls' }).classList.contains('has-new')).toBe(true)
    expect(fetchMock.mock.calls.filter(([url]) => String(url).startsWith('/api/public/wall?sort='))).toHaveLength(1)
    state.posts = [...state.posts, { ...post, id: 'two', authorName: 'New attendee' }]
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Refresh live posts' })) })
    expect(screen.getByRole('button', { name: 'Expand post by New attendee' })).toBeTruthy()
    expect(screen.getByRole('status').textContent).toBe('0 new posts ready')
    expect(screen.queryByTestId('new-posts-indicator')).toBeNull()
  })

  it('removes withdrawn posts and their expanded dialog while immersive', async () => {
    const { state } = setupWall()
    vi.useFakeTimers()
    await act(async () => { render(<App />) })
    fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    fireEvent.click(screen.getByRole('button', { name: 'Pin post' }))
    fireEvent.click(screen.getByRole('button', { name: 'Expand post by Community member' }))
    state.changes.liveKnownIds = []
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Expand post by Community member' })).toBeNull()
    expect(JSON.parse(localStorage.getItem('eventwall:pins:communityevent') ?? '[]')).toEqual([])
  })

  it('keeps cached posts without dropping fullscreen and can reconnect through immersive controls', async () => {
    const { state } = setupWall()
    vi.useFakeTimers()
    await act(async () => { render(<App />) })
    fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    state.disconnected = true
    await act(async () => { await vi.advanceTimersByTimeAsync(180_000) })
    expect(screen.getByRole('button', { name: 'Expand post by Community member' })).toBeTruthy()
    expect(screen.getByText(/offline · showing last loaded posts/i)).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Refresh live posts' }).textContent).toBe('Reconnect')
    expect(document.fullscreenElement).toBe(screen.getByRole('main'))
    expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(true)
    state.disconnected = false
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Refresh live posts' })) })
    expect(screen.getByRole('button', { name: 'Expand post by Community member' })).toBeTruthy()
    expect(screen.queryByText(/offline/i)).toBeNull()
  })

  it('keeps the host wall and external participation link available when intake is paused', async () => {
    setupWall({ submissionsOpen: false })
    render(<App />)
    expect(await screen.findByRole('heading', { name: /the room is/i })).toBeTruthy()
    expect((await screen.findAllByRole('link', { name: /submit a question/i }))[0].getAttribute('href')).toBe('https://questions.example.test/')
    expect(screen.queryByRole('button', { name: /send to the team/i })).toBeNull()
  })

  it.each(['normal', 'immersive'])('shows all promos below pins and persists the local toggle in %s mode', async mode => {
    const { state, fetchMock } = setupWall()
    state.promos = [
      { id: 'survey', title: 'Developer survey', description: 'Take the developer survey', url: 'https://example.test/survey', createdAtUtc: new Date().toISOString(), active: true, version: 1 },
      { id: 'community', title: 'Community day', description: 'Join community day', url: null, createdAtUtc: new Date().toISOString(), active: true, version: 1 },
    ]
    render(<App />)
    const toggle = await screen.findByRole('button', { name: 'Show promotional blurbs' })
    await screen.findByRole('button', { name: 'Pin post' })
    expect(toggle.getAttribute('aria-pressed')).toBe('false')
    expect(screen.queryByText('Take the developer survey')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Pin post' }))
    if (mode === 'immersive') fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    fireEvent.click(toggle)
    const promos = screen.getByRole('region', { name: 'Promotional blurbs' })
    expect(promos.textContent).toContain('Developer survey')
    expect(promos.textContent).toContain('Community day')
    expect(screen.queryByText('Take the developer survey')).toBeNull()
    expect(within(promos).queryByRole('img')).toBeNull()
    expect(screen.getByRole('main').contains(toggle)).toBe(true)
    expect(screen.getByRole('region', { name: 'Pinned posts' }).compareDocumentPosition(promos) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(promos.compareDocumentPosition(screen.getByRole('region', { name: 'Approved live posts' })) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(localStorage.getItem('eventwall:promos:communityevent')).toBe('true')
    expect(fetchMock.mock.calls.every(([url]) => !String(url).includes('/api/admin'))).toBe(true)
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Sort by newest approved' })) })
    expect(within(promos).getByRole('button', { name: 'Developer survey' })).toBeTruthy()
    const survey = within(promos).getByRole('button', { name: 'Developer survey' })
    fireEvent.click(survey)
    let dialog = screen.getByRole('dialog', { name: 'Promo: Developer survey' })
    expect(within(dialog).getByText('Take the developer survey')).toBeTruthy()
    expect(within(dialog).getByRole('link').getAttribute('href')).toBe('https://example.test/survey')
    expect(within(dialog).getByRole('img', { name: 'QR code for https://example.test/survey' })).toBeTruthy()
    expect(screen.getByRole('main').contains(dialog)).toBe(true)
    fireEvent(dialog, new Event('cancel', { cancelable: true }))
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(document.activeElement).toBe(survey)
    fireEvent.click(within(promos).getByRole('button', { name: 'Community day' }))
    dialog = screen.getByRole('dialog', { name: 'Promo: Community day' })
    expect(within(dialog).getByText('Join community day')).toBeTruthy()
    expect(within(dialog).queryByRole('img')).toBeNull()
    expect(within(dialog).queryByRole('link')).toBeNull()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close promo' }))
    cleanup()
    render(<App />)
    expect(await screen.findByRole('button', { name: 'Developer survey' })).toBeTruthy()
    expect(screen.queryByRole('dialog')).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Show promotional blurbs' }))
    expect(screen.queryByRole('region', { name: 'Promotional blurbs' })).toBeNull()
    expect(localStorage.getItem('eventwall:promos:communityevent')).toBe('false')
  })

  it('updates and removes promos through polling and keeps cached promos while offline', async () => {
    const { state } = setupWall()
    state.promos = [{ id: 'survey', title: 'Survey', description: 'Survey before edit', url: null, createdAtUtc: new Date().toISOString(), active: true, version: 1 }]
    vi.useFakeTimers()
    await act(async () => { render(<App />) })
    fireEvent.click(screen.getByRole('button', { name: 'Show promotional blurbs' }))
    fireEvent.click(screen.getByRole('button', { name: 'Survey' }))
    expect(screen.getByText('Survey before edit')).toBeTruthy()
    state.promos[0] = { ...state.promos[0], description: 'Survey after edit', version: 2 }
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(screen.getByText('Survey after edit')).toBeTruthy()
    expect(screen.queryByText('Survey before edit')).toBeNull()
    state.disconnected = true
    await act(async () => { await vi.advanceTimersByTimeAsync(180_000) })
    expect(screen.getByText('Survey after edit')).toBeTruthy()
    state.disconnected = false
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Refresh live posts' })) })
    expect(screen.getByText('Survey after edit')).toBeTruthy()
    state.promos = []
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(screen.queryByText('Survey after edit')).toBeNull()
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.getByText('No promotional blurbs yet.')).toBeTruthy()
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Show promotional blurbs' }))
  })

  it('keeps fallback immersive mode active when Escape closes a promo popup', async () => {
    const { state } = setupWall()
    state.promos = [{ id: 'survey', title: 'Survey', description: 'Help shape .NET', url: null, createdAtUtc: new Date().toISOString(), active: true, version: 1 }]
    Object.defineProperty(HTMLElement.prototype, 'requestFullscreen', { value: undefined })
    render(<App />)
    fireEvent.click(await screen.findByRole('button', { name: 'Show promotional blurbs' }))
    fireEvent.click(screen.getByRole('button', { name: 'Immerse mode' }))
    fireEvent.click(screen.getByRole('button', { name: 'Survey' }))
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(true)
    fireEvent(screen.getByRole('dialog'), new Event('cancel', { cancelable: true }))
    expect(screen.queryByRole('dialog')).toBeNull()
    expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(true)
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.getByRole('main').classList.contains('immersive-wall')).toBe(false)
  })

  it('keeps promo preferences isolated by event', async () => {
    localStorage.setItem('eventwall:promos:communityevent', 'true')
    setupWall({ slug: 'another-event' })
    render(<App />)
    expect((await screen.findByRole('button', { name: 'Show promotional blurbs' })).getAttribute('aria-pressed')).toBe('false')
    fireEvent.click(screen.getByRole('button', { name: 'Show promotional blurbs' }))
    expect(localStorage.getItem('eventwall:promos:another-event')).toBe('true')
    expect(localStorage.getItem('eventwall:promos:communityevent')).toBe('true')
  })
})
