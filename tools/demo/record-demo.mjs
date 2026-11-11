// Records a captioned walkthrough of Event Wall running in demo mode.
// Usage: start the app with tools/demo/start-demo.ps1 -Reset, then `npm run record` in tools/demo.
import { chromium } from 'playwright-core'
import { spawn } from 'node:child_process'
import fs from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '..', '..')
const BASE = (process.env.EVENTWALL_URL ?? 'http://127.0.0.1:5080').replace(/\/$/, '')
const ADMIN = (process.env.EVENTWALL_ADMIN_URL ?? 'http://127.0.0.1:5081').replace(/\/$/, '')
const SUBMISSION = (process.env.EVENTWALL_SUBMISSION_URL ?? 'http://127.0.0.1:5082').replace(/\/$/, '')
const PASSCODE = process.env.STAFF_PASSCODE
if (!PASSCODE) throw new Error('Set STAFF_PASSCODE to the passcode printed by the local demo launcher.')
const OUT_DIR = path.resolve(process.env.DEMO_OUTPUT ?? path.join(root, 'demo-output'))
const FRAMES_DIR = path.join(OUT_DIR, 'frames')
const VIDEO = path.join(OUT_DIR, 'eventwall-demo.mp4')
const VIEWPORT = { width: 1440, height: 810 }
const SCALE = 4 / 3
const EDGE = process.env.EDGE_PATH

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms))
const ease = t => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2)

