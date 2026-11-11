import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { chromium } from 'playwright-core'
import fs from 'node:fs/promises'

function required(name) {
  const value = process.env[name]
  if (!value) throw new Error(`Set ${name} in the verification process environment.`)
  return value
}
const PUBLIC = required('EVENTWALL_URL')
const ADMIN = required('EVENTWALL_ADMIN_URL')
const SUBMISSION = required('EVENTWALL_SUBMISSION_URL')
const passcode = required('STAFF_PASSCODE')
assert.notEqual(new URL(PUBLIC).origin, new URL(ADMIN).origin)
const browser = await chromium.launch({ channel: 'msedge', headless: true })
let request
let publicRequest
let token
let submitted
let originalOpen
async function write(path, data) {
  const response = await request.post(`${ADMIN}${path}`, {
    data,
    headers: { 'X-CSRF-TOKEN': token },
  })
  assert.ok(response.ok(), `${path}: HTTP ${response.status()}`)
  return response.status() === 204 ? null : response.json()
}
try {
  const anonymous = await browser.newContext()
  publicRequest = anonymous.request
  assert.equal((await anonymous.request.get(PUBLIC)).status(), 200, 'The public wall must not require a password, including in staging')
  const anonymousEvent = await (await anonymous.request.get(`${PUBLIC}/api/public/event`)).json()
  assert.equal(anonymousEvent.demoMode, true)
  originalOpen = anonymousEvent.submissionsOpen
  assert.equal((await anonymous.request.get(`${PUBLIC}/api/admin/queue`)).status(), 404)
  assert.equal((await anonymous.request.get(ADMIN)).status(), 200, 'Allowed-network admin UI must show staff sign-in without a staging password')
  assert.equal((await anonymous.request.get(`${ADMIN}/api/admin/queue`)).status(), 401, 'Staff APIs must still require the staff session')
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
  })
  request = context.request
  for (const [site, role] of [[PUBLIC, 'public'], [ADMIN, 'admin'], [SUBMISSION, 'submission']]) {
    const root = await request.get(site)
    assert.equal(root.status(), 200)
    assert.equal(root.headers()['www-authenticate'], undefined, 'Neither website may introduce a staging Basic-auth challenge')
    assert.match(await root.text(), new RegExp(`eventwall-role" content="${role}"`))
    assert.match(root.headers()['x-robots-tag'], /noindex/)
    assert.equal(root.headers()['x-content-type-options'], 'nosniff')
  }
  const info = await (await publicRequest.get(`${PUBLIC}/api/public/event`)).json()
  assert.equal(info.demoMode, true)
  assert.equal(typeof info.submissionsOpen, 'boolean')
  const initialWall = await (await publicRequest.get(`${PUBLIC}/api/public/wall`)).json()
  assert.ok(initialWall.items.length >= 10, 'Synthetic live posts must be seeded')
  assert.ok(initialWall.items.some(post => post.images?.length === 4), 'Existing demo fixtures must gain photo metadata after upgrading')
  for (const path of ['session-status', 'queue', 'sources', 'csrf', 'analytics', 'export.csv', 'team']) {
    assert.equal((await request.get(`${PUBLIC}/api/admin/${path}`)).status(), 404)
  }
  assert.equal((await request.post(`${PUBLIC}/api/admin/login`, { data: { passcode } })).status(), 404)
  assert.equal((await request.get(`${ADMIN}/api/admin/queue`)).status(), 401)
  assert.equal((await request.get(`${ADMIN}/api/public/wall`)).status(), 404)
  assert.equal((await request.post(`${ADMIN}/api/public/submissions`, { data: {} })).status(), 404)

  const page = await context.newPage()
  const publicAssets = new Set()
  const adminAssets = new Set()
  page.on('response', response => {
    const url = new URL(response.url())
    if (url.pathname.endsWith('.js')) {
      if (url.origin === new URL(PUBLIC).origin) publicAssets.add(response.url())
      else if (url.origin === new URL(ADMIN).origin) adminAssets.add(url.pathname)
    }
  })
  await page.goto(`${PUBLIC}/?view=staff`)
  await page.getByRole('heading', { name: /the room is/i }).waitFor()
  await page.getByRole('button', { name: 'Light theme' }).click()
  assert.equal(await page.locator('html').getAttribute('data-theme'), 'light')
  assert.equal(await page.locator('html').evaluate(element => getComputedStyle(element).getPropertyValue('--accent').trim()), '#512bd4')
  for (const name of ['ASP.NET Core', 'Blazor Server', 'React', 'TypeScript', 'Aspire', 'GitHub Copilot']) {
    assert.ok(await page.getByRole('link', { name, exact: true }).isVisible())
  }
  const notices = await request.get(`${PUBLIC}/THIRD-PARTY-NOTICES.txt`)
  assert.equal(notices.status(), 200)
  assert.match(await notices.text(), /MICROSOFT SOFTWARE LICENSE TERMS/)
  await page.locator('.post-images img').first().scrollIntoViewIfNeeded()
  await page.waitForFunction(() => [...document.querySelectorAll('.post-images img')].some(image => image.complete && image.naturalWidth > 0))
  await page.getByRole('button', { name: /expand post by/i }).first().click()
  await page.getByRole('dialog', { name: 'Expanded post' }).waitFor()
  await page.getByRole('button', { name: 'Close expanded post' }).click()
  assert.equal(await page.getByRole('button', { name: 'Staff' }).count(), 0)
  for (const url of publicAssets) {
    assert.doesNotMatch(await (await request.get(url)).text(), /\/api\/admin|Shared staff passcode/)
  }
  await page.goto(`${PUBLIC}/?view=submit`)
  await page.getByLabel('Your question', { exact: true }).waitFor()
  assert.equal(new URL(page.url()).origin, new URL(SUBMISSION).origin)
  assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark', 'Questions use an independent origin preference')
  await page.getByRole('link', { name: 'Third-party licenses and notices' }).waitFor()
  const text = `Azure rehearsal ${randomUUID()} <script>alert('never execute')</script>`
  assert.equal((await publicRequest.post(`${PUBLIC}/api/public/submissions`, { data: { text } })).status(), 404)
  assert.equal((await publicRequest.get(`${SUBMISSION}/api/public/wall`)).status(), 404)

  await page.goto(ADMIN)
  assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark', 'Admin has an independent browser preference')
  await page.getByRole('button', { name: 'Light theme' }).click()
  await page.getByLabel('Access code').fill(passcode)
  await page.getByRole('button', { name: /Open staff console/ }).click()
  await page.getByRole('heading', { name: /keep the signal/i }).waitFor()
  await page.getByRole('button', { name: 'Grid', exact: true }).click()
  await page.locator('.queue-grid .queue-item').first().waitFor()
  const postFont = await page.locator('.queue-item-main > p').first().evaluate(element => getComputedStyle(element).fontSize)
  assert.equal(postFont, '19px')
  await page.getByRole('tab', { name: 'Approved', exact: true }).click()
  await page.locator('.queue-item').first().waitFor()
  await page.getByRole('button', { name: /Portal (paused|open)/ }).click()
  await page.getByRole('dialog').waitFor()
  await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  assert.equal((await (await request.get(`${ADMIN}/api/admin/settings`)).json()).submissionsOpen, originalOpen)
  await page.getByRole('tab', { name: 'Review queue', exact: true }).click()
  const blockable = page.getByRole('button', { name: 'Block user', exact: true })
  if (await blockable.count() > 0) {
    await blockable.first().click()
    await page.getByRole('dialog', { name: 'Block this user?' }).waitFor()
    await page.getByRole('button', { name: 'Cancel', exact: true }).click()
  }
  await page.reload()
  await page.getByRole('heading', { name: /keep the signal/i }).waitFor()
  assert.equal(await page.locator('html').getAttribute('data-theme'), 'light', 'Staff theme persists across loads')
  for (const path of adminAssets) {
    assert.equal((await request.get(`${PUBLIC}${path}`)).status(), 404)
  }
  token = (await (await request.get(`${ADMIN}/api/admin/csrf`)).json()).requestToken
  const sources = await (await request.get(`${ADMIN}/api/admin/sources`)).json()
  assert.equal(sources.length, 4)
  assert.ok(sources.every(source => source.simulated))
  assert.equal((await request.post(`${ADMIN}/api/admin/settings/submissions`, { data: { open: true } })).status(), 400)
  await write('/api/admin/settings/submissions', { open: true })
  const questionPage = await anonymous.newPage()
  await questionPage.goto(SUBMISSION)
  await questionPage.getByLabel('Your question', { exact: true }).fill(text)
  await questionPage.getByLabel('Name (optional)', { exact: true }).fill('Azure rehearsal')
  const sending = questionPage.waitForRequest(request =>
    new URL(request.url()).pathname === '/api/public/submissions' && request.method() === 'POST')
  await questionPage.getByRole('button', { name: /send to the team/i }).click()
  const sentHeaders = await (await sending).allHeaders()
  await questionPage.getByRole('heading', { name: 'Safely in the queue.' }).waitFor()
  assert.equal((await publicRequest.post(`${SUBMISSION}/api/public/submissions`, {
    headers: {
      'Idempotency-Key': sentHeaders['idempotency-key'],
      'X-Submission-Token': sentHeaders['x-submission-token'],
    },
    data: { text, displayName: 'Azure rehearsal' },
  })).status(), 429)
  const queueUrl = `${ADMIN}/api/admin/queue?search=${encodeURIComponent(text)}`
  const queue = await (await request.get(queueUrl)).json()
  assert.equal(queue.length, 1)
  submitted = queue[0]
  assert.equal(submitted.status, 'Pending')
  assert.ok(!(await (await publicRequest.get(`${PUBLIC}/api/public/wall`)).json()).items.some(post => post.id === submitted.id))
  await write(`/api/admin/posts/${submitted.id}/approve`, { version: submitted.version })
  await page.goto(PUBLIC)
  await page.getByText(text, { exact: true }).waitFor()
  assert.equal(await page.locator('.post-text script').count(), 0)
  const published = (await (await request.get(queueUrl)).json())[0]
  await write(`/api/admin/posts/${published.id}/withdraw`, { version: published.version })
  submitted = null
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
  await page.getByText(text, { exact: true }).waitFor({ state: 'detached', timeout: 10000 })
  await write('/api/admin/settings/submissions', { open: false })
  await page.goto(`${PUBLIC}/?view=submit`)
  await page.getByLabel('Your question', { exact: true }).fill('Can I still send a question to the hosts?')
  await page.getByRole('button', { name: /send to the team/i }).click()
  await page.getByRole('alert').waitFor()
  await page.goto(PUBLIC)
  await page.getByRole('heading', { name: /the room is/i }).waitFor()
  if (process.env.DEMO_OUTPUT) {
    await fs.mkdir(process.env.DEMO_OUTPUT, { recursive: true })
    await page.screenshot({ path: `${process.env.DEMO_OUTPUT}/azure-public-wall.png`, fullPage: true })
    await page.goto(ADMIN)
    await page.getByRole('heading', { name: /keep the signal/i }).waitFor()
    await page.screenshot({ path: `${process.env.DEMO_OUTPUT}/azure-private-admin.png`, fullPage: true })
  }
  console.log('PASS: anonymous HTTPS wall/questions, browser submission to real SQL, staff-only authentication, noindex, isolated admin routes/assets, simulated feeds/photos, staff grid/Approved, confirmation cancellation, persisted purple themes, license notices, paused intake, idempotency, CSRF, text safety, approval and live withdrawal.')
} catch (error) {
  throw new Error(error.message.split('\n')[0])
} finally {
  try {
    if (request && token) {
      await write('/api/admin/settings/submissions', { open: originalOpen })
      if (submitted) {
        const queue = await (await request.get(`${ADMIN}/api/admin/queue?search=${encodeURIComponent(submitted.text)}`)).json()
        const post = queue.find(item => item.id === submitted.id)
        if (post?.status === 'Live') await write(`/api/admin/posts/${post.id}/withdraw`, { version: post.version })
      }
    }
  } finally {
    await browser.close()
  }
}
