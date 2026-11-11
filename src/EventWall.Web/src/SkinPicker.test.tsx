import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import SkinPicker from './SkinPicker'
import { NetBotFlyby as Flyby } from './NetBot'
import { firstFlybyDelayMs, flybyDurationMs, maxFlybyGapMs, minFlybyGapMs, nextFlybyGap, planFlight, useNetBotFlight } from './netbotFlight'
import { initializeSkin, setSkin } from './skin'

function NetBotFlyby() {
  const { flight } = useNetBotFlight()
  return flight && <Flyby flight={flight} />
}

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  localStorage.clear()
  delete document.documentElement.dataset.skin
})

it('defaults to Aurora and lets visitors choose another wall style', () => {
  initializeSkin()
  expect(document.documentElement.dataset.skin).toBe('aurora')
  expect(localStorage.getItem('eventwall:skin')).toBeNull()
  render(<SkinPicker />)
  const picker = screen.getByRole('combobox', { name: 'Wall style' }) as HTMLSelectElement
  expect(picker.value).toBe('aurora')
  fireEvent.change(picker, { target: { value: 'aurora-pink' } })
  expect(document.documentElement.dataset.skin).toBe('aurora-pink')
  expect(localStorage.getItem('eventwall:skin')).toBe('aurora-pink')
  fireEvent.change(picker, { target: { value: 'classic' } })
  expect(document.documentElement.dataset.skin).toBeUndefined()
  expect(picker.value).toBe('classic')
  expect(localStorage.getItem('eventwall:skin')).toBe('classic')
})

it.each([
  { saved: 'classic', attribute: undefined },
  { saved: 'aurora', attribute: 'aurora' },
  { saved: 'aurora-pink', attribute: 'aurora-pink' },
])('restores the saved $saved wall style', ({ saved, attribute }) => {
  localStorage.setItem('eventwall:skin', saved)
  initializeSkin()
  render(<SkinPicker />)
  expect(document.documentElement.dataset.skin).toBe(attribute)
  expect((screen.getByRole('combobox', { name: 'Wall style' }) as HTMLSelectElement).value).toBe(saved)
})

it('uses the default wall style when the saved value is invalid', () => {
  localStorage.setItem('eventwall:skin', 'invalid')
  initializeSkin()
  expect(document.documentElement.dataset.skin).toBe('aurora')
})

it('uses the default wall style and warns when storage cannot be read', () => {
  const warning = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('Storage disabled') })
  initializeSkin()
  expect(document.documentElement.dataset.skin).toBe('aurora')
  expect(warning).toHaveBeenCalledWith('Could not read the saved wall style.', expect.any(Error))
})

it('still changes skin and reports when persistence is unavailable', () => {
  vi.spyOn(console, 'warn').mockImplementation(() => undefined)
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage disabled') })
  initializeSkin()
  render(<SkinPicker />)
  fireEvent.change(screen.getByRole('combobox', { name: 'Wall style' }), { target: { value: 'classic' } })
  expect(document.documentElement.dataset.skin).toBeUndefined()
  expect(screen.getByRole('status').textContent).toContain('could not save')
})

it('flies NetBot at random 1-2 minute gaps only while a Aurora skin is active', () => {
  vi.useFakeTimers()
  const random = vi.spyOn(Math, 'random').mockReturnValue(1)
  setSkin('classic')
  render(<NetBotFlyby />)
  act(() => { vi.advanceTimersByTime(firstFlybyDelayMs) })
  expect(screen.queryByTestId('netbot-flyby')).toBeNull()

  act(() => { setSkin('aurora') })
  act(() => { vi.advanceTimersByTime(firstFlybyDelayMs - 1) })
  expect(screen.queryByTestId('netbot-flyby')).toBeNull()
  act(() => { vi.advanceTimersByTime(1) })
  expect(screen.getByTestId('netbot-flyby').getAttribute('aria-hidden')).toBe('true')
  act(() => { vi.advanceTimersByTime(flybyDurationMs) })
  expect(screen.queryByTestId('netbot-flyby')).toBeNull()

  // The gap after this flight uses the longest possible wait.
  act(() => { vi.advanceTimersByTime(maxFlybyGapMs - flybyDurationMs - 1) })
  expect(screen.queryByTestId('netbot-flyby')).toBeNull()
  random.mockReturnValue(0)
  act(() => { vi.advanceTimersByTime(1) })
  expect(screen.getByTestId('netbot-flyby')).toBeTruthy()
  act(() => { vi.advanceTimersByTime(minFlybyGapMs) })
  expect(screen.getByTestId('netbot-flyby')).toBeTruthy()

  act(() => { setSkin('classic') })
  expect(screen.queryByTestId('netbot-flyby')).toBeNull()
})

it('keeps fly-by gaps between one and two minutes', () => {
  expect(nextFlybyGap(() => 0)).toBe(60_000)
  expect(nextFlybyGap(() => .5)).toBe(90_000)
  expect(nextFlybyGap(() => 1)).toBe(120_000)
})

it('plans every rocket pose travelling the way its art faces, never mirrored', () => {
  const sequence = (...values: number[]) => () => values.shift() ?? 0
  const cruiseLeft = planFlight(sequence(0, 0, 0, 0))
  expect(cruiseLeft.kind).toBe('cruise-left')
  expect(cruiseLeft.style).toMatchObject({ top: '8vh', '--fly-tilt': '-4deg', '--fly-rise': '-14vh' })
  const cruise = planFlight(sequence(.5, 1, 1, 1))
  expect(cruise.kind).toBe('cruise')
  expect(cruise.style).toMatchObject({ top: '72vh', '--fly-tilt': '4deg', '--fly-rise': '10vh' })
  const launch = planFlight(sequence(1, 0, 0, 0))
  expect(launch.kind).toBe('launch')
  expect(launch.style).toMatchObject({ left: '6vw', '--fly-tilt': '-6deg', '--fly-drift': '-10vw' })
  for (const { style } of [cruiseLeft, cruise, launch]) expect(style).not.toHaveProperty('--fly-flip')
})

it('keeps NetBot grounded when reduced motion is requested', () => {
  vi.useFakeTimers()
  vi.stubGlobal('matchMedia', (query: string) => ({ matches: query.includes('reduce'), addEventListener() {}, removeEventListener() {} }))
  setSkin('aurora')
  render(<NetBotFlyby />)
  act(() => { vi.advanceTimersByTime(firstFlybyDelayMs + maxFlybyGapMs) })
  expect(screen.queryByTestId('netbot-flyby')).toBeNull()
})
