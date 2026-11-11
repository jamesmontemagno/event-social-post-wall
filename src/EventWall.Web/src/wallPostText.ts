const closingBrackets = new Map([['(', ')'], ['[', ']'], ['{', '}']])

export function formatWallPostText(text: string) {
  let removed = false
  const formatted = text.replace(/([ \t]*)([[({]?)\b[a-z][a-z\d+.-]*:\/\/[^\s<>"']+/gi,
    (match, spacing: string, opening: string) => {
      const raw = match.slice(spacing.length + opening.length)
      const url = raw.replace(/[.,!?;:)\]}]+$/, '')
      if (!/^https?:\/\/t\.co(?::\d+)?(?:[/?#]|$)/i.test(url)) return match
      removed = true
      const punctuation = raw.slice(url.length)
      const closing = closingBrackets.get(opening)
      return closing && punctuation.startsWith(closing) ? punctuation.slice(1) : punctuation
    })
  return removed ? formatted.trim() : text
}
