import assert from 'node:assert/strict'
import { test } from 'node:test'

let instance = 0
async function fixture(t) {
  const originals = Object.fromEntries(['document', 'sessionStorage', 'localStorage', 'fetch', 'turnstile'].map(name => [name, Object.getOwnPropertyDescriptor(globalThis, name)]))
  t.after(() => {
    for (const [name, descriptor] of Object.entries(originals)) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor)
      else delete globalThis[name]
    }
  })
  const values = new Map()
  const storage = { getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, value), removeItem: key => values.delete(key) }
  globalThis.sessionStorage = storage
  globalThis.localStorage = { ...storage }
  const elements = {
    'question-text': { value: 'How do I build a .NET app?' },
    'question-name': { value: 'Attendee' },
    'question-website': { value: '' },
    'questions-browser-warning': { textContent: '' },
  }
  const listeners = new Map()
  globalThis.document = {
    documentElement: { dataset: { theme: 'dark' } },
    getElementById: id => elements[id],
    addEventListener: (name, handler) => listeners.set(name, handler),
  }
  const calls = []
  globalThis.fetch = async (url, options) => {
    calls.push({ url, options })
    return new Response(JSON.stringify({ received: true }), { status: 202 })
  }
  const url = new URL('../../src/EventWall.Api/wwwroot/questions/questions.js', import.meta.url)
  url.search = `instance=${++instance}`
  return { client: await import(url), calls, values, elements, listeners }
}

test('imports without fetching or polling and bootstraps only on request', async t => {
  const { client, calls } = await fixture(t)
  assert.equal(calls.length, 0)
  await client.loadConfig()
  assert.equal(calls.length, 1)
  assert.equal(calls[0].url, '/api/public/submission-config')
  assert.equal(calls[0].options.credentials, 'same-origin')
})

test('rejects invalid configuration shapes rather than breaking the server circuit', async t => {
  const { client } = await fixture(t)
  for (const data of [null, [], 42, { name: 'Event' }, { slug: 'event', name: 'Event', demoMode: false, submissionToken: 5 }]) {
    globalThis.fetch = async () => new Response(JSON.stringify(data), { status: 200 })
    assert.equal((await client.loadConfig()).ok, false)
  }
  globalThis.fetch = async () => new Response(JSON.stringify({ slug: 'event', name: 'Event', demoMode: false, submissionToken: 'test-token' }), { status: 200 })
  assert.equal((await client.loadConfig()).ok, true)
  for (const turnstileSiteKey of ['', 5]) {
    globalThis.fetch = async () => new Response(JSON.stringify({ slug: 'event', name: 'Event', demoMode: false, submissionToken: 'test-token', turnstileSiteKey }), { status: 200 })
    assert.equal((await client.loadConfig()).ok, false)
  }
  globalThis.fetch = async () => new Response(JSON.stringify({ slug: 'event', name: 'Event', demoMode: false, submissionToken: 'test-token', turnstileSiteKey: 'site-key' }), { status: 200 })
  assert.equal((await client.loadConfig()).ok, true)
})

test('requires a fresh Turnstile token for every send', async t => {
  const { client, calls } = await fixture(t)
  const widget = { response: '', resets: 0, options: null }
  globalThis.turnstile = {
    render: (container, options) => { assert.equal(container, '#question-challenge'); widget.options = options; return 'widget-1' },
    getResponse: id => { assert.equal(id, 'widget-1'); return widget.response },
    reset: id => { assert.equal(id, 'widget-1'); widget.resets++; widget.response = '' },
    remove: () => {},
  }
  assert.deepEqual(await client.renderChallenge('site-key'), { ok: true, message: null })
  assert.equal(widget.options.sitekey, 'site-key')
  assert.equal(widget.options.action, 'submit-question')
  const pending = await client.submit({}, 'key', 'token')
  assert.equal(pending.code, 'challenge-required')
  assert.equal(calls.length, 0)
  widget.response = 'challenge-token'
  assert.equal((await client.submit({}, 'key', 'token')).ok, true)
  assert.equal(calls[0].options.headers['X-Turnstile-Token'], 'challenge-token')
  assert.equal(widget.resets, 1)
  assert.equal((await client.submit({}, 'key', 'token')).code, 'challenge-required')
  assert.equal(calls.length, 1)
})

