import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import WallPromos from './WallPromos'

afterEach(() => { cleanup(); vi.restoreAllMocks(); localStorage.clear() })

it('keeps the toggle usable and reports a persistence failure', () => {
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('Denied', 'SecurityError') })
  render(<WallPromos eventSlug="test" promos={[]} />)
  fireEvent.click(screen.getByRole('button', { name: 'Show promotional blurbs' }))
  expect(screen.getByText('No promotional blurbs yet.')).toBeTruthy()
  expect(screen.getByRole('alert').textContent).toContain('could not save')
  expect(screen.getByRole('button', { name: 'Show promotional blurbs' }).getAttribute('aria-pressed')).toBe('true')
})

it('reports an unreadable saved preference without hiding the toggle', () => {
  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new DOMException('Denied', 'SecurityError') })
  render(<WallPromos eventSlug="test" promos={[]} />)
  expect(screen.getByRole('alert').textContent).toContain('Could not read')
  fireEvent.click(screen.getByRole('button', { name: 'Show promotional blurbs' }))
  expect(screen.getByText('No promotional blurbs yet.')).toBeTruthy()
})
