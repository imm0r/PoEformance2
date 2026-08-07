/*
 * The gallery: one tree request for the whole album, then thumbnails fetched
 * lazily as they scroll into view.
 *
 * Nothing is loaded eagerly. A private repository means every image costs an
 * authenticated API request, so the difference between "load everything" and
 * "load what is on screen" is the difference between a working album and a
 * rate-limited one halfway through the afternoon.
 */
(function (global) {
  'use strict';

  var PS = global.PS;
  var el = PS.el;

  var state = {
    cfg: null,
    items: [],
    visible: [],
    filter: null,
    pending: null,   // items found by polling but not shown yet
    lightbox: -1
  };

  var nodes = {};
  var observer = null;

  function boot(cfg) {
    state.cfg = cfg;
    document.title = cfg.title;
    renderShell();
    load(true);

    setInterval(function () {
      if (document.visibilityState === 'visible' && state.lightbox < 0) poll();
    }, 60000);
    document.addEventListener('visibilitychange', function () {
      if (document.visibilityState === 'visible') poll();
    });
  }

  function renderShell() {
    var app = document.getElementById('app');
    app.innerHTML = '';

    nodes.count = el('span', { class: 'topbar__count' });
    nodes.filters = el('div', { class: 'filters' });
    nodes.feed = el('main', { class: 'feed' });
    nodes.newPill = el('button', {
      class: 'pill is-hidden',
      onclick: function () { adopt(); }
    });

    app.appendChild(el('header', { class: 'topbar' }, [
      el('div', { class: 'topbar__brand' }, [
        el('h1', { class: 'topbar__title', text: state.cfg.title }),
        nodes.count
      ]),
      el('div', { class: 'topbar__actions' }, [
        el('a', { class: 'btn btn--primary', href: 'upload.html' }, [
          // Two labels: on a narrow phone the full wording pushes the album
          // title into an ellipsis, and the title is what tells you where you are.
          el('span', { class: 'btn__wide', text: 'Fotos hinzufügen' }),
          el('span', { class: 'btn__narrow', text: '+ Fotos' })
        ]),
        el('button', { class: 'btn btn--ghost', title: 'Neu laden', onclick: function () { load(false); } }, ['⟳'])
      ])
    ]));
    app.appendChild(nodes.filters);
    app.appendChild(nodes.newPill);
    app.appendChild(nodes.feed);
    buildLightbox(app);
  }

  function setStatus(content) {
    nodes.feed.innerHTML = '';
    nodes.feed.appendChild(content);
  }

  async function load(first) {
    if (first) {
      setStatus(el('div', { class: 'status' }, [
        el('div', { class: 'spinner' }),
        el('p', { text: 'Album wird geladen …' })
      ]));
    }
    try {
      var tree = await PS.gh.tree(state.cfg);
      state.items = PS.album.fromTree(tree.entries);
      state.pending = null;
      nodes.newPill.classList.add('is-hidden');
      render();
      if (tree.truncated) {
        PS.toast('Das Album ist so groß, dass GitHub die Liste gekürzt hat — es fehlen Fotos.', 'error');
      }
    } catch (error) {
      setStatus(el('div', { class: 'status status--error' }, [
        el('p', { text: PS.escapeError(error) }),
        el('button', { class: 'btn', onclick: function () { load(true); } }, ['Nochmal versuchen']),
        el('button', {
          class: 'btn btn--ghost',
          onclick: function () { PS.forget(); location.reload(); }
        }, ['Anderen Code eingeben'])
      ]));
    }
  }

  /** Background refresh: never disturb the scroll position, just offer the update. */
  async function poll() {
    try {
      var tree = await PS.gh.tree(state.cfg);
      var next = PS.album.fromTree(tree.entries);
      if (next.length === state.items.length) return;
      var known = new Set(state.items.map(function (i) { return i.id; }));
      var fresh = next.filter(function (i) { return !known.has(i.id); }).length;
      if (!fresh) {
        // Nothing new, but the count moved: something was removed straight in
        // the repository. Drop the stale tiles rather than let them 404.
        state.items = next;
        render();
        return;
      }
      state.pending = next;
      nodes.newPill.textContent = PS.plural(fresh, 'neues Foto', 'neue Fotos') + ' — antippen';
      nodes.newPill.classList.remove('is-hidden');
    } catch (error) {
      // A failed background poll is not worth interrupting anyone over.
    }
  }

  function adopt() {
    if (!state.pending) return;
    state.items = state.pending;
    state.pending = null;
    nodes.newPill.classList.add('is-hidden');
    render();
    global.scrollTo({ top: 0, behavior: 'smooth' });
  }

  function render() {
    renderFilters();
    state.visible = state.filter
      ? state.items.filter(function (i) { return i.uploader === state.filter; })
      : state.items;

    nodes.count.textContent = state.items.length
      ? PS.plural(state.items.length, 'Foto', 'Fotos')
      : '';

    if (observer) observer.disconnect();
    observer = new IntersectionObserver(onVisible, { rootMargin: '600px 0px' });

    if (!state.visible.length) {
      setStatus(el('div', { class: 'status' }, [
        el('div', { class: 'status__emoji', text: '📷' }),
        el('p', { text: state.items.length ? 'Von dieser Person ist noch nichts dabei.' : 'Noch keine Fotos hier.' }),
        el('a', { class: 'btn btn--primary', href: 'upload.html' }, ['Das erste Foto hochladen'])
      ]));
      return;
    }

    nodes.feed.innerHTML = '';
    PS.album.byDay(state.visible).forEach(function (group) {
      nodes.feed.appendChild(el('h2', { class: 'day', text: PS.formatDay(group.day) }));
      var grid = el('div', { class: 'grid' });
      group.items.forEach(function (item) {
        grid.appendChild(tileFor(item));
      });
      nodes.feed.appendChild(grid);
    });
  }

  function renderFilters() {
    var uploaders = [];
    state.items.forEach(function (item) {
      if (uploaders.indexOf(item.uploader) < 0) uploaders.push(item.uploader);
    });
    uploaders.sort(function (a, b) { return a.localeCompare(b, 'de'); });

    nodes.filters.innerHTML = '';
    if (uploaders.length < 2) return;

    function chip(label, value) {
      return el('button', {
        class: 'chip' + (state.filter === value ? ' is-active' : ''),
        onclick: function () { state.filter = value; render(); }
      }, [label]);
    }
    nodes.filters.appendChild(chip('Alle', null));
    uploaders.forEach(function (name) { nodes.filters.appendChild(chip(name, name)); });
  }

  function tileFor(item) {
    var img = el('img', { alt: 'Foto von ' + item.uploader, loading: 'lazy' });
    var tile = el('button', {
      class: 'tile',
      onclick: function () { openLightbox(state.visible.indexOf(item)); }
    }, [img, el('span', { class: 'tile__by', text: item.uploader })]);
    tile._item = item;
    tile._img = img;
    observer.observe(tile);
    return tile;
  }

  function onVisible(entries) {
    entries.forEach(function (entry) {
      if (!entry.isIntersecting) return;
      var tile = entry.target;
      observer.unobserve(tile);
      PS.gh.blobUrl(state.cfg, tile._item.thumbSha).then(function (url) {
        tile._img.src = url;
        tile.classList.add('is-loaded');
      }).catch(function () {
        tile.classList.add('is-broken');
      });
    });
  }

  // --- lightbox ----------------------------------------------------------

  function buildLightbox(app) {
    nodes.lbImage = el('img', { class: 'lightbox__image', alt: '' });
    nodes.lbCaption = el('div', { class: 'lightbox__caption' });
    nodes.lbDownload = el('a', { class: 'btn btn--ghost', download: '' }, ['Speichern']);
    nodes.lbSpinner = el('div', { class: 'spinner spinner--light' });

    nodes.lightbox = el('div', { class: 'lightbox is-hidden' }, [
      el('div', { class: 'lightbox__bar' }, [
        nodes.lbCaption,
        el('div', { class: 'lightbox__tools' }, [
          nodes.lbDownload,
          el('button', { class: 'btn btn--ghost', onclick: closeLightbox }, ['✕'])
        ])
      ]),
      el('div', { class: 'lightbox__stage' }, [
        nodes.lbSpinner,
        nodes.lbImage,
        el('button', { class: 'lightbox__nav lightbox__nav--prev', onclick: function () { step(-1); } }, ['‹']),
        el('button', { class: 'lightbox__nav lightbox__nav--next', onclick: function () { step(1); } }, ['›'])
      ])
    ]);
    app.appendChild(nodes.lightbox);

    document.addEventListener('keydown', function (event) {
      if (state.lightbox < 0) return;
      if (event.key === 'Escape') closeLightbox();
      if (event.key === 'ArrowLeft') step(-1);
      if (event.key === 'ArrowRight') step(1);
    });

    var startX = null;
    nodes.lightbox.addEventListener('pointerdown', function (e) { startX = e.clientX; });
    nodes.lightbox.addEventListener('pointerup', function (e) {
      if (startX === null) return;
      var dx = e.clientX - startX;
      startX = null;
      if (Math.abs(dx) > 60) step(dx > 0 ? -1 : 1);
    });
  }

  function openLightbox(index) {
    if (index < 0) return;
    state.lightbox = index;
    nodes.lightbox.classList.remove('is-hidden');
    document.body.classList.add('is-locked');
    showCurrent();
  }

  function closeLightbox() {
    var item = state.visible[state.lightbox];
    state.lightbox = -1;
    nodes.lightbox.classList.add('is-hidden');
    document.body.classList.remove('is-locked');
    nodes.lbImage.removeAttribute('src');
    // Full-size photos are megabytes each; hand them back rather than letting a
    // long browsing session pile them up in memory. The disk cache keeps them.
    if (item) PS.gh.forgetBlob(item.photoSha);
  }

  function step(delta) {
    var next = state.lightbox + delta;
    if (next < 0 || next >= state.visible.length) return;
    var previous = state.visible[state.lightbox];
    state.lightbox = next;
    showCurrent();
    if (previous) PS.gh.forgetBlob(previous.photoSha);
  }

  async function showCurrent() {
    var item = state.visible[state.lightbox];
    var token = item.id;
    nodes.lbImage.classList.add('is-loading');
    nodes.lbSpinner.classList.remove('is-hidden');
    nodes.lbCaption.textContent = item.uploader + ' · ' + PS.formatDayShort(item.day) +
      ', ' + PS.formatTime(item.time) + ' Uhr';
    nodes.lbDownload.setAttribute('download', item.day + '_' + item.time + '_' + item.uploader + '.jpg');

    // Show the thumbnail immediately so there is never a blank stage.
    try {
      nodes.lbImage.src = await PS.gh.blobUrl(state.cfg, item.thumbSha);
    } catch (e) { /* the full size below is the one that matters */ }

    try {
      var url = await PS.gh.blobUrl(state.cfg, item.photoSha);
      if (state.visible[state.lightbox] && state.visible[state.lightbox].id !== token) return; // moved on
      nodes.lbImage.src = url;
      nodes.lbDownload.href = url;
    } catch (error) {
      PS.toast(PS.escapeError(error), 'error');
    } finally {
      nodes.lbImage.classList.remove('is-loading');
      nodes.lbSpinner.classList.add('is-hidden');
    }
  }

  PS.requireAccess(document.getElementById('app'), boot);
})(typeof globalThis !== 'undefined' ? globalThis : this);
