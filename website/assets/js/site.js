// Touch Deck website. Works without GSAP (screens still switch, text still types);
// GSAP + ScrollTrigger add the scrubbed tilt, zoom and parallax. Reduced motion turns those off.
(function () {
  "use strict";
  var reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var hasGsap = typeof window.gsap !== "undefined" && typeof window.ScrollTrigger !== "undefined";

  // Reduced motion: show still screens instead of the animated ones.
  if (reduce) {
    document.querySelectorAll('img[src$="-anim.webp"]').forEach(function (img) {
      img.src = img.getAttribute("src").replace(/-anim\.webp$/, ".png");
    });
  }

  // ---------- Screens: which step is in the middle of the viewport ----------
  var steps = Array.prototype.slice.call(document.querySelectorAll(".step"));
  var dots = Array.prototype.slice.call(document.querySelectorAll(".stage-dots i"));
  var rect = document.getElementById("stage-rect");
  var current = 0;

  function show(i) {
    if (i === current) return;
    var dir = i > current ? 1 : -1;
    current = i;
    document.querySelectorAll(".stage .layer").forEach(function (img) {
      img.classList.toggle("on", +img.dataset.i === i);
    });
    dots.forEach(function (d, k) { d.classList.toggle("on", k === i); });
    if (hasGsap && !reduce && rect) {
      // A quick turn of the board, like flicking to the next page.
      gsap.fromTo(rect, { rotateY: -16 * dir, scale: 0.96 },
        { rotateY: 0, scale: 1, duration: 0.9, ease: "power3.out", overwrite: "auto" });
    }
  }

  if ("IntersectionObserver" in window && steps.length) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) show(+e.target.dataset.step);
      });
    }, { rootMargin: "-45% 0px -45% 0px" });
    steps.forEach(function (s) { io.observe(s); });
  }

  // ---------- Problems: strike each pain as it scrolls past ----------
  var pains = document.querySelectorAll(".pain");
  if ("IntersectionObserver" in window) {
    var pio = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) { e.target.classList.add("lit"); pio.unobserve(e.target); }
      });
    }, { rootMargin: "0px 0px -35% 0px" });
    pains.forEach(function (p) { pio.observe(p); });
  } else {
    pains.forEach(function (p) { p.classList.add("lit"); });
  }

  // ---------- Console: paste fails, then the board types ----------
  // Plays once when the console scrolls into view; the little board's Paste replays it.
  var typed = document.getElementById("typed");
  var toast = document.getElementById("toast");
  var con = document.getElementById("console");
  var replay = document.getElementById("replay");
  var LINE = "export API_TOKEN=tdk_9f2c41e07ab3";
  var timers = [];
  function later(fn, ms) { timers.push(setTimeout(fn, ms)); }
  function runConsole() {
    timers.forEach(clearTimeout); timers = [];
    typed.textContent = "";
    toast.classList.remove("show");
    if (reduce) { toast.classList.add("show"); typed.textContent = LINE; return; }
    later(function () { toast.classList.add("show"); }, 250);
    var i = 0;
    later(function step() {
      typed.textContent = LINE.slice(0, ++i);
      if (i < LINE.length) later(step, 38 + Math.random() * 40);
    }, 1400);
  }
  if (con && typed) {
    var played = false;
    if ("IntersectionObserver" in window) {
      var cio = new IntersectionObserver(function (entries) {
        if (entries[0].isIntersecting && !played) { played = true; cio.disconnect(); runConsole(); }
      }, { threshold: 0.6 });
      cio.observe(con);
    } else {
      played = true; runConsole();
    }
    if (replay) replay.addEventListener("click", function () { played = true; runConsole(); });
  }

  // ---------- Scroll-driven motion (GSAP) ----------
  if (!hasGsap) {
    var wl = document.getElementById("cable-glow");
    if (wl) wl.setAttribute("stroke-dasharray", "1000 0");
    return;
  }
  gsap.registerPlugin(ScrollTrigger);
  var mm = gsap.matchMedia();

  mm.add({ motion: "(prefers-reduced-motion: no-preference)", wide: "(min-width: 901px)" }, function (ctx) {
    var wide = ctx.conditions.wide;

    // Hero: the two boards drift apart and settle as you scroll away (parallax).
    gsap.from(".hero-stage .board.rect", { y: 60, rotateY: -32, opacity: 0, duration: 1.4, ease: "power3.out", delay: 0.1 });
    gsap.from(".hero-stage .board.round", { y: 90, rotateY: -40, opacity: 0, duration: 1.5, ease: "power3.out", delay: 0.3 });
    gsap.from(".hero-copy > *", { y: 24, opacity: 0, duration: 0.9, ease: "power3.out", stagger: 0.08 });
    document.querySelectorAll(".hero-stage [data-parallax]").forEach(function (el) {
      gsap.to(el, {
        yPercent: +el.dataset.parallax * (wide ? 1 : 0.6), ease: "none",
        scrollTrigger: { trigger: ".hero", start: "top top", end: "bottom top", scrub: true }
      });
    });
    gsap.to(".glow", { yPercent: 30, scale: 1.2, ease: "none",
      scrollTrigger: { trigger: ".hero", start: "top top", end: "bottom top", scrub: true } });

    // How it works: the cable lights up, then the three roles appear.
    var glow = document.getElementById("cable-glow");
    if (glow) {
      var len = glow.getTotalLength();
      gsap.set(glow, { attr: { "stroke-dasharray": len + " " + len, "stroke-dashoffset": len } });
      var tl = gsap.timeline({ scrollTrigger: { trigger: "#wire", start: "top 80%", end: "bottom 45%", scrub: 0.6 } });
      tl.to(glow, { attr: { "stroke-dashoffset": 0 }, ease: "none", duration: 1 })
        .from("#wire .wl", { opacity: 0, x: -14, stagger: 0.25, duration: 0.4 }, ">-0.1");
    }

    // Screens: the stage tilts through the section and zooms in for the jiggler.
    gsap.fromTo(".stage-tilt", { rotateY: wide ? 14 : 6, rotateX: 6 }, {
      rotateY: wide ? -10 : -4, rotateX: -2, ease: "none",
      scrollTrigger: { trigger: ".scrolly", start: "top bottom", end: "bottom top", scrub: 0.8 }
    });
    gsap.fromTo(".stage-tilt", { scale: 0.96 }, {
      scale: wide ? 1.04 : 1.0, ease: "none",
      scrollTrigger: { trigger: '.step[data-step="2"]', start: "top bottom", end: "center center", scrub: 0.8 }
    });
    gsap.fromTo("#stage-round", { y: 16, rotateZ: 8 }, {
      y: -16, rotateZ: -6, ease: "none",
      scrollTrigger: { trigger: ".scrolly", start: "top bottom", end: "bottom top", scrub: 1 }
    });

    // Boards: each card's device turns to face you as it scrolls in.
    gsap.utils.toArray(".board-card .board").forEach(function (b, k) {
      gsap.fromTo(b, { rotateX: 24, rotateY: k % 2 ? 18 : -18, y: 30 }, {
        rotateX: 0, rotateY: 0, y: 0, ease: "none",
        scrollTrigger: { trigger: b, start: "top 95%", end: "top 45%", scrub: 0.6 }
      });
    });

    // Apps: the screenshot slides up into its card.
    gsap.utils.toArray(".app-fig img").forEach(function (img) {
      gsap.from(img, { y: 40, ease: "none",
        scrollTrigger: { trigger: img, start: "top bottom", end: "top 55%", scrub: 0.6 } });
    });
  });

  mm.add("(prefers-reduced-motion: reduce)", function () {
    var g = document.getElementById("cable-glow");
    if (g) g.setAttribute("stroke-dasharray", "1000 0");
  });

  // Images load late (lazy) on the stage; recompute trigger positions once they have.
  window.addEventListener("load", function () { ScrollTrigger.refresh(); });
})();
