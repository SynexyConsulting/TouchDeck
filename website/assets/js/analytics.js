// Touch Deck website: GA4 events on top of the page view the Google tag in <head> sends.
// Links are classified by where they go, so new links are tracked without extra markup:
//   download_click  links to the TouchDeckUpdates releases
//   outbound_click  any other link off this site
//   nav_click       in-page anchors and the other pages of this site
// plus faq_open, demo_replay and section_view (each section once per page load).
// Without the Google tag (blocked, or offline) gtag is missing and nothing happens.
(function () {
  "use strict";
  if (typeof window.gtag !== "function") return;

  // GA4's gtag already sends with sendBeacon, so a hit survives the click navigating away.
  function track(name, params) {
    window.gtag("event", name, params || {});
  }

  function clean(text) {
    return (text || "").replace(/\s+/g, " ").trim().slice(0, 100);
  }

  // Which part of the page an element is in: the section's id, or header/main/footer.
  function sectionOf(el) {
    var s = el.closest("section[id], header, main, footer");
    if (!s) return "page";
    return s.id || s.tagName.toLowerCase();
  }

  // ---------- Links ----------
  document.addEventListener("click", function (e) {
    var a = e.target.closest("a[href]");
    if (!a) return;
    var url = new URL(a.getAttribute("href"), location.href);
    // A download card's title (<strong>) says which one it is; otherwise use the link text.
    var strong = a.querySelector("strong");
    var text = clean(strong ? strong.textContent : a.textContent) || a.getAttribute("aria-label") || "";
    var section = sectionOf(a);

    if (url.host !== location.host) {
      if (/\/TouchDeckUpdates\/releases/.test(url.pathname)) {
        track("download_click", { link_text: text, link_url: url.href, section: section });
      } else {
        track("outbound_click", { link_domain: url.hostname, link_url: url.href, link_text: text, section: section });
      }
    } else {
      var target = url.pathname === location.pathname && url.hash ? url.hash : url.pathname + url.hash;
      track("nav_click", { link_text: text, target: target, section: section });
    }
  });

  // ---------- FAQ ----------
  document.querySelectorAll("#faq details").forEach(function (d) {
    d.addEventListener("toggle", function () {
      if (d.open) track("faq_open", { question: clean(d.querySelector("summary").textContent) });
    });
  });

  // ---------- Console demo ----------
  var replay = document.getElementById("replay");
  if (replay) replay.addEventListener("click", function () { track("demo_replay"); });

  // ---------- Sections seen ----------
  var sections = document.querySelectorAll("section[id]");
  if ("IntersectionObserver" in window && sections.length) {
    var seen = {};
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        var id = e.target.id;
        if (!e.isIntersecting || seen[id]) return;
        seen[id] = true;
        io.unobserve(e.target);
        track("section_view", { section: id });
      });
    // Seen = reached the middle fifth of the window. A ratio threshold would never fire
    // for sections taller than the window (the screens tour is).
    }, { rootMargin: "-40% 0px -40% 0px" });
    sections.forEach(function (s) { io.observe(s); });
  }
})();
