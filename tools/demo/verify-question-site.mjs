import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import path from 'node:path'
import { chromium } from 'playwright-core'

const wall = process.env.EVENTWALL_URL ?? 'http://127.0.0.1:5080'
const questions = process.env.EVENTWALL_SUBMISSION_URL ?? 'http://127.0.0.1:5082'
const admin = process.env.EVENTWALL_ADMIN_URL ?? 'http://127.0.0.1:5081'
const browser = await chromium.launch({ channel: 'msedge', headless: true })
async function verifyFormParity() {
  const text = 'How can I deploy a .NET app with Aspire?'
  async function scenario(respond, check) {
    const context = await browser.newContext()
    try {
      const page = await context.newPage()
      const posts = []
      await page.route('**/api/public/submissions', async route => {
        posts.push({ headers: route.request().headers(), payload: route.request().postDataJSON() })
        await respond(route, posts.length)
      })
      await page.goto(questions)
      await page.getByLabel('Your question', { exact: true }).fill(text)
      await check(page, posts)
    } finally { await context.close() }
  }
  const receipt = route => route.fulfill({ status: 202, json: { received: true } })
  let releaseReceipt
  const blockedReceipt = new Promise(resolve => { releaseReceipt = resolve })
  await scenario(async route => { await blockedReceipt; await receipt(route) }, async (page, posts) => {
    const posted = page.waitForRequest(request => request.url().endsWith('/api/public/submissions') && request.method() === 'POST')
    await page.evaluate(() => {
      const form = document.querySelector('.question-card form')
      form.requestSubmit()
      form.requestSubmit()
    })
    await posted
    assert.equal(await page.getByRole('button', { name: /sending/i }).isDisabled(), true)
    await page.waitForTimeout(200)
    assert.equal(posts.length, 1, 'Repeated submit events must share one in-flight send')
    releaseReceipt()
    await page.getByRole('heading', { name: 'Safely in the queue.' }).waitFor()
  })
  await scenario(receipt, async (page, posts) => {
    assert.equal(await page.getByLabel('Your question', { exact: true }).getAttribute('maxlength'), '2000')
    assert.equal(await page.getByLabel('Name (optional)', { exact: true }).getAttribute('maxlength'), '80')
    await page.getByLabel('Name (optional)', { exact: true }).fill('Community attendee')
    await page.getByRole('button', { name: /send to the team/i }).click()
    await page.getByRole('heading', { name: 'Safely in the queue.' }).waitFor()
    assert.equal(posts.length, 1)
    assert.equal(posts[0].payload.text, text)
    assert.ok(posts[0].headers['x-submission-token'])
    assert.match(posts[0].headers['idempotency-key'], /^[a-f0-9-]{36}$/)
    await page.getByRole('button', { name: 'Ask another question' }).click()
    assert.equal(await page.getByLabel('Your question', { exact: true }).inputValue(), '')
    assert.equal(await page.getByLabel('Name (optional)', { exact: true }).inputValue(), 'Community attendee')
    assert.equal(await page.evaluate(() => sessionStorage.getItem('eventwall:question-draft')), null)
  })
  await scenario(async (route, count) => count === 1
    ? route.fulfill({ status: 409, json: { title: 'Submissions are paused.', code: 'submissions-paused' } })
    : receipt(route), async (page, posts) => {
    await page.getByRole('button', { name: /send to the team/i }).click()
    await page.getByRole('alert').filter({ hasText: 'Your draft has been kept' }).waitFor()
    assert.equal(await page.getByLabel('Your question', { exact: true }).inputValue(), text)
    await page.getByRole('button', { name: /send to the team/i }).click()
    await page.getByRole('heading', { name: 'Safely in the queue.' }).waitFor()
    assert.equal(posts[0].headers['idempotency-key'], posts[1].headers['idempotency-key'])
  })
  await scenario(async (route, count) => count === 1
    ? route.fulfill({ status: 403, json: { title: 'Token expired', code: 'submission-token-invalid' } })
    : receipt(route), async (page, posts) => {
    let renewals = 0
    page.on('request', request => { if (request.url().endsWith('/api/public/submission-config')) renewals++ })
    await page.getByRole('button', { name: /send to the team/i }).click()
    await page.getByRole('button', { name: /refresh and retry/i }).waitFor()
    assert.equal(renewals, 0)
    await page.getByRole('button', { name: /refresh and retry/i }).click()
    await page.getByRole('heading', { name: 'Safely in the queue.' }).waitFor()
    assert.equal(renewals, 1)
    assert.equal(posts[0].headers['idempotency-key'], posts[1].headers['idempotency-key'])
  })
  await scenario(route => route.fulfill({ status: 429, headers: { 'Retry-After': '2' }, json: { title: 'Too many requests' } }), async (page, posts) => {
    await page.getByRole('button', { name: /send to the team/i }).click()
    assert.equal(await page.getByRole('button', { name: /please wait before retrying/i }).isDisabled(), true)
    await page.getByRole('button', { name: /send to the team/i }).waitFor()
    assert.equal(posts.length, 1, 'Cooldown must never automatically resubmit')
    assert.equal(await page.getByLabel('Your question', { exact: true }).inputValue(), text)
  })
  await scenario(route => route.abort('failed'), async (page, posts) => {
    await page.getByRole('button', { name: /send to the team/i }).click()
    await page.getByRole('alert').filter({ hasText: 'Your draft has been kept' }).waitFor()
    const key = posts[0].headers['idempotency-key']
    await page.reload()
    await page.getByLabel('Your question', { exact: true }).waitFor()
    assert.equal(await page.getByLabel('Your question', { exact: true }).inputValue(), text)
    await page.getByRole('button', { name: /send to the team/i }).click()
    await page.getByRole('alert').filter({ hasText: 'Your draft has been kept' }).waitFor()
    assert.equal(posts[1].headers['idempotency-key'], key, 'A restored attempt must not create a duplicate identity')
    await page.getByLabel('Your question', { exact: true }).fill(`${text} And containers?`)
    const changedRequest = page.waitForRequest(request => request.url().endsWith('/api/public/submissions') && request.method() === 'POST')
    await page.getByRole('button', { name: /send to the team/i }).click()
    const changed = await changedRequest
    await page.getByRole('alert').filter({ hasText: 'Your draft has been kept' }).waitFor()
    assert.notEqual(changed.headers()['idempotency-key'], key)
  })
  console.log('PASS: Blazor form limits, protected transport, receipt/ask-another, paused drafts, explicit token renewal, cooldown and recovered idempotency.')
}
try {
  await verifyFormParity()
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
  const request = context.request
  for (const [origin, role] of [[wall, 'public'], [questions, 'submission']]) {
    const root = await request.get(origin)
    assert.equal(root.status(), 200)
    assert.match(await root.text(), new RegExp(`eventwall-role" content="${role}"`))
    assert.equal((await request.get(`${origin}/api/admin/queue`)).status(), 404)
  }
  assert.equal((await request.post(`${wall}/api/public/submissions`, { data: {} })).status(), 404)
  assert.equal((await request.get(`${wall}/api/public/submission-config`)).status(), 404)
  assert.equal((await request.get(`${questions}/api/public/wall`)).status(), 404)
  assert.equal((await request.get(`${questions}/api/public/event`)).status(), 404)
  const page = await context.newPage()
  const calls = []
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) calls.push(request.url()) })
  await page.goto(questions)
  await page.getByLabel('Your question', { exact: true }).waitFor()
  assert.ok(calls.every(url => url.endsWith('/api/public/submission-config')))
  const initialCalls = calls.length
  // Do not advance SignalR's clock: that would manufacture a connection timeout.
  await page.waitForTimeout(120_000)
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
  assert.equal(calls.length, initialCalls, 'Idle and visibility changes must not poll any API')
  assert.equal(await page.getByRole('link', { name: /live wall/i }).count(), 0)
  assert.ok(await page.getByRole('link', { name: 'Third-party licenses and notices' }).isVisible())
  const output = process.env.DEMO_OUTPUT
  if (output) await fs.mkdir(output, { recursive: true })
  for (const theme of ['dark', 'light']) {
    if (await page.locator('html').getAttribute('data-theme') !== theme) await page.getByRole('button', { name: 'Light theme' }).click()
    for (const width of [1440, 390, 340]) {
      await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 })
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), `No question overflow at ${width}px`)
      if (output) await page.screenshot({ path: path.join(output, `questions-${theme}-${width}.png`), fullPage: true })
    }
  }
  await page.goto(`${wall}/?view=submit`)
  await page.getByLabel('Your question', { exact: true }).waitFor()
  assert.equal(new URL(page.url()).origin, new URL(questions).origin)
  const missingToken = await request.post(`${questions}/api/public/submissions`, {
    data: { text: 'Missing token must never reach moderation.' }, headers: { 'Idempotency-Key': crypto.randomUUID() },
  })
  assert.equal(missingToken.status(), 403)
  await page.goto(wall)
  await page.getByRole('heading', { name: /the room is/i }).waitFor()
  assert.equal(await page.getByRole('textbox').count(), 0)
  assert.equal(await page.getByRole('link', { name: 'Submit a question', exact: true }).getAttribute('href'), `${questions.replace(/\/$/, '')}/`)
  if (output) await page.screenshot({ path: path.join(output, 'wall-link-only.png'), fullPage: true })
  const staff = await request.get(`${admin}/api/admin/queue`)
  assert.ok([401, 403].includes(staff.status()), 'Admin remains authenticated/network restricted')
  console.log('PASS: three-role boundaries, link-only wall/legacy redirect, zero idle API polling, guarded direct POST, themes, credits, and 1440/390/340px layouts.')
} finally {
  await browser.close()
}
