/*
 * Shared plumbing: where the album lives, who is looking at it, and the small
 * DOM helpers the three pages have in common.
 *
 * Classic script, not an ES module, on purpose: modules are blocked under the
 * file:// origin, and "just open index.html" has to keep working when GitHub
 * Pages is not set up (or is down).
 */
(function (global) {
  'use strict';

  var PS = global.PS || (global.PS = {});
  var STORE = 'ps:';

  function read(key) {
    try { return global.localStorage.getItem(STORE + key) || ''; } catch (e) { return ''; }
  }
  function write(key, value) {
    try {
      if (value) global.localStorage.setItem(STORE + key, value);
      else global.localStorage.removeItem(STORE + key);
    } catch (e) { /* private mode: settings just do not survive the tab */ }
  }

  var defaults = global.PHOTOSHARE_CONFIG || {};
  var config = null;

  /**
   * Pull settings out of the share link, remember them, then scrub the URL.
   *
   * The token travels in the fragment so it never reaches a web server (not
   * even as a Referer), and we drop it from the address bar right away so a
   * screenshot of the gallery does not hand out write access.
   */
  function absorbHash() {
    var hash = global.location.hash.replace(/^#/, '');
    if (!hash) return false;
    var params = new URLSearchParams(hash);
    var touched = false;
    [['r', 'repo'], ['k', 'token'], ['b', 'branch'], ['t', 'title']].forEach(function (pair) {
      var value = params.get(pair[0]);
      if (value) { write(pair[1], value); touched = true; }
    });
    if (touched) {
      global.history.replaceState(null, '', global.location.pathname + global.location.search);
    }
    return touched;
  }

  /*
   * Someone with the album already open taps the share link again — from
   * WhatsApp, from a bookmark, from the gate screen. The browser sees the same
   * document with a different fragment and does NOT reload, so nothing would
   * pick the code up. Absorb it here and reload deliberately.
   */
  global.addEventListener('hashchange', function () {
    if (absorbHash()) {
      config = null;
      global.location.reload();
    }
  });

  PS.config = function () {
    if (config) return config;
    absorbHash();
    var repo = read('repo') || defaults.repo || '';
    var parts = repo.split('/');
    config = {
      owner: parts[0] || '',
      name: parts[1] || '',
      repo: repo,
      branch: read('branch') || defaults.branch || 'main',
      title: read('title') || defaults.title || 'Fotoalbum',
      token: read('token')
    };
    return config;
  };

  PS.setToken = function (token) { write('token', token); config = null; };
  PS.setRepo = function (repo) { write('repo', repo); config = null; };
  PS.forget = function () {
    ['repo', 'token', 'branch', 'title'].forEach(function (k) { write(k, ''); });
    config = null;
  };

  PS.name = function (value) {
    if (value === undefined) return read('name');
    write('name', value);
    return value;
  };

  /** Accept a full share link, a bare "owner/repo#k=..." or just the token. */
  PS.applyPastedAccess = function (input) {
    var text = (input || '').trim();
    if (!text) return false;
    var hash = text.indexOf('#');
    if (hash >= 0) {
      var params = new URLSearchParams(text.slice(hash + 1));
      var applied = false;
      [['r', 'repo'], ['k', 'token'], ['b', 'branch'], ['t', 'title']].forEach(function (pair) {
        var value = params.get(pair[0]);
        if (value) { write(pair[1], value); applied = true; }
      });
      config = null;
      return applied;
    }
    if (/^(github_pat_|ghp_|gho_|ghs_)/.test(text)) { PS.setToken(text); return true; }
    if (/^[\w.-]+\/[\w.-]+$/.test(text)) { PS.setRepo(text); return true; }
    return false;
  };

  // --- DOM helpers -------------------------------------------------------

  PS.el = function (tag, attrs, children) {
    var node = document.createElement(tag);
    Object.keys(attrs || {}).forEach(function (key) {
      if (key === 'class') node.className = attrs[key];
      else if (key === 'text') node.textContent = attrs[key];
      else if (key.slice(0, 2) === 'on') node.addEventListener(key.slice(2), attrs[key]);
      else if (attrs[key] !== null && attrs[key] !== undefined) node.setAttribute(key, attrs[key]);
    });
    (children || []).forEach(function (child) {
      node.appendChild(typeof child === 'string' ? document.createTextNode(child) : child);
    });
    return node;
  };

  PS.toast = function (message, kind) {
    var host = document.querySelector('.toasts');
    if (!host) {
      host = PS.el('div', { class: 'toasts' });
      document.body.appendChild(host);
    }
    var node = PS.el('div', { class: 'toast' + (kind ? ' toast--' + kind : ''), text: message });
    host.appendChild(node);
    setTimeout(function () { node.classList.add('is-leaving'); }, kind === 'error' ? 6000 : 3000);
    setTimeout(function () { node.remove(); }, kind === 'error' ? 6400 : 3400);
  };

  var WEEKDAYS = ['Sonntag', 'Montag', 'Dienstag', 'Mittwoch', 'Donnerstag', 'Freitag', 'Samstag'];
  var MONTHS = ['Januar', 'Februar', 'März', 'April', 'Mai', 'Juni', 'Juli',
    'August', 'September', 'Oktober', 'November', 'Dezember'];

  /** "2026-08-07" -> "Freitag, 7. August 2026", without pulling in Intl edge cases. */
  PS.formatDay = function (iso) {
    var parts = iso.split('-').map(Number);
    var date = new Date(parts[0], parts[1] - 1, parts[2]);
    if (isNaN(date.getTime())) return iso;
    return WEEKDAYS[date.getDay()] + ', ' + parts[2] + '. ' + MONTHS[parts[1] - 1] + ' ' + parts[0];
  };

  /** "2026-08-07" -> "7. Aug. 2026", for places where the full day does not fit. */
  PS.formatDayShort = function (iso) {
    var parts = iso.split('-').map(Number);
    if (!MONTHS[parts[1] - 1]) return iso;
    var month = MONTHS[parts[1] - 1];
    var short = month.length > 5 ? month.slice(0, 3) + '.' : month;
    return parts[2] + '. ' + short + ' ' + parts[0];
  };

  PS.formatTime = function (hhmmss) {
    return hhmmss.slice(0, 2) + ':' + hhmmss.slice(2, 4);
  };

  PS.plural = function (n, one, many) {
    return n + ' ' + (n === 1 ? one : many);
  };

  /**
   * Gate the page behind an access code. Renders a paste form when we have no
   * usable repo/token yet and calls `onReady` once we do.
   */
  PS.requireAccess = function (mount, onReady) {
    var cfg = PS.config();
    // Name the tab before anything else. Someone who has three albums open, or
    // who bookmarked the bare URL, should not be looking at "Fotoalbum".
    if (cfg.title) document.title = cfg.title;
    if (cfg.repo && cfg.token) { onReady(cfg); return; }

    var input = PS.el('input', {
      type: 'password',
      class: 'field',
      autocomplete: 'off',
      autocapitalize: 'off',
      spellcheck: 'false',
      placeholder: 'Zugangs-Link oder Code einfügen'
    });
    var hint = PS.el('p', { class: 'gate__hint', text: 'Du hast den Link per WhatsApp bekommen? Einfach hier einfügen.' });

    function submit() {
      if (!PS.applyPastedAccess(input.value)) {
        hint.textContent = 'Damit kann ich nichts anfangen — bitte den kompletten Link einfügen.';
        hint.classList.add('is-error');
        return;
      }
      var next = PS.config();
      if (!next.repo || !next.token) {
        hint.textContent = 'Da fehlt noch etwas — bitte den kompletten Link einfügen, nicht nur einen Teil.';
        hint.classList.add('is-error');
        return;
      }
      mount.innerHTML = '';
      onReady(next);
    }

    mount.innerHTML = '';
    mount.appendChild(PS.el('div', { class: 'gate' }, [
      PS.el('div', { class: 'gate__icon', text: '🔒' }),
      PS.el('h1', { class: 'gate__title', text: cfg.title }),
      PS.el('p', { class: 'gate__lead', text: 'Dieses Album ist privat.' }),
      input,
      PS.el('button', { class: 'btn btn--primary', onclick: submit, text: 'Album öffnen' }),
      hint
    ]));

    input.addEventListener('keydown', function (event) {
      if (event.key === 'Enter') submit();
    });
    input.focus();
  };

  PS.escapeError = function (error) {
    return (error && error.message) ? error.message : String(error);
  };
})(typeof globalThis !== 'undefined' ? globalThis : this);
