/*
 * Unit tests for the EXIF date reader.
 *
 * This is the one piece of byte-level parsing in the app, and getting it wrong
 * is invisible: photos still upload, they just get filed under the wrong day.
 * So: build JPEG headers by hand, in both byte orders, and check what comes
 * back — including the cases where the answer has to be "no idea".
 *
 * Usage: node photoshare/tools/exif-test.mjs
 */
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
// exif.js is a classic script that hangs itself off the global object.
new Function(readFileSync(path.join(here, '../app/js/exif.js'), 'utf8')).call(globalThis);
const { exifDate } = globalThis.PS;

/**
 * Assemble a JPEG whose APP1 block carries `tags` in the Exif sub-IFD.
 * Only the shape matters here, so the image itself is an empty scan.
 */
function jpegWithExif({ little = true, tags = {}, ifd0 = {} }) {
  const parts = [];
  const tiff = [];
  const u16 = (v) => (little ? [v & 0xff, v >> 8] : [v >> 8, v & 0xff]);
  const u32 = (v) => (little
    ? [v & 0xff, (v >> 8) & 0xff, (v >> 16) & 0xff, (v >>> 24) & 0xff]
    : [(v >>> 24) & 0xff, (v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff]);

  tiff.push(...(little ? [0x49, 0x49] : [0x4d, 0x4d]), ...u16(0x002a), ...u32(8));

  const ifd0Tags = Object.entries(ifd0);
  const subTags = Object.entries(tags);

  // Layout: IFD0 (with a pointer to the sub-IFD), then the sub-IFD, then the
  // ASCII payloads each 20-byte string lives at.
  const ifd0Start = 8;
  const ifd0Size = 2 + (ifd0Tags.length + (subTags.length ? 1 : 0)) * 12 + 4;
  const subStart = ifd0Start + ifd0Size;
  const subSize = 2 + subTags.length * 12 + 4;
  let dataAt = subStart + subSize;

  const strings = [];
  function stringEntry(tag, text) {
    const offset = dataAt;
    strings.push({ offset, text });
    dataAt += 20;
    return [...u16(tag), ...u16(2), ...u32(20), ...u32(offset)];
  }

  const ifd0Body = [];
  ifd0Tags.forEach(([tag, text]) => ifd0Body.push(...stringEntry(Number(tag), text)));
  if (subTags.length) ifd0Body.push(...u16(0x8769), ...u16(4), ...u32(1), ...u32(subStart));
  const subBody = [];
  subTags.forEach(([tag, text]) => subBody.push(...stringEntry(Number(tag), text)));

  tiff.push(...u16(ifd0Tags.length + (subTags.length ? 1 : 0)), ...ifd0Body, ...u32(0));
  tiff.push(...u16(subTags.length), ...subBody, ...u32(0));
  strings.forEach(({ text }) => {
    const bytes = Buffer.from(text.padEnd(19, ' ').slice(0, 19) + '\0', 'latin1');
    tiff.push(...bytes);
  });

  const app1 = [...Buffer.from('Exif\0\0', 'latin1'), ...tiff];
  parts.push(0xff, 0xd8);
  parts.push(0xff, 0xe1, ((app1.length + 2) >> 8) & 0xff, (app1.length + 2) & 0xff, ...app1);
  parts.push(0xff, 0xda, 0x00, 0x02, 0xff, 0xd9);
  return new Uint8Array(parts).buffer;
}

const TAG_DATE_TIME = 0x0132;
const TAG_ORIGINAL = 0x9003;
const TAG_DIGITIZED = 0x9004;

let failures = 0;
function check(name, actual, expected) {
  const ok = String(actual) === String(expected);
  if (!ok) failures++;
  console.log(`${ok ? 'ok  ' : 'FAIL'}  ${name}${ok ? '' : `\n        got ${actual}, want ${expected}`}`);
}

function iso(date) {
  if (!date) return String(date);
  const pad = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

check('little-endian DateTimeOriginal',
  iso(exifDate(jpegWithExif({ tags: { [TAG_ORIGINAL]: '2026:08:07 15:30:12' } }))),
  '2026-08-07 15:30:12');

check('big-endian DateTimeOriginal',
  iso(exifDate(jpegWithExif({ little: false, tags: { [TAG_ORIGINAL]: '2019:12:24 18:05:00' } }))),
  '2019-12-24 18:05:00');

check('DateTimeDigitized when the original is missing',
  iso(exifDate(jpegWithExif({ tags: { [TAG_DIGITIZED]: '2021:03:01 07:00:00' } }))),
  '2021-03-01 07:00:00');

check('IFD0 DateTime as the last resort',
  iso(exifDate(jpegWithExif({ ifd0: { [TAG_DATE_TIME]: '2020:06:15 12:00:00' } }))),
  '2020-06-15 12:00:00');

check('the shutter time wins over the file date',
  iso(exifDate(jpegWithExif({
    ifd0: { [TAG_DATE_TIME]: '2024:01:01 00:00:00' },
    tags: { [TAG_ORIGINAL]: '2023:07:04 21:15:30' }
  }))),
  '2023-07-04 21:15:30');

check('a JPEG without EXIF', exifDate(jpegWithExif({})), null);
check('not a JPEG at all', exifDate(new Uint8Array([1, 2, 3, 4, 5, 6]).buffer), null);
check('an empty buffer', exifDate(new ArrayBuffer(0)), null);

// A camera whose battery died reports 1980; a phone with a wrong clock reports
// next year. Both are worse than falling back to the file date.
check('a dead camera clock is rejected',
  exifDate(jpegWithExif({ tags: { [TAG_ORIGINAL]: '1980:01:01 00:00:00' } })), null);
check('a date in the future is rejected',
  exifDate(jpegWithExif({ tags: { [TAG_ORIGINAL]: `${new Date().getFullYear() + 2}:01:01 00:00:00` } })), null);
check('a garbled date string is rejected',
  exifDate(jpegWithExif({ tags: { [TAG_ORIGINAL]: 'not a date at all' } })), null);

// Truncation must not throw: a half-downloaded photo should still upload,
// just without a capture date.
const full = new Uint8Array(jpegWithExif({ tags: { [TAG_ORIGINAL]: '2026:08:07 15:30:12' } }));
let threwAt = null;
for (let cut = 1; cut < full.length && threwAt === null; cut += 3) {
  try {
    exifDate(full.slice(0, cut).buffer);
  } catch (error) {
    threwAt = `${error} at ${cut} bytes`;
  }
}
check('every truncation is survivable', threwAt || 'no throw', 'no throw');

console.log(failures === 0 ? '\nall EXIF checks passed' : `\n${failures} failed`);
process.exit(failures ? 1 : 0);
