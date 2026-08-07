/*
 * The naming scheme is the database.
 *
 *   photos/2026-08-07/153012__Oma-Lotte__a1b2c3d4.jpg
 *   thumbs/2026-08-07/153012__Oma-Lotte__a1b2c3d4.jpg
 *
 * Day, time, who uploaded it and a content hash, all in the path. That buys
 * three things worth more than a tidier schema: the gallery can group and
 * caption every photo from a single tree listing with no extra requests; two
 * phones uploading at once can never collide, because they only ever create
 * new paths; and re-uploading the same photo lands on the same name, so the
 * duplicate is a no-op instead of a second copy.
 */
(function (global) {
  'use strict';

  var PS = global.PS || (global.PS = {});

  var PHOTO_DIR = 'photos';
  var THUMB_DIR = 'thumbs';
  var PATTERN = /^(\d{4}-\d{2}-\d{2})\/(\d{6})__(.+?)__([0-9a-f]{8})\.jpg$/;

  /**
   * Reduce a display name to something safe in a path while keeping it
   * readable — umlauts survive, everything that could confuse a URL does not.
   */
  function slug(name) {
    var cleaned = (name || '').normalize('NFC')
      .replace(/[^\p{L}\p{N}]+/gu, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 24);
    return cleaned || 'Anonym';
  }

  function unslug(value) {
    return value.replace(/-/g, ' ');
  }

  var album = {
    PHOTO_DIR: PHOTO_DIR,
    THUMB_DIR: THUMB_DIR,
    slug: slug,
    unslug: unslug,

    /** Path of a photo, relative to the repo root. */
    path: function (dir, day, time, uploader, hash) {
      return dir + '/' + day + '/' + time + '__' + slug(uploader) + '__' + hash + '.jpg';
    },

    /** Turn a `thumbs/...` tree entry into a gallery item, or null if it is not one. */
    parse: function (entry) {
      if (entry.path.slice(0, THUMB_DIR.length + 1) !== THUMB_DIR + '/') return null;
      var match = PATTERN.exec(entry.path.slice(THUMB_DIR.length + 1));
      if (!match) return null;
      return {
        id: match[4],
        day: match[1],
        time: match[2],
        uploader: unslug(match[3]),
        thumbSha: entry.sha,
        thumbPath: entry.path,
        photoPath: PHOTO_DIR + '/' + entry.path.slice(THUMB_DIR.length + 1),
        photoSha: null,
        size: entry.size || 0
      };
    },

    /**
     * Build the gallery model from a tree listing: newest first, and only
     * thumbs whose full-size photo actually made it up (an upload that died
     * between the two commits should stay invisible rather than show a
     * thumbnail that opens into nothing).
     */
    fromTree: function (entries) {
      var photoShas = new Map();
      entries.forEach(function (entry) {
        if (entry.path.slice(0, PHOTO_DIR.length + 1) === PHOTO_DIR + '/') {
          photoShas.set(entry.path, entry.sha);
        }
      });

      var items = [];
      entries.forEach(function (entry) {
        var item = album.parse(entry);
        if (!item) return;
        var sha = photoShas.get(item.photoPath);
        if (!sha) return;
        item.photoSha = sha;
        items.push(item);
      });

      items.sort(function (a, b) {
        if (a.day !== b.day) return a.day < b.day ? 1 : -1;
        if (a.time !== b.time) return a.time < b.time ? 1 : -1;
        return a.id < b.id ? -1 : 1;
      });
      return items;
    },

    /** The set of hashes already in the album, for skipping duplicate uploads. */
    hashes: function (entries) {
      var seen = new Set();
      entries.forEach(function (entry) {
        var item = album.parse(entry);
        if (item) seen.add(item.id);
      });
      return seen;
    },

    /** Group a sorted item list into [{day, items}], keeping the sort order. */
    byDay: function (items) {
      var groups = [], current = null;
      items.forEach(function (item) {
        if (!current || current.day !== item.day) {
          current = { day: item.day, items: [] };
          groups.push(current);
        }
        current.items.push(item);
      });
      return groups;
    }
  };

  PS.album = album;
})(typeof globalThis !== 'undefined' ? globalThis : this);