// ---------------------------------------------------------------------------
// In-page overlay: captions, title cards, highlight ring, fake cursor.
// ---------------------------------------------------------------------------
function overlayScript() {
  const css = `
    html::-webkit-scrollbar, body::-webkit-scrollbar, *::-webkit-scrollbar { width: 0 !important; height: 0 !important; }
    #demo-layer { position: fixed; inset: 0; z-index: 2147483000; pointer-events: none; font-family: 'Manrope', 'Segoe UI', sans-serif; }
    #demo-cursor { position: fixed; left: 0; top: 0; width: 26px; height: 26px; transform: translate(-100px, -100px); transition: opacity .2s; filter: drop-shadow(0 3px 6px rgba(0,0,0,.55)); }
    #demo-cursor svg { width: 26px; height: 26px; }
    .demo-ripple { position: fixed; width: 44px; height: 44px; margin: -22px 0 0 -22px; border-radius: 50%; border: 2px solid #d5fa68; animation: demo-ripple .55s ease-out forwards; }
    @keyframes demo-ripple { from { transform: scale(.3); opacity: .95; } to { transform: scale(1.35); opacity: 0; } }
    #demo-caption { position: fixed; left: 40px; bottom: 36px; max-width: 640px; padding: 18px 22px 18px 20px; border-radius: 16px;
      border: 1px solid rgba(213,250,104,.22); background: rgba(12,13,17,.86); backdrop-filter: blur(14px);
      box-shadow: 0 18px 50px rgba(0,0,0,.45); opacity: 0; transform: translateY(14px); transition: opacity .45s ease, transform .45s ease; }
    #demo-caption.show { opacity: 1; transform: translateY(0); }
    #demo-caption .k { display: inline-flex; align-items: center; gap: 8px; color: #d5fa68; font: 600 11px 'DM Mono', Consolas, monospace; letter-spacing: .14em; text-transform: uppercase; }
    #demo-caption .k::before { content: ''; width: 18px; height: 2px; background: #d5fa68; border-radius: 2px; }
    #demo-caption strong { display: block; margin-top: 8px; color: #f4f5f9; font: 700 24px/1.2 'Manrope', 'Segoe UI', sans-serif; letter-spacing: -.02em; }
    #demo-caption p { margin: 7px 0 0; color: #b9bac6; font: 500 14.5px/1.5 'DM Sans', 'Segoe UI', sans-serif; }
    #demo-card { position: fixed; inset: 0; display: grid; place-items: center; opacity: 0; transition: opacity .6s ease;
      background: radial-gradient(1200px 600px at 20% 10%, rgba(213,250,104,.12), transparent 60%),
                  radial-gradient(900px 600px at 90% 90%, rgba(124,92,255,.18), transparent 60%), #0d0e12; }
    #demo-card.show { opacity: 1; }
    #demo-card .inner { width: min(1100px, 86vw); color: #f2f3f7; }
    #demo-card .eyebrow { color: #d5fa68; font: 600 13px 'DM Mono', Consolas, monospace; letter-spacing: .2em; text-transform: uppercase; }
    #demo-card h1 { margin: 18px 0 14px; font: 800 76px/1.02 'Manrope', 'Segoe UI', sans-serif; letter-spacing: -.045em; }
    #demo-card h1 em { color: #d5fa68; font-style: normal; }
    #demo-card p.lead { max-width: 820px; margin: 0; color: #b6b8c4; font: 500 21px/1.5 'DM Sans', 'Segoe UI', sans-serif; }
    #demo-card .chips { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 34px; }
    #demo-card .chip { padding: 10px 16px; border: 1px solid rgba(255,255,255,.12); border-radius: 999px; background: rgba(255,255,255,.04); color: #e6e7ee; font: 600 15px 'DM Sans', 'Segoe UI', sans-serif; }
    #demo-card .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 14px; margin-top: 34px; }
    #demo-card .tile { padding: 20px; border: 1px solid rgba(255,255,255,.1); border-radius: 16px; background: rgba(255,255,255,.035); }
    #demo-card .tile b { display: block; color: #d5fa68; font: 700 13px 'DM Mono', Consolas, monospace; letter-spacing: .12em; text-transform: uppercase; }
    #demo-card .tile span { display: block; margin-top: 10px; color: #dfe0e8; font: 500 16px/1.45 'DM Sans', 'Segoe UI', sans-serif; }
    #demo-card .foot { margin-top: 28px; color: #858796; font: 500 13px 'DM Mono', Consolas, monospace; letter-spacing: .06em; }
    #demo-ring { position: fixed; border: 2px solid #d5fa68; border-radius: 16px; box-shadow: 0 0 0 6px rgba(213,250,104,.16), 0 0 38px rgba(213,250,104,.35);
      opacity: 0; transition: opacity .3s ease, left .3s ease, top .3s ease, width .3s ease, height .3s ease; }
    #demo-ring.show { opacity: 1; }
    #demo-panel { position: fixed; right: 40px; top: 110px; width: 760px; max-height: 640px; overflow: hidden; padding: 20px 22px; border-radius: 16px;
      border: 1px solid rgba(255,255,255,.12); background: rgba(14,15,20,.95); box-shadow: 0 24px 70px rgba(0,0,0,.6);
      opacity: 0; transform: translateY(10px) scale(.985); transition: opacity .4s ease, transform .4s ease; }
    #demo-panel.show { opacity: 1; transform: none; }
    #demo-panel h4 { margin: 0 0 4px; color: #f2f3f7; font: 700 18px 'Manrope', 'Segoe UI', sans-serif; }
    #demo-panel .sub { margin: 0 0 14px; color: #8d8f9c; font: 500 12px 'DM Mono', Consolas, monospace; }
    #demo-panel table { width: 100%; border-collapse: collapse; font: 500 12.5px/1.4 'DM Sans', 'Segoe UI', sans-serif; color: #d9dae2; }
    #demo-panel th { padding: 7px 8px; color: #d5fa68; font: 600 10.5px 'DM Mono', Consolas, monospace; letter-spacing: .08em; text-align: left; text-transform: uppercase; border-bottom: 1px solid rgba(255,255,255,.12); }
    #demo-panel td { padding: 7px 8px; border-bottom: 1px solid rgba(255,255,255,.06); vertical-align: top; }
    #demo-panel td.t { max-width: 380px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  `
  const cursorSvg = '<svg viewBox="0 0 24 24"><path d="M4 2.5 L4 19.5 L8.6 15.4 L11.6 22 L14.6 20.7 L11.7 14.3 L18 14.3 Z" fill="#ffffff" stroke="#111216" stroke-width="1.4" stroke-linejoin="round"/></svg>'
  const install = () => {
    if (document.getElementById('demo-layer')) return
    const style = document.createElement('style')
    style.textContent = css
    document.head.appendChild(style)
    const layer = document.createElement('div')
    layer.id = 'demo-layer'
    layer.innerHTML = `<div id="demo-ring"></div><div id="demo-panel"></div><div id="demo-caption"><span class="k"></span><strong></strong><p></p></div><div id="demo-card"><div class="inner"></div></div><div id="demo-cursor">${cursorSvg}</div>`
    document.body.appendChild(layer)
    const cursor = layer.querySelector('#demo-cursor')
    document.addEventListener('mousemove', event => {
      cursor.style.transform = `translate(${event.clientX - 4}px, ${event.clientY - 2}px)`
    }, true)
    document.addEventListener('mousedown', event => {
      const ripple = document.createElement('div')
      ripple.className = 'demo-ripple'
      ripple.style.left = `${event.clientX}px`
      ripple.style.top = `${event.clientY}px`
      layer.appendChild(ripple)
      setTimeout(() => ripple.remove(), 700)
    }, true)
  }
  const el = id => { install(); return document.getElementById(id) }
  window.__demo = {
    caption(kicker, title, body) {
      const box = el('demo-caption')
      const apply = () => {
        box.querySelector('.k').textContent = kicker
        box.querySelector('strong').textContent = title
        box.querySelector('p').textContent = body ?? ''
        box.classList.add('show')
      }
      if (box.classList.contains('show')) {
        box.classList.remove('show')
        setTimeout(apply, 380)
      } else apply()
    },
    hideCaption() { el('demo-caption').classList.remove('show') },
    card(html) { const card = el('demo-card'); card.querySelector('.inner').innerHTML = html; card.classList.add('show') },
    hideCard() { el('demo-card').classList.remove('show') },
    ring(rect) {
      const ring = el('demo-ring')
      const pad = 8
      Object.assign(ring.style, { left: `${rect.x - pad}px`, top: `${rect.y - pad}px`, width: `${rect.width + pad * 2}px`, height: `${rect.height + pad * 2}px` })
      ring.classList.add('show')
    },
    hideRing() { el('demo-ring').classList.remove('show') },
    panel(html) { const panel = el('demo-panel'); panel.innerHTML = html; panel.classList.add('show') },
    hidePanel() { el('demo-panel').classList.remove('show') },
    cursorVisible(visible) { el('demo-cursor').style.opacity = visible ? '1' : '0' },
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install)
  else install()
}

