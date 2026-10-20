#!/usr/bin/env python3
"""Tests for release_version.py. Run: python3 -I -m unittest discover -s scripts -p 'test_release_version.py'"""

import contextlib
import io
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import release_version  # noqa: E402
from release_version import NotARelease  # noqa: E402


def run(*argv):
    """Runs a command; returns (exit code, stdout, stderr)."""
    out, err = io.StringIO(), io.StringIO()
    code = 0
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        try:
            release_version.main(list(argv))
        except SystemExit as e:
            code = e.code or 0
    return code, out.getvalue(), err.getvalue()


class Versions(unittest.TestCase):
    def test_three_whole_numbers_are_a_release_version(self):
        for v in ("0.1.10", "1.0.0", "0.0.0", "65534.0.1"):
            self.assertEqual(release_version.release_version(v), v)

    def test_anything_else_is_refused(self):
        # CFBundleVersion takes whole numbers only, and AssemblyVersion no part over 65534.
        for v in ("0.1", "0.1.0.0", "01.1.0", "0.1.09", "0.2.0-beta.1", "0.1.0+abc", "v0.1.0", " 0.1.0",
                  "0.1.65535", ""):
            with self.subTest(v=v), self.assertRaises(NotARelease):
                release_version.release_version(v)

    def test_a_tag_names_its_version(self):
        self.assertEqual(release_version.version_of_tag("rpm-v0.1.10"), "0.1.10")

    def test_another_products_tag_is_refused(self):
        with self.assertRaises(NotARelease):
            release_version.version_of_tag("dm-v0.1.10")

    def test_the_workflow_ref_of_a_tag_names_its_version(self):
        self.assertEqual(release_version.version_of_ref("refs/tags/rpm-v0.1.10"), "0.1.10")

    def test_a_run_on_a_branch_is_refused(self):
        with self.assertRaises(NotARelease) as caught:
            release_version.version_of_ref("refs/heads/main")
        self.assertIn("not on a tag", str(caught.exception))

    def test_the_cli_prints_the_version_or_fails(self):
        self.assertEqual(run("ref", "refs/tags/rpm-v0.1.10"), (0, "0.1.10\n", ""))
        code, out, err = run("check", "0.2.0-beta.1")
        self.assertEqual((code, out), (1, ""))
        self.assertTrue(err.startswith("FATAL: "))


