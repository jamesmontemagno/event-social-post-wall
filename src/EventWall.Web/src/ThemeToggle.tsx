import { useEffect, useState } from 'react'
import { readTheme, themeStorageKey } from './theme'

export default function ThemeToggle() {
  const [theme, setTheme] = useState(readTheme)
  const [error, setError] = useState('')
  useEffect(() => {
    document.documentElement.dataset.theme = theme
  }, [theme])

  function toggle() {
    const next = theme === 'dark' ? 'light' : 'dark'
    setTheme(next)
    try {
      localStorage.setItem(themeStorageKey, next)
      setError('')
    } catch (cause) {
      console.warn('Could not save the color theme.', cause)
      setError('Theme changed, but this browser could not save your preference.')
    }
  }

  return (
    <span className="theme-control">
      <button className="theme-toggle" type="button" onClick={toggle}
        aria-label="Light theme" aria-pressed={theme === 'light'}
        title={`Switch to ${theme === 'dark' ? 'light' : 'dark'} theme`}>
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
          {theme === 'dark'
            ? <><circle cx="12" cy="12" r="4" /><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5" /></>
            : <path d="M20.5 14A8.5 8.5 0 0 1 10 3.5 8.5 8.5 0 1 0 20.5 14Z" />}
        </svg>
        <span>{theme === 'dark' ? 'Light' : 'Dark'}</span>
      </button>
      {error && <span className="theme-error" role="status">{error}</span>}
    </span>
  )
}
