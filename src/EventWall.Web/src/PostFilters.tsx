import { useState } from 'react'
import type { FormEvent } from 'react'
import { api } from './shared'
import DismissibleNotice from './DismissibleNotice'
import type { AdminEventSettings, PostFilterSettings } from './shared'

const rules = [
  ['profanity', 'Profanity', 'English starter terms and your blocked terms; limited common obfuscations.'],
  ['allCaps', 'All caps', 'At least 3 words and 10 cased letters, all uppercase. Technical acronym-only posts are protected.'],
  ['singleWord', 'Single word', 'One substantive word after links, mentions, and hashtags are removed.'],
  ['emojiOnly', 'Emoji only', 'Emoji reactions without substantive text. Mixed useful text and emoji are allowed.'],
  ['locationOnly', 'Location/check-in only', 'Short check-ins or exact configured location phrases, not useful messages mentioning places.'],
  ['noSubstantiveText', 'No substantive text', 'Only links, mentions, hashtags, or punctuation.'],
  ['repeatedCharacters', 'Repeated-character spam', 'At least 5 identical characters making up 80% or more of the message.'],
  ['duplicateText', 'Exact duplicate text', 'Matches an earlier post from any source, regardless of author or status. Case and whitespace must match.'],
] as const

const listFields = [
  ['blockedTerms', 'Blocked terms', 'Additional whole words or phrases to reject.'],
  ['allowedTerms', 'Allowed profanity terms', 'Exempt only these profanity terms, not other rules or blocked accounts.'],
  ['locationPhrases', 'Location-only phrases', 'Exact places or check-in phrases, such as New York. Not a worldwide city lookup.'],
] as const

function listsFrom(settings: PostFilterSettings) {
  return {
    blockedTerms: settings.blockedTerms.join('\n'),
    allowedTerms: settings.allowedTerms.join('\n'),
    locationPhrases: settings.locationPhrases.join('\n'),
  }
}

export default function PostFilters({ initial, csrf }: { initial: AdminEventSettings; csrf: string }) {
  const [settings, setSettings] = useState(initial.postFilters)
  const [lists, setLists] = useState(() => listsFrom(initial.postFilters))
  const [version, setVersion] = useState(initial.filterSettingsVersion)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')

  function applySaved(saved: AdminEventSettings) {
    setSettings(saved.postFilters)
    setLists(listsFrom(saved.postFilters))
    setVersion(saved.filterSettingsVersion)
  }

  async function save(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError('')
    setNotice('')
    const postFilters: PostFilterSettings = {
      ...settings,
      blockedTerms: lists.blockedTerms.split('\n').map(term => term.trim()).filter(Boolean),
      allowedTerms: lists.allowedTerms.split('\n').map(term => term.trim()).filter(Boolean),
      locationPhrases: lists.locationPhrases.split('\n').map(term => term.trim()).filter(Boolean),
    }
    try {
      applySaved(await api<AdminEventSettings>('/api/admin/settings/filters', {
        method: 'POST', body: JSON.stringify({ postFilters, version }),
      }, csrf))
      setNotice('Post filters saved. Existing posts and decisions are unchanged.')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not save post filters.')
    } finally {
      setBusy(false)
    }
  }

  async function reload() {
    setBusy(true)
    setError('')
    setNotice('')
    try {
      applySaved(await api<AdminEventSettings>('/api/admin/settings'))
      setNotice('Saved post filters reloaded; local edits discarded.')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not reload post filters.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="admin-panel post-filters" aria-labelledby="post-filters-title">
      <div className="queue-header"><div><h2 id="post-filters-title">Post filters</h2><p>Keep low-signal posts out of the review queue, without losing them.</p></div></div>
      <p>These event-specific rules apply to new social, portal, and staff-added posts, and material source edits. Matches are automatically rejected and retained in the Rejected view with reasons. Staff can approve false positives. Passing posts still need approval.</p>
      <p>All rules run locally. English profanity and location heuristics are not comprehensive and can make mistakes. Saving never re-filters existing posts.</p>
      <form onSubmit={event => void save(event)}>
        <fieldset disabled={busy || !csrf}>
          <legend>Automatic rejection rules</legend>
          <div className="post-filter-rules">{rules.map(([key, label, description]) => (
            <label key={key} className="post-filter-rule">
              <input type="checkbox" checked={settings[key]} onChange={event => {
                setSettings(current => ({ ...current, [key]: event.target.checked }))
                setNotice('')
              }} />
              <span><strong>{label}</strong><span>{description}</span></span>
            </label>
          ))}</div>
          <div className="post-filter-lists">{listFields.map(([key, label, description]) => (
            <label key={key} htmlFor={`filter-${key}`}>
              <strong>{label}</strong><span id={`filter-${key}-help`}>{description} One per line, up to 25 entries of 40 characters each.</span>
              <textarea id={`filter-${key}`} rows={5} value={lists[key]} aria-describedby={`filter-${key}-help`}
                onChange={event => { setLists(current => ({ ...current, [key]: event.target.value })); setNotice('') }} />
            </label>
          ))}</div>
          <div className="form-actions">
            <button type="submit" className="button button-primary">{busy ? 'Saving...' : 'Save post filters'}</button>
            <button type="button" className="button button-muted" onClick={() => void reload()}>Reload saved settings (discard edits)</button>
          </div>
        </fieldset>
      </form>
      {error && <div className="notice notice-error" role="alert">{error}</div>}
      {notice && <DismissibleNotice message={notice} onDismiss={() => setNotice('')} />}
    </section>
  )
}
