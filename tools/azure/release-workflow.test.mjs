import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { test } from 'node:test'
import { runInNewContext } from 'node:vm'

const workflow = readFileSync(new URL('../../.github/workflows/release-azure.yml', import.meta.url), 'utf8').replace(/\r\n/g, '\n')
const pagesWorkflow = readFileSync(new URL('../../.github/workflows/publish-pages.yml', import.meta.url), 'utf8').replace(/\r\n/g, '\n')
const ciWorkflow = readFileSync(new URL('../../.github/workflows/ci.yml', import.meta.url), 'utf8').replace(/\r\n/g, '\n')

function jobLines(job, source = workflow) {
  const lines = source.split(/\r?\n/)
  const start = lines.indexOf(`  ${job}:`)
  assert.notEqual(start, -1, `Missing ${job} job`)
  const nextJob = lines.findIndex((line, index) => index > start && /^  [\w-]+:$/.test(line))
  return lines.slice(start + 1, nextJob === -1 ? undefined : nextJob)
}

function jobSteps(job, source = workflow) {
  const steps = []
  for (const line of jobLines(job, source)) {
    if (line.startsWith('      - ')) steps.push([])
    if (steps.length) steps.at(-1).push(line)
  }
  return steps.map(step => step.join('\n'))
}

test('shared release tests run once and gate every role build', () => {
  const shared = jobLines('test').join('\n')
  assert.doesNotMatch(shared, /^\s+(?:matrix|continue-on-error):/m)
  assert.ok(shared.includes("if: vars.ENABLE_AZURE_DEPLOYMENT == 'true' && github.ref == 'refs/heads/main'"))
  for (const command of [
    'dotnet test EventWall.slnx --configuration Release',
    'npm test --prefix src/EventWall.Web',
  ]) {
    assert.equal(workflow.split(command).length - 1, 1, `${command} must run once`)
    assert.ok(shared.includes(command))
  }
  assert.ok(shared.includes('npm ci --prefix src/EventWall.Web'))
  assert.match(jobLines('build').join('\n'), /^    needs: test$/m)
})

test('role builds retain isolated web builds and submission checks', () => {
  const steps = jobSteps('build')
  const web = steps.find(step => step.includes('name: Build isolated web app'))
  assert.ok(web)
  assert.match(web, /^        if: matrix.role != 'submission'$/m)
  assert.ok(web.includes('VITE_SITE_ROLE: ${{ matrix.role }}'))
  assert.ok(web.includes('npm ci --prefix src/EventWall.Web'))
  assert.ok(web.includes('npm run build --prefix src/EventWall.Web'))
  assert.ok(web.includes('node tools/demo/verify-build.mjs "${{ matrix.role }}" src/EventWall.Web/dist'))
  const publish = steps.find(step => step.includes('name: Publish API package'))
  assert.ok(publish)
  assert.ok(publish.includes('dotnet publish src/EventWall.Api --configuration Release --output publish -p:EventWallSiteRole=${{ matrix.role }}'))
  assert.ok(publish.includes('if [ "${{ matrix.role }}" = "submission" ]; then'))
  assert.ok(publish.includes('node --test tools/demo/questions-client.test.mjs'))
  assert.ok(publish.includes('node tools/demo/verify-build.mjs submission publish/wwwroot'))
  assert.ok(publish.includes('zip -r ../eventwall-${{ matrix.role }}.zip .'))
})

test('CI skips repeated API dependency builds only after selecting the API', async () => {
  const steps = jobSteps('build', ciWorkflow)
  const detection = steps.find(step => step.includes('name: Detect changed projects'))
  const script = detection.split('          script: |\n')[1].replace(/^            /gm, '')
  const apiIndex = steps.findIndex(step => step.includes('name: Build API\n'))
  const solutionIndex = steps.findIndex(step => step.includes('name: Build .NET solution for shared changes'))
  assert.ok(solutionIndex >= 0 && apiIndex > solutionIndex)
  for (const name of ['Build Aspire AppHost', 'Build API test project', 'Build database setup tool']) {
    const index = steps.findIndex(step => step.includes(`name: ${name}\n`))
    assert.ok(index > apiIndex)
    assert.ok(steps[index].includes("${{ steps.changes.outputs.api == 'true' && '--no-dependencies' || '' }}"))
  }
  for (const [paths, expectedApi] of [
    [['src/EventWall.Api/Program.cs'], true],
    [['EventWall.slnx'], true],
    [['src/EventWall.AppHost/Program.cs'], false],
    [['tests/EventWall.Api.Tests/UnitTest1.cs'], false],
    [['tools/EventWall.DatabaseSetup/Program.cs'], false],
    [null, true],
  ]) {
    const outputs = {}
    await runInNewContext(`(async () => {\n${script}\n})()`, {
      context: { payload: { pull_request: paths ? { number: 1 } : undefined } },
      github: {
        rest: { pulls: { listFiles: {} } },
        paginate: async () => paths.map(filename => ({ filename })),
      },
      core: { setOutput: (name, value) => { outputs[name] = value } },
    })
    assert.equal(outputs.api, expectedApi, `API selection for ${paths ?? 'manual run'}`)
  }
  assert.equal(ciWorkflow.split('npm ci --prefix src/EventWall.Web').length - 1, 1)
})

