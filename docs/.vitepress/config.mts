import { defineConfig } from 'vitepress'

// Site lives at https://docs.melodybridge.app (custom domain, root path).
export default defineConfig({
  title: 'MelodyBridge',
  description: 'Self-hosted music toolbox: fetch playlists, download the tracks, publish M3U and Jellyfin playlists.',
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
      { text: 'Features', link: '/features' },
      { text: 'User guide', link: '/user-guide' },
      {
        text: 'Contribute',
        items: [
          { text: 'Developer guide', link: '/developer' },
          { text: 'Photino build', link: '/photino' }
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
          { text: 'Features', link: '/features' },
          { text: 'User guide', link: '/user-guide' }
        ]
      },
      {
        text: 'Deploy and configure',
        items: [
          { text: 'Docker', link: '/docker' },
          { text: 'Accounts and OAuth', link: '/accounts' },
          { text: 'Lucida and FlareSolverr', link: '/lucida' }
        ]
      },
      {
        text: 'Contribute',
        items: [
          { text: 'Developer guide', link: '/developer' },
          { text: 'Photino desktop build', link: '/photino' }
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
