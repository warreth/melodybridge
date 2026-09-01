# Domain setup

This repository serves two GitHub Pages sites on the melodybridge.app
domain, registered through Cloudflare:

| Site | URL | Source |
|---|---|---|
| Docs | https://docs.melodybridge.app | this repo, `docs/` built with VitePress |
| Landing page | https://melodybridge.app | the `website/` folder, plain HTML |

One GitHub repository can host one Pages site, so the landing page lives
in its own repository (`warreth/melodybridge.app` below). If you prefer,
you can also keep both in this repo and publish the docs from a branch,
but two repos is the cleanest setup.

## 1. Docs site (this repo)

The workflow `.github/workflows/deploy.yml` already builds `docs/` and
deploys it to GitHub Pages. Do this once in the repository settings:

1. GitHub repo > **Settings** > **Pages**.
2. Under **Build and deployment**, set **Source** to
   **GitHub Actions**.
3. Push to main with a docs change, or run the workflow from the
   **Actions** tab. The first run creates the `github-pages` environment.
4. Back in **Settings** > **Pages** > **Custom domain**, enter
   `docs.melodybridge.app` and save. GitHub validates the DNS record
   (created in step 3 below) and serves the site on it.
5. Tick **Enforce HTTPS** once it is available. GitHub issues the
   certificate for the custom domain automatically.

The workflow also writes a CNAME file into the build output, so the
custom domain survives every deploy.

## 2. Landing page repo

1. Create a new public repository, for example
   `warreth/melodybridge.app`. Do not add a README or license.
2. Copy the `website/` folder from this repo into it:

   ```bash
   cd website
   git init -b main
   git remote add origin https://github.com/warreth/melodybridge.app.git
   git add .
   git commit -m "add landing page"
   git push -u origin main
   ```

3. Add a workflow file `.github/workflows/deploy.yml` in that repo (same
   shape as the one here, minus the VitePress build; copy
   `website/deploy-landing.yml` from this repo to
   `.github/workflows/deploy.yml` there).
4. In that repo: **Settings** > **Pages** > set **Source** to
   **GitHub Actions**, then set the custom domain to `melodybridge.app`
   and enable **Enforce HTTPS**.

The `website/CNAME` file in that repo keeps the domain attached across
deploys.

## 3. Cloudflare DNS

In the Cloudflare dashboard for melodybridge.app, go to **DNS** and add
these records:

| Type | Name | Content | Proxy |
|---|---|---|---|
| CNAME | docs | `<github-user>.github.io` (for example `warreth.github.io`) | DNS only |
| CNAME | @ | `<github-user>.github.io` | DNS only |

Notes:

- Replace `<github-user>` with the GitHub account that owns the repos.
- Start with the proxy **off** (grey cloud, "DNS only"). GitHub Pages
  then issues and renews its own certificate for the domain. If you
  later want the orange cloud, read
  [GitHub's proxy guidance](https://docs.github.com/en/pages/configuring-a-custom-domain-for-your-github-pages-site/managing-a-custom-domain-for-your-github-pages-site)
  first: the certificate renewal can break behind Cloudflare's proxy.
- The apex (`@`) CNAME works because Cloudflare flattens CNAME records
  at the apex. Do not add an A record for `185.199.x.x` as well; one
  CNAME is enough.
- DNS changes need a few minutes to propagate. GitHub's custom domain
  check turns green once the record is visible.

## 4. Check

After a few minutes:

- https://docs.melodybridge.app shows the docs
- https://melodybridge.app shows the landing page
- Both redirect to HTTPS and show a valid certificate

If GitHub says the domain is not configured, re-check the DNS records
and give it ten minutes. The Pages settings page always shows the
current state.
