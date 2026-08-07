/*
 * Just enough EXIF to answer one question: when was this photo taken?
 *
 * `File.lastModified` is not that date. On a phone that has synced from a
 * camera, or on photos someone forwarded through a messenger, it is the date
 * the file was copied — which would file the whole afternoon under today and
 * scramble the album's ordering. So read the real capture time out of the JPEG
 * when it is there, and only fall back to the file date when it is not.
 *
 * Deliberately read from the ORIGINAL bytes: the uploader re-encodes through a
 * canvas, which strips EXIF — including GPS. That is a feature (nobody wants
 * their home address in a family album), but it means the date has to be
 * rescued before the re-encode.
 */
(function (global) {
  'use strict';

  var PS = global.PS || (global.PS = {});

  var TAG_DATE_TIME = 0x0132;          // IFD0: file change date
  var TAG_EXIF_IFD = 0x8769;           // IFD0: pointer to the Exif sub-IFD
  var TAG_DATE_TIME_ORIGINAL = 0x9003; // Exif: shutter time
  var TAG_DATE_TIME_DIGITIZED = 0x9004;

  function readIfd(view, tiffStart, ifdOffset, little, wanted, found) {
    if (ifdOffset <= 0 || tiffStart + ifdOffset + 2 > view.byteLength) return;
    var base = tiffStart + ifdOffset;
    var count = view.getUint16(base, little);
    for (var i = 0; i < count; i++) {
      var entry = base + 2 + i * 12;
      if (entry + 12 > view.byteLength) return;
      var tag = view.getUint16(entry, little);
      if (wanted.indexOf(tag) < 0) continue;

      var type = view.getUint16(entry + 2, little);
      var components = view.getUint32(entry + 4, little);

      if (tag === TAG_EXIF_IFD) {
        readIfd(view, tiffStart, view.getUint32(entry + 8, little), little, wanted, found);
        continue;
      }
      if (type !== 2 || components < 19) continue; // ASCII "YYYY:MM:DD HH:MM:SS\0"

      var valueOffset = tiffStart + view.getUint32(entry + 8, little);
      if (valueOffset + 19 > view.byteLength) continue;
      var chars = '';
      for (var c = 0; c < 19; c++) chars += String.fromCharCode(view.getUint8(valueOffset + c));
      found[tag] = chars;
    }
  }

  function parseStamp(text) {
    var match = /^(\d{4}):(\d{2}):(\d{2})[ T](\d{2}):(\d{2}):(\d{2})$/.exec(text || '');
    if (!match) return null;
    var date = new Date(+match[1], +match[2] - 1, +match[3], +match[4], +match[5], +match[6]);
    if (isNaN(date.getTime())) return null;
    // Cameras with a dead clock love 1980; anything in the future is a wrong clock too.
    if (date.getFullYear() < 1990 || date.getTime() > Date.now() + 86400000) return null;
    return date;
  }

  /**
   * Capture time from a JPEG's EXIF block, or null when there is none.
   * Never throws — a photo with a mangled header should still upload.
   */
  PS.exifDate = function (buffer) {
    try {
      var view = new DataView(buffer);
      if (view.byteLength < 4 || view.getUint16(0, false) !== 0xffd8) return null; // not a JPEG

      var offset = 2;
      while (offset + 4 <= view.byteLength) {
        if (view.getUint8(offset) !== 0xff) return null; // marker stream out of sync
        var marker = view.getUint8(offset + 1);
        if (marker === 0xd8 || marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) {
          offset += 2;
          continue;
        }
        if (marker === 0xda) return null; // start of scan: no metadata beyond here
        var length = view.getUint16(offset + 2, false);
        if (length < 2) return null;

        if (marker === 0xe1 && offset + 10 <= view.byteLength) {
          var tag = '';
          for (var i = 0; i < 4; i++) tag += String.fromCharCode(view.getUint8(offset + 4 + i));
          if (tag === 'Exif') {
            var tiff = offset + 10;
            var order = view.getUint16(tiff, false);
            if (order !== 0x4949 && order !== 0x4d4d) return null;
            var little = order === 0x4949;
            if (view.getUint16(tiff + 2, little) !== 0x002a) return null;

            var found = {};
            readIfd(view, tiff, view.getUint32(tiff + 4, little), little,
              [TAG_DATE_TIME, TAG_EXIF_IFD, TAG_DATE_TIME_ORIGINAL, TAG_DATE_TIME_DIGITIZED], found);

            return parseStamp(found[TAG_DATE_TIME_ORIGINAL]) ||
              parseStamp(found[TAG_DATE_TIME_DIGITIZED]) ||
              parseStamp(found[TAG_DATE_TIME]);
          }
        }
        offset += 2 + length;
      }
      return null;
    } catch (e) {
      return null;
    }
  };
})(typeof globalThis !== 'undefined' ? globalThis : this);
