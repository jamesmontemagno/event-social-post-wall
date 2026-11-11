import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import ThemeToggle from './ThemeToggle'
import { initializeTheme } from './theme'

afterEach(() => {
  cleanup()
  vi.restoreAllMocks()
  localStorage.clear()
  delete document.documentElement.dataset.theme
})

it('preserves dark as the default and toggles both ways accessibly', () => {
  initializeTheme()
  expect(document.documentElement.dataset.theme).toBe('dark')
  render(<ThemeToggle />)
  const toggle = screen.getByRole('button', { name: 'Light theme', pressed: false })
  fireEvent.click(toggle)
  expect(document.documentElement.dataset.theme).toBe('light')
  expect(toggle.getAttribute('aria-pressed')).toBe('true')
  expect(localStorage.getItem('eventwall:theme')).toBe('light')
  fireEvent.click(toggle)
  expect(document.documentElement.dataset.theme).toBe('dark')
  expect(localStorage.getItem('eventwall:theme')).toBe('dark')
})

it('restores a saved light preference on startup and remount', () => {
  localStorage.setItem('eventwall:theme', 'light')
  initializeTheme()
  expect(document.documentElement.dataset.theme).toBe('light')
  const { unmount } = render(<ThemeToggle />)
  expect(screen.getByRole('button', { name: 'Light theme', pressed: true })).toBeTruthy()
  unmount()
  render(<ThemeToggle />)
  expect(screen.getByRole('button', { name: 'Light theme', pressed: true })).toBeTruthy()
})

it('ignores invalid stored values', () => {
  localStorage.setItem('eventwall:theme', 'invalid')
  initializeTheme()
  expect(document.documentElement.dataset.theme).toBe('dark')
})

it('still changes theme and reports when persistence is unavailable', () => {
  const warning = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage disabled') })
  render(<ThemeToggle />)
  fireEvent.click(screen.getByRole('button', { name: 'Light theme' }))
  expect(document.documentElement.dataset.theme).toBe('light')
  expect(screen.getByRole('status').textContent).toContain('could not save')
  expect(warning).toHaveBeenCalledOnce()
})

it('keeps the UI usable when reading browser storage fails', () => {
  const warning = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('Storage disabled') })
  initializeTheme()
  render(<ThemeToggle />)
  expect(screen.getByRole('button', { name: 'Light theme', pressed: false })).toBeTruthy()
  expect(warning).toHaveBeenCalled()
})
