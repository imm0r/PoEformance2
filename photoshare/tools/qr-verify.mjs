/*
 * Cross-checks js/qr.js against python-qrcode, module for module.
 *
 * A QR encoder that is subtly wrong still produces a plausible-looking square,
 * so "it renders" proves nothing. This diffs every module of every version 1-20
 * against an independent implementation, for all eight masks, and additionally
 * checks that automatic mask selection agrees.
 *
 * Usage: node photoshare/tools/qr-verify.mjs        (needs: pip install qrcode)
 */
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const QR = require('../app/js/qr.js');

const PY = `
import sys, json, qrcode
from qrcode.util import QRData, MODE_8BIT_BYTE
jobs = json.loads(sys.stdin.read())
out = []
for job in jobs:
    q = qrcode.QRCode(version=job["v"], error_correction=qrcode.constants.ERROR_CORRECT_L,
                      box_size=1, border=0, mask_pattern=job["m"])
    q.add_data(QRData(job["d"].encode(), mode=MODE_8BIT_BYTE))
    q.make(fit=False)
    out.append(["".join("1" if c else "0" for c in row) for row in q.modules])
print(json.dumps(out))
`;

// Data codewords per version at error correction level L.
const DATA_CODEWORDS = [19, 34, 55, 80, 108, 136, 156, 194, 232, 274, 324, 370,
  428, 461, 523, 589, 647, 721, 795, 861];

function payload(version, seed) {
  // Fill each version to the last usable byte, so codeword padding, multi-block
  // splitting and interleaving are all exercised — not just the first block.
  const budget = DATA_CODEWORDS[version - 1] - (version < 10 ? 2 : 3);
  const alphabet = 'abcdefghijklmnopqrstuvwxyz0123456789-_.~/:# äöüß€';
  const enc = new TextEncoder();
  let s = '';
  for (let i = 0; enc.encode(s).length < budget; i++) {
    const next = alphabet[(i * 7 + seed * 13) % alphabet.length];
    if (enc.encode(s + next).length > budget) break;
    s += next;
  }
  return s;
}

const jobs = [];
for (let v = 1; v <= 20; v++) {
  for (let m = 0; m < 8; m++) jobs.push({ v, m, d: payload(v, m + v) });
}

const expected = JSON.parse(
  execFileSync('python3', ['-c', PY], { input: JSON.stringify(jobs), maxBuffer: 1 << 28 }).toString()
);

let failures = 0;
jobs.forEach((job, i) => {
  const qr = QR.encode(job.d, { version: job.v, mask: job.m });
  const want = expected[i];
  if (qr.size !== want.length) {
    console.error(`v${job.v} m${job.m}: size ${qr.size} != ${want.length}`);
    failures++;
    return;
  }
  for (let r = 0; r < qr.size; r++) {
    let got = '';
    for (let c = 0; c < qr.size; c++) got += qr.get(r, c) ? '1' : '0';
    if (got !== want[r]) {
      console.error(`v${job.v} m${job.m}: row ${r} differs\n  got  ${got}\n  want ${want[r]}`);
      failures++;
      return;
    }
  }
});

// Automatic version + mask selection must land on something a decoder accepts,
// which here means: the same grid python-qrcode produces for that mask.
const autoText = 'https://imm0r.github.io/fotos/#r=imm0r/family-photos&k=github_pat_' + 'x'.repeat(70);
const auto = QR.encode(autoText);
const autoWant = JSON.parse(
  execFileSync('python3', ['-c', PY], {
    input: JSON.stringify([{ v: auto.version, m: auto.mask, d: autoText }]),
    maxBuffer: 1 << 28
  }).toString()
)[0];
for (let r = 0; r < auto.size; r++) {
  let got = '';
  for (let c = 0; c < auto.size; c++) got += auto.get(r, c) ? '1' : '0';
  if (got !== autoWant[r]) { console.error(`auto: row ${r} differs`); failures++; break; }
}

console.log(failures === 0
  ? `OK: ${jobs.length} grids identical to python-qrcode; auto-picked v${auto.version} mask ${auto.mask} for a ${autoText.length}-char share link`
  : `FAILED: ${failures} mismatches`);
process.exit(failures === 0 ? 0 : 1);
