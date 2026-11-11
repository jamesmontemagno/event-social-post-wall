import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Event Social Post Wall',
  description: 'Run a human-moderated social wall and audience question site for your event.',
  base: process.env.DOCS_BASE || '/',
  cleanUrls: true,
  lastUpdated: true,
  themeConfig: {
    search: { provider: 'local' },
    nav: [
      { text: 'Get started', link: '/getting-started' },
      { text: 'Deploy', link: '/deployment/azure' },
      { text: 'Platforms', link: '/platforms/' },
      { text: 'GitHub', link: 'https://github.com/jamesmontemagno/event-social-post-wall' },
    ],
    sidebar: [
      { text: 'Start here', items: [
        { text: 'Overview', link: '/' },
        { text: 'Local setup', link: '/getting-started' },
        { text: 'Architecture', link: '/architecture' },
        { text: 'Environment settings', link: '/configuration' },
        { text: 'Demo and branding', link: '/customization' },
      ] },
      { text: 'Deployment', items: [
        { text: 'Azure', link: '/deployment/azure' },
        { text: 'GitHub Actions and OIDC', link: '/deployment/github-actions' },
        { text: 'Self-hosting', link: '/deployment/self-hosting' },
        { text: 'Documentation website', link: '/deployment/documentation' },
      ] },
      { text: 'Platforms', items: [
        { text: 'Before enabling a source', link: '/platforms/' },
        ...['twitch', 'youtube', 'x', 'bluesky', 'mastodon', 'turnstile'].map((name, index) => ({
          text: ['Twitch', 'YouTube', 'X', 'Bluesky', 'Mastodon', 'Cloudflare Turnstile'][index],
          link: `/platforms/${name}`,
        })),
      ] },
      { text: 'Operate an event', items: [
        { text: 'Moderation and host guide', link: '/operations/event-day' },
        { text: 'Azure scaling and costs', link: '/operations/scaling' },
        { text: 'Database, backups, upgrades', link: '/operations/database' },
        { text: 'Security and privacy', link: '/operations/security' },
        { text: 'Troubleshooting', link: '/operations/troubleshooting' },
      ] },
    ],
    editLink: {
      pattern: 'https://github.com/jamesmontemagno/event-social-post-wall/edit/main/docs/:path',
      text: 'Improve this page',
    },
    footer: { message: 'MIT licensed application. Third-party terms remain applicable.', copyright: 'Copyright 2026 James Montemagno' },
  },
})
