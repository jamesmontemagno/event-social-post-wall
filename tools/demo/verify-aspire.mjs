import assert from 'node:assert/strict'
import { chromium } from 'playwright-core'

const publicUrl = 'http://127.0.0.1:5173'
const adminUrl = 'http://127.0.0.1:5174'
const submissionUrl = 'http://127.0.0.1:5082'
assert.ok(process.env.STAFF_PASSCODE, 'Supply the local AppHost staff parameter through STAFF_PASSCODE')

const browser = await chromium.launch({ channel: 'msedge', headless: true })
try {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
  const request = context.request
  const dashboard = await context.newPage()
  await dashboard.goto('http://localhost:15888')
  assert.equal(new URL(dashboard.url()).pathname, '/login', 'The dashboard must still require its token')
  await dashboard.close()
  for (const origin of [publicUrl, adminUrl, submissionUrl]) {
    assert.equal((await request.get(`${origin}/api/health`)).status(), 200)
    const notices = await request.get(`${origin}/THIRD-PARTY-NOTICES.txt`)
    assert.equal(notices.status(), 200)
    const text = await notices.text()
    for (const expected of ['React', 'react-dom', 'scheduler', 'typescript', 'Aspire.Hosting', 'SQLitePCLRaw',
      'MIT', 'Apache', 'MICROSOFT SOFTWARE LICENSE TERMS', 'GitHub Copilot']) {
      assert.ok(text.includes(expected), `Notices must retain ${expected}`)
    }
  }
  assert.equal((await request.get(`${publicUrl}/api/admin/queue`)).status(), 404)
  assert.equal((await request.get(`${adminUrl}/api/public/wall`)).status(), 404)
  assert.equal((await request.get(`${adminUrl}/api/admin/queue`)).status(), 401)
  assert.equal((await request.get('http://127.0.0.1:5080/api/admin/queue')).status(), 404)
  assert.equal((await request.get('http://127.0.0.1:5081/api/public/wall')).status(), 404)
  assert.equal((await request.post(`${publicUrl}/api/public/submissions`, { data: {} })).status(), 404)
  assert.equal((await request.get(`${submissionUrl}/api/public/wall`)).status(), 404)
  assert.equal((await request.get('http://127.0.0.1:5082/api/admin/queue')).status(), 404)

  const event = await (await request.get(`${publicUrl}/api/public/event`)).json()
  assert.equal(event.demoMode, true)
  const wall = await (await request.get(`${publicUrl}/api/public/wall`)).json()
  assert.ok(wall.items.some(post => post.images?.length === 4))

  const page = await context.newPage()
  const loadedStaffModules = []
  page.on('request', request => {
    if (/\/src\/(AdminApp|Staff|Admin\.css)/.test(request.url())) loadedStaffModules.push(request.url())
  })
  await page.goto(`${publicUrl}/?view=staff`)
  await page.getByRole('heading', { name: /the room is/i }).waitFor()
  assert.equal(await page.getByLabel('Access code').count(), 0)
  assert.equal(loadedStaffModules.length, 0)
  await page.locator('.post-images img').first().scrollIntoViewIfNeeded()
  await page.waitForFunction(() => [...document.querySelectorAll('.post-images img')].some(image => image.complete && image.naturalWidth > 0))
  for (const name of ['ASP.NET Core', 'Blazor Server', 'React', 'TypeScript', 'Aspire', 'GitHub Copilot']) {
    assert.match(await page.getByRole('link', { name, exact: true }).getAttribute('href'), /^https:\/\//)
  }
  assert.equal(await page.getByRole('link', { name: 'Submit a question', exact: true }).getAttribute('href'), `${submissionUrl}/`)
  await page.goto(submissionUrl)
  await page.getByLabel('Your question', { exact: true }).waitFor()
  await page.getByRole('contentinfo', { name: 'Technology and acknowledgements' }).waitFor()
  assert.equal(await page.getByRole('link', { name: 'Third-party licenses and notices' }).getAttribute('href'), '/THIRD-PARTY-NOTICES.txt')
  await page.setViewportSize({ width: 390, height: 844 })
  await page.getByRole('contentinfo').scrollIntoViewIfNeeded()
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'Footer must not cause mobile overflow')

  const staffPage = await context.newPage()
  await staffPage.goto(adminUrl)
  const wallLink = staffPage.getByRole('link', { name: 'Open wall (opens in a new tab)' })
  const questionsLink = staffPage.getByRole('link', { name: 'Open questions portal (opens in a new tab)' })
  for (const width of [340, 390, 760, 1024, 1440]) {
    await staffPage.setViewportSize({ width, height: 844 })
    for (const link of [wallLink, questionsLink]) {
      const bounds = await link.boundingBox()
      assert.ok(bounds && bounds.x >= 0 && bounds.x + bounds.width <= width && bounds.height >= 44,
        `Admin links must fit and remain touch-friendly at ${width}px`)
    }
    assert.ok(await staffPage.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), `Admin must not overflow at ${width}px`)
  }
  await staffPage.setViewportSize({ width: 1440, height: 1000 })
  const [openedWall] = await Promise.all([context.waitForEvent('page'), wallLink.click()])
  await openedWall.waitForURL(`${publicUrl}/`)
  await openedWall.getByRole('heading', { name: /the room is/i }).waitFor()
  await openedWall.close()
  const [openedQuestions] = await Promise.all([context.waitForEvent('page'), questionsLink.click()])
  await openedQuestions.waitForURL(`${submissionUrl}/`)
  await openedQuestions.getByLabel('Your question', { exact: true }).waitFor()
  await openedQuestions.close()
  await staffPage.getByLabel('Access code').fill(process.env.STAFF_PASSCODE)
  await staffPage.getByRole('button', { name: /open staff console/i }).click()
  await staffPage.getByRole('heading', { name: /keep the signal/i }).waitFor()
  await staffPage.getByRole('button', { name: 'Grid', exact: true }).click()
  await staffPage.getByRole('tab', { name: 'Approved', exact: true }).click()
  await staffPage.locator('.queue-item').first().waitFor()
  assert.ok((await (await request.get(`${adminUrl}/api/admin/queue?status=Live`)).json()).length > 0)
  const question = `How can I build an app with the latest .NET features? ${crypto.randomUUID()}`
  await page.getByLabel('Your question', { exact: true }).fill(question)
  await page.getByRole('button', { name: /send to the team/i }).click()
  await page.getByRole('heading', { name: 'Safely in the queue.' }).waitFor()
  const submitted = await (await request.get(`${adminUrl}/api/admin/queue?search=${encodeURIComponent(question)}`)).json()
  assert.equal(submitted.length, 1, 'Same-origin Blazor submission must reach the staff queue')
  assert.equal(submitted[0].status, 'Pending')
  console.log('Aspire proof passed: five ready resources, admin site links, same-origin Blazor submission to the pending queue, isolated APIs, staff login, demo photos, and responsive layouts.')
} finally {
  await browser.close()
}
