import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import PromoCard from './PromoCard'

afterEach(() => { cleanup(); vi.restoreAllMocks() })

it('retains the readable link when QR capacity is exceeded and recovers after the URL changes', () => {
  vi.spyOn(console, 'error').mockImplementation(() => undefined)
  const complex = `https://example.test/${'界'.repeat(2000)}`
  const { rerender } = render(<PromoCard promo={{ title: 'Survey', description: 'Help shape .NET', url: complex }} />)
  expect(screen.getByRole('alert').textContent).toContain('too complex')
  expect(screen.getByRole('link').getAttribute('href')).toBe(complex)
  rerender(<PromoCard promo={{ title: 'Survey', description: 'Help shape .NET', url: 'https://example.test/survey' }} />)
  expect(screen.queryByRole('alert')).toBeNull()
  expect(screen.getByRole('img', { name: 'QR code for https://example.test/survey' })).toBeTruthy()
})
