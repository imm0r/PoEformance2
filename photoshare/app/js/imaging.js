/*
 * Turning a phone photo into something a git repository can live with.
 *
 * Straight off a modern phone a single photo is 4-8 MB. Fifty guests times a
 * hundred photos each would be a repository nobody can clone and a gallery
 * nobody can load on hotel wifi. Re-encoding to a 2560px long edge lands
 * around 400-700 KB with no visible loss on any screen at the party, and the
 * separate 480px thumbnail means the grid costs kilobytes per photo.
 *
 * The re-encode also drops the EXIF block, so GPS coordinates never leave the
 * phone. The capture date is read out beforehand (see exif.js).
 */
(function (global) {
  'use strict';

  var PS = global.PS || (global.PS = {});

  // iOS caps canvas area hard; rasterising a 48-megapixel photo at full size
  // gets the canvas silently zeroed out instead of throwing.
  var MAX_RASTER_EDGE = 4096;

  function canvasOf(width, height) {
    var canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    var ctx = canvas.getContext('2d');
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';
    return { canvas: canvas, ctx: ctx };
  }

  /** Decode a blob through an <img>, which applies EXIF orientation for us. */
  PS.decodeImage = function (blob) {
    return new Promise(function (resolve, reject) {
      var url = URL.createObjectURL(blob);
      var img = new Image();
      img.decoding = 'async';
      img.onload = function () {
        resolve({
          img: img,
          width: img.naturalWidth,
          height: img.naturalHeight,
          release: function () { URL.revokeObjectURL(url); }
        });
      };
      img.onerror = function () {
        URL.revokeObjectURL(url);
        reject(new Error('Dieses Bildformat kann der Browser nicht öffnen (bei HEIC-Fotos vom iPhone hilft: Einstellungen → Kamera → Formate → "Maximale Kompatibilität").'));
      };
      img.src = url;
    });
  };

  /**
   * Scale to a long edge of `maxEdge` and encode as JPEG.
   *
   * Halving in steps rather than one big jump: a single drawImage from 8000px
   * to 480px point-samples and turns fine detail into noise.
   */
  PS.encodeJpeg = function (decoded, maxEdge, quality) {
    var scale = Math.min(1, maxEdge / Math.max(decoded.width, decoded.height));
    var targetW = Math.max(1, Math.round(decoded.width * scale));
    var targetH = Math.max(1, Math.round(decoded.height * scale));

    var startEdge = Math.min(Math.max(decoded.width, decoded.height), MAX_RASTER_EDGE);
    var startScale = startEdge / Math.max(decoded.width, decoded.height);
    var current = canvasOf(
      Math.max(targetW, Math.round(decoded.width * startScale)),
      Math.max(targetH, Math.round(decoded.height * startScale))
    );
    current.ctx.drawImage(decoded.img, 0, 0, current.canvas.width, current.canvas.height);

    while (current.canvas.width > targetW * 2 && current.canvas.height > targetH * 2) {
      var next = canvasOf(
        Math.max(targetW, Math.round(current.canvas.width / 2)),
        Math.max(targetH, Math.round(current.canvas.height / 2))
      );
      next.ctx.drawImage(current.canvas, 0, 0, next.canvas.width, next.canvas.height);
      current = next;
    }

    if (current.canvas.width !== targetW || current.canvas.height !== targetH) {
      var last = canvasOf(targetW, targetH);
      last.ctx.drawImage(current.canvas, 0, 0, targetW, targetH);
      current = last;
    }

    return new Promise(function (resolve, reject) {
      current.canvas.toBlob(function (blob) {
        if (blob) resolve(blob);
        else reject(new Error('Das Bild ließ sich nicht umwandeln.'));
      }, 'image/jpeg', quality);
    });
  };

  /**
   * Short content fingerprint, used as the photo's identity so re-uploading
   * the same file is a no-op.
   *
   * crypto.subtle only exists in a secure context, which a page opened straight
   * from disk is not — and opening the app from disk is a documented fallback.
   * The FNV-1a path is not a cryptographic hash and does not need to be: it
   * only has to give the same file the same name.
   */
  PS.sha8 = async function (buffer) {
    if (global.crypto && global.crypto.subtle) {
      var digest = await crypto.subtle.digest('SHA-256', buffer);
      var bytes = new Uint8Array(digest).subarray(0, 4);
      return Array.from(bytes).map(function (b) { return b.toString(16).padStart(2, '0'); }).join('');
    }
    var view = new Uint8Array(buffer);
    var hash = 0x811c9dc5;
    for (var i = 0; i < view.length; i++) {
      hash ^= view[i];
      hash = Math.imul(hash, 0x01000193) >>> 0;
    }
    // Mix the length in so two files that differ only by trailing padding differ here too.
    hash = (hash ^ Math.imul(view.length, 0x9e3779b1)) >>> 0;
    return hash.toString(16).padStart(8, '0');
  };

  PS.formatBytes = function (bytes) {
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return Math.round(bytes / 1024) + ' KB';
    return (bytes / 1024 / 1024).toFixed(1).replace('.', ',') + ' MB';
  };
})(typeof globalThis !== 'undefined' ? globalThis : this);
