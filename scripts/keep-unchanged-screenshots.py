#!/usr/bin/env python3
"""Reverts regenerated screenshots whose only difference from the committed version is raster noise (#832).

The capture is deterministic at the app level (seeded clocks and ids, frozen animations, masked secrets,
load-state anchors), but a browser's rasterizer may still blend anti-aliased edges one least-significant
bit differently between runs. A byte-exact commit gate would re-commit such a PNG forever; this reverts a
regenerated file back to the committed one when EVERY pixel differs by at most 1 per channel and the
dimensions match — and keeps the regeneration for anything larger, which is what a real UI change produces.

Usage: keep-unchanged-screenshots.py <dir>   (compares each *.png against `git show HEAD:<path>`)
Prints one line per reverted file; exits 0 always (a defect here must not fail the manual build — the worst
outcome is an unnecessary commit, which is yesterday's status quo, not a broken pipeline).
"""
import subprocess
import sys
import zlib
import struct
from pathlib import Path


def decode(data):
    pos = 8
    idat = b""
    w = h = channels = None
    while pos < len(data):
        ln, typ = struct.unpack(">I4s", data[pos:pos + 8])
        pos += 8
        chunk = data[pos:pos + ln]
        pos += ln + 4
        if typ == b"IHDR":
            w, h, bd, ct = struct.unpack(">IIBB", chunk[:10])
            if bd != 8:
                return None  # unexpected depth — treat as changed
            channels = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ct]
        elif typ == b"IDAT":
            idat += chunk
    raw = zlib.decompress(idat)
    stride = w * channels
    rows = []
    prev = bytearray(stride)
    i = 0
    for _ in range(h):
        f = raw[i]
        i += 1
        line = bytearray(raw[i:i + stride])
        i += stride
        if f == 1:
            for x in range(channels, stride):
                line[x] = (line[x] + line[x - channels]) & 255
        elif f == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 255
        elif f == 3:
            for x in range(stride):
                a = line[x - channels] if x >= channels else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 255
        elif f == 4:
            for x in range(stride):
                a = line[x - channels] if x >= channels else 0
                b = prev[x]
                c = prev[x - channels] if x >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 255
        rows.append(bytes(line))
        prev = line
    return w, h, channels, rows


def only_raster_noise(old, new):
    a = decode(old)
    b = decode(new)
    if a is None or b is None or a[:3] != b[:3]:
        return False
    for ra, rb in zip(a[3], b[3]):
        if ra == rb:
            continue
        for x, (va, vb) in enumerate(zip(ra, rb)):
            if abs(va - vb) > 1:
                return False
    return True


def main():
    directory = Path(sys.argv[1])
    for png in sorted(directory.glob("*.png")):
        rel = png.as_posix()
        head = subprocess.run(["git", "show", f"HEAD:{rel}"], capture_output=True)
        if head.returncode != 0:
            continue  # a brand-new figure — commit it
        new = png.read_bytes()
        if head.stdout == new:
            continue  # byte-identical — nothing to do
        try:
            if only_raster_noise(head.stdout, new):
                png.write_bytes(head.stdout)
                print(f"kept committed {rel} (regeneration differed only by raster noise)")
        except Exception as e:  # noqa: BLE001 — see module docstring
            print(f"warning: could not compare {rel}: {e}")


if __name__ == "__main__":
    main()
