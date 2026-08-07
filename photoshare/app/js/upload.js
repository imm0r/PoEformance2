/*
 * The upload page — the part relatives actually touch.
 *
 * Design constraint: someone's aunt, on a phone, on hotel wifi, tapping a link
 * from WhatsApp. So: no account, no app, one name field, one button. Each
 * photo is shrunk on the device before it goes anywhere, which is what makes
 * uploading forty holiday photos over a bad connection survivable.
 */
(function (global) {
  'use strict';

  var PS = global.PS;
  var el = PS.el;

  var PHOTO_EDGE = 2560, PHOTO_QUALITY = 0.85;
  var THUMB_EDGE = 480, THUMB_QUALITY = 0.7;

  var state = {
    cfg: null,
    known: new Set(),   // content hashes already in the album
    jobs: [],
    running: 0,
    uploaded: 0,
    savedBytes: 0
  };

  var nodes = {};

  async function boot(cfg) {
    state.cfg = cfg;
    document.title = 'Hochladen · ' + cfg.title;
    renderShell();

    try {
      await PS.gh.verify(cfg);
      var tree = await PS.gh.tree(cfg);
      state.known = PS.album.hashes(tree.entries);
      nodes.status.textContent = state.known.size
        ? 'Im Album sind schon ' + PS.plural(state.known.size, 'Foto', 'Fotos') + '.'
        : 'Noch keine Fotos im Album — mach den Anfang.';
    } catch (error) {
      nodes.status.textContent = PS.escapeError(error);
      nodes.status.classList.add('is-error');
      nodes.pick.disabled = true;
      return;
    }

    nodes.pick.disabled = false;
  }

  function renderShell() {
    var app = document.getElementById('app');
    app.innerHTML = '';

    nodes.name = el('input', {
      class: 'field',
      type: 'text',
      maxlength: '24',
      placeholder: 'z. B. Oma Lotte',
      value: PS.name(),
      autocomplete: 'name'
    });
    nodes.name.addEventListener('input', function () {
      PS.name(nodes.name.value.trim());
      updatePickState();
    });

    nodes.input = el('input', { type: 'file', accept: 'image/*', multiple: 'multiple', class: 'visually-hidden' });
    nodes.input.addEventListener('change', function () {
      accept(Array.from(nodes.input.files || []));
      nodes.input.value = '';
    });

    nodes.pick = el('button', {
      class: 'btn btn--primary btn--big',
      disabled: 'disabled',
      onclick: function () {
        if (!requireName()) return;
        nodes.input.click();
      }
    }, ['Fotos auswählen']);

    nodes.status = el('p', { class: 'hint', text: 'Verbinde mit dem Album …' });
    nodes.list = el('div', { class: 'queue' });
    nodes.summary = el('div', { class: 'summary is-hidden' });

    var drop = el('div', { class: 'drop' }, [
      el('div', { class: 'drop__emoji', text: '📸' }),
      nodes.pick,
      el('p', { class: 'drop__hint', text: 'oder Fotos einfach hierher ziehen' })
    ]);
    ['dragenter', 'dragover'].forEach(function (type) {
      drop.addEventListener(type, function (e) { e.preventDefault(); drop.classList.add('is-over'); });
    });
    ['dragleave', 'drop'].forEach(function (type) {
      drop.addEventListener(type, function (e) { e.preventDefault(); drop.classList.remove('is-over'); });
    });
    drop.addEventListener('drop', function (e) {
      if (!requireName()) return;
      accept(Array.from(e.dataTransfer.files || []));
    });

    app.appendChild(el('header', { class: 'topbar' }, [
      el('div', { class: 'topbar__brand' }, [
        el('h1', { class: 'topbar__title', text: state.cfg.title })
      ]),
      el('div', { class: 'topbar__actions' }, [
        el('a', { class: 'btn btn--ghost', href: 'index.html' }, ['Zum Album'])
      ])
    ]));
    app.appendChild(el('div', { class: 'panel' }, [
      el('label', { class: 'label' }, ['Wer lädt hoch?']),
      nodes.name,
      drop,
      nodes.status,
      nodes.input,
      nodes.summary,
      nodes.list
    ]));

    updatePickState();
  }

  function updatePickState() {
    nodes.pick.classList.toggle('is-waiting', !PS.name());
  }

  function requireName() {
    if (PS.name()) return true;
    nodes.name.focus();
    nodes.name.classList.add('is-error');
    setTimeout(function () { nodes.name.classList.remove('is-error'); }, 1600);
    PS.toast('Bitte zuerst den Namen eintragen — dann weiß man später, wer das Foto gemacht hat.');
    return false;
  }

  function accept(files) {
    var images = files.filter(function (file) {
      return file.type.indexOf('image/') === 0 || /\.(jpe?g|png|heic|heif|webp|gif)$/i.test(file.name);
    });
    if (!images.length) {
      PS.toast('Darin waren keine Bilder.');
      return;
    }
    images.forEach(function (file) {
      var job = { file: file, state: 'warten', error: null, node: null, uploader: PS.name() };
      state.jobs.push(job);
      job.node = jobNode(job);
      nodes.list.appendChild(job.node);
    });
    pump();
  }

  function jobNode(job) {
    var label = el('div', { class: 'job__name', text: job.file.name });
    var status = el('div', { class: 'job__state', text: 'wartet' });
    var bar = el('div', { class: 'job__bar' }, [el('i')]);
    var node = el('div', { class: 'job' }, [
      el('div', { class: 'job__thumb' }),
      el('div', { class: 'job__body' }, [label, status, bar])
    ]);
    job._status = status;
    job._node = node;
    job._thumb = node.querySelector('.job__thumb');
    return node;
  }

  function mark(job, text, cls) {
    job._status.textContent = text;
    job._node.className = 'job' + (cls ? ' job--' + cls : '');
  }

  function pump() {
    while (state.running < 2) {
      var job = state.jobs.find(function (j) { return j.state === 'warten'; });
      if (!job) break;
      job.state = 'läuft';
      state.running++;
      run(job).catch(function () { /* run() records its own failures */ }).then(function () {
        state.running--;
        pump();
        finishIfDone();
      });
    }
    guardNavigation();
  }

  async function run(job) {
    try {
      mark(job, 'wird verkleinert …', 'busy');
      var buffer = await job.file.arrayBuffer();
      var hash = await PS.sha8(buffer);

      if (state.known.has(hash)) {
        job.state = 'doppelt';
        mark(job, 'war schon im Album', 'skip');
        return;
      }

      var taken = PS.exifDate(buffer) || new Date(job.file.lastModified || Date.now());
      var day = [
        taken.getFullYear(),
        String(taken.getMonth() + 1).padStart(2, '0'),
        String(taken.getDate()).padStart(2, '0')
      ].join('-');
      var time = [taken.getHours(), taken.getMinutes(), taken.getSeconds()]
        .map(function (n) { return String(n).padStart(2, '0'); }).join('');

      var decoded = await PS.decodeImage(new Blob([buffer], { type: job.file.type || 'image/jpeg' }));
      var photo, thumb;
      try {
        photo = await PS.encodeJpeg(decoded, PHOTO_EDGE, PHOTO_QUALITY);
        thumb = await PS.encodeJpeg(decoded, THUMB_EDGE, THUMB_QUALITY);
      } finally {
        decoded.release();
      }

      job._thumb.style.backgroundImage = 'url(' + URL.createObjectURL(thumb) + ')';

      mark(job, 'wird hochgeladen … (' + PS.formatBytes(photo.size) + ')', 'busy');
      var photoPath = PS.album.path(PS.album.PHOTO_DIR, day, time, job.uploader, hash);
      var thumbPath = PS.album.path(PS.album.THUMB_DIR, day, time, job.uploader, hash);

      // Photo before thumbnail: the gallery lists thumbnails, so if the second
      // request never lands the half-uploaded photo stays hidden instead of
      // showing a tile that opens into nothing.
      await PS.gh.putFile(state.cfg, photoPath, await PS.toBase64(photo), 'Foto von ' + job.uploader);
      await PS.gh.putFile(state.cfg, thumbPath, await PS.toBase64(thumb), 'Vorschau von ' + job.uploader);

      state.known.add(hash);
      state.uploaded++;
      state.savedBytes += Math.max(0, job.file.size - photo.size - thumb.size);
      job.state = 'fertig';
      mark(job, 'fertig', 'done');
    } catch (error) {
      job.state = 'fehler';
      job.error = PS.escapeError(error);
      mark(job, job.error, 'error');
      job._node.appendChild(el('button', {
        class: 'btn btn--ghost btn--small',
        onclick: function () {
          this.remove();
          job.state = 'warten';
          mark(job, 'wartet');
          pump();
        }
      }, ['Nochmal versuchen']));
    }
  }

  function finishIfDone() {
    var open = state.jobs.some(function (j) { return j.state === 'warten' || j.state === 'läuft'; });
    guardNavigation();
    if (open || !state.jobs.length) return;

    var failed = state.jobs.filter(function (j) { return j.state === 'fehler'; }).length;
    nodes.summary.className = 'summary' + (failed ? ' summary--warn' : ' summary--ok');
    nodes.summary.innerHTML = '';
    nodes.summary.appendChild(el('strong', {
      text: state.uploaded
        ? PS.plural(state.uploaded, 'Foto ist im Album', 'Fotos sind im Album') + '.'
        : 'Es wurde nichts Neues hochgeladen.'
    }));
    if (state.savedBytes > 0) {
      nodes.summary.appendChild(el('span', {
        text: ' Dabei ' + PS.formatBytes(state.savedBytes) + ' Datenvolumen gespart.'
      }));
    }
    if (failed) {
      nodes.summary.appendChild(el('span', {
        text: ' ' + PS.plural(failed, 'Foto hat', 'Fotos haben') + ' nicht geklappt.'
      }));
    }
    nodes.summary.appendChild(el('a', { class: 'btn btn--primary', href: 'index.html' }, ['Album ansehen']));
  }

  var guarded = false;
  function guardNavigation() {
    var busy = state.jobs.some(function (j) { return j.state === 'warten' || j.state === 'läuft'; });
    if (busy === guarded) return;
    guarded = busy;
    if (busy) global.addEventListener('beforeunload', warn);
    else global.removeEventListener('beforeunload', warn);
  }
  function warn(event) {
    event.preventDefault();
    event.returnValue = '';
  }

  PS.requireAccess(document.getElementById('app'), boot);
})(typeof globalThis !== 'undefined' ? globalThis : this);