// ---------------------------------------------------------------------------
// Screencast capture.
// ---------------------------------------------------------------------------
class Recorder {
  constructor(page) {
    this.page = page
    this.frames = []
    this.writes = []
  }

  async start() {
    await fs.rm(FRAMES_DIR, { recursive: true, force: true })
    await fs.mkdir(FRAMES_DIR, { recursive: true })
    this.cdp = await this.page.context().newCDPSession(this.page)
    this.cdp.on('Page.screencastFrame', ({ data, metadata, sessionId }) => {
      this.cdp.send('Page.screencastFrameAck', { sessionId }).catch(() => undefined)
      const file = path.join(FRAMES_DIR, `f${String(this.frames.length).padStart(6, '0')}.jpg`)
      this.frames.push({ file, t: metadata.timestamp ?? Date.now() / 1000 })
      this.writes.push(fs.writeFile(file, Buffer.from(data, 'base64')))
    })
    await this.cdp.send('Page.startScreencast', this.options())
    this.startedAt = Date.now() / 1000
  }

  options() {
    return {
      format: 'jpeg',
      quality: 92,
      maxWidth: Math.round(VIEWPORT.width * SCALE),
      maxHeight: Math.round(VIEWPORT.height * SCALE),
      everyNthFrame: 1,
    }
  }

  // Full document navigations can end a screencast; restarting is harmless if it is still running.
  async resume() {
    await this.cdp.send('Page.startScreencast', this.options()).catch(() => undefined)
  }

  // Holds the last frame while a full navigation would otherwise flash a blank document.
  async pause() {
    await this.cdp.send('Page.stopScreencast').catch(() => undefined)
  }

