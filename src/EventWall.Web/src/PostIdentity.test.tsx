import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, it } from 'vitest'
import PostIdentity, { ProfileName } from './PostIdentity'
import type { Post } from './shared'

afterEach(cleanup)
const post: Post = { id: 'one', source: 'Bluesky', authorName: 'Viewer', text: 'Hello', receivedAtUtc: '2026-10-26' }

it('always shows a recognizable service icon without needing an avatar', () => {
  render(<PostIdentity post={post} />)
  expect(screen.getByRole('img', { name: 'Bluesky' })).toBeTruthy()
  expect(screen.queryByAltText('Avatar of Viewer')).toBeNull()
})
it('shows supplied avatars without sending a referrer and falls back on load failure', () => {
  render(<PostIdentity post={{ ...post, authorAvatarUrl: 'https://cdn.bsky.app/avatar.jpg' }} />)
  const image = screen.getByAltText('Avatar of Viewer')
  expect(image.getAttribute('referrerpolicy')).toBe('no-referrer')
  fireEvent.error(image)
  expect(screen.queryByAltText('Avatar of Viewer')).toBeNull()
  expect(screen.getByRole('img', { name: 'Bluesky' })).toBeTruthy()
})
it('keeps the portal bot tied to the question rather than its author or moderation metadata', () => {
  const question: Post = { ...post, source: 'Portal', id: 'question-one', authorAvatarUrl: 'https://example.test/unused.png' }
  const { rerender } = render(<PostIdentity post={question} />)
  const image = screen.getByRole('img', { name: '.NET bot question icon' })
  const url = image.getAttribute('src')
  expect(url).toMatch(/\/portal-bots\/[\w-]+\.png$/)
  expect(image.getAttribute('referrerpolicy')).toBe('no-referrer')
  expect(screen.getByRole('img', { name: 'Event portal' })).toBeTruthy()
  rerender(<PostIdentity post={{ ...question, authorName: 'A renamed attendee', status: 'Live', version: 3 }} />)
  expect(screen.getByRole('img', { name: '.NET bot question icon' }).getAttribute('src')).toBe(url)
  rerender(<PostIdentity post={{ ...question, id: 'question-two' }} />)
  expect(screen.getByRole('img', { name: '.NET bot question icon' }).getAttribute('src')).not.toBe(url)
})
it('uses all twelve local bot variants across portal questions', () => {
  const { rerender } = render(<PostIdentity post={{ ...post, source: 'Portal' }} />)
  const urls = new Set<string | null>()
  for (let index = 0; index < 120; index++) {
    rerender(<PostIdentity post={{ ...post, source: 'Portal', id: `question-${index}` }} />)
    urls.add(screen.getByRole('img', { name: '.NET bot question icon' }).getAttribute('src'))
  }
  expect(urls.size).toBe(12)
})
it('retains the portal service icon when a bot fails and can show a different question bot', () => {
  const { rerender } = render(<PostIdentity post={{ ...post, source: 'Portal', id: 'question-one' }} />)
  fireEvent.error(screen.getByRole('img', { name: '.NET bot question icon' }))
  expect(screen.queryByRole('img', { name: '.NET bot question icon' })).toBeNull()
  expect(screen.getByRole('img', { name: 'Event portal' })).toBeTruthy()
  rerender(<PostIdentity post={{ ...post, source: 'Portal', id: 'question-two' }} />)
  expect(screen.getByRole('img', { name: '.NET bot question icon' })).toBeTruthy()
})
it('links the profile safely without turning a display name into an identity', () => {
  render(<ProfileName post={{ ...post, authorProfileUrl: 'https://bsky.app/profile/did:plc:test' }} />)
  expect(screen.getByRole('link', { name: 'Viewer' }).getAttribute('rel')).toBe('noopener noreferrer')
})
