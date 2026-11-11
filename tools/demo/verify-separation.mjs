import assert from 'node:assert/strict'
import { randomUUID } from 'node:crypto'
import { chromium } from 'playwright-core'
import { submissionHeaders } from './submission.mjs'

const PUBLIC = process.env.EVENTWALL_URL ?? 'http://127.0.0.1:5080'
const ADMIN = process.env.EVENTWALL_ADMIN_URL ?? 'http://127.0.0.1:5081'
const SUBMISSION = process.env.EVENTWALL_SUBMISSION_URL ?? 'http://127.0.0.1:5082'
const browser = await chromium.launch({
  ...(process.env.EDGE_PATH ? { executablePath: process.env.EDGE_PATH } : { channel: 'msedge' }),
  headless: true,
})
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
  const request = context.request
  for (const path of ['/session-status', '/login', '/csrf', '/queue', '/sources', '/analytics', '/export.json', '/settings', '/export.csv', '/team', '/team/00000000-0000-0000-0000-000000000001/code', '/team/00000000-0000-0000-0000-000000000001/status', '/posts/00000000-0000-0000-0000-000000000001/approve', '/posts/00000000-0000-0000-0000-000000000001/block-author']) {
    for (const method of ['GET', 'POST', 'HEAD', 'OPTIONS']) {
      const response = await request.fetch(`${PUBLIC}/api/admin${path}`, { method, data: method === 'POST' ? {} : undefined })
      assert.equal(response.status(), 404, `Public ${method} ${path} must not exist`)
    }
  }
  assert.equal((await request.get(`${ADMIN}/api/admin/queue`)).status(), 401)
  assert.equal((await request.post(`${ADMIN}/api/admin/posts/00000000-0000-0000-0000-000000000001/block-author`, { data: { version: 0 } })).status(), 401)
  assert.equal((await request.post(`${ADMIN}/api/public/submissions`, { data: {} })).status(), 404)
  assert.equal((await request.get(`${ADMIN}/api/public/wall`)).status(), 404)
  assert.equal((await request.post(`${PUBLIC}/api/public/submissions`, { data: {} })).status(), 404)
  assert.equal((await request.get(`${PUBLIC}/api/public/submission-config`)).status(), 404)
  assert.equal((await request.get(`${SUBMISSION}/api/public/wall`)).status(), 404)
  assert.equal((await request.get(`${SUBMISSION}/api/admin/queue`)).status(), 404)

  const page = await context.newPage()
  const assets = new Set()
  page.on('response', response => {
    if (new URL(response.url()).pathname.endsWith('.js')) assets.add(response.url())
  })
  await page.goto(`${PUBLIC}/?view=staff`)
  await page.getByRole('heading', { name: /the room is/i }).waitFor()
  assert.equal(await page.getByRole('button', { name: 'Staff' }).count(), 0)
  assert.equal(await page.getByLabel('Access code').count(), 0)
  assert.ok(assets.size > 0)
  for (const asset of assets) {
    const code = await (await request.get(asset)).text()
    assert.doesNotMatch(code, /\/api\/admin|Shared staff passcode|Open staff console/, `Admin code leaked into ${asset}`)
  }
  const document = await (await request.get(PUBLIC)).text()
  assert.match(document, /eventwall-role" content="public"/)
  assert.match(await (await request.get(ADMIN)).text(), /eventwall-role" content="admin"/)
  const imagePost = (await (await request.get(`${PUBLIC}/api/public/wall`)).json()).items.find(post => post.images?.length === 4)
  assert.ok(imagePost, 'Demo dataset must include a live four-image post')
  const imageCard = page.locator('.post-card').filter({ hasText: imagePost.text })
  await imageCard.locator('.post-images img').first().waitFor()
  await imageCard.getByRole('button', { name: `Expand post by ${imagePost.authorName}` }).click()
  const expandedImages = page.getByRole('dialog', { name: 'Expanded post' }).locator('.post-images img')
  assert.equal(await expandedImages.count(), 4)
  await expandedImages.first().scrollIntoViewIfNeeded()
  await page.waitForFunction(() => [...document.querySelectorAll('.post-modal .post-images img')].every(image => image.complete && image.naturalWidth > 0))
  await page.getByRole('button', { name: 'Close expanded post' }).click()

  const text = `Public isolation rehearsal ${randomUUID()} <img src=x onerror=alert(1)>`
  const protectedHeaders = await submissionHeaders(request, SUBMISSION, randomUUID())
  const submit = (data, key = randomUUID()) => request.post(`${SUBMISSION}/api/public/submissions`, {
    data, headers: { ...protectedHeaders, 'Idempotency-Key': key },
  })
  const key = randomUUID()
  assert.equal((await submit({ text, displayName: 'Isolation tester' }, key)).status(), 202)
  const limited = await submit({ text, displayName: 'Isolation tester' }, key)
  assert.equal(limited.status(), 429)
  assert.ok(Number(limited.headers()['retry-after']) > 0)
  assert.ok(Number(limited.headers()['retry-after']) <= 180)
  assert.ok(!(await (await request.get(`${PUBLIC}/api/public/wall`)).json()).items.some(post => post.text === text))

  await page.goto(ADMIN)
  assert.ok(process.env.STAFF_PASSCODE, 'Set STAFF_PASSCODE to your local demo passcode.')
  await page.getByLabel('Access code').fill(process.env.STAFF_PASSCODE)
  await page.getByRole('button', { name: /Open staff console/ }).click()
  await page.getByRole('heading', { name: /keep the signal/i }).waitFor()
  await page.locator('.queue-item .post-images img').first().waitFor()
  for (const asset of assets) {
    if (new URL(asset).origin === new URL(ADMIN).origin) {
      assert.equal((await request.get(`${PUBLIC}${new URL(asset).pathname}`)).status(), 404,
        'Admin JavaScript assets must not be served by the public deployment')
    }
  }
  const queue = await (await request.get(`${ADMIN}/api/admin/queue?search=${encodeURIComponent(text)}`)).json()
  assert.equal(queue.length, 1, 'Rate-limited retries must not create duplicates')
  const post = queue[0]
  assert.equal((await request.post(`${ADMIN}/api/admin/posts/${post.id}/approve`, { data: { version: post.version } })).status(), 400)
  const { requestToken } = await (await request.get(`${ADMIN}/api/admin/csrf`)).json()
  const act = (action, version) => request.post(`${ADMIN}/api/admin/posts/${post.id}/${action}`, {
    data: { version }, headers: { 'X-CSRF-TOKEN': requestToken },
  })
  assert.equal((await act('approve', post.version)).status(), 200)
  const wall = await (await request.get(`${PUBLIC}/api/public/wall`)).json()
  assert.ok(wall.items.some(item => item.id === post.id))
  await page.goto(PUBLIC)
  await page.getByText(text, { exact: true }).waitFor()
  assert.equal(await page.locator('.post-text img').count(), 0, 'Submitted HTML must render as text')
  const published = await (await request.get(`${ADMIN}/api/admin/queue?search=${encodeURIComponent(text)}`)).json()
  assert.equal((await act('withdraw', published[0].version)).status(), 200)
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
  await page.getByText(text, { exact: true }).waitFor({ state: 'detached', timeout: 6000 })
  assert.equal((await request.get(`${PUBLIC}/api/admin/queue`)).status(), 404, 'Staff cookies must not enable admin on the public host')

  await page.goto(ADMIN)
  await page.getByRole('heading', { name: /keep the signal/i }).waitFor()
  await page.getByRole('button', { name: 'Grid', exact: true }).click()
  const grid = await page.locator('.queue-grid').evaluate(element => ({
    columns: getComputedStyle(element).gridTemplateColumns.split(' ').length,
    postFont: parseFloat(getComputedStyle(element.querySelector('.queue-item-main > p')).fontSize),
    buttonFont: parseFloat(getComputedStyle(element.querySelector('.button')).fontSize),
  }))
  assert.ok(grid.columns >= 2, 'Desktop grid must display multiple columns')
  assert.ok(grid.postFont >= 19 && grid.buttonFont >= 15, 'Staff post/action text must be larger')
  const candidate = page.locator('.queue-item').filter({ has: page.getByRole('button', { name: 'Block user', exact: true, disabled: false }) }).first()
  const candidateText = await candidate.locator('.queue-item-main > p').textContent()
  const selectedPost = (await (await request.get(`${ADMIN}/api/admin/queue?status=Pending`)).json()).find(item => item.text === candidateText)
  assert.ok(selectedPost?.canBlockAuthor)
  assert.equal((await request.post(`${ADMIN}/api/admin/posts/${selectedPost.id}/block-author`, { data: { version: selectedPost.version } })).status(), 400)
  await candidate.getByRole('button', { name: 'Block user', exact: true }).click()
  await page.getByRole('dialog', { name: 'Block this user?' }).waitFor()
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()
  assert.equal((await (await request.get(`${ADMIN}/api/admin/queue?status=Pending`)).json()).find(item => item.id === selectedPost.id).authorBlocked, false)
  await candidate.getByRole('button', { name: 'Block user', exact: true }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Block user', exact: true }).click()
  await page.getByText('Account blocked for this event. Future posts will be auto-rejected; existing posts are unchanged.', { exact: true }).waitFor()
  const unchanged = (await (await request.get(`${ADMIN}/api/admin/queue?status=Pending`)).json()).find(item => item.id === selectedPost.id)
  assert.equal(unchanged.status, selectedPost.status)
  assert.equal(unchanged.version, selectedPost.version)
  assert.equal(unchanged.authorBlocked, true)
  await page.getByRole('tab', { name: 'Approved', exact: true }).click()
  await page.getByRole('heading', { name: 'Approved and live' }).waitFor()
  const liveCard = page.locator('.queue-item').first()
  await liveCard.waitFor()
  const liveText = await liveCard.locator('.queue-item-main > p').textContent()
  const livePost = (await (await request.get(`${ADMIN}/api/admin/queue?status=Live`)).json()).find(item => item.text === liveText)
  await liveCard.getByRole('button', { name: 'Withdraw', exact: true }).click()
  await page.getByText('Post withdrawn from the live wall.', { exact: true }).waitFor()
  assert.equal((await request.get(`${PUBLIC}/api/public/wall/items/${livePost.id}`)).status(), 404)

  await page.getByRole('button', { name: 'Portal open', exact: true }).click()
  await page.getByRole('dialog', { name: 'Pause portal submissions?' }).waitFor()
  await page.keyboard.press('Escape')
  assert.equal((await (await request.get(`${PUBLIC}/api/public/event`)).json()).submissionsOpen, true)
  await page.getByRole('button', { name: 'Portal open', exact: true }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Pause submissions' }).click()
  await page.getByRole('button', { name: 'Portal paused', exact: true }).waitFor()
  assert.equal((await (await request.get(`${PUBLIC}/api/public/event`)).json()).submissionsOpen, false)
  await page.getByRole('button', { name: 'Portal paused', exact: true }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Open submissions' }).click()
  await page.getByRole('button', { name: 'Portal open', exact: true }).waitFor()
  assert.equal((await (await request.get(`${PUBLIC}/api/public/event`)).json()).submissionsOpen, true)
  const exported = await (await request.get(`${ADMIN}/api/admin/export.json`)).json()
  assert.equal(exported.posts.find(post => post.id === imagePost.id).images.length, 4)
  const csv = await (await request.get(`${ADMIN}/api/admin/export.csv`)).text()
  assert.match(csv.split('\r\n')[0], /imagesJson/)
  await page.setViewportSize({ width: 390, height: 844 })
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'Staff grid must not overflow on mobile')
  console.log('PASS: origin/bundle isolation, authentication/CSRF, safe submissions, approval/withdrawal, larger staff fonts, responsive grid, Approved tab, confirmed account block preserving existing posts, confirmed portal open/pause and cancellation.')
} catch (error) {
  throw new Error(error.message.split('\n')[0])
} finally {
  await browser.close()
}
