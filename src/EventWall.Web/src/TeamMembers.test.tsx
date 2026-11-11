import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import TeamMembers from './TeamMembers'

beforeEach(() => {
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: function (this: HTMLDialogElement) { this.open = true } })
  Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value: function (this: HTMLDialogElement) { this.open = false } })
})
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

function setup() {
  let member = { id: 'member-id', username: 'TestModerator', enabled: true, version: 1, createdAtUtc: '2026-11-03T12:00:00Z',
    codeRotatedAtUtc: '2026-11-03T12:00:00Z', lastSignedInAtUtc: null, moderationActions: 3 }
  const code = '0123456789abcdef0123456789abcdef'
  const writes: { path: string; body: unknown; csrf: string | null }[] = []
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input)
    if (init?.method !== 'POST') return new Response(JSON.stringify([member]))
    writes.push({ path, body: JSON.parse(String(init.body)), csrf: new Headers(init.headers).get('X-CSRF-TOKEN') })
    if (path === '/api/admin/team') return new Response(JSON.stringify({ member: { ...member, id: 'new-member-id', username: 'NewModerator', moderationActions: 0 }, accessCode: code }))
    if (path.endsWith('/status')) {
      member = { ...member, enabled: false, version: member.version + 1 }
      return new Response(JSON.stringify(member))
    }
    member = { ...member, enabled: true, version: member.version + 1 }
    return new Response(JSON.stringify({ member, accessCode: code }))
  })
  vi.stubGlobal('fetch', fetchMock)
  const copy = vi.fn().mockResolvedValue(undefined)
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: copy } })
  render(<TeamMembers csrf="csrf-token" />)
  return { code, writes, copy, fetchMock }
}

it('tracks activity and shows a private invitation once without placing the code in requests or storage', async () => {
  const { code, writes, copy } = setup()
  await screen.findByText('TestModerator')
  expect(screen.getByText('3')).toBeTruthy()
  expect(screen.getByText('Not signed in yet')).toBeTruthy()
  fireEvent.change(screen.getByLabelText('New member username'), { target: { value: 'NewModerator' } })
  fireEvent.click(screen.getByRole('button', { name: 'Invite team member' }))
  const dialog = await screen.findByRole('dialog', { name: 'Invitation ready' })
  expect((within(dialog).getByLabelText('Invitation code') as HTMLInputElement).value).toBe(code)
  fireEvent.click(within(dialog).getByRole('button', { name: 'Copy invitation' }))
  await screen.findByText('Invitation copied. Share it privately with this team member.')
  expect(copy).toHaveBeenCalledWith(expect.stringContaining(`Username: NewModerator\nAccess code: ${code}`))
  expect(writes).toEqual([{ path: '/api/admin/team', body: { username: 'NewModerator' }, csrf: 'csrf-token' }])
  fireEvent.click(within(dialog).getByRole('button', { name: 'Done' }))
  expect(screen.queryByLabelText('Invitation code')).toBeNull()
})

it('confirms disabling, keeps activity and restores access only with a new code', async () => {
  const { writes } = setup()
  await screen.findByText('TestModerator')
  fireEvent.click(screen.getByRole('button', { name: 'Disable access' }))
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel' }))
  expect(writes).toHaveLength(0)
  fireEvent.click(screen.getByRole('button', { name: 'Disable access' }))
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Disable access' }))
  await screen.findByText('Disabled')
  expect(screen.getByText('3')).toBeTruthy()
  fireEvent.click(screen.getByRole('button', { name: 'Invite again' }))
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Create new code' }))
  await screen.findByRole('dialog', { name: 'Invitation ready' })
  expect(screen.getByText('Active moderator')).toBeTruthy()
  expect(writes.map(item => item.body)).toEqual([{ version: 1, enabled: false }, { version: 2 }])
})

it('reports clipboard failure while keeping the code available for manual copy', async () => {
  const { copy } = setup()
  copy.mockRejectedValue(new DOMException('Denied', 'NotAllowedError'))
  await screen.findByText('TestModerator')
  fireEvent.click(screen.getByRole('button', { name: 'Rotate code' }))
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Create new code' }))
  const dialog = await screen.findByRole('dialog', { name: 'Invitation ready' })
  fireEvent.click(within(dialog).getByRole('button', { name: 'Copy invitation' }))
  expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'Clipboard access failed. Select and copy the username and code below instead.')
  expect(within(dialog).getByLabelText('Invitation code')).toBeTruthy()
})

it('surfaces stale member errors rather than reporting a successful rotation', async () => {
  const { fetchMock } = setup()
  await screen.findByText('TestModerator')
  fireEvent.click(screen.getByRole('button', { name: 'Rotate code' }))
  fetchMock.mockImplementationOnce(async () => new Response('{"title":"This member changed. Reload the team."}', { status: 409 }))
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Create new code' }))
  expect(await screen.findByRole('alert')).toHaveProperty('textContent', 'This member changed. Reload the team.')
  expect(screen.queryByRole('dialog', { name: 'Invitation ready' })).toBeNull()
})
