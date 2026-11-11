import { expect, it } from 'vitest'
import { formatWallPostText } from './wallPostText'

it.each([
  ['A question with  original spacing.\nAnother line.', 'A question with  original spacing.\nAnother line.'],
  ['Can Aspire deploy to Kubernetes? https://t.co/PB6cDv0XLE', 'Can Aspire deploy to Kubernetes?'],
  ['https://t.co/PB6cDv0XLE Can Aspire deploy to Kubernetes?', 'Can Aspire deploy to Kubernetes?'],
  ['Can https://t.co/one Aspire deploy https://t.co/two to Kubernetes?', 'Can Aspire deploy to Kubernetes?'],
  ['More information https://t.co/abc.', 'More information.'],
  ['Can Aspire deploy (https://t.co/abc)?', 'Can Aspire deploy?'],
  ['More information [https://t.co/abc].', 'More information.'],
  ['Read {https://t.co/abc} and discuss.', 'Read and discuss.'],
  ['Visit HTTPS://T.CO/AbC?ref=event#details #communityevent', 'Visit #communityevent'],
  ['Legacy http://t.co/abc #communityevent', 'Legacy #communityevent'],
  ['https://t.co/one https://t.co/two', ''],
  ['(https://t.co/one)', ''],
  ['See https://learn.microsoft.com/dotnet https://t.co/abc', 'See https://learn.microsoft.com/dotnet'],
  ['See https://example.test/?next=https://t.co/abc', 'See https://example.test/?next=https://t.co/abc'],
  ['Keep https://t.co.example.test/abc', 'Keep https://t.co.example.test/abc'],
  ['Keep https://t.co@example.test/abc', 'Keep https://t.co@example.test/abc'],
  ['Keep https://sub.t.co/abc', 'Keep https://sub.t.co/abc'],
  ['Keep ftp://example.test/https://t.co/abc', 'Keep ftp://example.test/https://t.co/abc'],
  ['Keep https://t.co:invalid/abc', 'Keep https://t.co:invalid/abc'],
])('formats wall text %j as %j', (text, expected) => {
  expect(formatWallPostText(text)).toBe(expected)
})