test('blocks sending when the Turnstile script cannot load', async t => {
  const { client, calls } = await fixture(t)
  delete globalThis.turnstile
  globalThis.document.createElement = () => ({})
  globalThis.document.head = { appendChild: script => script.onerror() }
  const rendered = await client.renderChallenge('site-key')
  assert.equal(rendered.ok, false)
  assert.match(rendered.message, /challenges\.cloudflare\.com/)
  const reply = await client.submit({}, 'key', 'token')
  assert.equal(reply.code, 'challenge-required')
  assert.equal(calls.length, 0)
})

test('posts original protected payload from the browser and clears only a confirmed receipt', async t => {
  const { client, calls, values } = await fixture(t)
  const payload = { text: 'How do I build a .NET app?', displayName: 'Attendee', website: '' }
  const reply = await client.submit(payload, 'test-idempotency-key', 'test-token')
  assert.equal(reply.ok, true)
  assert.equal(calls[0].url, '/api/public/submissions')
  assert.equal(calls[0].options.credentials, 'same-origin')
  assert.equal(calls[0].options.headers['X-Submission-Token'], 'test-token')
  assert.equal(calls[0].options.headers['Idempotency-Key'], 'test-idempotency-key')
  assert.deepEqual(JSON.parse(calls[0].options.body), payload)
  assert.equal(values.has('eventwall:question-draft'), false)
})

test('preserves draft and attempted identity on pause without retaining a token', async t => {
  const { client, elements, values, listeners } = await fixture(t)
  listeners.get('input')({ target: { id: 'question-text' } })
  globalThis.fetch = async () => new Response(JSON.stringify({ title: 'Paused', code: 'submissions-paused' }), { status: 409 })
  const payload = { text: elements['question-text'].value, displayName: 'Attendee', website: '' }
  const reply = await client.submit(payload, 'same-key', 'never-store-this-token')
  assert.equal(reply.ok, false)
  const draft = client.restoreDraft()
  assert.equal(draft.text, payload.text)
  assert.equal(draft.key, 'same-key')
  assert.deepEqual(draft.attempt, payload)
  assert.doesNotMatch(values.get('eventwall:question-draft'), /never-store-this-token/)
})

test('surfaces token errors and both Retry-After representations without sending again', async t => {
  const { client } = await fixture(t)
  let count = 0
  globalThis.fetch = async () => {
    count++
    return new Response(JSON.stringify({ title: 'Expired', code: 'submission-token-invalid' }), { status: 403 })
  }
  assert.equal((await client.submit({}, 'key', 'token')).code, 'submission-token-invalid')
  for (const headers of [{ 'Retry-After': '3' }, {}]) {
    globalThis.fetch = async () => {
      count++
      return new Response(JSON.stringify({ title: 'Limited', retryAfterSeconds: 7 }), { status: 429, headers })
    }
    assert.equal((await client.submit({}, 'key', 'token')).retryAfterSeconds, headers['Retry-After'] ? 3 : 7)
  }
  assert.equal(count, 3)
})

test('does not discard a draft on a network failure, malformed JSON or an invalid receipt', async t => {
  const { client, values } = await fixture(t)
  globalThis.fetch = async () => { throw new TypeError('Network unavailable') }
  await assert.rejects(client.submit({}, 'key', 'token'), /Network unavailable/)
  assert.ok(values.has('eventwall:question-draft'))
  globalThis.fetch = async () => new Response('unavailable', { status: 503 })
  assert.equal((await client.submit({}, 'key', 'token')).ok, false)
  globalThis.fetch = async () => new Response('{"received":false}', { status: 202 })
  await client.submit({}, 'key', 'token')
  assert.ok(values.has('eventwall:question-draft'))
})

test('recovers UTF-16 input values and explicitly reports unusable recovery storage', async t => {
  const { client, elements, values, listeners } = await fixture(t)
  elements['question-text'].value = 'How can I write a .NET app? 😀'
  listeners.get('input')({ target: { id: 'question-text' } })
  assert.equal(client.restoreDraft().text, elements['question-text'].value)
  values.set('eventwall:question-draft', 'invalid-json')
  assert.equal(client.restoreDraft(), null)
  assert.match(elements['questions-browser-warning'].textContent, /copy your question/)
})

test('uses the shared theme key and reports blocked preference storage', async t => {
  const { client, values } = await fixture(t)
  assert.deepEqual(client.toggleTheme(), { light: true, error: null })
  assert.equal(values.get('eventwall:theme'), 'light')
  assert.equal(client.isLightTheme(), true)
  globalThis.localStorage.setItem = () => { throw new DOMException('Blocked', 'SecurityError') }
  const reply = client.toggleTheme()
  assert.equal(reply.light, false)
  assert.match(reply.error, /could not save/)
  assert.equal(client.isLightTheme(), false)
})
