/*
 * The GitHub side of the app. Every request the app makes goes through here,
 * so the token lives in exactly one place and every failure gets translated
 * into something a relative can act on.
 *
 * Reading uses the Git Data API rather than the Contents API: one tree call
 * returns the whole album (Contents caps a directory at 1000 entries and does
 * not paginate), and blobs are addressed by content hash — which doubles as a
 * perfect cache key, since a given sha can never change.
 */
(function (global) {
  'use strict';

  var PS = global.PS || (global.PS = {});
  var API = 'https://api.github.com';
  var CACHE_NAME = 'photoshare-blobs-v1';

  var objectUrls = new Map(); // sha -> object URL, so a thumb is only materialised once
  var cachePromise = null;

  function cache() {
    if (cachePromise) return cachePromise;
    cachePromise = (async function () {
      try { return await global.caches.open(CACHE_NAME); } catch (e) { return null; }
    })();
    return cachePromise;
  }

  function headers(cfg, accept) {
    return {
      Authorization: 'Bearer ' + cfg.token,
      Accept: accept || 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28'
    };
  }

  async function explain(response, cfg) {
    if (response.status === 401) {
      return new Error('Der Zugangscode wird nicht mehr akzeptiert. Wahrscheinlich ist er abgelaufen — frag nach einem neuen Link.');
    }
    if (response.status === 403 && response.headers.get('x-ratelimit-remaining') === '0') {
      var reset = Number(response.headers.get('x-ratelimit-reset') || 0) * 1000;
      var minutes = Math.max(1, Math.ceil((reset - Date.now()) / 60000));
      return new Error('GitHub bremst gerade. In etwa ' + minutes + ' Minuten geht es weiter.');
    }
    if (response.status === 403) {
      return new Error('Dieser Zugangscode darf nur ansehen. Öffne einmal den Upload-Link auf diesem Gerät, ' +
        'dann kannst du hochladen und löschen.');
    }
    if (response.status === 404) {
      // GitHub hides private repositories a token cannot see behind a 404
      // rather than a 403, so "does not exist" and "no access" are the same
      // answer. In practice it is almost always the token: the repository
      // access field defaults to "Public repositories", which can never see a
      // private album. Say that, instead of leaving someone to guess.
      var album = cfg ? cfg.owner + '/' + cfg.name : 'das Album';
      return new Error('Kein Zugriff auf ' + album + '. Wenn der Name stimmt, liegt es am Zugangscode: ' +
        'Beim Erstellen des Tokens muss unter "Repository access" ausdrücklich dieses Repository ' +
        'ausgewählt sein — die Voreinstellung "Public repositories" kann ein privates Album nicht sehen.');
    }
    var detail = '';
    try { detail = (await response.json()).message || ''; } catch (e) { /* body was not JSON */ }
    return new Error('GitHub antwortet mit ' + response.status + (detail ? ': ' + detail : ''));
  }

  /** Wrap explain() so callers can still branch on the raw status. */
  async function failure(response, cfg) {
    var error = await explain(response, cfg);
    error.status = response.status;
    return error;
  }

  async function request(cfg, path, init) {
    var options = Object.assign({ method: 'GET' }, init);
    options.headers = Object.assign(headers(cfg, options.accept), options.headers || {});
    delete options.accept;
    var response = await fetch(API + path, options);
    if (!response.ok) throw await failure(response, cfg);
    return response;
  }

  /** Run `jobs` with a small concurrency cap so a big album does not open 300 sockets. */
  async function pool(jobs, limit) {
    var index = 0, results = new Array(jobs.length);
    var workers = new Array(Math.min(limit, jobs.length)).fill(0).map(async function () {
      for (;;) {
        var mine = index++;
        if (mine >= jobs.length) return;
        results[mine] = await jobs[mine]();
      }
    });
    await Promise.all(workers);
    return results;
  }

  var gh = {};

  /** Cheap check that repo + token actually fit together. */
  gh.verify = async function (cfg) {
    var response = await request(cfg, '/repos/' + cfg.owner + '/' + cfg.name);
    return response.json();
  };

  /** The complete file list of the album branch, in one request. */
  gh.tree = async function (cfg) {
    var response;
    try {
      response = await request(cfg,
        '/repos/' + cfg.owner + '/' + cfg.name + '/git/trees/' +
        encodeURIComponent(cfg.branch) + '?recursive=1&cachebust=' + Date.now());
    } catch (error) {
      // A repository with no commits has no branch to list, and the tree
      // endpoint 404s exactly like a repository the code cannot reach. Ask the
      // repository itself which of the two it is: "the album is empty" and
      // "there is no album" send you looking in completely different places.
      if (error.status === 404) {
        try {
          await gh.verify(cfg);
          return { truncated: false, entries: [] };
        } catch (inner) {
          throw error;
        }
      }
      throw error;
    }
    var body = await response.json();
    return {
      truncated: !!body.truncated,
      entries: (body.tree || []).filter(function (e) { return e.type === 'blob'; })
    };
  };

  /**
   * Fetch a blob by sha and hand back an object URL.
   *
   * Content-addressed, so a cache hit is always correct and never stale — the
   * only reason to re-download is a cache eviction.
   */
  gh.blobUrl = async function (cfg, sha) {
    if (objectUrls.has(sha)) return objectUrls.get(sha);

    var key = 'https://photoshare.invalid/blob/' + sha;
    var store = await cache();
    var hit = store ? await store.match(key) : null;

    if (!hit) {
      var response = await request(cfg,
        '/repos/' + cfg.owner + '/' + cfg.name + '/git/blobs/' + sha,
        { accept: 'application/vnd.github.raw' });
      var bytes = await response.arrayBuffer();
      hit = new Response(bytes, { headers: { 'Content-Type': 'image/jpeg' } });
      if (store) {
        try { await store.put(key, hit.clone()); } catch (e) { /* quota: just skip caching */ }
      }
    }

    var url = URL.createObjectURL(await hit.blob());
    objectUrls.set(sha, url);
    return url;
  };

  /**
   * Fetch a blob as text. Comments are tiny and content-addressed like
   * everything else, so once read they never need reading again.
   */
  var texts = new Map();
  gh.blobText = async function (cfg, sha) {
    if (texts.has(sha)) return texts.get(sha);
    var response = await request(cfg,
      '/repos/' + cfg.owner + '/' + cfg.name + '/git/blobs/' + sha,
      { accept: 'application/vnd.github.raw' });
    var text = await response.text();
    texts.set(sha, text);
    return text;
  };

  gh.forgetBlob = function (sha) {
    var url = objectUrls.get(sha);
    if (!url) return;
    URL.revokeObjectURL(url);
    objectUrls.delete(sha);
  };

  gh.clearCache = async function () {
    objectUrls.forEach(function (url) { URL.revokeObjectURL(url); });
    objectUrls.clear();
    try { await global.caches.delete(CACHE_NAME); } catch (e) { /* nothing cached */ }
    cachePromise = null;
  };

  /**
   * Create a file. Two people uploading at the same time race for the branch
   * tip, which GitHub rejects with a 409 — so back off and try again.
   *
   * A 422 means the path already exists, which for content-addressed names can
   * only be the same photo twice. That counts as success.
   */
  gh.putFile = async function (cfg, path, base64, message) {
    var body = JSON.stringify({ message: message, content: base64, branch: cfg.branch });
    var url = '/repos/' + cfg.owner + '/' + cfg.name + '/contents/' +
      path.split('/').map(encodeURIComponent).join('/');

    for (var attempt = 0; ; attempt++) {
      var response = await fetch(API + url, {
        method: 'PUT',
        headers: Object.assign(headers(cfg), { 'Content-Type': 'application/json' }),
        body: body
      });
      if (response.ok) { PS.ability('write'); return 'created'; }
      if (response.status === 422) { PS.ability('write'); return 'exists'; }
      if (response.status === 403) PS.ability('read');
      if ((response.status === 409 || response.status >= 500) && attempt < 5) {
        await new Promise(function (r) { setTimeout(r, 400 * Math.pow(2, attempt) + Math.random() * 300); });
        continue;
      }
      throw await failure(response, cfg);
    }
  };

  /**
   * Delete a file. `sha` is the blob hash, which the tree listing already
   * carries — so no extra lookup is needed to remove something.
   *
   * A 404 counts as done: the file is gone, which is the point. Same 409
   * retry as writing, for the same reason.
   */
  gh.deleteFile = async function (cfg, path, sha, message) {
    var body = JSON.stringify({ message: message, sha: sha, branch: cfg.branch });
    var url = '/repos/' + cfg.owner + '/' + cfg.name + '/contents/' +
      path.split('/').map(encodeURIComponent).join('/');

    for (var attempt = 0; ; attempt++) {
      var response = await fetch(API + url, {
        method: 'DELETE',
        headers: Object.assign(headers(cfg), { 'Content-Type': 'application/json' }),
        body: body
      });
      if (response.ok) { PS.ability('write'); return; }
      if (response.status === 404) return; // already gone; says nothing about the token
      if (response.status === 403) PS.ability('read');
      if ((response.status === 409 || response.status >= 500) && attempt < 5) {
        await new Promise(function (r) { setTimeout(r, 400 * Math.pow(2, attempt) + Math.random() * 300); });
        continue;
      }
      throw await failure(response, cfg);
    }
  };

  gh.pool = pool;
  PS.gh = gh;
})(typeof globalThis !== 'undefined' ? globalThis : this);
