export type Theme = 'dark' | 'light'
export const themeStorageKey = 'eventwall:theme'

export function readTheme(): Theme {
  try {
    return localStorage.getItem(themeStorageKey) === 'light' ? 'light' : 'dark'
  } catch (error) {
    console.warn('Could not read the saved color theme.', error)
    return 'dark'
  }
}

export function initializeTheme() {
  document.documentElement.dataset.theme = readTheme()
}
