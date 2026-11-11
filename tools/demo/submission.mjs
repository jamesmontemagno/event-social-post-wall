import assert from 'node:assert/strict'

export async function submissionHeaders(request, origin, key) {
  const response = await request.get(`${origin}/api/public/submission-config`)
  assert.equal(response.status(), 200, 'Submission bootstrap must be available without SQL')
  assert.match(response.headers()['cache-control'], /no-store/)
  const { submissionToken } = await response.json()
  assert.ok(submissionToken)
  return { 'Idempotency-Key': key, 'X-Submission-Token': submissionToken }
}
