using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DirectN;
using DirectN.Extensions.Com;
using DirectN.Extensions.Utilities;
using WebView2;
using WebView2.Utilities;

namespace PoEformance.Config;

/// <summary>
/// A browser on the game's own trade site, which is where the trade queries actually run.
/// </summary>
/// <remarks>
/// WHY A WHOLE BROWSER FOR TWO HTTP REQUESTS. The trade endpoints are Cloudflare-gated. A
/// session cookie on its own gets a 403; passing the check wants the browser's cookies, its
/// User-Agent AND its TLS fingerprint, together. No HTTP client this tool could write replays
/// that reliably, and the ones that come close do it by impersonating a browser - which is
/// exactly the thing the check exists to catch.
///
/// So the request is made BY a browser, as a same-origin fetch from a page already on
/// pathofexile.com. Cloudflare is satisfied because nothing is being imitated.
///
/// AND NO SECRET EVER LEAVES IT. This side never reads a cookie, never stores one and never
/// logs one: it posts a name in and gets asking prices back. The sign-in lives in this window's
/// own browser profile, next to the executable, and that is the whole of it. The cookie-manager
/// route was available and deliberately not taken.
///
/// IT IS A REAL WINDOW AND IT STAYS ONE. Once the session works it is HIDDEN rather than closed
/// or made headless - see <see cref="TradeSession.HideWhenSignedIn"/> - and that distinction is
/// the point: a hidden window can be brought back with a button, and what it is doing is still a
/// page anybody can look at. Hiding it changes when it is on screen, not what it does. It comes
/// back by itself the moment the site answers 401 or 403, because that means the sign-in needs a
/// person.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class TradeSessionWindow : Window
{
    private readonly string _url;
    private readonly string _profile;
    private readonly Action<string> _heard;

    private ComObject<ICoreWebView2Controller>? _controller;
    private ComObject<ICoreWebView2>? _webView;

    // Both callbacks are kept in fields on purpose: the browser holds only a native reference,
    // so nothing on the managed side would otherwise stop a GC tearing the bridge down.
    private CoreWebView2WebMessageReceivedEventHandler? _messageHandler;
    private CoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler? _scriptHandler;
    private EventRegistrationToken _messageToken;

    /// <param name="url">The trade page to open - the search page for the league being played.</param>
    /// <param name="profile">Where the sign-in is kept. Its own folder, never the config window's.</param>
    /// <param name="heard">Called with every JSON message the page posts back.</param>
    public TradeSessionWindow(string title, string url, string profile, Action<string> heard)
        : base(title)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(heard);
        _url = url;
        _profile = profile;
        _heard = heard;

        WebView2Utilities.Initialize(Assembly.GetEntryAssembly()!, throwOnError: false);

        if (WebView2Utilities.GetAvailableCoreWebView2BrowserVersionString() is null)
        {
            // No Evergreen runtime. Said in the window, where the person looking for the page
            // already is - the same failure the config window reports.
            Text = $"{title} - WebView2 runtime not found (install the Evergreen runtime)";
            return;
        }

        WebView2.Functions.CreateCoreWebView2EnvironmentWithOptions(
            PWSTR.Null,
            PWSTR.From(_profile),
            null!,
            new CoreWebView2CreateCoreWebView2EnvironmentCompletedHandler((result, environment) =>
            {
                environment.CreateCoreWebView2Controller(
                    Handle,
                    new CoreWebView2CreateCoreWebView2ControllerCompletedHandler((_, controller) =>
                        AttachController(controller)));
            }));
    }

    /// <summary>Whether the page's helper has come up and queries can be sent.</summary>
    public bool Ready { get; set; }

    /// <summary>Posts a message to the page. Only valid on this window's own thread.</summary>
    public void Post(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        _webView?.Object.PostWebMessageAsJson(PWSTR.From(json));
    }

    private void AttachController(ICoreWebView2Controller controller)
    {
        _controller = new ComObject<ICoreWebView2Controller>(controller);
        controller.put_Bounds(ClientRect).ThrowOnError();
        controller.get_CoreWebView2(out ICoreWebView2 webView).ThrowOnError();
        _webView = new ComObject<ICoreWebView2>(webView);

        _messageHandler = new CoreWebView2WebMessageReceivedEventHandler((_, args) =>
        {
            args.get_WebMessageAsJson(out PWSTR message).ThrowOnError();
            _heard(message.ToStringAndDispose() ?? string.Empty);
        });
        webView.add_WebMessageReceived(_messageHandler, ref _messageToken).ThrowOnError();

        // Registered BEFORE the first navigation, so the helper is present on the very first
        // page rather than only after a reload - which would look like a window that opens and
        // then answers nothing.
        _scriptHandler = new CoreWebView2AddScriptToExecuteOnDocumentCreatedCompletedHandler((_, _) => { });
        webView.AddScriptToExecuteOnDocumentCreated(PWSTR.From(Helper), _scriptHandler).ThrowOnError();

        webView.Navigate(PWSTR.From(_url)).ThrowOnError();
    }

    /// <summary>
    /// The page-side helper, injected into every document this window loads.
    /// </summary>
    /// <remarks>
    /// It answers two commands. <c>tradeQuery</c> waits for a name and runs the two-step trade
    /// query as a SAME-ORIGIN fetch - the search posts the query and gets an id plus a list of
    /// result ids, the fetch turns the first few of those into listings - and posts back what it
    /// found. <c>exchangeProbe</c> is the diagnostic described on <see cref="TradeProbe"/>: it
    /// reads the site's own currency ids out of its static data, asks the currency exchange about
    /// several of them in ONE request, and reports the rate-limit headers and one raw listing.
    ///
    /// <c>credentials: 'include'</c> is what makes it the signed-in browser asking rather than
    /// an anonymous one, and it is the only reason any of this works.
    ///
    /// TEN RESULTS, sorted by price ascending, because what is wanted is the cheap end of the
    /// market and not a catalogue: the price comes from the median of the cheapest few.
    ///
    /// It reports the STATUS of a failure rather than just failing, because 401 and 403 mean
    /// something different from a timeout - one is "sign in again", the other is "try later".
    ///
    /// Contract from the AHK tool (imm0r/PoEformance), which has been running it since
    /// 2026-06-24.
    /// </remarks>
    private const string Helper = """
        (function () {
          if (window.__poeformanceTrade) { return; }
          window.__poeformanceTrade = 1;
          var post = function (o) { try { window.chrome.webview.postMessage(o); } catch (e) { } };
          var limitsOf = function (r) {
            var out = [];
            try {
              r.headers.forEach(function (v, k) {
                if (k.toLowerCase().indexOf('x-rate-limit') === 0) { out.push(k + '=' + v); }
              });
            } catch (e) { }
            return out.join('   ');
          };
          window.chrome.webview.addEventListener('message', async function (e) {
            var m = e.data;
            if (m && m.cmd === 'exchangeProbe') {
              try {
                // The site's own currency ids, which are neither poe.ninja's nor the metadata
                // paths - and the table any exchange pricing would have to join through.
                var statics = await fetch('/api/trade2/data/static', { credentials: 'include' });
                if (!statics.ok) {
                  post({ id: m.id, ok: false, status: statics.status, error: 'static data' });
                  return;
                }
                var tags = [], named = {};
                var stat = await statics.json();
                (stat.result || []).forEach(function (group) {
                  if (group && group.id === 'Currency') {
                    (group.entries || []).forEach(function (one) {
                      if (one && one.id) { tags.push(one.id + ' = ' + (one.text || '?')); named[one.id] = one.text || ''; }
                    });
                  }
                });
                if (tags.length === 0) {
                  post({ id: m.id, ok: false, status: statics.status, error: 'no currency ids in the static data' });
                  return;
                }

                // LOOK THE TWO IDS UP; DO NOT SPELL THEM. The previous probe hard-coded
                // 'divine' and 'exalted' while holding the site's own table of forty ids in a
                // variable beside it, and got back a 200 with an empty result - which reads
                // exactly like an empty market and is not one. Matching on the name the site
                // itself prints is the difference between a wrong answer and no answer.
                var idFor = function (name) {
                  for (var k in named) {
                    if (named[k] && named[k].toLowerCase() === name) { return k; }
                  }
                  return null;
                };
                var div = idFor('divine orb'), exa = idFor('exalted orb');
                if (!div || !exa) {
                  post({ id: m.id, ok: false, status: statics.status, tags: tags,
                         error: 'the site lists no "Divine Orb" (' + div + ') or "Exalted Orb" (' + exa + ')' });
                  return;
                }
                // WANT TAKES EXACTLY ONE. Asking about twelve came back
                // 400 "Too many items `want` items selected." - and the reference had said so
                // all along: Exiled Exchange builds every query as want: [oneTag] and puts its
                // array on the OTHER side, have. So a purse cannot be priced in one request,
                // and there is nothing left to measure about batching the want side.
                //
                // What is left is worth one request: the EXALTED-PER-DIVINE RATE. Every figure
                // this tool shows is computed in Exalted and displayed as Divine through that
                // one number, poe.ninja put it at 454.2, and Exiled Exchange showed 785 for the
                // same league. Whichever is right, the live exchange is the thing that knows.
                var ask = async function (league, want, have) {
                  var r = await fetch('/api/trade2/exchange/' + encodeURIComponent(league), {
                    method: 'POST',
                    headers: { 'content-type': 'application/json' },
                    credentials: 'include',
                    body: JSON.stringify({
                      engine: 'new',
                      query: { status: { option: 'online' }, have: have, want: want },
                      sort: { have: 'asc' }
                    })
                  });
                  // THE BODY OF A FAILURE IS THE ANSWER. GGG replies to a bad query with
                  // {"error":{"code":N,"message":"..."}} that usually names the offending field,
                  // and the first probe discarded it - which is why its 400 taught us nothing.
                  var text = await r.text();
                  var out = { league: league, status: r.status, limits: limitsOf(r), body: text.slice(0, 900) };
                  try { out.json = JSON.parse(text); } catch (e) { }
                  out.keys = Object.keys((out.json && out.json.result) || {});
                  return out;
                };

                var got = await ask(m.league, [div], [exa]);

                // AN EMPTY ANSWER IS TWO DIFFERENT FACTS WEARING ONE FACE: a query the site
                // cannot match, or a league that simply has no currency exchange. Only a second
                // league tells them apart, and the difference decides the whole feature - an
                // exchange that does not run in Standard cannot price this player's purse at all.
                var elsewhere = null;
                if (got.keys.length === 0 && got.status === 200) {
                  try {
                    var lg = await fetch('/api/trade2/data/leagues', { credentials: 'include' });
                    var leagues = (await lg.json()).result || [];
                    for (var n = 0; n < leagues.length; n++) {
                      var other = leagues[n] && (leagues[n].id || leagues[n].text);
                      if (other && other !== m.league) { elsewhere = await ask(other, [div], [exa]); break; }
                    }
                  } catch (e) { }
                }

                // THE OTHER ENDPOINT. The reference picks between two APIs per item -
                // apiToSatisfySearch in common.ts returns "bulk" (the exchange asked above) or
                // "trade" (/api/trade2/search) - and BOTH return live listings a person can
                // whisper and click. So an empty exchange cannot be read as "this league has no
                // market": it only means the exchange has nothing, and the search may still be
                // full. Asked in the played league, always, because that is the contradiction.
                var listed = null;
                try {
                  var s = await fetch('/api/trade2/search/' + encodeURIComponent(m.league), {
                    method: 'POST',
                    headers: { 'content-type': 'application/json' },
                    credentials: 'include',
                    body: JSON.stringify({
                      query: {
                        status: { option: 'online' },
                        // The site's own display name for the id, not a string typed from memory.
                        type: named[div],
                        stats: [{ type: 'and', filters: [] }],
                        filters: { trade_filters: { filters: { price: { option: exa } } } }
                      },
                      sort: { price: 'asc' }
                    })
                  });
                  var stext = await s.text();
                  listed = { status: s.status, body: stext.slice(0, 500), total: 0, rate: 0 };
                  var sj = null;
                  try { sj = JSON.parse(stext); } catch (e) { }
                  listed.total = (sj && sj.total) || 0;
                  var ids = (sj && sj.result) || [];

                  // sort: {price: 'asc'} means the first id is the cheapest, so three is plenty
                  // to read a going rate and cheap against the fetch limit.
                  if (ids.length && sj.id) {
                    var f = await fetch('/api/trade2/fetch/' + ids.slice(0, 3).join(',')
                                        + '?query=' + encodeURIComponent(sj.id),
                                        { credentials: 'include' });
                    var fj = await f.json();
                    var rows = (fj && fj.result) || [];
                    for (var q = 0; q < rows.length; q++) {
                      var price = rows[q] && rows[q].listing && rows[q].listing.price;
                      if (!price || !price.amount) { continue; }
                      // PER ORB, not per listing: a stack of twenty priced as one lot would
                      // otherwise read as a single orb costing twenty times too much.
                      var stack = (rows[q].item && rows[q].item.stackSize) || 1;
                      listed.rate = price.amount / stack;
                      listed.raw = JSON.stringify(rows[q]).slice(0, 1200);
                      break;
                    }
                  }
                } catch (e) { }

                var chosen = (elsewhere && elsewhere.keys.length > 0) ? elsewhere : got;
                var results = (chosen.json && chosen.json.result) || {};
                var keys = chosen.keys;

                // ONE OFFER ONLY, as the reference does: a listing carrying several offers gives
                // no way to say which of them the price is, so it cannot be read as a rate.
                var rate = 0;
                for (var i = 0; i < keys.length; i++) {
                  var listing = results[keys[i]] && results[keys[i]].listing;
                  var offers = (listing && listing.offers) || [];
                  if (offers.length !== 1) { continue; }
                  var paid = offers[0].exchange, per = offers[0].item;
                  if (!paid || !per || !per.amount) { continue; }
                  // sort: {have: 'asc'} already puts the cheapest first, so the first readable
                  // listing IS the going rate - no need to scan for a minimum.
                  rate = paid.amount / per.amount;
                  break;
                }

                // BOTH ATTEMPTS, ALWAYS, naming the ids actually sent. When the answer is empty
                // the only useful thing left is what exactly was asked and where.
                var said = function (a) {
                  return a ? (a.league + ': ' + a.status + ' ' + a.keys.length + ' listings ' + (a.body || '')) : '';
                };

                post({
                  id: m.id,
                  ok: chosen.status >= 200 && chosen.status < 300,
                  status: chosen.status,
                  limits: chosen.limits,
                  tags: tags,
                  asked: 1,
                  got: keys.length,
                  rate: rate,
                  league: chosen.league,
                  listed: listed ? listed.total : 0,
                  listedRate: listed ? listed.rate : 0,
                  raw: ((keys.length ? JSON.stringify(results[keys[0]]) : '')
                        || (listed && listed.raw) || '').slice(0, 3000),
                  error: 'want ' + div + ', have ' + exa + '   |   exchange ' + said(got)
                       + (elsewhere ? '   |   exchange ' + said(elsewhere) : '')
                       + (listed ? '   |   search ' + m.league + ': ' + listed.status + ' '
                                 + listed.total + ' listed ' + (listed.rate ? '' : listed.body) : '')
                });
              } catch (err) {
                post({ id: m.id, ok: false, status: 0, error: String(err) });
              }
              return;
            }
            if (!m || m.cmd !== 'tradeQuery') { return; }
            try {
              var searchUrl = '/api/trade2/search/poe2/' + encodeURIComponent(m.league);
              var body = JSON.stringify({ query: { status: { option: 'online' }, name: m.name }, sort: { price: 'asc' } });
              var search = await fetch(searchUrl, {
                method: 'POST',
                headers: { 'content-type': 'application/json' },
                credentials: 'include',
                body: body
              });
              if (!search.ok) { post({ id: m.id, ok: false, status: search.status, error: 'search' }); return; }
              var found = await search.json();
              var ids = (found.result || []).slice(0, 10);
              if (!found.id || ids.length === 0) { post({ id: m.id, ok: true, status: 200, listings: [] }); return; }
              var fetched = await fetch(
                '/api/trade2/fetch/' + ids.join(',') + '?query=' + found.id + '&realm=poe2',
                { credentials: 'include' });
              if (!fetched.ok) { post({ id: m.id, ok: false, status: fetched.status, error: 'fetch' }); return; }
              var page = await fetched.json();
              var listings = (page.result || []).map(function (r) {
                return (r && r.listing && r.listing.price)
                  ? { amount: r.listing.price.amount, currency: r.listing.price.currency }
                  : null;
              }).filter(Boolean);
              post({ id: m.id, ok: true, status: 200, listings: listings });
            } catch (err) {
              post({ id: m.id, ok: false, status: 0, error: String(err) });
            }
          });
          post({ cmd: 'tradeHelperReady' });
        })();
        """;

    protected override bool OnResized(WindowResizedType type, SIZE size)
    {
        if (_controller is { IsDisposed: false } controller)
        {
            controller.Object.put_Bounds(ClientRect);
        }

        return base.OnResized(type, size);
    }

    /// <summary>
    /// Tears the bridge down. Every step is optional and none of them may throw.
    /// </summary>
    /// <remarks>
    /// The same rule the config window learned the hard way: this runs after the window is gone,
    /// nothing is left to catch an exception here, and one escaping takes the whole process with
    /// it. "Already cleaned up" has to be an ordinary outcome.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                if (_webView is { IsDisposed: false } webView && _messageHandler is not null)
                {
                    webView.Object.remove_WebMessageReceived(_messageToken);
                }

                if (_controller is { IsDisposed: false } controller)
                {
                    controller.Object.Close();
                }
            }
            catch (Exception exception)
                when (exception is ObjectDisposedException or InvalidOperationException or COMException)
            {
                // The browser tore itself down first.
            }

            _webView?.Dispose();
            _controller?.Dispose();
            _webView = null;
            _controller = null;
        }

        base.Dispose(disposing);
    }
}
