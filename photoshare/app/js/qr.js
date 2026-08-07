/*
 * Minimal QR encoder: byte mode, error correction level L, versions 1-20.
 *
 * Why hand-rolled: the app must run with zero external requests (a CDN script
 * would break the "open index.html from anywhere" fallback and leak the page
 * to a third party). Level L only, because the only thing we ever encode is a
 * share URL and L gives the most capacity per version.
 *
 * The block/alignment tables below were extracted from python-qrcode rather
 * than typed from memory, and the whole encoder is diffed module-for-module
 * against it in tools/qr-verify.mjs.
 */
(function (global) {
  'use strict';

  // [version] -> [[blockCount, totalCodewords, dataCodewords], ...]
  var RS_BLOCKS = [
    [[1, 26, 19]], [[1, 44, 34]], [[1, 70, 55]], [[1, 100, 80]], [[1, 134, 108]],
    [[2, 86, 68]], [[2, 98, 78]], [[2, 121, 97]], [[2, 146, 116]],
    [[2, 86, 68], [2, 87, 69]], [[4, 101, 81]], [[2, 116, 92], [2, 117, 93]],
    [[4, 133, 107]], [[3, 145, 115], [1, 146, 116]], [[5, 109, 87], [1, 110, 88]],
    [[5, 122, 98], [1, 123, 99]], [[1, 135, 107], [5, 136, 108]],
    [[5, 150, 120], [1, 151, 121]], [[3, 141, 113], [4, 142, 114]],
    [[3, 135, 107], [5, 136, 108]]
  ];

  var ALIGN = [
    [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42],
    [6, 26, 46], [6, 28, 50], [6, 30, 54], [6, 32, 58], [6, 34, 62],
    [6, 26, 46, 66], [6, 26, 48, 70], [6, 26, 50, 74], [6, 30, 54, 78],
    [6, 30, 56, 82], [6, 30, 58, 86], [6, 34, 62, 90]
  ];

  // Remainder bits appended after the interleaved codewords, per version.
  function remainderBits(v) {
    if (v === 1) return 0;
    if (v <= 6) return 7;
    if (v <= 13) return 0;
    return 3;
  }

  // GF(256) with primitive polynomial 0x11d.
  var EXP = new Uint8Array(256), LOG = new Uint8Array(256);
  (function () {
    var x = 1;
    for (var i = 0; i < 255; i++) {
      EXP[i] = x;
      LOG[x] = i;
      x <<= 1;
      if (x & 0x100) x ^= 0x11d;
    }
  })();

  function gmul(a, b) {
    if (a === 0 || b === 0) return 0;
    return EXP[(LOG[a] + LOG[b]) % 255];
  }

  // Generator polynomial for `degree` error correction codewords.
  function generatorPoly(degree) {
    var poly = [1];
    for (var d = 0; d < degree; d++) {
      var next = new Array(poly.length + 1).fill(0);
      for (var i = 0; i < poly.length; i++) {
        next[i] ^= gmul(poly[i], 1);
        next[i + 1] ^= gmul(poly[i], EXP[d]);
      }
      poly = next;
    }
    return poly;
  }

  function ecCodewords(data, count) {
    var gen = generatorPoly(count);
    var rem = new Uint8Array(data.length + count);
    rem.set(data);
    for (var i = 0; i < data.length; i++) {
      var factor = rem[i];
      if (factor === 0) continue;
      for (var j = 0; j < gen.length; j++) rem[i + j] ^= gmul(gen[j], factor);
    }
    return rem.subarray(data.length);
  }

  function bchDigit(v) {
    var n = 0;
    while (v !== 0) { n++; v >>>= 1; }
    return n;
  }

  var G15 = 0x537, G15_MASK = 0x5412, G18 = 0x1f25;

  function bchFormat(data) {
    var d = data << 10;
    while (bchDigit(d) - bchDigit(G15) >= 0) d ^= G15 << (bchDigit(d) - bchDigit(G15));
    return ((data << 10) | d) ^ G15_MASK;
  }

  function bchVersion(data) {
    var d = data << 12;
    while (bchDigit(d) - bchDigit(G18) >= 0) d ^= G18 << (bchDigit(d) - bchDigit(G18));
    return (data << 12) | d;
  }

  var MASKS = [
    function (i, j) { return (i + j) % 2 === 0; },
    function (i) { return i % 2 === 0; },
    function (i, j) { return j % 3 === 0; },
    function (i, j) { return (i + j) % 3 === 0; },
    function (i, j) { return (Math.floor(i / 2) + Math.floor(j / 3)) % 2 === 0; },
    function (i, j) { return (i * j) % 2 + (i * j) % 3 === 0; },
    function (i, j) { return ((i * j) % 2 + (i * j) % 3) % 2 === 0; },
    function (i, j) { return ((i + j) % 2 + (i * j) % 3) % 2 === 0; }
  ];

  function dataCapacity(version) {
    var total = 0;
    RS_BLOCKS[version - 1].forEach(function (g) { total += g[0] * g[2]; });
    return total;
  }

  function countBits(version) { return version < 10 ? 8 : 16; }

  function pickVersion(byteLength) {
    for (var v = 1; v <= 20; v++) {
      var needed = 4 + countBits(v) + 8 * byteLength;
      if (needed <= dataCapacity(v) * 8) return v;
    }
    throw new Error('payload too long for a version-20 QR code');
  }

  function buildCodewords(bytes, version) {
    var capacity = dataCapacity(version);
    var bits = [];
    function push(value, len) {
      for (var i = len - 1; i >= 0; i--) bits.push((value >>> i) & 1);
    }
    push(4, 4);                              // byte mode
    push(bytes.length, countBits(version));
    for (var i = 0; i < bytes.length; i++) push(bytes[i], 8);

    var terminator = Math.min(4, capacity * 8 - bits.length);
    push(0, terminator);
    while (bits.length % 8 !== 0) bits.push(0);

    var words = new Uint8Array(capacity);
    for (var b = 0; b < bits.length; b += 8) {
      var byte = 0;
      for (var k = 0; k < 8; k++) byte = (byte << 1) | bits[b + k];
      words[b / 8] = byte;
    }
    for (var p = bits.length / 8, alt = 0; p < capacity; p++, alt++) {
      words[p] = alt % 2 === 0 ? 0xec : 0x11;
    }
    return words;
  }

  // Split into blocks, compute EC, then interleave data words and EC words.
  function interleave(words, version) {
    var blocks = [], offset = 0, maxData = 0, maxEc = 0;
    RS_BLOCKS[version - 1].forEach(function (g) {
      for (var n = 0; n < g[0]; n++) {
        var data = words.subarray(offset, offset + g[2]);
        offset += g[2];
        var ec = ecCodewords(data, g[1] - g[2]);
        blocks.push({ data: data, ec: ec });
        maxData = Math.max(maxData, data.length);
        maxEc = Math.max(maxEc, ec.length);
      }
    });

    var out = [];
    for (var i = 0; i < maxData; i++) {
      blocks.forEach(function (bl) { if (i < bl.data.length) out.push(bl.data[i]); });
    }
    for (var j = 0; j < maxEc; j++) {
      blocks.forEach(function (bl) { if (j < bl.ec.length) out.push(bl.ec[j]); });
    }
    return Uint8Array.from(out);
  }

  function Grid(size) {
    this.size = size;
    this.cells = new Int8Array(size * size).fill(-1); // -1 = not yet placed
  }
  Grid.prototype.get = function (r, c) { return this.cells[r * this.size + c]; };
  Grid.prototype.set = function (r, c, v) { this.cells[r * this.size + c] = v ? 1 : 0; };
  Grid.prototype.free = function (r, c) { return this.cells[r * this.size + c] === -1; };

  function placeFinders(grid) {
    var size = grid.size;
    [[0, 0], [size - 7, 0], [0, size - 7]].forEach(function (origin) {
      for (var r = -1; r <= 7; r++) {
        for (var c = -1; c <= 7; c++) {
          var rr = origin[0] + r, cc = origin[1] + c;
          if (rr < 0 || cc < 0 || rr >= size || cc >= size) continue;
          var edge = (r >= 0 && r <= 6 && (c === 0 || c === 6)) ||
                     (c >= 0 && c <= 6 && (r === 0 || r === 6));
          var core = r >= 2 && r <= 4 && c >= 2 && c <= 4;
          grid.set(rr, cc, edge || core);
        }
      }
    });
  }

  function placeTiming(grid) {
    for (var i = 8; i < grid.size - 8; i++) {
      if (grid.free(i, 6)) grid.set(i, 6, i % 2 === 0);
      if (grid.free(6, i)) grid.set(6, i, i % 2 === 0);
    }
  }

  function placeAlignment(grid, version) {
    var pos = ALIGN[version - 1];
    for (var a = 0; a < pos.length; a++) {
      for (var b = 0; b < pos.length; b++) {
        var row = pos[a], col = pos[b];
        if (!grid.free(row, col)) continue; // overlaps a finder pattern
        for (var r = -2; r <= 2; r++) {
          for (var c = -2; c <= 2; c++) {
            var edge = Math.abs(r) === 2 || Math.abs(c) === 2 || (r === 0 && c === 0);
            grid.set(row + r, col + c, edge);
          }
        }
      }
    }
  }

  function placeVersionInfo(grid, version) {
    if (version < 7) return;
    var bits = bchVersion(version), size = grid.size;
    for (var i = 0; i < 18; i++) {
      var bit = ((bits >> i) & 1) === 1;
      grid.set(Math.floor(i / 3), (i % 3) + size - 8 - 3, bit);
      grid.set((i % 3) + size - 8 - 3, Math.floor(i / 3), bit);
    }
  }

  function placeFormatInfo(grid, mask) {
    var bits = bchFormat((1 << 3) | mask); // 0b01 = level L
    var size = grid.size;
    for (var i = 0; i < 15; i++) {
      var bit = ((bits >> i) & 1) === 1;
      if (i < 6) grid.set(i, 8, bit);
      else if (i < 8) grid.set(i + 1, 8, bit);
      else grid.set(size - 15 + i, 8, bit);

      if (i < 8) grid.set(8, size - i - 1, bit);
      else if (i === 8) grid.set(8, 15 - i, bit);
      else grid.set(8, 15 - i - 1, bit);
    }
    grid.set(size - 8, 8, true); // the always-dark module
  }

  function placeData(grid, words, mask) {
    var size = grid.size, maskFn = MASKS[mask];
    var row = size - 1, inc = -1, bitIndex = 7, byteIndex = 0;
    for (var col = size - 1; col > 0; col -= 2) {
      if (col === 6) col--; // the vertical timing pattern is not a data column
      for (;;) {
        for (var c = 0; c < 2; c++) {
          if (!grid.free(row, col - c)) continue;
          var dark = false;
          if (byteIndex < words.length) dark = ((words[byteIndex] >>> bitIndex) & 1) === 1;
          if (maskFn(row, col - c)) dark = !dark;
          grid.set(row, col - c, dark);
          if (--bitIndex === -1) { byteIndex++; bitIndex = 7; }
        }
        row += inc;
        if (row < 0 || row >= size) { row -= inc; inc = -inc; break; }
      }
    }
  }

  function penalty(grid) {
    var size = grid.size, score = 0, r, c, i;

    // Rule 1: runs of five or more same-coloured modules.
    for (var pass = 0; pass < 2; pass++) {
      for (r = 0; r < size; r++) {
        var run = 1, prev = -1;
        for (c = 0; c < size; c++) {
          var v = pass === 0 ? grid.get(r, c) : grid.get(c, r);
          if (v === prev) { run++; } else { if (run >= 5) score += 3 + (run - 5); run = 1; prev = v; }
        }
        if (run >= 5) score += 3 + (run - 5);
      }
    }

    // Rule 2: 2x2 blocks of one colour.
    for (r = 0; r < size - 1; r++) {
      for (c = 0; c < size - 1; c++) {
        var s = grid.get(r, c) + grid.get(r, c + 1) + grid.get(r + 1, c) + grid.get(r + 1, c + 1);
        if (s === 0 || s === 4) score += 3;
      }
    }

    // Rule 3: finder-like 1:1:3:1:1 patterns.
    var pat = [1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0];
    for (r = 0; r < size; r++) {
      for (c = 0; c + 11 <= size; c++) {
        if (matches(grid, r, c, pat, true)) score += 40;
        if (matches(grid, r, c, pat, false)) score += 40;
      }
    }

    // Rule 4: deviation from a 50% dark ratio.
    var dark = 0;
    for (i = 0; i < grid.cells.length; i++) dark += grid.cells[i];
    var ratio = Math.abs(dark * 100 / grid.cells.length - 50);
    score += Math.floor(ratio / 5) * 10;

    return score;
  }

  function matches(grid, r, c, pat, horizontal) {
    var fwd = true, rev = true;
    for (var i = 0; i < 11; i++) {
      var v = horizontal ? grid.get(r, c + i) : grid.get(c + i, r);
      if (v !== pat[i]) fwd = false;
      if (v !== pat[10 - i]) rev = false;
    }
    return fwd || rev;
  }

  function build(bytes, version, mask) {
    var grid = new Grid(version * 4 + 17);
    placeFinders(grid);
    placeAlignment(grid, version);
    placeTiming(grid);
    placeVersionInfo(grid, version);
    placeFormatInfo(grid, mask);
    placeData(grid, interleave(buildCodewords(bytes, version), version), mask);
    return grid;
  }

  /**
   * Encode `text` as a QR code.
   * Returns { size, version, mask, get(row, col) -> 0|1 }.
   */
  function encode(text, options) {
    var opts = options || {};
    var bytes = new TextEncoder().encode(text);
    var version = opts.version || pickVersion(bytes.length);

    var best = null;
    var masks = opts.mask === undefined || opts.mask === null ? [0, 1, 2, 3, 4, 5, 6, 7] : [opts.mask];
    masks.forEach(function (m) {
      var grid = build(bytes, version, m);
      var score = masks.length === 1 ? 0 : penalty(grid);
      if (!best || score < best.score) best = { grid: grid, score: score, mask: m };
    });

    return {
      size: best.grid.size,
      version: version,
      mask: best.mask,
      get: function (r, c) { return best.grid.get(r, c); }
    };
  }

  /** Render an encoded QR code as a standalone SVG string. */
  function toSvg(qr, options) {
    var opts = options || {};
    var quiet = opts.quiet === undefined ? 4 : opts.quiet;
    var dim = qr.size + quiet * 2;
    var path = [];
    for (var r = 0; r < qr.size; r++) {
      for (var c = 0; c < qr.size; c++) {
        if (qr.get(r, c)) path.push('M' + (c + quiet) + ' ' + (r + quiet) + 'h1v1h-1z');
      }
    }
    return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ' + dim + ' ' + dim + '" ' +
      'shape-rendering="crispEdges" role="img" aria-label="QR-Code">' +
      '<rect width="' + dim + '" height="' + dim + '" fill="' + (opts.light || '#fff') + '"/>' +
      '<path fill="' + (opts.dark || '#000') + '" d="' + path.join('') + '"/></svg>';
  }

  global.QR = { encode: encode, toSvg: toSvg };
})(typeof globalThis !== 'undefined' ? globalThis : this);

if (typeof module !== 'undefined' && module.exports) module.exports = globalThis.QR;
