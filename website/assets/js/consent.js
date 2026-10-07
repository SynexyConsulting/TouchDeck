// Touch Deck website: cookie consent banner for Google Analytics (Consent Mode v2).
// The defaults are set in each page's <head>, before the Google tag:
//   - analytics cookies off until "Accept" in the EEA, UK and Switzerland (by Google's region);
//   - on elsewhere until "Decline"; ad storage always off (the site has no ads).
// The choice is kept in localStorage ("td-consent": granted | denied) and applied in <head> on
// the next page load. A [data-consent-open] button (footer: Cookie settings) shows the banner again.
(function () {
  "use strict";
  var KEY = "td-consent";

  function stored() {
    try { return localStorage.getItem(KEY); } catch (e) { return null; }
  }

  function save(choice) {
    try { localStorage.setItem(KEY, choice); } catch (e) { /* private mode: applies to this page only */ }
  }

  // GA's cookies (_ga, _ga_<id>) sit on the widest domain it could set, e.g. .synexyconsulting.com.
  function dropAnalyticsCookies() {
    var parts = location.hostname.split(".");
    document.cookie.split(";").forEach(function (c) {
      var name = c.split("=")[0].trim();
      if (!/^_ga/.test(name)) return;
      for (var i = 0; i < parts.length - 1; i++) {
        var domain = parts.slice(i).join(".");
        document.cookie = name + "=; Max-Age=0; path=/; domain=." + domain;
      }
      document.cookie = name + "=; Max-Age=0; path=/";
    });
  }

  function choose(choice, banner) {
    save(choice);
    if (typeof window.gtag === "function") window.gtag("consent", "update", { analytics_storage: choice });
    if (choice === "denied") dropAnalyticsCookies();
    banner.hidden = true;
  }

  function build() {
    var banner = document.createElement("div");
    banner.className = "consent";
    banner.setAttribute("role", "region");
    banner.setAttribute("aria-label", "Cookie choice");
    banner.innerHTML =
      '<p>Touch Deck uses Google Analytics cookies to learn which parts of this site help people. ' +
      'No ads, and nothing is sold. <a href="license.html#privacy">More about this</a>.</p>' +
      '<div class="consent-actions">' +
      '<button type="button" class="pill pill-ghost pill-sm" data-choice="denied">Decline</button>' +
      '<button type="button" class="pill pill-amber pill-sm" data-choice="granted">Accept</button>' +
      "</div>";
    banner.addEventListener("click", function (e) {
      var b = e.target.closest("[data-choice]");
      if (b) choose(b.getAttribute("data-choice"), banner);
    });
    document.body.appendChild(banner);
    return banner;
  }

  var banner = null;
  if (!stored()) banner = build();

  document.querySelectorAll("[data-consent-open]").forEach(function (b) {
    b.addEventListener("click", function () {
      if (!banner) banner = build();
      banner.hidden = false;
      var first = banner.querySelector("button");
      if (first) first.focus();
    });
  });
})();
