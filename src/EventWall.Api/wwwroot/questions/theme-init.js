try {
  document.documentElement.dataset.theme = localStorage.getItem('eventwall:theme') === 'light' ? 'light' : 'dark'
} catch (error) {
  if (!(error instanceof DOMException)) throw error
  console.warn('Could not read the saved color theme.', error.name)
  document.documentElement.dataset.theme = 'dark'
}
