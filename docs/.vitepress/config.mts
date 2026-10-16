import { defineConfig } from 'vitepress'

// Site lives at https://docs.melodybridge.app (custom domain, root path).
export default defineConfig({
  title: 'MelodyBridge',
  description: 'Self-hosted music toolbox: save Spotify and YouTube playlists, download the tracks through a plugin waterfall, and publish M3U, Jellyfin, Plex or Navidrome playlists.',
  lang: 'en',
  head: [
    ['link', { rel: 'icon', type: 'image/svg+xml', href: '/favicon.svg' }]
  ],
  cleanUrls: true,
  // localhost links in dev instructions are not pages on this site
  ignoreDeadLinks: [/^https?:\/\/localhost/],
  themeConfig: {
    siteTitle: 'MelodyBridge',
    logo: '/favicon.svg',
    nav: [
      { text: 'Docs', link: '/' },
      { text: 'Quick start', link: '/quickstart' },
      {
        text: 'Contribute',
        items: [
          { text: 'Developer guide', link: '/developer' }
        ]
      },
      {
        text: 'Community',
        items: [
          { text: 'GitHub', link: 'https://github.com/warreth/melodybridge' },
          { text: 'Report an issue', link: 'https://github.com/warreth/melodybridge/issues' }
        ]
      }
    ],
    sidebar: [
      {
        text: 'Getting started',
        items: [
          { text: 'Overview', link: '/' },
          { text: 'Quick start', link: '/quickstart' },
          { text: 'Docker deployment', link: '/docker' }
        ]
      },
      {
        text: 'Core concepts',
        items: [
          { text: 'How it works', link: '/concepts' }
        ]
      },
      {
        text: 'Publishing',
        items: [
          { text: 'Publishing playlists', link: '/publishing' }
        ]
      },
      {
        text: 'Setup',
        items: [
          { text: 'Accounts and imports', link: '/accounts' },
          { text: 'Lucida and FlareSolverr', link: '/lucida' },
          { text: 'Soulseek and slskd', link: '/slskd' },
          { text: 'Qobuz and qobuz-dlp', link: '/qobuz' },
          { text: 'Deezer and deemix', link: '/deemix' }
        ]
      },
      {
        text: 'Reference',
        items: [
          { text: 'Settings and options', link: '/reference' }
        ]
      },
      {
        text: 'Contribute',
        items: [
          { text: 'Developer guide', link: '/developer' }
        ]
      }
    ],
    socialLinks: [
      { icon: 'github', link: 'https://github.com/warreth/melodybridge' }
    ],
    outline: { level: [2, 3] },
    docFooter: { prev: 'Previous', next: 'Next' },
    search: {
      provider: 'local',
      options: {
        translations: {
          button: { buttonText: 'Search', buttonAriaLabel: 'Search docs' },
          modal: {
            noResultsText: 'No results',
            resetButtonTitle: 'Clear query',
            footer: { selectText: 'Select', navigateText: 'Navigate', closeText: 'Close' }
          }
        }
      }
    }
  }
})