test('all release roles retain reusable deployment packages for 14 to 30 days', () => {
  assert.match(workflow, /^        role: \[public, admin, submission\]$/m)
  const uploads = jobSteps('build').filter(step => step.includes('uses: actions/upload-artifact@'))
  assert.equal(uploads.length, 1, 'All roles must use the same upload policy')
  const upload = uploads[0]
  assert.ok(upload.includes('name: eventwall-${{ matrix.role }}'))
  assert.ok(upload.includes('path: eventwall-${{ matrix.role }}.zip'))
  assert.match(upload, /^          if-no-files-found: error$/m)
  const retention = upload.match(/^          retention-days: (\d+)$/m)
  assert.ok(retention, 'Deployment packages need explicit retention')
  assert.ok(Number(retention[1]) >= 14 && Number(retention[1]) <= 30)
})

test('staging and protected production reuse the same run packages', () => {
  assert.match(workflow, /^    needs: build$/m)
  assert.match(workflow, /^    needs: staging$/m)
  assert.ok(workflow.includes('    if: ${{ inputs.deployProduction }}'))
  assert.match(workflow, /^      name: production$/m)
  for (const environment of ['staging', 'production']) {
    const steps = jobSteps(environment)
    const downloads = steps.filter(step => step.includes('uses: actions/download-artifact@'))
    assert.equal(downloads.length, 1)
    assert.match(downloads[0], /^          pattern: eventwall-\*$/m)
    assert.match(downloads[0], /^          merge-multiple: true$/m)
    assert.doesNotMatch(downloads[0], /^          (?:run-id|repository|name):/m)
    assert.ok(steps.every(step => !step.includes('dotnet publish ') && !step.includes('npm run build ')))
    if (environment === 'production') {
      const gateIndex = steps.findIndex(step => step.includes('Require a protected production approval gate'))
      assert.ok(gateIndex >= 0 && gateIndex < steps.indexOf(downloads[0]))
      assert.ok(steps[gateIndex].includes('any(.type == "required_reviewers")'))
      assert.match(steps[gateIndex], /^            exit 1$/m)
    }
  }
})

test('Pages deployment bundles have explicit short retention', () => {
  assert.ok(pagesWorkflow.includes("vars.ENABLE_DOCS_PUBLISH == 'true' && inputs.confirmPublic"))
  assert.ok(pagesWorkflow.includes('path: docs/.vitepress/dist'))
  assert.ok(!pagesWorkflow.includes('VITE_API_BASE_URL'))
  const upload = pagesWorkflow.match(/      - uses: actions\/upload-pages-artifact@[^\n]+\n((?:        .*\n)+)/)
  assert.ok(upload, 'Missing Pages artifact upload')
  const retention = upload[1].match(/^          retention-days: (\d+)$/m)
  assert.ok(retention, 'Pages bundles need explicit retention')
  assert.ok(Number(retention[1]) >= 1 && Number(retention[1]) <= 7)
})

for (const [environment, targets] of Object.entries({
  staging: ['AZURE_WEBAPP_ADMIN_STAGING', 'AZURE_WEBAPP_STAGING', 'AZURE_WEBAPP_SUBMISSION_STAGING'],
  production: ['AZURE_WEBAPP_ADMIN_PRODUCTION', 'AZURE_WEBAPP_PRODUCTION', 'AZURE_WEBAPP_SUBMISSION_PRODUCTION'],
})) {
  test(`${environment} authenticates immediately before each role deployment`, () => {
    const steps = jobSteps(environment)
    const deployments = steps.flatMap((step, index) => step.includes('az webapp deploy ') ? [{ step, index }] : [])
    assert.equal(deployments.length, 3, 'Each role must have a separate deployment step')

    for (const [roleIndex, { step, index }] of deployments.entries()) {
      const login = steps[index - 1] ?? ''
      assert.match(login, /^        uses: azure\/login@/m)
      assert.doesNotMatch(login, /^        (?:if|continue-on-error):/m)
      for (const [input, variable] of [
        ['client-id', 'AZURE_CLIENT_ID'],
        ['tenant-id', 'AZURE_TENANT_ID'],
        ['subscription-id', 'AZURE_SUBSCRIPTION_ID'],
      ]) {
        assert.ok(login.includes(`${input}: \${{ vars.${variable} }}`), `Missing ${input} in ${environment} login`)
      }
      assert.match(step, /^        run: az webapp deploy /m)
      assert.ok(step.includes(`--name "\${{ vars.${targets[roleIndex]} }}"`))
      assert.ok(step.includes(`--src-path eventwall-${['admin', 'public', 'submission'][roleIndex]}.zip`))
    }
  })
}
