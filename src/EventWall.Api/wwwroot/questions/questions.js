const draftKey = 'eventwall:question-draft'
const turnstileScript = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit'
const challengeLoadFailure = 'The verification check could not load. Make sure challenges.cloudflare.com is not blocked, then reload the page.'
let lastAttempt = null
let challengeRequired = false
let challengeWidget = null
let turnstileLoading = null

function storageFailure(error) {
  if (!(error instanceof DOMException || error instanceof SyntaxError)) throw error
  console.warn('Could not keep a question recovery draft.', error.name)
  const warning = document.getElementById('questions-browser-warning')
  if (warning) warning.textContent = 'This browser could not save a recovery draft. Keep this page open, or copy your question before reloading.'
}

export function restoreDraft() {
  try {
    const draft = JSON.parse(sessionStorage.getItem(draftKey) ?? 'null')
    if (draft === null) return null
    if (typeof draft.text !== 'string' || draft.text.length > 2000 ||
        typeof draft.displayName !== 'string' || draft.displayName.length > 80 || typeof draft.website !== 'string') {
      throw new SyntaxError('Invalid recovery draft')
    }
    const validAttempt = draft.attempt && typeof draft.key === 'string' &&
      typeof draft.attempt.text === 'string' && typeof draft.attempt.displayName === 'string' &&
      typeof draft.attempt.website === 'string'
    if (draft.attempt && !validAttempt) storageFailure(new SyntaxError('Invalid recovery attempt'))
    lastAttempt = validAttempt ? { payload: draft.attempt, key: draft.key } : null
    return { text: draft.text, displayName: draft.displayName, website: draft.website, attempt: lastAttempt?.payload ?? null, key: lastAttempt?.key ?? null }
  } catch (error) {
    storageFailure(error)
    return null
  }
}

function rememberDraft() {
  const text = document.getElementById('question-text')
  if (!text) return
  try {
    sessionStorage.setItem(draftKey, JSON.stringify({
      text: text.value,
      displayName: document.getElementById('question-name').value,
      website: document.getElementById('question-website').value,
      attempt: lastAttempt?.payload ?? null,
      key: lastAttempt?.key ?? null,
    }))
  } catch (error) { storageFailure(error) }
}

document.addEventListener('input', event => {
  if (['question-text', 'question-name', 'question-website'].includes(event.target.id)) rememberDraft()
})

export function isLightTheme() {
  return document.documentElement.dataset.theme === 'light'
}

export function toggleTheme() {
  const light = !isLightTheme()
  document.documentElement.dataset.theme = light ? 'light' : 'dark'
  try {
    localStorage.setItem('eventwall:theme', light ? 'light' : 'dark')
    return { light, error: null }
  } catch (error) {
    if (!(error instanceof DOMException)) throw error
    console.warn('Could not save the color theme.', error.name)
    return { light, error: 'Theme changed, but this browser could not save your preference.' }
  }
}

async function request(path, options) {
  const response = await fetch(path, { ...options, credentials: 'same-origin' })
  let data
  try { data = await response.json() }
  catch (error) {
    if (!(error instanceof SyntaxError)) throw error
    console.warn('The question endpoint returned an unreadable response.', error.name)
  }
  const retry = Number(response.headers.get('Retry-After') ?? data?.retryAfterSeconds)
  const valid = data !== null && typeof data === 'object' && !Array.isArray(data)
  return {
    ok: response.ok && valid,
    status: response.status,
    data: response.ok && valid ? data : null,
    message: response.ok && valid ? null : data?.detail ?? data?.title ?? `Request failed (${response.status})`,
    code: data?.code,
    retryAfterSeconds: Number.isFinite(retry) && retry > 0 ? Math.ceil(retry) : null,
  }
}

export async function loadConfig() {
  const reply = await request('/api/public/submission-config')
  const config = reply.data
  if (reply.ok && (typeof config.slug !== 'string' || !config.slug ||
      typeof config.name !== 'string' || !config.name || typeof config.demoMode !== 'boolean' ||
      typeof config.submissionToken !== 'string' || !config.submissionToken ||
      (config.turnstileSiteKey != null && (typeof config.turnstileSiteKey !== 'string' || !config.turnstileSiteKey)))) {
    return { ...reply, ok: false, data: null, message: 'The question page returned an invalid configuration. Please try loading again.' }
  }
  return reply
}

function loadTurnstile() {
  if (globalThis.turnstile) return Promise.resolve(globalThis.turnstile)
  turnstileLoading ??= new Promise((resolve, reject) => {
    const script = document.createElement('script')
    script.src = turnstileScript
    script.async = true
    script.onload = () => globalThis.turnstile ? resolve(globalThis.turnstile) : reject(new Error('Turnstile did not initialize'))
    script.onerror = () => reject(new Error('Turnstile could not load'))
    document.head.appendChild(script)
  }).catch(error => {
    turnstileLoading = null
    throw error
  })
  return turnstileLoading
}

export async function renderChallenge(siteKey) {
  challengeRequired = true
  try {
    const turnstile = await loadTurnstile()
    if (challengeWidget !== null) {
      try { turnstile.remove(challengeWidget) }
      catch (error) { console.warn('Could not remove the previous verification widget.', error.name) }
    }
    challengeWidget = turnstile.render('#question-challenge', {
      sitekey: siteKey,
      action: 'submit-question',
      theme: isLightTheme() ? 'light' : 'dark',
      'refresh-expired': 'auto',
    }) ?? null
    if (challengeWidget === null) throw new Error('Turnstile did not render')
    return { ok: true, message: null }
  } catch (error) {
    challengeWidget = null
    console.warn('Could not load the verification check.', error.message)
    return { ok: false, message: challengeLoadFailure }
  }
}

function resetChallenge() {
  if (challengeWidget === null) return
  try { globalThis.turnstile.reset(challengeWidget) }
  catch (error) { console.warn('Could not reset the verification check.', error.name) }
}

export async function submit(payload, key, token) {
  lastAttempt = { payload, key }
  rememberDraft()
  const headers = { 'Content-Type': 'application/json', 'Idempotency-Key': key, 'X-Submission-Token': token }
  if (challengeRequired) {
    const response = challengeWidget === null ? '' : globalThis.turnstile.getResponse(challengeWidget)
    if (!response) {
      return {
        ok: false, status: 0, data: null, code: 'challenge-required', retryAfterSeconds: null,
        message: challengeWidget === null ? challengeLoadFailure : 'Please complete the verification check before sending.',
      }
    }
    headers['X-Turnstile-Token'] = response
  }
  let reply
  try {
    reply = await request('/api/public/submissions', { method: 'POST', headers, body: JSON.stringify(payload) })
  } finally {
    // Turnstile tokens are single-use, so every send (including a retry) needs a fresh one.
    if (challengeRequired) resetChallenge()
  }
  if (reply.ok && reply.data.received !== true) {
    return { ...reply, ok: false, data: null, message: 'The question endpoint did not confirm receipt.' }
  }
  if (reply.ok && reply.data?.received === true) {
    lastAttempt = null
    try { sessionStorage.removeItem(draftKey) } catch (error) { storageFailure(error) }
  }
  return reply
}
