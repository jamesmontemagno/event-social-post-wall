import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { useState } from 'react'
import DismissibleNotice, { NOTICE_TIMEOUT_MS } from './DismissibleNotice'

function Host() {
  const [notice, setNotice] = useState('Post rejected.')
  return notice ? <DismissibleNotice message={notice} onDismiss={() => setNotice('')} /> : null
}

beforeEach(() => { vi.useFakeTimers() })
afterEach(() => { cleanup(); vi.useRealTimers() })

it('dismisses itself after five seconds', () => {
  render(<Host />)
  act(() => { vi.advanceTimersByTime(NOTICE_TIMEOUT_MS - 1) })
  expect(screen.getByRole('status').textContent).toBe('Post rejected.')
  act(() => { vi.advanceTimersByTime(1) })
  expect(screen.queryByRole('status')).toBeNull()
})

it('closes immediately from the dismiss button', () => {
  render(<Host />)
  fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
  expect(screen.queryByRole('status')).toBeNull()
})

it('stays open while hovered or focused and restarts the countdown afterward', () => {
  render(<Host />)
  const close = screen.getByRole('button', { name: 'Dismiss notification' })
  fireEvent.mouseEnter(close.parentElement!)
  act(() => { vi.advanceTimersByTime(NOTICE_TIMEOUT_MS * 2) })
  fireEvent.mouseLeave(close.parentElement!)
  fireEvent.focus(close)
  act(() => { vi.advanceTimersByTime(NOTICE_TIMEOUT_MS * 2) })
  expect(screen.getByRole('status')).toBeTruthy()
  fireEvent.blur(close)
  act(() => { vi.advanceTimersByTime(NOTICE_TIMEOUT_MS) })
  expect(screen.queryByRole('status')).toBeNull()
})