  async stop() {
    const end = Date.now() / 1000
    await this.cdp.send('Page.stopScreencast').catch(() => undefined)
    await Promise.all(this.writes)
    const lines = []
    let total = 0
    this.frames.forEach((frame, index) => {
      const next = this.frames[index + 1]?.t ?? end
      const duration = Number(Math.min(Math.max(next - frame.t, 1 / 120), 12).toFixed(4))
      total += duration
      lines.push(`file '${frame.file.replaceAll('\\', '/')}'`, `duration ${duration.toFixed(4)}`)
    })
    // The concat demuxer drops the final entry's duration, so repeat the last frame and trim to `total` when encoding.
    const last = this.frames.at(-1)
    if (last) lines.push(`file '${last.file.replaceAll('\\', '/')}'`, `duration ${(1 / 30).toFixed(4)}`)
    const list = path.join(OUT_DIR, 'frames.txt')
    await fs.writeFile(list, lines.join('\n'))
    return { list, count: this.frames.length, seconds: end - this.startedAt, total }
  }
}

function run(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { stdio: ['ignore', 'inherit', 'inherit'] })
    child.on('error', reject)
    child.on('exit', code => (code === 0 ? resolve() : reject(new Error(`${command} exited with ${code}`))))
  })
}

// ---------------------------------------------------------------------------
// Choreography helpers.
// ---------------------------------------------------------------------------
let page
let mouse = { x: VIEWPORT.width / 2, y: VIEWPORT.height / 2 }

const demo = (method, ...args) => page.evaluate(([name, values]) => window.__demo[name](...values), [method, args])
const caption = (kicker, title, body) => demo('caption', kicker, title, body)

async function glide(x, y, ms = 650) {
  const from = { ...mouse }
  const steps = Math.max(12, Math.round(ms / 16))
  for (let i = 1; i <= steps; i++) {
    const t = ease(i / steps)
    await page.mouse.move(from.x + (x - from.x) * t, from.y + (y - from.y) * t)
    await sleep(ms / steps)
  }
  mouse = { x, y }
}

async function reveal(locator, ms = 1100) {
  await locator.waitFor()
  const box = await locator.boundingBox()
  if (!box) return
  if (box.y < 96 || box.y + Math.min(box.height, 360) > VIEWPORT.height - 190) {
    const scrollY = await page.evaluate(() => window.scrollY)
    await scrollTo(Math.max(0, scrollY + box.y - VIEWPORT.height * 0.28), ms)
    await sleep(150)
  }
}

async function center(locator) {
  await reveal(locator)
  const box = await locator.boundingBox()
  if (!box) throw new Error('Element is not visible')
  return { x: box.x + box.width / 2, y: box.y + box.height / 2, box }
}

async function click(locator, { ms = 650, settle = 220 } = {}) {
  const target = await center(locator)
  await glide(target.x, target.y, ms)
  await sleep(settle)
  await page.mouse.down()
  await sleep(70)
  await page.mouse.up()
}

async function type(locator, text, delay = 38) {
  await click(locator)
  await locator.pressSequentially(text, { delay })
}

async function clearField(locator) {
  await click(locator)
  await locator.press('Control+A')
  await locator.press('Backspace')
}

async function ring(locator) {
  await reveal(locator)
  const box = await locator.boundingBox()
  if (box) await demo('ring', box)
}

async function scrollTo(y, ms = 1800) {
  await page.evaluate(async ([targetY, duration]) => {
    const startY = window.scrollY
    const start = performance.now()
    await new Promise(resolve => {
      const step = now => {
        const t = Math.min(1, (now - start) / duration)
        const k = t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2
        window.scrollTo(0, startY + (targetY - startY) * k)
        if (t < 1) requestAnimationFrame(step)
        else resolve()
      }
      requestAnimationFrame(step)
    })
  }, [y, ms])
}

async function staffApi(method, url, body) {
  const request = page.context().request
  const { requestToken } = await (await request.get(`${ADMIN}/api/admin/csrf`)).json()
  const response = await request.fetch(`${ADMIN}${url}`, {
    method,
    headers: { 'X-CSRF-TOKEN': requestToken, 'Content-Type': 'application/json' },
    data: body ? JSON.stringify(body) : undefined,
  })
  if (!response.ok()) throw new Error(`${method} ${url} failed: ${response.status()} ${await response.text()}`)
  return response.status() === 204 ? null : response.json()
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch])
}

