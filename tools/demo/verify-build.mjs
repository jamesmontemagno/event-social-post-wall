import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import path from 'node:path'

const [role, directory] = process.argv.slice(2)
assert.ok(['public', 'admin', 'submission'].includes(role), 'Supply role and build directory')
if (role === 'submission') {
  const files = await fs.readdir(directory)
  assert.ok(!files.includes('index.html') && !files.includes('assets'), 'Submission must not contain a React entry point or bundle')
  for (const name of ['questions.js', 'questions.css', 'theme.css', 'footer.css', 'theme-init.js', 'reconnect.js']) {
    assert.ok((await fs.stat(path.join(directory, 'questions', name))).size > 0, `Missing Blazor question asset: ${name}`)
  }
  assert.ok((await fs.stat(path.join(directory, '_framework', 'blazor.web.js'))).size > 0)
  const javascript = await fs.readFile(path.join(directory, 'questions', 'questions.js'), 'utf8')
  assert.doesNotMatch(javascript, /\/api\/admin|\/api\/public\/wall|setInterval/)
  assert.match(javascript, /X-Submission-Token/)
  assert.match(await fs.readFile(path.join(directory, 'THIRD-PARTY-NOTICES.txt'), 'utf8'), /GitHub Copilot/)
  console.log('PASS: Blazor Server submission assets, notices and protected browser transport; no React bundle')
  process.exit(0)
}
const html = await fs.readFile(path.join(directory, 'index.html'), 'utf8')
assert.ok(html.includes(`<meta name="eventwall-role" content="${role}">`))
const files = await fs.readdir(path.join(directory, 'assets'))
const javascript = (await Promise.all(files.filter(file => file.endsWith('.js')).map(file => fs.readFile(path.join(directory, 'assets', file), 'utf8')))).join('\n')
if (role !== 'admin') assert.doesNotMatch(javascript, /\/api\/admin|Shared staff passcode|Open staff console/)
if (role === 'public') assert.doesNotMatch(javascript, /X-Submission-Token|\/api\/public\/submissions|submission-config|What would you like to ask/)
console.log(`PASS: ${role} role marker and complete production bundle isolation`)
