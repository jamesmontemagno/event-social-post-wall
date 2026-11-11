const modal = document.getElementById('components-reconnect-modal')
const retryButton = document.getElementById('question-reconnect-retry')
const resumeButton = document.getElementById('question-reconnect-resume')

modal.addEventListener('cancel', event => event.preventDefault())
modal.addEventListener('components-reconnect-state-changed', event => {
  if (event.detail.state === 'hide') {
    modal.close()
  } else if (!modal.open) {
    modal.showModal()
  }
})

retryButton.addEventListener('click', async () => {
  retryButton.disabled = true
  try {
    if (await Blazor.reconnect()) modal.close()
    else modal.className = 'components-reconnect-rejected'
  } catch (error) {
    console.warn('Could not reconnect the question page.', error)
    modal.className = 'components-reconnect-failed'
  } finally { retryButton.disabled = false }
})

resumeButton.addEventListener('click', async () => {
  resumeButton.disabled = true
  try {
    if (await Blazor.resumeCircuit()) modal.close()
    else modal.className = 'components-reconnect-rejected'
  } catch (error) {
    console.warn('Could not resume the question page.', error)
    modal.className = 'components-reconnect-resume-failed'
  } finally { resumeButton.disabled = false }
})

document.getElementById('question-reconnect-reload').addEventListener('click', () => location.reload())
