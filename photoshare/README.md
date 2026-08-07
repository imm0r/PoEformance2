# photoshare

A private photo album for a family gathering, hosted entirely on GitHub.

Guests open a link, type their name, and pick photos. No account, no app, no
server. The photos land in a **private** repository; the gallery is a static
page that can only load them with the access code baked into the share link.

```
    share link (#k=token)
            |
   +--------+--------+            +---------------------------+
   |  static app     |  HTTPS     |  private repo             |
   |  GitHub Pages   +----------->|  photos/  thumbs/         |
   |  (public, no    |  api.      |  (never public)           |
   |   secrets)      |  github    +---------------------------+
   +-----------------+  .com
```

## Why it is built this way

**No backend, and no build step.** Three HTML files, six scripts, one
stylesheet, zero dependencies. Nothing to deploy, nothing to keep running,
nothing to pay for after the party is over. Open `app/index.html` straight from
disk and it still works.

**Photos are shrunk on the phone, before upload.** A modern phone photo is
4-8 MB. Fifty guests would produce a repository nobody can clone over the hotel
wifi they are all sharing. Each photo is re-encoded to a 2560px long edge
(~400-700 KB) plus a 480px thumbnail, so the grid costs kilobytes per photo and
the upload survives a bad connection. The re-encode also strips EXIF, which
means **GPS coordinates never leave the phone** — worth knowing, because most
photos taken at home carry a home address.

**The file name is the database.** A photo is stored as
`photos/2026-08-07/153012__Oma-Lotte__a1b2c3d4.jpg`: day, time, who uploaded it,
and a hash of the content. That buys three things a metadata file would not.
The gallery can group, caption and sort everything from one API call. Two phones
uploading at the same moment cannot collide, because they only ever create new
paths. And re-uploading the same photo produces the same name, so a duplicate is
a no-op instead of a second copy.

**Comments are files too.** A comment lands at
`comments/<photo-id>/20260807T235900__Jonas__4f2a.txt`. A shared `comments.json`
would need read-modify-write, and two relatives typing at the same moment would
silently lose one of the two — separate files cannot collide. The tree listing
the gallery already fetches carries every author and timestamp, so the thread
count on a tile costs nothing; only the text itself is a request, and only once
a photo is open.

**Two links, not one.** Almost everyone only wants to look. A read-only link
cannot overwrite or delete anything, even if it ends up in the wrong group chat.

## Setup

### The short way

```
./photoshare/tools/setup.sh evas-treff evas-treff-app "Eva's Treff"
```

