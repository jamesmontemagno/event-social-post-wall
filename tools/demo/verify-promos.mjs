import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { mkdir } from 'node:fs/promises'
import { join } from 'node:path'
import { chromium } from 'playwright-core'

const wallUrl = process.env.EVENTWALL_URL ?? 'http://127.0.0.1:5273'
const adminUrl = process.env.EVENTWALL_ADMIN_URL ?? 'http://127.0.0.1:5274'
const require = createRequire(import.meta.url)
const browser = await chromium.launch({ channel: 'msedge', headless: true })
const urls = ['https://example.test/survey', 'https://example.test/café/开发者?tag=dotnet', 'https://example.test/' + 'a'.repeat(2028)]
let promos = urls.map((url, index) => ({ id: String(index + 1), title: ['Developer survey', 'Community day', 'After-hours sessions'][index], description: 'Join the .NET community and find out more.', url, version: 1, createdAtUtc: new Date().toISOString() }))
promos.push({ id: '4', title: 'Thank you!', description: 'Thank you for joining Community Event!', url: null, version: 1, createdAtUtc: new Date().toISOString() })
const post = { id: 'question', text: 'How can I get started with .NET?', source: 'Portal', authorName: 'Community member', receivedAtUtc: new Date().toISOString() }

async function decode(page, svg) {
  await page.addScriptTag({ path: require.resolve('jsqr/dist/jsQR.js') })
  return svg.evaluate(async element => {
    const image = new Image()
    image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(new XMLSerializer().serializeToString(element))}`
    await image.decode()
    const canvas = document.createElement('canvas')
    canvas.width = canvas.height = element.viewBox.baseVal.width * 4
    const context = canvas.getContext('2d')
    context.drawImage(image, 0, 0, canvas.width, canvas.height)
    return window.jsQR(context.getImageData(0, 0, canvas.width, canvas.height).data, canvas.width, canvas.height)?.data
  })
}

async function capture(page, name) {
  const directory = process.env.EVENTWALL_SCREENSHOT_DIR
  if (!directory) return
  await mkdir(directory, { recursive: true })
  await page.screenshot({ path: join(directory, `${name}.png`), fullPage: true })
}

try {
  for (const theme of ['dark', 'light']) {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' })
    await context.addInitScript(value => localStorage.setItem('eventwall:theme', value), theme)
    const page = await context.newPage()
    const errors = []
    page.on('pageerror', error => errors.push(error.message))
    await page.route('**/api/public/**', route => {
      const path = new URL(route.request().url()).pathname
      return route.fulfill({ json: path.endsWith('/event')
        ? { slug: 'promo-test', name: 'Community Event', submissionSiteUrl: 'https://questions.example.test/' }
        : path.endsWith('/changes')
          ? { revision: 1, newCount: 0, liveKnownIds: [post.id], promos }
          : { revision: 1, newCount: 0, items: [post], promos } })
    })
    await page.goto(wallUrl)
    const toggle = page.getByRole('button', { name: 'Show promotional blurbs' })
    await toggle.waitFor()
    assert.equal(await toggle.getAttribute('aria-pressed'), 'false')
    assert.equal(await page.getByText('Developer survey', { exact: true }).count(), 0)
    await page.getByRole('button', { name: 'Pin post', exact: true }).click()
    await capture(page, `promos-before-${theme}`)
    await toggle.focus()
    await page.keyboard.press('Enter')
    const region = page.getByRole('region', { name: 'Promotional blurbs', exact: true })
    await region.waitFor()
    assert.equal(await region.getByRole('button').count(), 4)
    assert.equal(await region.getByRole('img').count(), 0)
    assert.equal(await page.getByText(promos[0].description, { exact: true }).count(), 0)
    for (const promo of promos) {
      await region.getByRole('button', { name: promo.title, exact: true }).click()
      const dialog = page.getByRole('dialog', { name: `Promo: ${promo.title}`, exact: true })
      if (promo.url) assert.equal(await decode(page, dialog.getByRole('img', { name: `QR code for ${promo.url}`, exact: true })), promo.url)
      else assert.equal(await dialog.getByRole('img').count(), 0)
      await page.keyboard.press('Escape')
    }
    for (const mode of ['normal', 'immersive']) {
      if (mode === 'immersive') await page.getByRole('button', { name: 'Immerse mode', exact: true }).click()
      for (const width of [1440, 340]) {
        await page.setViewportSize({ width, height: 1000 })
        assert.ok(await region.evaluate(element => {
          const pinned = document.querySelector('.pinned-section')
          const feed = document.querySelector('.feed-section')
          const cards = [...element.querySelectorAll('.promo-title-button')]
          const main = element.closest('main')
          return !!(pinned.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING) &&
            !!(element.compareDocumentPosition(feed) & Node.DOCUMENT_POSITION_FOLLOWING) &&
            main.contains(document.querySelector('.promo-toggle')) &&
            cards.every(card => card.scrollWidth <= card.clientWidth && card.getBoundingClientRect().right <= innerWidth)
        }), `${theme}/${mode}/${width}: placement, fullscreen containment and wrapping`)
        await toggle.focus()
        await page.waitForFunction(() => getComputedStyle(document.querySelector('.promo-toggle')).opacity === '1')
        assert.ok(await toggle.evaluate(element => getComputedStyle(element).opacity === '1'), 'Keyboard focus reveals controls')
        if (width === 340 && mode === 'immersive') {
          assert.ok(await toggle.evaluate(element => {
            const toggle = element.getBoundingClientRect()
            const controls = document.querySelector('.immersive-controls').getBoundingClientRect()
            return toggle.top >= controls.bottom
          }), 'Mobile controls must not overlap')
        }
        if (width === 1440) await capture(page, `promos-after-${theme}-${mode}`)
        const title = region.getByRole('button', { name: 'Developer survey', exact: true })
        await title.focus()
        await page.keyboard.press('Enter')
        const dialog = page.getByRole('dialog', { name: 'Promo: Developer survey', exact: true })
        await dialog.waitFor()
        assert.ok(await dialog.locator('.promo-modal').evaluate(element => {
          const box = element.getBoundingClientRect()
          return box.left >= 0 && box.right <= innerWidth && box.top >= 0 && box.bottom <= innerHeight &&
            element.scrollWidth <= element.clientWidth && document.fullscreenElement?.contains(element) !== false
        }), `${theme}/${mode}/${width}: promo popup must fit the viewport and fullscreen`)
        if (width === 1440) await capture(page, `promo-popup-${theme}-${mode}`)
        await page.keyboard.press('Escape')
        await dialog.waitFor({ state: 'detached' })
        assert.ok(await title.evaluate(element => document.activeElement === element), 'Closing returns focus to the promo title')
      }
    }
    await page.reload()
    await region.waitFor()
    assert.equal(await toggle.getAttribute('aria-pressed'), 'true')
    assert.equal(errors.length, 0, errors.join('\n'))
    await context.close()
  }

  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } })
  await page.route('**/api/**', async route => {
    const request = route.request()
    const path = new URL(request.url()).pathname
    let data = {}
    if (path.endsWith('/event')) data = { slug: 'promo-test', name: 'Community Event', wallSiteUrl: wallUrl, submissionSiteUrl: 'https://questions.example.test/' }
    if (path.endsWith('/session-status')) data = { authenticated: true, isAdministrator: false, username: 'Moderator' }
    if (path.endsWith('/csrf')) data = { requestToken: 'smoke-token' }
    if (path.endsWith('/queue')) data = []
    if (path === '/api/admin/promos') {
      if (request.method() === 'POST') {
        assert.equal(request.headers()['x-csrf-token'], 'smoke-token')
        const body = request.postDataJSON()
        data = { ...body, id: 'created', version: 1, createdAtUtc: new Date().toISOString() }
        promos = [data, ...promos]
      } else data = promos
    }
    if (path.endsWith('/created/delete')) {
      promos = promos.filter(promo => promo.id !== 'created')
      return route.fulfill({ status: 204 })
    }
    return route.fulfill({ json: data })
  })
  await page.goto(adminUrl)
  await page.getByRole('tab', { name: 'Promos', exact: true }).click()
  await page.getByRole('textbox', { name: 'Title', exact: true }).fill('After-hours')
  await page.getByRole('textbox', { name: 'Description', exact: true }).fill('Join our after-hours session')
  await page.getByRole('textbox', { name: 'URL (optional)', exact: true }).fill(urls[0])
  await page.getByRole('button', { name: 'Create promo', exact: true }).click()
  const saved = page.getByRole('region', { name: 'Saved promo: After-hours', exact: true })
  await saved.waitFor()
  await capture(page, 'promos-staff')
  for (const theme of ['light', 'dark']) {
    await page.evaluate(value => { document.documentElement.dataset.theme = value }, theme)
    for (const width of [1440, 340]) {
      await page.setViewportSize({ width, height: 1000 })
      assert.ok(await page.locator('.promo-editor').evaluate(element => element.scrollWidth <= element.clientWidth), 'Staff editor must wrap')
      assert.ok(await page.getByRole('textbox', { name: 'Description', exact: true }).evaluate(element => {
        const style = getComputedStyle(element)
        return style.color !== style.backgroundColor && element.getBoundingClientRect().height >= 44
      }), 'Staff fields must remain readable and touch friendly')
    }
  }
  await saved.getByRole('button', { name: 'Delete promo', exact: true }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Delete promo', exact: true }).click()
  await saved.waitFor({ state: 'detached' })
  console.log('Promo smoke passed: title-only cards, popup details, exact QR decoding (ordinary, Unicode, 2048-character URL), dark/light, desktop/mobile, normal/fullscreen, keyboard/focus restoration, persistence, moderator creation/deletion.')
} finally {
  await browser.close()
}
