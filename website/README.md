# melodybridge.app

The landing page for MelodyBridge, served by GitHub Pages at
https://melodybridge.app.

Plain HTML and CSS, no build step, no dependencies. The files:

- `index.html` - the page
- `style.css` - the styles (design tokens in `:root`, dark mode included)
- `favicon.svg` - the logo
- `CNAME` - the custom domain for GitHub Pages
- `deploy-landing.yml` - the deploy workflow, copy to
  `.github/workflows/deploy.yml` in this repo

See [../DOMAIN-SETUP.md](../DOMAIN-SETUP.md) for the full domain and
GitHub Pages configuration steps.
