import { cleanup, render, screen, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import AdminApp from './AdminApp'
import type { EventInfo } from './shared'

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})

function mockAdminFetch(event: Partial<EventInfo> = {}) {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) =>
    new Response(JSON.stringify(String(input).includes('/api/admin/session-status')
      ? { configured: true, authenticated: false }
      : {
          slug: 'communityevent', name: 'Community Event', submissionsOpen: true,
          wallSiteUrl: 'https://wall.example.test/event/',
          submissionSiteUrl: 'https://questions.example.test/ask/',
          ...event,
        }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })))
}

it('serves the staff sign-in without embedding the wall or question form', async () => {
  mockAdminFetch()
  render(<AdminApp />)
  expect(await screen.findByLabelText('Access code')).toBeTruthy()
  expect(screen.getByLabelText('Username')).toBeTruthy()
  expect(screen.getByText('PRIVATE CONTROL ROOM')).toBeTruthy()
  for (const name of ['ASP.NET Core', 'Blazor Server', 'React', 'TypeScript', 'Aspire', 'GitHub Copilot']) {
    expect(screen.getByRole('link', { name }).getAttribute('href')).toMatch(/^https:\/\//)
  }
  expect(screen.getByRole('link', { name: 'Third-party licenses and notices' }).getAttribute('href')).toBe('/THIRD-PARTY-NOTICES.txt')
  expect(screen.queryByRole('button', { name: 'Join the conversation' })).toBeNull()
  expect(screen.queryByRole('heading', { name: /the room is/i })).toBeNull()
})

it('opens the configured wall and questions portal from the staff navigation in new tabs', async () => {
  mockAdminFetch()
  render(<AdminApp />)

  const navigation = screen.getByRole('navigation', { name: 'Main navigation' })
  expect(within(navigation).getByText('Staff console')).toBeTruthy()
  for (const [label, href] of [
    ['Open wall', 'https://wall.example.test/event/'],
    ['Open questions portal', 'https://questions.example.test/ask/'],
  ]) {
    const link = await within(navigation).findByRole('link', { name: `${label} (opens in a new tab)` })
    expect(link.getAttribute('href')).toBe(href)
    expect(link.getAttribute('target')).toBe('_blank')
    expect(link.getAttribute('rel')?.split(' ')).toEqual(expect.arrayContaining(['noopener', 'noreferrer']))
  }
})

it.each([
  {
    event: { wallSiteUrl: null }, disabledLabel: 'Open wall', missingSetting: 'Public__WallSiteUrl',
    availableLabel: 'Open questions portal', availableHref: 'https://questions.example.test/ask/',
    availableSetting: 'Public__SubmissionSiteUrl',
  },
  {
    event: { submissionSiteUrl: null }, disabledLabel: 'Open questions portal', missingSetting: 'Public__SubmissionSiteUrl',
    availableLabel: 'Open wall', availableHref: 'https://wall.example.test/event/',
    availableSetting: 'Public__WallSiteUrl',
  },
])('disables only $disabledLabel when its site address is unavailable', async ({
  event, disabledLabel, missingSetting, availableLabel, availableHref, availableSetting,
}) => {
  mockAdminFetch(event)
  render(<AdminApp />)

  expect(await screen.findByText(`Set ${missingSetting} to valid site URLs on the admin deployment to enable the links.`)).toBeTruthy()
  expect(screen.getByRole('button', { name: disabledLabel }).hasAttribute('disabled')).toBe(true)
  expect(screen.getByRole('link', { name: `${availableLabel} (opens in a new tab)` }).getAttribute('href')).toBe(availableHref)
  expect(screen.queryByText(new RegExp(availableSetting))).toBeNull()
})

it('reports event-loading errors instead of guessing site destinations', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    if (String(input).includes('/api/admin/session-status')) {
      return new Response(JSON.stringify({ configured: true, authenticated: false }), { status: 200 })
    }
    return new Response(JSON.stringify({ title: 'Event settings unavailable' }), { status: 503 })
  }))
  render(<AdminApp />)

  expect((await screen.findByRole('alert')).textContent).toContain('Event settings unavailable')
  expect(screen.getByRole('button', { name: 'Open wall' }).hasAttribute('disabled')).toBe(true)
  expect(screen.getByRole('button', { name: 'Open questions portal' }).hasAttribute('disabled')).toBe(true)
})
