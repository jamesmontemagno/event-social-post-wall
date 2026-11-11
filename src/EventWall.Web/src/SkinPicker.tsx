import { useState } from 'react'
import { setSkin, skinOptions, useSkin } from './skin'
import type { Skin } from './skin'

export default function SkinPicker() {
  const skin = useSkin()
  const [error, setError] = useState('')

  return (
    <span className="theme-control skin-control">
      <select className="theme-toggle skin-select" aria-label="Wall style" title="Wall style" value={skin}
        onChange={event => setError(setSkin(event.target.value as Skin) ? '' : 'Style changed, but this browser could not save your preference.')}>
        {skinOptions.map(option => <option key={option.value} value={option.value}>{option.label}</option>)}
      </select>
      {error && <span className="theme-error" role="status">{error}</span>}
    </span>
  )
}