Needs the [GitHub CLI](https://cli.github.com) and `gh auth login`. It creates
the private photo repository and a public one for the app, points the app at
the photos, publishes it to GitHub Pages (switching Pages on by itself), and
prints the URL. Safe to re-run.

Two repositories, because Pages needs a paid plan to publish from a private
one. The split is the right shape anyway: the public repo holds only HTML and
JavaScript — no photos, no tokens.

Then create the two tokens by hand (step 2 below) — **GitHub has no API for
minting personal access tokens**, deliberately, since a token that could create
tokens would be a master key. The script prints the exact click path.

### The long way

Roughly ten minutes, once.

### 1. Create the album repository

A **new, private** repository — not an existing one. Everything in it becomes
readable by anyone holding a share link, so it should contain nothing else.

```
gh repo create familienfotos --private --add-readme
```

### 2. Create the two access tokens

GitHub → Settings → Developer settings → **Fine-grained personal access tokens**
→ *Generate new token*. Twice:

| Token | Repository access | Permissions | Give it to |
|---|---|---|---|
| view | Only `familienfotos` | Contents: **Read-only** | everyone |
| upload | Only `familienfotos` | Contents: **Read and write** | whoever contributes photos |

Set an **expiry** — the end of the month is usually right. When it lapses the
links stop working by themselves, which is the behaviour you want for something
that was shared into a group chat.

Do not use a classic token, and do not grant access to "all repositories". A
fine-grained token scoped to this one repository is the whole security model.

### 3. Publish the app

Either from this repository:

- Settings → Pages → Source: **GitHub Actions**
- Actions → *Photoshare Pages* → **Run workflow**

…or copy `photoshare/app/` anywhere else that serves static files. It is plain
HTML; there is no build. To skip hosting entirely, hand out `app/` as a folder —
`index.html` works from `file://` too (see *Limitations* for the small print).

Optionally fill in `app/config.js` with the repo name and album title. It only
saves typing: the share link carries the same values.

### 4. Hand out the links

Open `share.html`, paste the repository name and the two tokens, and it builds
both links — with a QR code, a copy button, and a WhatsApp button. That page
never talks to GitHub; it only assembles URLs, and the tokens stay in that
browser.

### 5. What to tell everyone

> Unsere Fotos vom Treffen sind hier: **\<Link>**
> Einfach antippen — kein Konto, keine App. Wer selbst Fotos dazulegen will,
> nimmt diesen Link: **\<Upload-Link>**, Namen eintragen, Fotos auswählen,
> fertig.

## How it works

- **Reading** is one `git/trees?recursive=1` call for the whole album, then one
  blob request per thumbnail — issued lazily as tiles scroll into view, because
  on a private repository every image costs an authenticated request. Blobs are
  addressed by content hash, so the browser's Cache Storage entry for a sha can
  never be stale; a second visit costs one API call in total.
- **Writing** is two `PUT /contents` calls per photo — full size first, then the
  thumbnail. The gallery lists thumbnails, so an upload interrupted between the
  two leaves the photo invisible rather than showing a tile that opens into
  nothing. Concurrent uploads race for the branch tip; a 409 is retried with
  backoff.
- **The access code** travels in the URL fragment, which browsers never send to
  a server — not even as a `Referer`. On arrival it is moved into
  `localStorage` and stripped from the address bar, so a screenshot of the
  gallery does not hand out the album.
- **The capture date** is read from the JPEG's EXIF block before re-encoding.
  `File.lastModified` is not the same thing — on photos that synced from a
  camera or came through a messenger it is the copy date, which would file the
  whole afternoon under today.

## Limitations

- **Album size.** Keep it under a couple of thousand photos (~1 GB); beyond
  that, cloning gets unpleasant and GitHub starts writing emails about
  repository size. One repository per event is the natural unit.
- **Rate limit.** 5000 API requests per hour, per token. A guest browsing a
  300-photo album for the first time spends ~300 of them; afterwards the cache
  serves them. Heavy simultaneous first-time browsing on a shared token can hit
  the limit — the app says so plainly and names the wait.
- **Anyone with the upload link can also delete**, because GitHub's Contents
  permission is not separable that way. Hand it out accordingly, and prefer the
  view link.
- **One access code per browser.** Opening the view link replaces whatever was
  stored, so a phone that could upload a minute ago is read-only afterwards.
  Opening the upload link again restores it. The app notices which of the two
  it is holding — it learns from what its writes actually do, because GitHub's
  `permissions` field is not documented to reflect a fine-grained token's
  access — and hides the delete button rather than offering one that refuses.
- **Deleting is a courtesy, not a permission.** The gallery offers a delete
  button only on photos this browser uploaded, but everyone shares one token
  and the name is self-declared, so the restriction lives in the UI and nowhere
  else. Anyone holding the upload link can remove anything through the API.
  Deleted photos stay in the repository's history and can be restored with git —
  just not from inside the app.
- **From `file://`** the gallery works, but `crypto.subtle` and the Cache API
  need a secure context: uploads fall back to a non-cryptographic content
  fingerprint (fine for naming, still deduplicates) and nothing is cached
  between visits. `npm run serve` gives you a proper `http://localhost`.
- **HEIC.** iPhones shooting in HEIC upload fine from Safari, which decodes it
  natively. Other browsers cannot, and the app says which setting to change
  (Camera → Formats → Most Compatible).

## Tests

```
cd photoshare
npm install && npx playwright install chromium
pip install qrcode          # reference implementation for the QR check
npm test
```

Three suites, all of which check the code against something other than itself:

- `tools/qr-verify.mjs` — the QR encoder, diffed module-for-module against
  python-qrcode across every version 1-20 and all eight masks, with payloads
  filled to capacity. A wrong encoder still draws a plausible square, so
  "it renders" proves nothing.
- `tools/exif-test.mjs` — hand-assembled JPEG headers in both byte orders,
  including the ones that must return "no idea": dead camera clocks, dates in
  the future, and every possible truncation of a valid file.
- `tools/e2e.mjs` — a real Chromium against a stubbed `api.github.com`. Covers
  the parts that only exist at runtime: the fragment being scrubbed, thumbnails
  served from cache on a second visit, a 12 MP photo actually arriving as a
  2560px JPEG, uploading the same file twice being a no-op, a read-only token
  producing a message that tells you what to do, and the Content-Security-Policy
  not breaking any of it.
