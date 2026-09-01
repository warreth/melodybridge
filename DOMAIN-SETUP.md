# Domain setup guide

This guide connects the two websites to your melodybridge.app domain
(from Cloudflare):

| Site | Final URL | Where it lives |
|---|---|---|
| Documentation | https://docs.melodybridge.app | this repo, the `docs/` folder, built by VitePress |
| Landing page | https://melodybridge.app | a second repo built from the `website/` folder |

Why a second repo? GitHub allows **one** Pages site per repository. The
main repo already uses its Pages slot for the docs, so the landing page
needs a repo of its own.

Everything below is done once, in the web browser and the Cloudflare
dashboard. No code changes needed.

---

## Part 1: the docs site (docs.melodybridge.app)

You only touch this repo.

**Step 1. Let GitHub Actions build the site**

1. Go to https://github.com/warreth/melodybridge/settings/pages
2. Under **Build and deployment**, find **Source**
3. Pick **GitHub Actions** from the dropdown

From now on the workflow in
`.github/workflows/deploy.yml` builds the docs on every push to main
that changes `docs/` or the docs toolchain, and publishes the result.

**Step 2. Run it once**

1. Go to https://github.com/warreth/melodybridge/actions
2. Click **Docs site** in the left list
3. Click **Run workflow**, then the green **Run workflow** button
4. Wait for the run to turn green. It fails here if Pages is not yet set
   to GitHub Actions; recheck Step 1 and run again

The site now answers on the temporary URL
https://warreth.github.io/melodybridge (or similar). Check that it loads.

**Step 3. Attach the domain**

1. First do the DNS part in Part 3 below (Cloudflare), otherwise
   validation in this step will fail
2. Back in https://github.com/warreth/melodybridge/settings/pages, find
   **Custom domain**
3. Type `docs.melodybridge.app` and press **Save**
4. Wait for the message "Your site is live at ..." or a DNS check
   message. If it says the domain is not configured, the DNS record is
   not visible yet; give it ten minutes and reload
5. Once the check passes, tick **Enforce HTTPS**. GitHub needs up to an
   hour to issue the certificate; the option stays greyed out until it
   is ready

The workflow writes a `CNAME` file into every build, so the domain
survives redeploys.

---

## Part 2: the landing page (melodybridge.app)

**Step 1. Create the repo**

1. Go to https://github.com/new
2. Repository name: `melodybridge.app`
3. Visibility: **Public** (private repos cannot use GitHub Pages on a
   custom domain without a paid plan)
4. Do **not** tick "Add a README". An empty repo is easier to push into
5. Click **Create repository**

**Step 2. Copy the landing page in**

The files live in the `website/` folder of the main repo. Run this in
your local clone:

```bash
cd website

# a fresh repo that contains only the landing page
rm -rf .git            # only if you copied the folder inside a clone
git init -b main
git add .
git commit -m "add landing page"

# add the Pages workflow (it sits in website/ but Pages needs it at
# the top level of the new repo)
mkdir -p .github/workflows
cp deploy-landing.yml .github/workflows/deploy.yml
git add .github
git commit -m "add pages deploy workflow"

git remote add origin https://github.com/warreth/melodybridge.app.git
git push -u origin main
```

**Step 3. Enable Pages, same as Part 1**

1. Go to https://github.com/warreth/melodybridge.app/settings/pages
2. **Source**: **GitHub Actions**
3. Then attach the domain: **Custom domain** = `melodybridge.app`,
   press Save, and tick **Enforce HTTPS** once available
4. The `CNAME` file in the repo keeps the domain attached across
   deploys

The site goes live at https://melodybridge.app once the DNS records
from Part 3 are in place.

---

## Part 3: Cloudflare DNS

Do this before or while attaching the domains above.

**Step 1. Open the DNS settings**

1. Log in at https://dash.cloudflare.com
2. Click your domain, melodybridge.app
3. In the left menu, click **DNS** and then **Records**

**Step 2. Add the record for the docs**

Click **Add record** and fill in:

| Field | Value |
|---|---|
| Type | CNAME |
| Name (subdomain) | docs |
| Target | warreth.github.io |
| Proxy status | **DNS only** (click the orange cloud until it turns grey) |
| TTL | Auto |

Click **Save**.

What this does: it tells the world "the server for
docs.melodybridge.app is GitHub Pages". The proxy must stay off
because GitHub Pages then issues and renews the HTTPS certificate for
your subdomain itself. With the orange cloud on, Cloudflare would try
to do the certificate itself and renewal can break.

**Step 3. Add the record for the root domain**

Click **Add record** again:

| Field | Value |
|---|---|
| Type | CNAME |
| Name | @ |
| Target | warreth.github.io |
| Proxy status | **DNS only** (grey cloud) |
| TTL | Auto |

Click **Save**.

The @ record covers bare melodybridge.app. Cloudflare allows a CNAME
there (which normally points at a name, not a number) by flattening it
behind the scenes. Do not add extra A records like 185.199.108.153; one
CNAME is enough and duplicates only cause confusion.

**Step 4. Check www (optional)**

If you also want www.melodybridge.app to work, repeat Step 2 with the
name `www` and the same target.

---

## Part 4: verify

1. DNS needs a few minutes to spread. Then open:
   - https://docs.melodybridge.app (the docs)
   - https://melodybridge.app (the landing page)
2. Both must load over https with a padlock
3. If a site shows "Not found": the Pages build is not green yet, or
   the custom domain was not saved. Check the Actions tab of the repo
4. If the browser warns about a certificate: wait for **Enforce HTTPS**
   to become available and tick it. This can take up to an hour after
   the domain check passes

Any time later, a plain `git push` that changes the docs or the
website is enough; the workflows pick it up automatically.
