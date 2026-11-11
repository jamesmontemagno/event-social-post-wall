import { useSyncExternalStore } from 'react'

export type Skin = 'classic' | 'aurora' | 'aurora-pink'
export const skinStorageKey = 'eventwall:skin'
const defaultSkin: Skin = 'aurora'
export const skinOptions: { value: Skin; label: string }[] = [
  { value: 'classic', label: 'Classic' },
  { value: 'aurora', label: 'Aurora' },
  { value: 'aurora-pink', label: 'Aurora pink' },
]

const listeners = new Set<() => void>()

function isSkin(value: string | null): value is Skin {
  return skinOptions.some(option => option.value === value)
}

export function readSkin(): Skin {
  try {
    const saved = localStorage.getItem(skinStorageKey)
    return isSkin(saved) ? saved : defaultSkin
  } catch (error) {
    console.warn('Could not read the saved wall style.', error)
    return defaultSkin
  }
}

function applySkin(skin: Skin) {
  if (skin === 'classic') delete document.documentElement.dataset.skin
  else document.documentElement.dataset.skin = skin
}

export function currentSkin(): Skin {
  const value = document.documentElement.dataset.skin ?? null
  // Classic is represented by the absence of data-skin after initialization.
  return isSkin(value) ? value : 'classic'
}

export function initializeSkin() {
  applySkin(readSkin())
}

/** Applies the skin immediately and returns false when the preference could not be saved. */
export function setSkin(skin: Skin): boolean {
  applySkin(skin)
  listeners.forEach(listener => listener())
  try {
    localStorage.setItem(skinStorageKey, skin)
    return true
  } catch (error) {
    console.warn('Could not save the wall style.', error)
    return false
  }
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}

export function useSkin() {
  return useSyncExternalStore(subscribe, currentSkin)
}
