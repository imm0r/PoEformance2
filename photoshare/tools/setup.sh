#!/usr/bin/env bash
#
# One-command setup: creates both repositories, publishes the app to GitHub
# Pages, and prints what is left to do by hand.
#
#   ./photoshare/tools/setup.sh evas-treff evas-treff-app "Eva's Treff"
#                               ^^^^^^^^^^ ^^^^^^^^^^^^^^ ^^^^^^^^^^^^
#                               photos     app            album title
#                               (private)  (public)
#
# Two repositories, because GitHub Pages needs a paid plan to publish from a
# private one. The split is the right shape anyway: the public repo holds only
# HTML and JavaScript — no photos, no tokens — and the private one holds
# everything that actually matters.
#
# Safe to re-run. Existing repositories are reused, not clobbered.

set -euo pipefail

PHOTOS_REPO=${1:-}
APP_REPO=${2:-}
TITLE=${3:-}

if [[ -z $PHOTOS_REPO || -z $APP_REPO || -z $TITLE ]]; then
  cat >&2 <<'USAGE'
usage: setup.sh <photo-repo> <app-repo> "<album title>"

  <photo-repo>  new PRIVATE repository for the photos, e.g. evas-treff
  <app-repo>    new PUBLIC repository for the gallery, e.g. evas-treff-app
  <album title> shown as the heading, e.g. "Eva's Treff"
USAGE
  exit 64
fi

APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../app" && pwd)"

command -v gh >/dev/null || { echo "The GitHub CLI (gh) is required: https://cli.github.com" >&2; exit 1; }
gh auth status >/dev/null 2>&1 || { echo "Not logged in. Run: gh auth login" >&2; exit 1; }

OWNER=$(gh api user --jq .login)
say() { printf '\n\033[1m%s\033[0m\n' "$*"; }

# --- repositories ----------------------------------------------------------

ensure_repo() {
  local name=$1 visibility=$2 description=$3
  if gh repo view "$OWNER/$name" >/dev/null 2>&1; then
    echo "  $OWNER/$name already exists — reusing it"
  else
    gh repo create "$OWNER/$name" "--$visibility" --description "$description" --add-readme >/dev/null
    echo "  created $OWNER/$name ($visibility)"
  fi
}

say "Repositories"
ensure_repo "$PHOTOS_REPO" private "Fotos — privat. Zugriff nur über die Album-Links."
ensure_repo "$APP_REPO" public "Fotoalbum-App (nur HTML/JS, keine Fotos, keine Tokens)."

# --- app contents ----------------------------------------------------------

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

git clone --quiet "https://github.com/$OWNER/$APP_REPO.git" "$WORK/app"
cp -r "$APP_DIR/." "$WORK/app/"

# JS string escaping, so an apostrophe or a backslash in the title cannot break
# the config file. Double quotes are used below, so only " and \ need escaping.
esc() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }

cat > "$WORK/app/config.js" <<EOF
/*
 * Written by tools/setup.sh. Points the app at the private photo repository.
 *
 * There is deliberately no token here — this file is public. The access code
 * arrives in the share link and stays in the visitor's browser.
 */
window.PHOTOSHARE_CONFIG = {
  repo: "$(esc "$OWNER/$PHOTOS_REPO")",
  branch: "main",
  title: "$(esc "$TITLE")"
};
EOF

mkdir -p "$WORK/app/.github/workflows"
cat > "$WORK/app/.github/workflows/pages.yml" <<'EOF'
name: Deploy

on:
  push:
    branches: [main]
  workflow_dispatch:

permissions:
  contents: read
  pages: write
  id-token: write

concurrency:
  group: pages
  cancel-in-progress: true

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment:
      name: github-pages
      url: ${{ steps.deploy.outputs.page_url }}
    steps:
      - uses: actions/checkout@v4
      # Pages is switched on by setup.sh, using your own credentials. It cannot
      # be done from here: creating a Pages site needs admin rights on the
      # repository, and a workflow's GITHUB_TOKEN never has those.
      - uses: actions/configure-pages@v5
      - uses: actions/upload-pages-artifact@v3
        with:
          path: .
      - id: deploy
        uses: actions/deploy-pages@v4
EOF

cat > "$WORK/app/README.md" <<EOF
# $TITLE — Galerie

Die App zum privaten Fotoalbum \`$OWNER/$PHOTOS_REPO\`.

Hier liegt **nur** HTML und JavaScript: keine Fotos, keine Zugangscodes. Ohne
den Link mit Zugangscode zeigt die Seite nichts an.

Quelle und Anleitung: [photoshare](https://github.com/$OWNER/PoEformance2/tree/main/photoshare)
EOF

# Switch Pages on before the first push, so the deploy has somewhere to land.
# This runs under your gh login, which has the admin rights the workflow token
# structurally cannot have.
say "GitHub Pages"
if gh api "repos/$OWNER/$APP_REPO/pages" >/dev/null 2>&1; then
  echo "  already enabled"
elif gh api -X POST "repos/$OWNER/$APP_REPO/pages" -f build_type=workflow >/dev/null 2>&1; then
  echo "  enabled (source: GitHub Actions)"
else
  echo "  could not enable it automatically. Switch it on by hand:"
  echo "  https://github.com/$OWNER/$APP_REPO/settings/pages -> Source: GitHub Actions"
fi

say "Publishing the app"
git -C "$WORK/app" add -A
if git -C "$WORK/app" diff --cached --quiet; then
  echo "  nothing changed"
else
  git -C "$WORK/app" -c user.name="photoshare setup" -c user.email="noreply@github.com" \
    commit --quiet -m "Deploy $TITLE gallery"
  git -C "$WORK/app" push --quiet origin HEAD:main
  echo "  pushed"
fi

say "Waiting for GitHub Pages"
sleep 5
if ! gh run watch --repo "$OWNER/$APP_REPO" --exit-status \
      "$(gh run list --repo "$OWNER/$APP_REPO" --limit 1 --json databaseId --jq '.[0].databaseId')" >/dev/null 2>&1; then
  echo "  the deploy did not finish cleanly — check:"
  echo "  https://github.com/$OWNER/$APP_REPO/actions"
fi

URL="https://$OWNER.github.io/$APP_REPO"

# --- what only a human can do ---------------------------------------------

cat <<EOF

$(printf '\033[1mDone — except the tokens.\033[0m')

GitHub has no API for creating personal access tokens, on purpose: a token that
could mint more tokens would be a master key. So these two are yours to make.

  1. Open  https://github.com/settings/personal-access-tokens/new
  2. Repository access -> Only select repositories -> $PHOTOS_REPO
  3. Permissions -> Repository permissions -> Contents

     Make it twice:
       "$TITLE ansehen"    Contents: Read-only        -> for everyone
       "$TITLE hochladen"  Contents: Read and write   -> for contributors

     Set an expiry date. When it lapses the links stop working by themselves,
     which is what you want for something shared into a group chat.

  4. Open  $URL/share.html
     Paste $OWNER/$PHOTOS_REPO and both tokens. It builds the two links,
     with QR codes and a WhatsApp button. Nothing is sent anywhere — that page
     only assembles URLs.

The album itself: $URL
EOF
