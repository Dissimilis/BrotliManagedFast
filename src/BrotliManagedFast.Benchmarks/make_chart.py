"""Generate matching SVG charts, Markdown tables and CSV data from a BenchmarkDotNet report.

No dependencies. SVGs remain readable in GitHub's light and dark themes.
Usage: python make_chart.py <report.md or .csv> <out.svg> [--encode] [--table out.md]
       [--data out.csv] [--context "machine, runtime, date"]

<report.md> is the per-class GitHub report BenchmarkDotNet writes under
BenchmarkDotNet.Artifacts/results/ (BrotliManagedFast.Benchmarks.DecodeBenchmarks-report-github.md
or ...EncodeBenchmarks-report-github.md). Throughput is uncompressed bytes per second, so decode
and encode rows are comparable per file.
"""
import argparse
import csv
from html import escape
import math
from pathlib import Path
import re

# Description match, public label, colour. The "this library" line is drawn thicker.
SERIES = [
    ("Native", "Native (System.IO.Compression)", "#707070"),
    ("BrotliSharpLib", "BrotliSharpLib 0.3.3", "#bc620c"),
    ("BrotliManagedFast", "BrotliManagedFast (this library)", "#24783c"),
]
FILES = ["alice29.txt", "lcet10.txt", "plrabn12.txt", "mapsdatazrh", "json-1mb"]
UNITS = {"ns": 1.0, "us": 1e3, "ms": 1e6, "s": 1e9}
ROOT = Path(__file__).resolve().parents[2]


def corpus_size(name):
    if name == "json-1mb":
        return 1 << 20
    return (ROOT / "src" / "BrotliManagedFast.Tests" / "TestData" / name).stat().st_size


def duration(value):
    value = value.replace("μ", "u").replace("µ", "u").replace("*", "")
    match = re.fullmatch(r"([\d.,]+)\s*(ns|us|ms|s)", value.strip())
    if not match:
        raise ValueError(f"Invalid BDN duration: {value!r}")
    return float(match[1].replace(",", "")) * UNITS[match[2]]


def parse(path):
    """Read published CSV data or a BenchmarkDotNet GitHub report table. Returns (method, file, quality, mean_ns, error_ns)."""
    if Path(path).suffix.lower() == ".csv":
        with open(path, encoding="utf-8-sig", newline="") as source:
            return [(r["method"], r["file"], int(r["quality"]), float(r["mean_ns"]), float(r["error_ns"]))
                    for r in csv.DictReader(source)]
    rows = []
    header = None
    for line in Path(path).read_text(encoding="utf-8-sig").splitlines():
        if not line.startswith("|"):
            continue
        cells = [c.strip().strip("*").strip() for c in line.strip().strip("|").split("|")]
        if header is None:
            header = cells
            continue
        if set("".join(cells)) <= set("-: "):
            continue
        row = dict(zip(header, cells))
        try:
            rows.append((row["Method"].strip("'"), row["File"], int(row["Quality"]), duration(row["Mean"]), duration(row["Error"])))
        except (KeyError, ValueError):
            continue
    return rows


def select(rows, file):
    data = {}
    for key, label, colour in SERIES:
        points = sorted((q, mean, error) for method, f, q, mean, error in rows if key in method and f == file)
        if not points:
            raise ValueError(f"No measurements for {label} on {file}")
        data[label] = (points, colour)
    qualities = [q for q, _, _ in next(iter(data.values()))[0]]
    if any([q for q, _, _ in points] != qualities for points, _ in data.values()):
        raise ValueError("All series must cover the same qualities")
    return data, qualities


def table(rows):
    """One Markdown table per quality: mean time per implementation and throughput of this library."""
    out = []
    methods = [(key, label) for key, label, _ in SERIES]
    for quality in sorted({q for _, _, q, _, _ in rows}):
        out.append(f"Quality {quality}, mean time per operation (lower is better) and this library's throughput:\n")
        out.append("| File | " + " | ".join(label for _, label in methods) + " | Throughput |")
        out.append("|---|" + "---:|" * (len(methods) + 1))
        for file in FILES:
            cells = [file]
            ours = None
            for key, _ in methods:
                match = [(m, e) for method, f, q, m, e in rows if key in method and f == file and q == quality]
                if not match:
                    cells.append("not run")
                    continue
                mean, error = match[0]
                unit = "ms" if mean >= 1e6 else "us"
                cell = f"{mean / UNITS[unit]:.2f} ± {error / UNITS[unit]:.2f} {unit}"
                if key == "BrotliManagedFast":
                    cell = f"**{cell}**"
                    ours = mean
                cells.append(cell)
            cells.append(f"{corpus_size(file) / ours * 1e3:.0f} MB/s" if ours else "")
            out.append("| " + " | ".join(cells) + " |")
        out.append("")
    return "\n".join(out) + "\n"


