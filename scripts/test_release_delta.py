#!/usr/bin/env python3
"""Tests for release_delta.py. Run: python3 -I -m unittest discover -s scripts -p 'test_release_delta.py'"""

import contextlib
import functools
import http.server
import io
import json
import os
import pathlib
import sys
import tempfile
import threading
import unittest
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import release_delta  # noqa: E402

CHANNEL = "osx-arm64"


def make_package(path, version, files):
    """A package as release-mac.sh lays one out: a nuspec at the root and the app under lib/app."""
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("com.rpm.desktop.nuspec",
                   '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">'
                   f"<metadata><id>com.rpm.desktop</id><version>{version}</version></metadata></package>")
        for name, data in files.items():
            z.writestr(name, data)
    return str(path)


def run(*argv):
    """Runs a command; returns (exit code, stdout, stderr)."""
    out, err = io.StringIO(), io.StringIO()
    code = 0
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        try:
            release_delta.main(list(argv))
        except SystemExit as e:
            code = e.code or 0
    return code, out.getvalue(), err.getvalue()


class Quiet(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


class Versions(unittest.TestCase):
    def test_numbers_compare_as_numbers(self):
        self.assertLess(release_delta.version_key("0.1.9"), release_delta.version_key("0.1.10"))

    def test_a_pre_release_comes_before_its_release(self):
        self.assertLess(release_delta.version_key("0.2.0-beta.1"), release_delta.version_key("0.2.0"))
        self.assertLess(release_delta.version_key("0.2.0-beta.2"), release_delta.version_key("0.2.0-beta.10"))

    def test_build_metadata_does_not_order(self):
        self.assertEqual(release_delta.version_key("0.1.9+abc"), release_delta.version_key("0.1.9"))


class Commands(unittest.TestCase):
    def setUp(self):
        self.dir = pathlib.Path(tempfile.mkdtemp(prefix="rpm-release-delta-"))
        self.host = self.dir / "host" / CHANNEL
        self.host.mkdir(parents=True)
        # Served over HTTP, as the update host is: a missing feed is a 404, not a missing file.
        handler = functools.partial(Quiet, directory=str(self.dir / "host"))
        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.addCleanup(self.server.server_close)
        self.addCleanup(self.server.shutdown)

    def publish(self, assets):
        (self.host / f"releases.{CHANNEL}.json").write_text(json.dumps({"Assets": assets}))

    def feed_url(self):
        return f"http://127.0.0.1:{self.server.server_address[1]}/{CHANNEL}/"

    def published_full(self, version, files):
        name = f"com.rpm.desktop-{version}-{CHANNEL}-full.nupkg"
        path = make_package(self.host / name, version, files)
        return release_delta.asset_for(path, "Full")

    def test_previous_downloads_the_newest_full_package(self):
        old = self.published_full("0.1.8", {"lib/app/a": b"8"})
        new = self.published_full("0.1.9", {"lib/app/a": b"9"})
        self.publish([old, new])
        out = self.dir / "previous"

        code, stdout, _ = run("previous", "--feed", self.feed_url(), "--channel", CHANNEL,
                              "--version", "0.1.10", "--out", str(out))

        self.assertEqual(code, 0)
        self.assertEqual(stdout.strip(), str(out / new["FileName"]))
        self.assertEqual(release_delta.file_hashes(stdout.strip())[1], new["SHA256"])
        kept = json.loads((out / f"releases.{CHANNEL}.json").read_text())["Assets"]
        self.assertEqual([a["Version"] for a in kept], ["0.1.8", "0.1.9"])

    def test_previous_refuses_a_version_the_feed_already_has(self):
        self.publish([self.published_full("0.1.9", {"lib/app/a": b"9"})])

        code, _, err = run("previous", "--feed", self.feed_url(), "--channel", CHANNEL,
                           "--version", "0.1.9", "--out", str(self.dir / "previous"))

        self.assertEqual(code, 1)
        self.assertIn("not newer than 0.1.9", err)

    def test_previous_refuses_a_package_that_is_not_the_one_the_feed_names(self):
        asset = self.published_full("0.1.9", {"lib/app/a": b"9"})
        asset["SHA256"] = "0" * 64
        self.publish([asset])
        out = self.dir / "previous"

        code, _, err = run("previous", "--feed", self.feed_url(), "--channel", CHANNEL,
                           "--version", "0.1.10", "--out", str(out))

        self.assertEqual(code, 1)
        self.assertIn("is not the package the feed names", err)
        self.assertFalse((out / asset["FileName"]).exists())

    def test_previous_without_a_feed_stops_unless_this_is_the_first_release(self):
        code, _, err = run("previous", "--feed", self.feed_url(), "--channel", CHANNEL,
                           "--version", "0.1.10", "--out", str(self.dir / "previous"))
        self.assertEqual(code, 1)
        self.assertIn("--first-release", err)

        code, stdout, _ = run("previous", "--feed", self.feed_url(), "--channel", CHANNEL,
                              "--version", "0.1.10", "--out", str(self.dir / "previous"), "--first-release")
        self.assertEqual(code, 0)
        self.assertEqual(stdout, "")

    def test_previous_without_a_delta_keeps_the_feed_and_fetches_nothing(self):
        old = self.published_full("0.1.9", {"lib/app/a": b"9"})
        self.publish([old])
        out = self.dir / "previous"

        code, stdout, _ = run("previous", "--feed", self.feed_url(), "--channel", CHANNEL,
                              "--version", "0.1.10", "--out", str(out), "--no-delta")

        self.assertEqual(code, 0)
        self.assertEqual(stdout, "")
        self.assertFalse((out / old["FileName"]).exists())
        kept = json.loads((out / f"releases.{CHANNEL}.json").read_text())["Assets"]
        self.assertEqual([a["Version"] for a in kept], ["0.1.9"])

    def test_previous_without_a_delta_still_refuses_a_version_the_feed_already_has(self):
        self.publish([self.published_full("0.1.9", {"lib/app/a": b"9"})])

        code, _, err = run("previous", "--feed", self.feed_url(), "--channel", CHANNEL,
                           "--version", "0.1.9", "--out", str(self.dir / "previous"), "--no-delta")

        self.assertEqual(code, 1)
        self.assertIn("not newer than 0.1.9", err)

    def test_same_files_passes_for_the_same_content_compressed_differently(self):
        files = {"lib/app/Contents/MacOS/rpm": b"binary" * 1000, "lib/app/Contents/Info.plist": b"plist"}
        a = make_package(self.dir / "a.nupkg", "0.1.10", files)
        b = self.dir / "b.nupkg"
        with zipfile.ZipFile(b, "w", zipfile.ZIP_STORED) as z:
            with zipfile.ZipFile(a) as src:
                for name in reversed(src.namelist()):
                    z.writestr(name, src.read(name))

        code, stdout, _ = run("same-files", a, str(b))

        self.assertEqual(code, 0)
        self.assertIn("same files: 3", stdout)

    def test_same_files_names_a_file_that_differs_or_is_missing(self):
        a = make_package(self.dir / "a.nupkg", "0.1.10", {"lib/app/x": b"new", "lib/app/y": b"y"})
        b = make_package(self.dir / "b.nupkg", "0.1.10", {"lib/app/x": b"old"})

        code, _, err = run("same-files", a, b)

        self.assertEqual(code, 1)
        self.assertIn("lib/app/x", err)
        self.assertIn("lib/app/y", err)

    def test_feed_keeps_every_earlier_asset_and_adds_the_full_and_its_delta(self):
        old_full = self.published_full("0.1.9", {"lib/app/a": b"9"})
        old_delta = dict(old_full, Type="Delta", FileName=f"com.rpm.desktop-0.1.9-{CHANNEL}-delta.nupkg")
        previous = self.dir / "previous.json"
        previous.write_text(json.dumps({"Assets": [old_full, old_delta]}))
        full = make_package(self.dir / f"com.rpm.desktop-0.1.10-{CHANNEL}-full.nupkg", "0.1.10", {"lib/app/a": b"10"})
        delta = make_package(self.dir / f"com.rpm.desktop-0.1.10-{CHANNEL}-delta.nupkg", "0.1.10", {"lib/app/a.zsdiff": b"d"})
        out = self.dir / "out"
        out.mkdir()

        code, _, err = run("feed", "--previous", str(previous), "--channel", CHANNEL,
                           "--full", full, "--delta", delta, "--out", str(out))

        self.assertEqual(code, 0, err)
        assets = json.loads((out / f"releases.{CHANNEL}.json").read_text())["Assets"]
        self.assertEqual([(a["Version"], a["Type"]) for a in assets],
                         [("0.1.10", "Full"), ("0.1.10", "Delta"), ("0.1.9", "Full"), ("0.1.9", "Delta")])
        self.assertIn(old_full, assets)
        self.assertIn(old_delta, assets)
        new_delta = next(a for a in assets if a["Version"] == "0.1.10" and a["Type"] == "Delta")
        self.assertEqual(new_delta["SHA256"], release_delta.file_hashes(delta)[1])
        self.assertEqual(new_delta["Size"], os.path.getsize(delta))
        releases = (out / f"RELEASES-{CHANNEL}").read_text().splitlines()
        self.assertEqual([line.split()[1] for line in releases], [os.path.basename(full), old_full["FileName"]])

    def test_feed_refuses_a_delta_for_another_version(self):
        previous = self.dir / "previous.json"
        previous.write_text(json.dumps({"Assets": []}))
        full = make_package(self.dir / "full.nupkg", "0.1.10", {"lib/app/a": b"10"})
        delta = make_package(self.dir / "delta.nupkg", "0.1.9", {"lib/app/a.zsdiff": b"d"})

        code, _, err = run("feed", "--previous", str(previous), "--channel", CHANNEL,
                           "--full", full, "--delta", delta, "--out", str(self.dir))

        self.assertEqual(code, 1)
        self.assertIn("not com.rpm.desktop 0.1.10", err)

    def test_feed_without_a_delta_after_an_earlier_full_must_say_so(self):
        previous = self.dir / "previous.json"
        previous.write_text(json.dumps({"Assets": [self.published_full("0.1.9", {"lib/app/a": b"9"})]}))
        full = make_package(self.dir / "full.nupkg", "0.1.10", {"lib/app/a": b"10"})

        code, _, err = run("feed", "--previous", str(previous), "--channel", CHANNEL,
                           "--full", full, "--no-delta", "--out", str(self.dir))
        self.assertEqual(code, 0, err)

        code, _, err = run("feed", "--previous", str(previous), "--channel", CHANNEL, "--full", full, "--out", str(self.dir))
        self.assertEqual(code, 2)


if __name__ == "__main__":
    unittest.main()
