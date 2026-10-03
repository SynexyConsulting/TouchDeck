"""Writes docs/license.html (for the website) from docs/license.md, so the two never drift apart.

    python tools/make_license_html.py

Handles the Markdown docs/license.md uses: headings, paragraphs, lists, tables, block quotes,
rules, **bold**, ***bold italic***, `code` and links. No dependencies.
"""
import html
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def slug(text):
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")


def inline(text):
    t = html.escape(text, quote=False)
    t = re.sub(r"`([^`]+)`", r"<code>\1</code>", t)
    t = re.sub(r"\*\*\*(.+?)\*\*\*", r"<strong><em>\1</em></strong>", t)
    t = re.sub(r"\*\*(.+?)\*\*", r"<strong>\1</strong>", t)
    t = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", r'<a href="\2">\1</a>', t)
    t = re.sub(r"&lt;(https?://[^&]+)&gt;", r'<a href="\1">\1</a>', t)
    t = re.sub(r"(?<![\"'>=])(https?://[^\s<)]*[^\s<).,;:])", r'<a href="\1">\1</a>', t)
    return t


def convert(md):
    out, para, items, rows = [], [], [], []
    list_tag = None

    def flush():
        nonlocal list_tag
        if para:
            out.append(f"<p>{inline(' '.join(para))}</p>")
            para.clear()
        if items:
            out.append(f"<{list_tag}>" + "".join(f"<li>{inline(i)}</li>" for i in items) + f"</{list_tag}>")
            items.clear()
            list_tag = None
        if rows:
            head, body = rows[0], rows[2:]
            out.append('<div class="table"><table><thead><tr>' + "".join(f"<th>{inline(c)}</th>" for c in head)
                       + "</tr></thead><tbody>" + "".join("<tr>" + "".join(f"<td>{inline(c)}</td>" for c in r) + "</tr>" for r in body)
                       + "</tbody></table></div>")
            rows.clear()

    for line in md.splitlines():
        s = line.rstrip()
        if not s:
            flush()
        elif m := re.match(r"^(#{1,3}) (.+)$", s):
            flush()
            level, text = len(m.group(1)), m.group(2)
            out.append(f'<h{level} id="{slug(text)}">{inline(text)}</h{level}>')
        elif s == "---":
            flush()
            out.append("<hr>")
        elif s.startswith("> "):
            flush()
            out.append(f"<blockquote>{inline(s[2:])}</blockquote>")
        elif s.startswith("|"):
            if para or items:
                flush()
            rows.append([c.strip() for c in s.strip("|").split("|")])
        elif m := re.match(r"^(- |\d+\. )(.+)$", s):
            if para or rows:
                flush()
            list_tag = list_tag or ("ol" if m.group(1)[0].isdigit() else "ul")
            items.append(m.group(2))
        else:
            if items or rows:
                flush()
            para.append(s)
    flush()
    return "\n".join(out)


PAGE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Licence · Touch Deck</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link href="https://fonts.googleapis.com/css2?family=Barlow:wght@500;600&family=JetBrains+Mono&display=swap" rel="stylesheet">
<style>
  :root {{
    --bg: #f6f7f9; --surf: #ffffff; --inner: #eef1f5; --text: #10151e; --dim: #4a5366;
    --line: #d9dee6; --accent: #b86e0c;
    color-scheme: light;
  }}
  @media (prefers-color-scheme: dark) {{
    :root {{ --bg: #07090d; --surf: #141a24; --inner: #10151e; --text: #e8ecf2; --dim: #8a94a6;
             --line: #1c2432; --accent: #f2a33a; color-scheme: dark; }}
  }}
  * {{ box-sizing: border-box; }}
  body {{ margin: 0; background: var(--bg); color: var(--text);
         font: 500 16px/1.6 Barlow, "Segoe UI", -apple-system, system-ui, sans-serif; }}
  main {{ max-width: 860px; margin: 0 auto; padding: 40px 20px 64px; }}
  h1 {{ font-weight: 600; font-size: 2rem; margin: 0 0 8px; }}
  h2 {{ font-weight: 600; font-size: 1.3rem; margin: 2.2em 0 .5em; }}
  h3 {{ font-weight: 600; font-size: 1.05rem; margin: 1.6em 0 .4em; }}
  a {{ color: var(--accent); }}
  code, blockquote {{ font-family: "JetBrains Mono", Menlo, Consolas, monospace; font-size: .88em; }}
  code {{ background: var(--inner); border-radius: 6px; padding: 1px 5px; }}
  blockquote {{ margin: 1em 0; padding: 10px 14px; background: var(--inner); border-left: 3px solid var(--accent);
               border-radius: 6px; overflow-wrap: anywhere; }}
  hr {{ border: 0; border-top: 1px solid var(--line); margin: 2.5em 0; }}
  .table {{ overflow-x: auto; margin: 1em 0; }}
  table {{ border-collapse: collapse; width: 100%; font-size: .92em; }}
  th, td {{ text-align: left; vertical-align: top; padding: 8px 10px; border-bottom: 1px solid var(--line); }}
  th {{ color: var(--dim); font-weight: 600; }}
  li {{ margin: .3em 0; }}
  .card {{ background: var(--surf); border-radius: 14px; padding: 8px 24px 20px; }}
  footer {{ color: var(--dim); font-size: .85em; margin-top: 32px; }}
</style>
</head>
<body>
<main>
<div class="card">
{body}
</div>
<footer>Generated from docs/license.md in the Touch Deck repository. The licence itself is the GNU GPL version 3 in the repository\'s LICENSE file (<a href="https://www.gnu.org/licenses/gpl-3.0.html">gnu.org</a>).</footer>
</main>
</body>
</html>
"""

if __name__ == "__main__":
    with open(os.path.join(ROOT, "docs", "license.md"), encoding="utf-8") as f:
        body = convert(f.read())
    os.makedirs(os.path.join(ROOT, "docs"), exist_ok=True)
    path = os.path.join(ROOT, "docs", "license.html")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(PAGE.format(body=body))
    print("wrote", path)
