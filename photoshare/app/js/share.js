/*
 * The organiser's page: turns a repository plus one or two tokens into the
 * links (and QR codes) that get handed around at the party.
 *
 * Two links, on purpose. Almost everyone only ever needs to look, and a
 * read-only token cannot be used to overwrite or delete anything even if the
 * link ends up in the wrong group chat. The upload link is the one to be
 * careful with, so it says so.
 *
 * Nothing here talks to GitHub — it is a link builder, and it runs fine
 * offline.
 */
(function (global) {
  'use strict';

  var PS = global.PS;
  var el = PS.el;
  var STORE = 'ps:share:';

  function remembered(key, fallback) {
    try { return global.localStorage.getItem(STORE + key) || fallback || ''; } catch (e) { return fallback || ''; }
  }
  function remember(key, value) {
    try { global.localStorage.setItem(STORE + key, value); } catch (e) { /* private mode */ }
  }

  var fields = {};
  var outputs = [];

  function baseUrl(page) {
    var url = new URL(global.location.href);
    url.hash = '';
    url.search = '';
    url.pathname = url.pathname.replace(/[^/]*$/, page);
    return url.toString();
  }

  function buildLink(page, token) {
    var params = new URLSearchParams();
    params.set('r', fields.repo.value.trim());
    if (fields.branch.value.trim() && fields.branch.value.trim() !== 'main') {
      params.set('b', fields.branch.value.trim());
    }
    if (fields.title.value.trim()) params.set('t', fields.title.value.trim());
    params.set('k', token.trim());
    return baseUrl(page) + '#' + params.toString();
  }

  function field(key, label, placeholder, fallback, type) {
    var input = el('input', {
      class: 'field',
      type: type || 'text',
      placeholder: placeholder,
      value: remembered(key, fallback),
      autocapitalize: 'off',
      spellcheck: 'false'
    });
    input.addEventListener('input', function () {
      remember(key, input.value);
      refresh();
    });
    fields[key] = input;
    return el('div', { class: 'formrow' }, [el('label', { class: 'label', text: label }), input]);
  }

  function linkCard(options) {
    var qrHost = el('div', { class: 'qr' });
    var urlBox = el('code', { class: 'urlbox' });
    var openLink = el('a', { class: 'btn btn--ghost', target: '_blank', rel: 'noopener' }, ['Öffnen']);
    var waLink = el('a', { class: 'btn btn--ghost', target: '_blank', rel: 'noopener' }, ['Per WhatsApp']);
    var download = el('a', { class: 'btn btn--ghost', download: options.file + '-qr.svg' }, ['QR speichern']);

    var copy = el('button', {
      class: 'btn btn--primary',
      onclick: async function () {
        try {
          await navigator.clipboard.writeText(urlBox.textContent);
          PS.toast('Link kopiert.');
        } catch (e) {
          // Clipboard access is blocked on plain http and in some in-app browsers.
          var range = document.createRange();
          range.selectNodeContents(urlBox);
          var selection = global.getSelection();
          selection.removeAllRanges();
          selection.addRange(range);
          PS.toast('Bitte von Hand kopieren (ist markiert).');
        }
      }
    }, ['Link kopieren']);

    var card = el('section', { class: 'card card--' + options.tone }, [
      el('h2', { class: 'card__title', text: options.title }),
      el('p', { class: 'card__lead', text: options.lead }),
      qrHost,
      urlBox,
      el('div', { class: 'card__actions' }, [copy, waLink, openLink, download])
    ]);

    outputs.push({
      page: options.page,
      tokenKey: options.tokenKey,
      message: options.message,
      card: card,
      qrHost: qrHost,
      urlBox: urlBox,
      openLink: openLink,
      waLink: waLink,
      download: download
    });
    return card;
  }

  function refresh() {
    var repoOk = /^[\w.-]+\/[\w.-]+$/.test(fields.repo.value.trim());
    outputs.forEach(function (out) {
      var token = fields[out.tokenKey].value.trim();
      var ready = repoOk && token.length > 10;
      out.card.classList.toggle('is-incomplete', !ready);
      if (!ready) {
        out.urlBox.textContent = repoOk
          ? 'Noch kein Token eingetragen.'
          : 'Oben "besitzer/repo" eintragen.';
        out.qrHost.innerHTML = '';
        return;
      }

      var link = buildLink(out.page, token);
      out.urlBox.textContent = link;
      out.openLink.href = link;
      out.waLink.href = 'https://wa.me/?text=' + encodeURIComponent(out.message + '\n' + link);

      var svg = QR.toSvg(QR.encode(link), { quiet: 2, dark: '#111', light: '#fff' });
      out.qrHost.innerHTML = svg;
      out.download.href = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
    });
  }

  function render() {
    var app = document.getElementById('app');
    var cfg = PS.config();

    app.appendChild(el('header', { class: 'topbar' }, [
      el('div', { class: 'topbar__brand' }, [
        el('h1', { class: 'topbar__title', text: 'Album teilen' })
      ]),
      el('div', { class: 'topbar__actions' }, [
        el('a', { class: 'btn btn--ghost', href: 'index.html' }, ['Zum Album'])
      ])
    ]));

    app.appendChild(el('div', { class: 'panel' }, [
      el('p', { class: 'hint', text: 'Diese Seite baut nur Links zusammen. Sie schickt nichts irgendwohin — die Tokens bleiben in diesem Browser.' }),
      field('repo', 'Album-Repository', 'besitzer/familienfotos', cfg.repo),
      field('branch', 'Branch', 'main', cfg.branch),
      field('title', 'Wie soll das Album heißen?', 'Familientreffen 2026', cfg.title),
      field('viewToken', 'Token zum Ansehen (Contents: Read-only)', 'github_pat_…', '', 'password'),
      field('uploadToken', 'Token zum Hochladen (Contents: Read and write)', 'github_pat_…', '', 'password')
    ]));

    app.appendChild(linkCard({
      tone: 'view',
      page: 'index.html',
      tokenKey: 'viewToken',
      file: 'ansehen',
      title: 'Zum Ansehen',
      lead: 'Der Link für die Runde. Wer ihn hat, sieht alle Fotos — ändern kann er nichts.',
      message: 'Unsere Fotos sind hier:'
    }));

    app.appendChild(linkCard({
      tone: 'upload',
      page: 'upload.html',
      tokenKey: 'uploadToken',
      file: 'hochladen',
      title: 'Zum Hochladen',
      lead: 'Nur an die weitergeben, die Fotos beisteuern. Dieser Link darf ins Album schreiben.',
      message: 'Hier kannst du deine Fotos dazulegen:'
    }));

    app.appendChild(el('section', { class: 'card card--note' }, [
      el('h2', { class: 'card__title', text: 'Kurz zur Sicherheit' }),
      el('ul', { class: 'notes' }, [
        el('li', { text: 'Der Code steckt hinter dem # im Link. Er wird nie an einen Webserver geschickt, aber wer den Link hat, kommt rein — behandle ihn wie einen Haustürschlüssel.' }),
        el('li', { text: 'Setze beim Erstellen des Tokens ein Ablaufdatum. Danach ist der Link von selbst wertlos.' }),
        el('li', { text: 'Ein Link ist ein Fehler weit weg von "widerrufen": Token auf GitHub löschen, neuen erstellen, neuen Link verschicken.' }),
        el('li', { text: 'Das Token darf ausschließlich auf dieses eine Foto-Repository zeigen — niemals ein Token mit Zugriff auf alle Repositories verteilen.' })
      ])
    ]));

    refresh();
  }

  render();
})(typeof globalThis !== 'undefined' ? globalThis : this);
