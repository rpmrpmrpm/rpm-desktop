"""Stack the posted shots into one tall contact sheet, so a sweep is one look."""
import struct, zlib, pathlib, sys

def read_png(path):
    d = pathlib.Path(path).read_bytes()
    i, idat, w, h, ct = 8, b"", 0, 0, 6
    while i < len(d):
        ln = struct.unpack(">I", d[i:i+4])[0]; typ = d[i+4:i+8]; data = d[i+8:i+8+ln]
        if typ == b"IHDR": w, h, _, ct = struct.unpack(">IIBB", data[:10])
        elif typ == b"IDAT": idat += data
        i += 12 + ln
    raw = zlib.decompress(idat)
    ch = {0:1, 2:3, 4:2, 6:4}[ct]
    stride, out, prev, pos = w*ch, bytearray(), bytearray(w*ch), 0
    for _ in range(h):
        f = raw[pos]; pos += 1
        line = bytearray(raw[pos:pos+stride]); pos += stride
        if f:
            for x in range(stride):
                a = line[x-ch] if x >= ch else 0
                b = prev[x]; c = prev[x-ch] if x >= ch else 0
                if f == 1: line[x] = (line[x]+a) & 255
                elif f == 2: line[x] = (line[x]+b) & 255
                elif f == 3: line[x] = (line[x]+(a+b)//2) & 255
                else:
                    pp = a+b-c; pa, pb, pc = abs(pp-a), abs(pp-b), abs(pp-c)
                    line[x] = (line[x] + (a if (pa<=pb and pa<=pc) else (b if pb<=pc else c))) & 255
        out += line; prev = line
    return w, h, ch, out

def write_png(path, w, h, rgb):
    raw = b"".join(b"\x00" + bytes(rgb[y*w*3:(y+1)*w*3]) for y in range(h))
    def chunk(t, d):
        c = t + d
        return struct.pack(">I", len(d)) + c + struct.pack(">I", zlib.crc32(c) & 0xffffffff)
    pathlib.Path(path).write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))

names = sys.argv[2:]
tiles = [read_png(pathlib.Path(sys.argv[1]) / n) for n in names]
W = min(t[0] for t in tiles)
GAP = 6
H = sum(t[1] for t in tiles) + GAP * (len(tiles) - 1)
sheet = bytearray(W * H * 3)
y0 = 0
for (w, h, ch, px) in tiles:
    for y in range(h):
        for x in range(W):
            o = (y*w + x) * ch
            d = ((y0 + y) * W + x) * 3
            sheet[d:d+3] = px[o:o+3]
    y0 += h + GAP
write_png(sys.argv[1] + "/sheet.png", W, H, sheet)
print("sheet.png", W, "x", H, "from", ", ".join(names))