// The export prefixes formula-like cells with an apostrophe; show the original value in the on-screen preview.
function csvCell(value) {
  return escapeHtml(String(value ?? '').replace(/^'(?=[=+\-@\t\r])/, ''))
}

function parseCsv(text) {
  const rows = []
  let row = []
  let cell = ''
  let quoted = false
  for (let i = 0; i < text.length; i++) {
    const ch = text[i]
    if (quoted) {
      if (ch === '"' && text[i + 1] === '"') { cell += '"'; i++ }
      else if (ch === '"') quoted = false
      else cell += ch
    } else if (ch === '"') quoted = true
    else if (ch === ',') { row.push(cell); cell = '' }
    else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && text[i + 1] === '\n') i++
      row.push(cell); rows.push(row); row = []; cell = ''
    } else cell += ch
  }
  if (cell || row.length) { row.push(cell); rows.push(row) }
  return rows
}

// ---------------------------------------------------------------------------
// Scenes.
// ---------------------------------------------------------------------------
async function main() {
  const health = await fetch(`${BASE}/api/health`).catch(() => null)
  if (!health?.ok) throw new Error(`Event Wall is not reachable at ${BASE}. Start it with tools/demo/start-demo.ps1 -Reset.`)
  const eventInfo = await (await fetch(`${BASE}/api/public/event`)).json()
  if (!eventInfo.demoMode) throw new Error('The running app is not in demo mode. Start it with tools/demo/start-demo.ps1.')

  await fs.mkdir(OUT_DIR, { recursive: true })
  const browser = await chromium.launch({
    ...(EDGE ? { executablePath: EDGE } : { channel: 'msedge' }),
    headless: true,
    args: ['--hide-scrollbars', '--force-color-profile=srgb', '--font-render-hinting=none'],
  })
  const context = await browser.newContext({ viewport: VIEWPORT, deviceScaleFactor: SCALE, acceptDownloads: true, reducedMotion: 'no-preference' })
  await context.addInitScript(overlayScript)
  page = await context.newPage()
  await page.goto(`${BASE}/`, { waitUntil: 'load' })
  await page.evaluate(() => localStorage.clear())
  await page.reload({ waitUntil: 'load' })
  await page.locator('.post-card').first().waitFor()
  await page.mouse.move(mouse.x, mouse.y)

  const recorder = new Recorder(page)
  async function switchSite(url) {
    await recorder.pause()
    await page.goto(url, { waitUntil: 'load' })
    await page.locator('.topbar').waitFor()
    await recorder.resume()
  }
  await demo('cursorVisible', false)
  await demo('card', `
    <div class="eyebrow">Community Event · Community Wall</div>
    <h1>Every channel.<br/>One <em>curated</em> stage.</h1>
    <p class="lead">A live, moderated feed of questions and moments from Twitch, YouTube, X, Bluesky, Mastodon, and attendees — built for hosts on stage.</p>
    <div class="chips"><span class="chip">▣ Twitch</span><span class="chip">▶ YouTube</span><span class="chip">X</span><span class="chip">☁ Bluesky</span><span class="chip">M Mastodon</span><span class="chip">✦ Web portal</span></div>
    <div class="foot">DEMO MODE · SIMULATED POSTS · NO SOCIAL API CALLS</div>`)
  await sleep(700)
  await recorder.start()
  await sleep(5200)
  await demo('hideCard')
  await demo('cursorVisible', true)
  await sleep(900)

  // 1. Live wall
  await caption('01 · Live wall', 'A beautiful waterfall for the stage', 'Only approved posts appear here — from every source, in one calm, readable feed.')
  await sleep(3200)
  await glide(760, 560, 900)
  await scrollTo(520, 2600)
  await sleep(1400)
  await scrollTo(0, 1800)
  await sleep(600)

  // 2. Expand + pin
  const question = page.locator('.post-card', { hasText: 'decide which features ship' })
  await caption('02 · Spotlight', 'Tap a post to bring it up big', 'Hosts can read a question to the room without squinting at a chat window.')
  await click(question.locator('.card-open'))
  await page.locator('.post-modal').waitFor()
  await sleep(3200)
  await caption('02 · Spotlight', 'Pin the ones you want to get to', 'Pins stay at the top for this host’s screen while the feed keeps flowing.')
  await click(page.locator('.modal-close'))
  await click(page.getByRole('button', { name: 'Pin post', exact: true }).first())
  await page.locator('.pinned-section').waitFor()
  await scrollTo(0, 1200)
  await ring(page.locator('.pinned-section'))
  await sleep(2800)
  await demo('hideRing')

  // 3. Portal submission
  await caption('03 · Web portal', 'Anyone can join the conversation', 'Attendees send questions from a simple portal. Every post is reviewed before it reaches the wall.')
  await switchSite(SUBMISSION)
  await page.locator('#question-text').waitFor()
  await sleep(1200)
  await type(page.locator('#question-text'), 'First time watching live! What’s the best way for a student to start contributing to .NET open source?', 30)
  await type(page.locator('#question-name'), 'Jordan in Toronto', 45)
  await sleep(500)
  await click(page.getByRole('button', { name: /Send to the team/ }))
  await page.locator('.question-success').waitFor()
  await sleep(2600)

  // 4. Staff login
  await switchSite(ADMIN)
  await caption('04 · Private control room', 'A separate deployment, just for staff', 'The public website has no admin routes or staff code. Moderation lives at a separate, restricted URL with staff authentication.')
  await page.locator('#staff-passcode').waitFor()
  await sleep(900)
  await type(page.locator('#staff-passcode'), PASSCODE, 40)
  await click(page.getByRole('button', { name: /Open staff console/ }))
  await page.locator('.queue-item').first().waitFor()
  await sleep(1400)

  // 5. Review queue streaming
  await caption('05 · Review queue', 'Questions from every source in one queue', 'Twitch and YouTube #question messages, X, Bluesky, and Mastodon event mentions, and portal questions — side by side.')
  await glide(1000, 600, 800)
  await scrollTo(380, 2200)
  await sleep(1200)
  await scrollTo(0, 1400)
  await page.locator('.queue-waiting').waitFor({ timeout: 20000 })
  await caption('05 · Review queue', 'New posts stream in without jumping', 'The queue tells you when more arrive, so the item you’re reading never moves under your cursor.')
  await ring(page.locator('.queue-waiting'))
  await sleep(2400)
  await demo('hideRing')
  await click(page.locator('.queue-waiting .button'))
  await sleep(1600)

  // 6. Search + approve the portal question
  await caption('06 · Moderate', 'Search, then Approve & send live', 'One click publishes to every wall screen. Questions from the portal are easy to find.')
  const search = page.getByLabel('Search queue')
  await type(search, 'Jordan', 70)
  const jordan = page.locator('.queue-item', { hasText: 'Jordan in Toronto' })
  await jordan.waitFor()
  await sleep(1100)
  await click(jordan.getByRole('button', { name: /Approve & send live/ }))
  await page.locator('.notice-success').waitFor()
  await sleep(2000)

  // 7. Reject spam
  await caption('06 · Moderate', 'Spam never reaches the stage', 'Reject anything off-topic or unsafe. Decisions are logged for the event report.')
  await clearField(search)
  await type(search, 'followers', 70)
  const spam = page.locator('.queue-item', { hasText: 'followers fast' })
  await spam.waitFor()
  await sleep(1000)
  await ring(spam)
  await sleep(900)
  await demo('hideRing')
  await click(spam.getByRole('button', { name: 'Reject' }))
  await sleep(1800)
  await clearField(search)
  await sleep(900)

  // 8. Filter by source + approve
  await caption('06 · Moderate', 'Filter by source when chat gets busy', 'Narrow to a single platform, approve the gems, and keep moving.')
  const sourceFilter = page.getByLabel('Filter by source')
  const filterAt = await center(sourceFilter)
  await glide(filterAt.x, filterAt.y)
  await sleep(250)
  await sourceFilter.selectOption('Twitch')
  await sleep(1300)
  const twitchItem = page.locator('.queue-item').first()
  await click(twitchItem.getByRole('button', { name: /Approve & send live/ }))
  await sleep(1500)
  await glide(filterAt.x, filterAt.y)
  await sourceFilter.selectOption('All')
  await sleep(1000)
  await click(page.locator('.queue-item').first().getByRole('button', { name: /Approve & send live/ }))
  await sleep(1600)

  // 9. Back to the wall: approved portal question is live
  await switchSite(BASE)
  await caption('07 · Back on stage', 'Approved posts appear on the wall', 'Jordan’s portal question is now live for the hosts.')
  const jordanCard = page.locator('.post-card', { hasText: 'Jordan in Toronto' })
  await jordanCard.waitFor()
  await sleep(600)
  await ring(jordanCard)
  await sleep(2600)
  await demo('hideRing')

  // 10. New posts chip (moderator approves from another seat)
  await caption('07 · Back on stage', 'The wall never shifts mid-question', 'When moderators approve more posts, hosts get a gentle chip — and choose when to bring them in.')
  const pending = await staffApi('GET', '/api/admin/queue?status=Pending')
  for (const post of pending.slice(0, 2)) {
    await staffApi('POST', `/api/admin/posts/${post.id}/approve`, { version: post.version })
  }
  await scrollTo(0, 1000)
  // The wall checks once a minute; trigger the same immediate check used when a host returns to the tab.
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
  await page.locator('.new-posts-row').waitFor({ timeout: 15000 })
  await sleep(600)
  await ring(page.locator('.new-posts-row'))
  await sleep(2200)
  await demo('hideRing')
  await click(page.locator('.new-posts-row .button'))
  await sleep(2400)

  // 11. Withdraw
  await caption('08 · Withdraw', 'Changed your mind? Withdraw it', 'A withdrawn post disappears from every wall screen on the next check, within about a minute — even if it’s pinned.')
  const live = await staffApi('GET', '/api/admin/queue?status=Live')
  const target = live.find(item => item.text.includes('São Paulo'))
  const targetCard = page.locator('.post-card', { hasText: 'São Paulo' })
  await ring(targetCard)
  await sleep(2200)
  await staffApi('POST', `/api/admin/posts/${target.id}/withdraw`, { version: target.version })
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
  await targetCard.waitFor({ state: 'detached', timeout: 10000 })
  await demo('hideRing')
  await sleep(2000)
  await scrollTo(0, 900)

  // 12. Social connections
  await demo('hideCaption')
  await switchSite(ADMIN)
  await click(page.getByRole('tab', { name: /Social connections/ }))
  await page.locator('.source-setup-card').first().waitFor()
  await caption('09 · Social connections', 'Official APIs, configured on the server', 'Twitch, YouTube, X, Bluesky, and Mastodon each show setup state. Demo mode simulates them with zero API calls or cost.')
  await sleep(1800)
  const xCard = page.locator('.source-setup-card').nth(2)
  await click(xCard.locator('summary'))
  await sleep(2600)

  // 13. Event report
  await caption('10 · Event report', 'Analytics when the show wraps', 'Totals, how fast the team made decisions, and a breakdown by status and source.')
  await scrollTo(0, 800)
  await click(page.getByRole('tab', { name: 'Event report' }))
  await page.locator('.metric-card').first().waitFor()
  await sleep(1200)
  await ring(page.locator('.metric-row'))
  await sleep(2400)
  await demo('hideRing')
  await glide(1100, 640, 800)
  await sleep(1600)

  // 14. Export
  await caption('11 · Export', 'Take everything with you', 'CSV and JSON exports of every post and decision — ready for recaps and follow-ups.')
  await click(page.getByRole('tab', { name: 'Review queue' }))
  await sleep(900)
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    click(page.getByRole('link', { name: /Export CSV/ })),
  ])
  const csv = await fs.readFile(await download.path(), 'utf8')
  await fs.writeFile(path.join(OUT_DIR, 'eventwall-demo-export.csv'), csv)
  const rows = parseCsv(csv)
  const header = rows[0]
  const pick = name => header.indexOf(name)
  const body = rows.slice(1).filter(row => row.length > 3).slice(-9).reverse()
  await demo('panel', `
    <h4>eventwall-communityevent.csv</h4>
    <p class="sub">${rows.length - 1} rows · ${header.length} columns · downloaded just now</p>
    <table><thead><tr><th>Source</th><th>Author</th><th>Text</th><th>Status</th></tr></thead><tbody>
    ${body.map(row => `<tr><td>${csvCell(row[pick('source')])}</td><td>${csvCell(row[pick('authorName')])}</td><td class="t">${csvCell(row[pick('text')])}</td><td>${csvCell(row[pick('status')])}</td></tr>`).join('')}
    </tbody></table>`)
  await sleep(4200)
  await demo('hidePanel')
  await sleep(600)

  // 15. Pause the portal
  await caption('12 · Portal control', 'Pause submissions with confirmation', 'When Q&A wraps, confirm the portal pause. The wall stays up.')
  await click(page.locator('.staff-toolbar > .button'))
  await click(page.getByRole('dialog').getByRole('button', { name: 'Pause submissions' }))
  await page.locator('.staff-toolbar > .button', { hasText: 'Portal paused' }).waitFor()
  await sleep(1800)
  await recorder.pause()
  await page.goto(SUBMISSION, { waitUntil: 'load' })
  await page.getByLabel('Your question', { exact: true }).fill('Can I still send a question to the hosts?')
  await page.getByRole('button', { name: /Send to the team/ }).click()
  await page.getByRole('alert').waitFor()
  await page.mouse.move(mouse.x, mouse.y)
  await sleep(300)
  await recorder.resume()
  await caption('12 · Portal control', 'Attendees see a friendly closed state', 'Reopen any time from the staff console.')
  await sleep(3200)

  // 16. Closing card
  await demo('hideCaption')
  await demo('cursorVisible', false)
  await demo('card', `
    <div class="eyebrow">Simple to run · Easy to deploy</div>
    <h1>Ship it on <em>Azure.</em></h1>
    <p class="lead">A host wall, a lightweight question website, and a separate staff-authenticated admin share one event database. Simple to operate, with clear boundaries.</p>
    <div class="grid">
      <div class="tile"><b>Host</b><span>Separate public/admin/submission App Services + Azure SQL. Admin is reachable from any IP; staff operations require the staff passcode.</span></div>
      <div class="tile"><b>Release</b><span>GitHub Actions deploys to staging, then waits for your approval before production</span></div>
      <div class="tile"><b>Rehearse</b><span>Demo mode streams simulated posts with zero social API calls — perfect for run-throughs</span></div>
    </div>
    <div class="foot">tools/demo/start-demo.ps1 -Reset  ·  npm run record</div>`)
  await sleep(6500)

  const capture = await recorder.stop()
  await browser.close()
  console.log(`Captured ${capture.count} frames over ${capture.seconds.toFixed(1)}s. Encoding ${VIDEO}…`)

  await run('ffmpeg', [
    '-y', '-hide_banner', '-loglevel', 'warning',
    '-f', 'concat', '-safe', '0', '-i', capture.list,
    '-vf', 'scale=1920:1080:flags=lanczos:in_range=pc:out_range=tv:in_color_matrix=bt601:out_color_matrix=bt709,fps=30,format=yuv420p',
    '-c:v', 'libx264', '-preset', 'slow', '-crf', '18',
    '-colorspace', 'bt709', '-color_primaries', 'bt709', '-color_trc', 'bt709', '-color_range', 'tv',
    '-t', capture.total.toFixed(3),
    '-movflags', '+faststart', VIDEO,
  ])
  if (!process.env.KEEP_FRAMES) {
    await fs.rm(FRAMES_DIR, { recursive: true, force: true })
    await fs.rm(capture.list, { force: true })
  }
  console.log(`Demo video ready: ${VIDEO}`)
}

main().catch(error => {
  console.error(error)
  process.exit(1)
})
