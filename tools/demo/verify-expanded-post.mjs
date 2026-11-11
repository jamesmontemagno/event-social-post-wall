import assert from 'node:assert/strict'
import { chromium } from 'playwright-core'

const url = process.env.EVENTWALL_URL ?? 'http://127.0.0.1:5173'
const posts = [
  { id: 'short', authorName: 'Attendee', text: 'Great question!' },
  { id: 'long', authorName: 'Another attendee', text: 'How can we build reliable .NET applications? '.repeat(45) },
  { id: 'word', authorName: 'LongDisplayName'.repeat(8), text: 'Question'.repeat(250) },
  { id: 'photo', authorName: 'Photo attendee', text: '', images: [{ url: 'https://fixture.invalid/photo.svg', alt: 'Portrait photo' }] },
].map(post => ({ ...post, source: 'Portal', sourceUrl: 'https://fixture.invalid/original', receivedAtUtc: new Date().toISOString() }))

const browser = await chromium.launch({
  ...(process.env.EDGE_PATH ? { executablePath: process.env.EDGE_PATH } : { channel: 'msedge' }),
  headless: true,
})
try {
  for (const theme of ['dark', 'light']) {
    const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, reducedMotion: 'reduce' })
    await context.addInitScript(theme => localStorage.setItem('eventwall:theme', theme), theme)
    const page = await context.newPage()
    await page.route('**/api/public/**', route => {
      const path = new URL(route.request().url()).pathname
      return route.fulfill({ json: path.endsWith('/event')
        ? { slug: 'layout-test', name: 'Community Event' }
        : path.endsWith('/changes')
          ? { revision: 1, newCount: 0, liveKnownIds: posts.map(post => post.id) }
          : { revision: 1, newCount: 0, items: posts } })
    })
    await page.route('https://fixture.invalid/photo.svg', route => route.fulfill({
      contentType: 'image/svg+xml',
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="600" height="900"><rect width="600" height="900" fill="#5728dc"/></svg>',
    }))
    await page.goto(url)
    const shortTrigger = page.getByRole('button', { name: 'Expand post by Attendee', exact: true })
    await shortTrigger.waitFor()
    await page.locator('.post-card').filter({ has: shortTrigger }).getByRole('button', { name: 'Pin post' }).click()

    for (const mode of ['normal', 'immersive']) {
      if (mode === 'immersive') {
        await page.setViewportSize({ width: 1440, height: 900 })
        await page.getByRole('button', { name: 'Immerse mode' }).click()
        assert.ok(await page.getByRole('main').evaluate(element => element.classList.contains('immersive-wall')))
      }
      for (const width of [1440, 340]) {
        await page.setViewportSize({ width, height: 900 })
        const sizes = new Map()
        for (const post of posts) {
          const label = `${theme}/${mode}/${width}/${post.id}`
          const trigger = page.getByRole('button', { name: `Expand post by ${post.authorName}`, exact: true })
          await trigger.focus()
          await page.keyboard.press('Enter')
          const dialog = page.getByRole('dialog', { name: 'Expanded post' })
          const modal = dialog.locator('.post-modal')
          await modal.waitFor()
          if (post.images) {
            await modal.getByRole('img', { name: 'Portrait photo' }).evaluate(image => image.decode())
          }
          const size = await modal.evaluate(element => {
            const box = element.getBoundingClientRect()
            const content = element.querySelector('.post-modal-content')
            const last = content.lastElementChild.getBoundingClientRect()
            const padding = parseFloat(getComputedStyle(element.closest('dialog')).paddingTop)
            return {
              width: box.width, height: box.height,
              inside: box.left >= padding - 1 && box.top >= padding - 1 && box.right <= innerWidth - padding + 1 && box.bottom <= innerHeight - padding + 1,
              horizontalOverflow: element.scrollWidth > element.clientWidth + 1,
              scrolls: element.scrollHeight > element.clientHeight + 1,
              bottomGap: box.bottom - last.bottom,
              bottomPadding: parseFloat(getComputedStyle(content).paddingBottom),
            }
          })
          sizes.set(post.id, size)
          assert.ok(size.inside, `${label}: modal must stay within the viewport`)
          assert.ok(size.width <= 1101, `${label}: modal must respect the 1100px reading-width limit`)
          assert.ok(!size.horizontalOverflow, `${label}: content must wrap without horizontal scrolling`)
          if (post.id === 'short') {
            assert.ok(await modal.getByRole('button', { name: 'Unpin post' }).isVisible(), `${label}: pinned controls must remain available`)
            assert.ok(size.height < 450, `${label}: short posts must not leave a tall empty box`)
            assert.ok(Math.abs(size.bottomGap - size.bottomPadding) <= 2, `${label}: bottom space must be content padding only`)
            if (width === 1440) assert.ok(size.width < 1080, `${label}: short posts must not fill the screen width`)
          }
          if (post.id === 'long' || post.id === 'word') {
            assert.ok(size.scrolls, `${label}: long content must scroll inside the bounded modal`)
            await modal.evaluate(element => { element.scrollTop = element.scrollHeight })
            assert.ok(await modal.evaluate(element => {
              const box = element.getBoundingClientRect()
              const close = element.querySelector('.modal-close').getBoundingClientRect()
              return close.top >= box.top && close.bottom <= box.bottom
            }), `${label}: Close must remain visible after scrolling`)
          }
          if (post.id === 'photo' && width === 1440) assert.ok(size.width >= 720, `${label}: photos must have enough room`)
          await modal.getByRole('button', { name: 'Close expanded post' }).click()
          await dialog.waitFor({ state: 'detached' })
          assert.ok(await trigger.evaluate(element => document.activeElement === element), `${label}: focus must return to the post`)
        }
        if (width === 1440) {
          assert.ok(sizes.get('long').width > sizes.get('short').width, 'Width must adapt to post content')
          assert.ok(sizes.get('long').height > sizes.get('short').height, 'Height must adapt to post content')
        }
      }
    }
    await context.close()
  }
  console.log('Expanded-post layout passed: content-sized short posts, bounded scrolling, long words/names, photos, pinned controls, focus return, desktop/mobile, both themes, and immersive mode.')
} finally {
  await browser.close()
}
