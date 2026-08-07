/*
 * Defaults for this deployment. Everything here can be overridden by the share
 * link, so leaving it empty is fine — the app then simply asks for a link.
 *
 * Filling `repo` in is worth it anyway: the share links get shorter, and a
 * guest who lands on the bare URL sees the album name instead of "Fotoalbum".
 *
 * Never put a token in this file. It is served to everyone who opens the page.
 */
window.PHOTOSHARE_CONFIG = {
  repo: '',            // e.g. 'imm0r/familienfotos'
  branch: 'main',
  title: 'Fotoalbum'
};
