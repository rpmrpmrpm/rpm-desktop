#!/usr/bin/env python3
"""Serve the room lab, and tell the page when a file it is built from changed.

    python3 tools/roomlab/serve.py        # -> http://localhost:8777

The page polls /mtime. An edit to index.html reloads it; an edit to room.frag
recompiles the shader in place, so the tuning on the panel survives.

The room's light is the lab's own text (see room.frag), so there is nothing to
extract from Core and nothing here tracks it.
"""
import base64
import functools
import http.server
import json
import pathlib
import socketserver
import sys
import urllib.parse

HERE = pathlib.Path(__file__).parent
# The covers the frames are filled with. They are read from the landing page, not
# copied, so that there is one set of pictures. If they are missing, the page draws
# its own tile instead.
COVERS = HERE.parents[1] / "site/covers"
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8777
# Where a posted canvas lands. These are generated pictures, not sources.
SHOTS = HERE / "shots"


def mtime(path):
    try:
        return path.stat().st_mtime
    except OSError:
        return 0.0


class Handler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        if self.path.startswith("/covers/"):
            self.cover(self.path.split("?")[0][len("/covers/"):])
            return
        if self.path.split("?")[0] == "/inline":
            self.inline()
            return
        if self.path.split("?")[0] == "/mtime":
            body = json.dumps({
                "html": mtime(HERE / "index.html"),
                "frag": mtime(HERE / "room.frag"),
            }).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)
            return
        super().do_GET()

    def do_POST(self):
        """The page handing back its own canvas as a PNG. See shotMode in index.html."""
        if self.path.split("?")[0] != "/shot":
            self.send_error(404)
            return
        query = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
        name = pathlib.Path(query.get("name", ["shot.png"])[0]).name
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        head, _, data = body.partition(b",")
        # A data URL for the picture, or the harness's own "text," for the note that
        # goes with it.
        if b"base64" not in head and head != b"text":
            self.send_error(400, "not a base64 data URL")
            return
        out = SHOTS / name
        SHOTS.mkdir(parents=True, exist_ok=True)
        out.write_bytes(base64.b64decode(data))
        print(f"shot {out} {out.stat().st_size} bytes")
        self.send_response(204)
        self.end_headers()

    def cover(self, name):
        """One of the landing page's covers, for a frame to hold."""
        path = COVERS / pathlib.Path(name).name
        if not path.is_file():
            self.send_error(404)
            return
        body = path.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", "image/jpeg")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def inline(self):
        """index.html with the two shader files already in it.

        The page normally fetches them, which happens after the load event and so
        after a headless screenshot is taken: the shot catches a blank canvas every
        time. Served this way the first frame is drawn during load, and `firefox
        --headless --screenshot` shows what the room actually looks like.
        """
        page = (HERE / "index.html").read_text(encoding="utf-8")
        # No newline after the tag: a shader's `#version` must be its first line, and
        # the tag's own text would otherwise push it to the second.
        block = ('<script id="fragSrc" type="x-shader">'
                 + (HERE / "room.frag").read_text(encoding="utf-8") + "</script>\n")
        body = page.replace("<!--INLINE-->", block).encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)


    def end_headers(self):
        self.send_header("Cache-Control", "no-store, max-age=0")
        super().end_headers()

    def log_message(self, fmt, *args):
        pass


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main():
    handler = functools.partial(Handler, directory=str(HERE))
    with Server(("127.0.0.1", PORT), handler) as httpd:
        print(f"room lab on http://localhost:{PORT}  (ctrl-c to stop)")
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print()


if __name__ == "__main__":
    main()