def chart(rows, encode, context):
    width, height = 1200, 630
    left, right, top, bottom = 80, 330, 80, 70
    pw, ph = width - left - right, height - top - bottom
    files = [f for f in FILES if any(r[1] == f for r in rows)]
    qualities = sorted({q for _, _, q, _, _ in rows})
    # One panel per file, side by side; x = quality (categorical), y = MB/s (log).
    panel_w = pw / len(files)
    values = [corpus_size(f) / mean * 1e3 for _, f, _, mean, _ in rows]
    y_lo, y_hi = math.log10(min(values) * .7), math.log10(max(values) * 1.4)

    def y(v):
        return top + ph - (math.log10(v) - y_lo) / (y_hi - y_lo) * ph

    op = "encode" if encode else "decode"
    title = f"Brotli {op} throughput on .NET, higher is better"
    detail = ("One-shot compression, window 22, uncompressed bytes per second." if encode
              else "One-shot decompression of the native encoder's output, uncompressed bytes per second.")
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" '
           'role="img" aria-labelledby="title desc" font-family="-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif">',
           f'<title id="title">{escape(title)}</title>',
           f'<desc id="desc">{escape(detail)} Log scale. Points show measured means; the accompanying table gives confidence margins.</desc>',
           f'<rect width="{width}" height="{height}" fill="white"/>',
           f'<text x="{left}" y="28" font-size="20" font-weight="600" fill="#202020">{escape(title)}</text>',
           f'<text x="{left}" y="49" font-size="12" fill="#555">{escape(context)}</text>',
           f'<text x="{left}" y="67" font-size="11" fill="#555">{escape(detail)}</text>']
    for decade in range(math.floor(y_lo), math.ceil(y_hi) + 1):
        for mantissa in (1, 2, 5):
            tick = mantissa * 10 ** decade
            if y_lo <= math.log10(tick) <= y_hi:
                yy = y(tick)
                out.extend([f'<line x1="{left}" y1="{yy:.1f}" x2="{left + pw}" y2="{yy:.1f}" stroke="#e4e7e5"/>',
                            f'<text x="{left - 10}" y="{yy + 4:.1f}" text-anchor="end" font-size="11" fill="#555">{tick:g}</text>'])
    out.append(f'<text x="20" y="{top + ph / 2}" transform="rotate(-90 20 {top + ph / 2})" text-anchor="middle" font-size="12" fill="#444">Throughput (MB/s, log scale)</text>')
    for i, file in enumerate(files):
        px = left + i * panel_w
        if i > 0:
            out.append(f'<line x1="{px:.1f}" y1="{top}" x2="{px:.1f}" y2="{top + ph}" stroke="#d0d4d2" stroke-dasharray="3 3"/>')
        out.append(f'<text x="{px + panel_w / 2:.1f}" y="{top + ph + 40}" text-anchor="middle" font-size="11.5" fill="#333">{escape(file)}</text>')

        def x(q):
            return px + panel_w * (0.18 + 0.64 * qualities.index(q) / max(1, len(qualities) - 1))

        for q in qualities:
            out.extend([f'<line x1="{x(q):.1f}" y1="{top + ph}" x2="{x(q):.1f}" y2="{top + ph + 5}" stroke="#999"/>',
                        f'<text x="{x(q):.1f}" y="{top + ph + 21}" text-anchor="middle" font-size="10.5" fill="#555">q{q}</text>'])
        for key, label, colour in SERIES:
            points = sorted((q, mean, error) for method, f, q, mean, error in rows if key in method and f == file)
            if not points:
                continue
            weight = 3 if "this library" in label else 2
            path = " ".join(("M" if j == 0 else "L") + f"{x(q):.1f},{y(corpus_size(file) / mean * 1e3):.1f}" for j, (q, mean, _) in enumerate(points))
            out.append(f'<path d="{path}" fill="none" stroke="{colour}" stroke-width="{weight}" stroke-linejoin="round"/>')
            for q, mean, error in points:
                v = corpus_size(file) / mean * 1e3
                tooltip = f"{label}, {file} q{q}: {v:.0f} MB/s ({mean / 1e6:.3g} ± {error / 1e6:.2g} ms)"
                out.append(f'<circle cx="{x(q):.1f}" cy="{y(v):.1f}" r="3.5" fill="{colour}"><title>{escape(tooltip)}</title></circle>')
    out.append(f'<line x1="{left}" y1="{top + ph}" x2="{left + pw}" y2="{top + ph}" stroke="#999"/>')
    ly = top + 16
    for key, label, colour in SERIES:
        weight = 3 if "this library" in label else 2
        out.extend([f'<line x1="{left + pw + 25}" y1="{ly - 4}" x2="{left + pw + 49}" y2="{ly - 4}" stroke="{colour}" stroke-width="{weight}"/>',
                    f'<text x="{left + pw + 59}" y="{ly}" font-size="12" fill="#333">{escape(label)}</text>'])
        ly += 30
    notes = ["All implementations: one thread.", "Same input bytes for every", "implementation in a panel.", "",
             "Means shown; see table for margins.", "Compare within this run only."]
    for note in notes:
        out.append(f'<text x="{left + pw + 25}" y="{ly + 25}" font-size="11" fill="#666">{escape(note)}</text>')
        ly += 18
    out.append("</svg>")
    return "\n".join(out) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input")
    parser.add_argument("output")
    parser.add_argument("--encode", action="store_true")
    parser.add_argument("--table")
    parser.add_argument("--data")
    parser.add_argument("--context", default="See README for hardware, runtime and measurement details.")
    args = parser.parse_args()
    rows = parse(args.input)
    if not rows:
        raise SystemExit("no benchmark rows found")
    Path(args.output).write_text(chart(rows, args.encode, args.context), encoding="utf-8")
    if args.table:
        Path(args.table).write_text(table(rows), encoding="utf-8")
    if args.data:
        with open(args.data, "w", encoding="utf-8", newline="") as target:
            writer = csv.writer(target)
            writer.writerow(["method", "file", "quality", "mean_ns", "error_ns"])
            writer.writerows(rows)
    print(f"Wrote {args.output}: {len(rows)} rows")


if __name__ == "__main__":
    main()
