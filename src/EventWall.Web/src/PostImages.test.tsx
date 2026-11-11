import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import PostImages from './PostImages'
import type { Post } from './shared'

afterEach(cleanup)
const post: Post = { id: 'one', source: 'Bluesky', authorName: 'Viewer', text: 'Hello', receivedAtUtc: '2026-10-26' }
it('renders at most four attachments with alt text and no referrer', () => {
  render(<PostImages post={{ ...post, images: Array.from({ length: 6 }, (_, index) => ({ url: `https://cdn.bsky.app/${index}.jpg`, alt: `Photo ${index}` })) }} />)
  expect(screen.getAllByRole('img')).toHaveLength(4)
  expect(screen.getByAltText('Photo 0').getAttribute('referrerpolicy')).toBe('no-referrer')
  expect(screen.queryByRole('button')).toBeNull()
})
it('shows an explicit unavailable state for failed images and handles missing alt text', () => {
  render(<PostImages post={{ ...post, images: [{ url: '/demo-images/code.svg', alt: null }] }} expanded />)
  fireEvent.error(screen.getByAltText('Image attached to this post'))
  expect(screen.getByText('Image unavailable')).toBeTruthy()
})
it('does not render a gallery for posts without photos', () => {
  const { container } = render(<PostImages post={post} />)
  expect(container.childElementCount).toBe(0)
})
it('provides named preview buttons only when enabled and never opens a failed attachment', () => {
  const onPreview = vi.fn()
  const images = [{ url: '/one.jpg', alt: 'First photo' }, { url: '/two.jpg', alt: 'Second photo' }]
  render(<PostImages post={{ ...post, images }} onPreview={onPreview} />)
  const second = screen.getByRole('button', { name: 'Preview image 2 of 2 by Viewer' })
  expect(second.getAttribute('aria-haspopup')).toBe('dialog')
  expect(second.getAttribute('type')).toBe('button')
  fireEvent.click(second)
  expect(onPreview).toHaveBeenCalledExactlyOnceWith(images[1])
  fireEvent.error(screen.getByAltText('First photo'))
  expect(screen.queryByRole('button', { name: 'Preview image 1 of 2 by Viewer' })).toBeNull()
  expect(screen.getByText('Image unavailable')).toBeTruthy()
  expect(onPreview).toHaveBeenCalledTimes(1)
})
