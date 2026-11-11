import { lazy, StrictMode, Suspense } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import { initializeTheme } from './theme'

initializeTheme()

const App = import.meta.env.VITE_SITE_ROLE === 'admin'
  ? lazy(() => import('./AdminApp'))
  : lazy(() => import('./App'))

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Suspense fallback={<div className="empty-state">Loading…</div>}><App /></Suspense>
  </StrictMode>,
)