class Head(unittest.TestCase):
    """A repository with an RPM folder and another product's folder beside it, and a bare repository
    as its origin, laid out as the real one is."""

    def setUp(self):
        self._dir = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        root = pathlib.Path(self._dir.name)
        # No global or system git configuration: a signing or hook setting on the machine running the
        # tests must not change what they see.
        empty = root / "gitconfig"
        empty.write_text("")
        env = {"GIT_CONFIG_GLOBAL": str(empty), "GIT_CONFIG_NOSYSTEM": "1",
               "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@example.com",
               "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@example.com"}
        patch = mock.patch.dict(os.environ, env)
        patch.start()
        self.addCleanup(patch.stop)
        self.addCleanup(self._dir.cleanup)

        self.origin = root / "origin.git"
        self.git(root, "init", "-q", "--bare", "-b", "main", str(self.origin))
        self.repo = root / "work"
        self.git(root, "init", "-q", "-b", "main", str(self.repo))
        self.git(self.repo, "remote", "add", "origin", str(self.origin))
        self.rpm = self.repo / "RPM"
        (self.rpm / "scripts").mkdir(parents=True)
        (self.rpm / "scripts" / "release-mac.sh").write_text("#!/bin/sh\n")
        (self.rpm / ".gitignore").write_text("models/\n")
        (self.repo / "DataMaker").mkdir()
        (self.repo / "DataMaker" / "app.cs").write_text("class A {}\n")
        self.commit("first")

    def git(self, where, *args):
        return subprocess.run(["git", "-C", str(where), *args], check=True, capture_output=True, text=True).stdout

    def commit(self, message):
        self.git(self.repo, "add", "-A")
        self.git(self.repo, "commit", "-q", "-m", message)
        self.git(self.repo, "push", "-q", "origin", "main")

    def tag(self, name, push=True, annotated=False):
        if annotated:
            self.git(self.repo, "tag", "-a", name, "-m", name)
        else:
            self.git(self.repo, "tag", name)
        if push:
            self.git(self.repo, "push", "-q", "origin", name)

    def head(self):
        return release_version.version_at_head(str(self.rpm), "origin")

    def refused(self):
        with self.assertRaises(NotARelease) as caught:
            self.head()
        return str(caught.exception)

    def test_a_pushed_tag_on_a_clean_head_is_the_version(self):
        self.tag("rpm-v0.1.10")
        self.assertEqual(self.head(), "0.1.10")

    def test_an_annotated_tag_counts_by_its_commit(self):
        self.tag("rpm-v0.1.10", annotated=True)
        self.assertEqual(self.head(), "0.1.10")

    def test_no_tag_on_head_is_refused(self):
        self.tag("rpm-v0.1.10")
        (self.rpm / "scripts" / "release-mac.sh").write_text("#!/bin/sh\necho later\n")
        self.commit("later")
        self.assertIn("HEAD has no rpm-v tag", self.refused())

    def test_another_products_tag_on_head_is_not_a_release(self):
        self.tag("dm-v0.1.10")
        self.assertIn("HEAD has no rpm-v tag", self.refused())

    def test_two_release_tags_on_head_are_refused(self):
        self.tag("rpm-v0.1.10")
        self.tag("rpm-v0.1.11")
        self.assertIn("One commit is one version", self.refused())

    def test_a_tag_that_is_not_a_release_version_is_refused(self):
        self.tag("rpm-v0.2.0-beta.1")
        self.assertIn("not a release version", self.refused())

    def test_a_tag_origin_does_not_have_is_refused(self):
        self.tag("rpm-v0.1.10", push=False)
        self.assertIn("origin has no tag rpm-v0.1.10", self.refused())

    def test_a_tag_origin_has_on_another_commit_is_refused(self):
        self.tag("rpm-v0.1.10")
        (self.rpm / "scripts" / "release-mac.sh").write_text("#!/bin/sh\necho later\n")
        self.commit("later")
        self.git(self.repo, "tag", "-f", "rpm-v0.1.10")
        self.assertIn("one version must be one commit", self.refused())

    def test_a_changed_file_in_the_folder_is_refused(self):
        self.tag("rpm-v0.1.10")
        (self.rpm / "scripts" / "release-mac.sh").write_text("#!/bin/sh\necho changed\n")
        message = self.refused()
        self.assertIn("changes that are not in rpm-v0.1.10", message)
        self.assertIn("scripts/release-mac.sh", message)

    def test_an_untracked_file_in_the_folder_is_refused(self):
        # Every file under Assets ships, so a stray one would be in the Mac build and not the Windows one.
        self.tag("rpm-v0.1.10")
        (self.rpm / "stray.png").write_bytes(b"png")
        self.assertIn("stray.png", self.refused())

    def test_an_ignored_file_in_the_folder_is_not_a_change(self):
        self.tag("rpm-v0.1.10")
        (self.rpm / "models").mkdir()
        (self.rpm / "models" / "beat.onnx").write_bytes(b"weights")
        self.assertEqual(self.head(), "0.1.10")

    def test_a_change_in_another_products_folder_is_not_a_change(self):
        self.tag("rpm-v0.1.10")
        (self.repo / "DataMaker" / "app.cs").write_text("class B {}\n")
        (self.repo / "DataMaker" / "new.cs").write_text("class C {}\n")
        self.assertEqual(self.head(), "0.1.10")

    def test_the_cli_takes_the_remote_by_name(self):
        self.tag("rpm-v0.1.10")
        self.assertEqual(run("head", "--repo", str(self.rpm), "--remote", "origin"), (0, "0.1.10\n", ""))


if __name__ == "__main__":
    unittest.main()
