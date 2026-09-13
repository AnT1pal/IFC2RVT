"""
Generates tools/Installer/app.ico from scratch.

Pure standard library on purpose: the icon is part of the build, and requiring Pillow to produce
a few hundred pixels would put a Python package between a clone of this repo and a release build.

The mark is the product in one glyph: a hollow outline on the left (DirectShape - geometry with
nothing behind it) becoming a filled solid on the right (a native element), with an arrow between.
Readable down to 16 px, which is the only size that really has to work.
"""

import struct
import zlib

BACKGROUND = (30, 36, 48, 255)      # dark slate
OUTLINE = (232, 236, 244, 255)      # near-white
ACCENT = (232, 163, 61, 255)        # amber
SIZES = (256, 128, 64, 48, 32, 16)


def blend(dst, src):
    """Alpha-composite src over dst."""
    a = src[3] / 255.0
    if a >= 1.0:
        return src
    if a <= 0.0:
        return dst
    return tuple(int(round(src[i] * a + dst[i] * (1 - a))) for i in range(3)) + (255,)


class Canvas:
    def __init__(self, size, fill):
        self.n = size
        self.px = [[fill for _ in range(size)] for _ in range(size)]

    def set(self, x, y, colour):
        if 0 <= x < self.n and 0 <= y < self.n:
            self.px[y][x] = blend(self.px[y][x], colour)

    def rect(self, x0, y0, x1, y1, colour):
        for y in range(int(y0), int(y1)):
            for x in range(int(x0), int(x1)):
                self.set(x, y, colour)

    def frame(self, x0, y0, x1, y1, width, colour):
        w = max(1, int(round(width)))
        self.rect(x0, y0, x1, y0 + w, colour)
        self.rect(x0, y1 - w, x1, y1, colour)
        self.rect(x0, y0, x0 + w, y1, colour)
        self.rect(x1 - w, y0, x1, y1, colour)

    def rounded_background(self, radius, colour):
        """Squircle-ish corners so the tile does not look like a raw bitmap."""
        n, r = self.n, radius
        for y in range(n):
            for x in range(n):
                dx = dy = 0
                if x < r:
                    dx = r - x
                elif x >= n - r:
                    dx = x - (n - r - 1)
                if y < r:
                    dy = r - y
                elif y >= n - r:
                    dy = y - (n - r - 1)
                if dx and dy and (dx * dx + dy * dy) > r * r:
                    self.px[y][x] = (0, 0, 0, 0)
                else:
                    self.px[y][x] = colour

    def triangle_right(self, x0, y_mid, height, length, colour):
        """Solid arrowhead pointing right."""
        for i in range(int(length)):
            half = (1 - i / length) * height / 2
            for y in range(int(round(y_mid - half)), int(round(y_mid + half)) + 1):
                self.set(int(x0 + i), y, colour)

    def to_png(self):
        raw = b""
        for row in self.px:
            raw += b"\x00" + b"".join(bytes(p) for p in row)

        def chunk(tag, data):
            body = tag + data
            return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

        header = struct.pack(">IIBBBBB", self.n, self.n, 8, 6, 0, 0, 0)
        return (b"\x89PNG\r\n\x1a\n"
                + chunk(b"IHDR", header)
                + chunk(b"IDAT", zlib.compress(raw, 9))
                + chunk(b"IEND", b""))


def draw(size):
    c = Canvas(size, (0, 0, 0, 0))
    u = size / 32.0                     # design grid is 32 units
    c.rounded_background(max(2, int(round(6 * u))), BACKGROUND)

    stroke = max(1, round(2 * u))
    top, bottom = 10 * u, 22 * u

    # Left: hollow box - imported geometry with nothing behind it.
    c.frame(4 * u, top, 13 * u, bottom, stroke, OUTLINE)

    # Middle: the conversion.
    bar_y = (top + bottom) / 2
    c.rect(14.5 * u, bar_y - stroke / 2, 17.5 * u, bar_y + stroke / 2, ACCENT)
    c.triangle_right(17.5 * u, bar_y, 6 * u, 3 * u, ACCENT)

    # Right: filled box - a native element.
    c.rect(21 * u, top, 28 * u, bottom, ACCENT)
    return c


def main():
    images = [(s, draw(s).to_png()) for s in SIZES]

    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""

    for size, png in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        blobs += png
        offset += len(png)

    with open("app.ico", "wb") as f:
        f.write(out + entries + blobs)

    print(f"app.ico: {len(images)} размеров, {(len(out) + len(entries) + len(blobs)) / 1024:.1f} КБ")


if __name__ == "__main__":
    main()
