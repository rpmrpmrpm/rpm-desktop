#!/usr/bin/env python3
"""The version of an rpm release, read from its tag, for release-mac.sh and the Windows workflow.

A release's version is written in one place: the tag rpm-v<X.Y.Z> on the commit it is built from.
The Windows workflow runs on that tag, and release-mac.sh --publish builds only the commit that the
tag names, so one version on two platforms has one source. The csproj's own version is 0.0.0, the
version of a build that is not a release.

  ref    For the workflow: the version in a GITHUB_REF, which must be refs/tags/rpm-v<X.Y.Z>.
  head   For release-mac.sh --publish: the version in the one rpm-v tag on HEAD. Fails when the
         folder being built has a change or an untracked file, or when origin does not have the tag
         at the same commit, because the Mac build would then not be what the tag names and what the
         Windows workflow built.
  check  For a build that is not published: fails unless the version has the form a release has.

A release version is three whole numbers, X.Y.Z, with no leading zeros and none over 65534. It has
three whole numbers because CFBundleVersion accepts nothing else. The limit is 65534 because that is
the largest part that .NET's AssemblyVersion accepts. A pre-release such as 0.2.0-beta.1 is refused:
nothing ships one, and CFBundleVersion has no form for it.

Standard library only. Run as `python3 -I scripts/release_version.py ...`.
"""

import argparse
import re
import subprocess
import sys

TAG_PREFIX = "rpm-v"
_RELEASE = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
_LARGEST_PART = 65534


class NotARelease(Exception):
    """Raised when the version, the tag or the checkout cannot be used for a release. The message says why."""


def fail(message):
    print(f"FATAL: {message}", file=sys.stderr)
    sys.exit(1)


def release_version(text):
    """Returns text if it is a version that a release can have, and raises NotARelease if not."""
    m = _RELEASE.match(text)
    if not m:
        raise NotARelease(f"'{text}' is not a release version: three whole numbers, X.Y.Z, with no leading zeros.")
    if any(int(part) > _LARGEST_PART for part in m.groups()):
        raise NotARelease(f"'{text}' has a part over {_LARGEST_PART}, which .NET's AssemblyVersion cannot hold.")
    return text


def version_of_tag(tag):
    """Returns the version that a release tag names."""
    if not tag.startswith(TAG_PREFIX):
        raise NotARelease(f"'{tag}' is not a release tag. A release tag is {TAG_PREFIX}<X.Y.Z>.")
    return release_version(tag[len(TAG_PREFIX):])


def version_of_ref(ref):
    """Returns the version in a GitHub Actions GITHUB_REF."""
    prefix = "refs/tags/"
    if not ref.startswith(prefix):
        raise NotARelease(
            f"This run is on {ref}, not on a tag. A release runs on a tag {TAG_PREFIX}<X.Y.Z>: "
            f"push one, or start the workflow on one.")
    return version_of_tag(ref[len(prefix):])


def _git(repo, *args):
    result = subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True)
    if result.returncode != 0:
        raise NotARelease(f"git {' '.join(args)} failed in {repo}: {result.stderr.strip()}")
    return result.stdout


def version_at_head(repo, remote):
    """Returns the version in the one release tag on HEAD, after checking that the checkout is what
    the tag names.

    repo is the rpm-desktop checkout the build reads. Ignored files are not changes: the models, the
    tools and the ANGLE pair are ignored, and the build checks them against their own pins."""
    tags = _git(repo, "tag", "--points-at", "HEAD", "--list", f"{TAG_PREFIX}*").split()
    if not tags:
        raise NotARelease(
            f"HEAD has no {TAG_PREFIX} tag, so this build has no release version. Tag the commit and push the "
            f"tag (that starts the Windows release), then run this on that commit: check it out, or "
            f"`git worktree add <folder> {TAG_PREFIX}<X.Y.Z>` and run the script from there.")
    if len(tags) > 1:
        raise NotARelease(f"HEAD has {len(tags)} release tags ({', '.join(sorted(tags))}). One commit is one version.")
    tag = tags[0]
    version = version_of_tag(tag)

    changes = _git(repo, "status", "--porcelain", "--", ".").splitlines()
    if changes:
        shown = "\n".join(f"         {line}" for line in changes[:10])
        more = f"\n         and {len(changes) - 10} more" if len(changes) > 10 else ""
        raise NotARelease(
            f"{repo} has changes that are not in {tag}, so this build would not be what the tag names. "
            f"Commit or stash them, or build from a worktree of the tag:\n{shown}{more}")

    head = _git(repo, "rev-parse", "HEAD").strip()
    listed = _git(repo, "ls-remote", "--tags", remote, f"refs/tags/{tag}", f"refs/tags/{tag}^{{}}")
    # A lightweight tag is one line naming the commit. An annotated tag is the tag object's line and
    # a peeled line ending in ^{} naming the commit, which is the one that counts.
    remote_commit = None
    for line in listed.splitlines():
        sha, name = line.split("\t", 1)
        if name == f"refs/tags/{tag}^{{}}" or (name == f"refs/tags/{tag}" and remote_commit is None):
            remote_commit = sha
    if remote_commit is None:
        raise NotARelease(
            f"{remote} has no tag {tag}. Push it first (git push {remote} {tag}): it is what starts the "
            f"Windows release of the same commit.")
    if remote_commit != head:
        raise NotARelease(
            f"{remote}'s {tag} names {remote_commit[:12]}, and the local one names {head[:12]}. The Windows "
            f"release was built from {remote}'s; one version must be one commit.")
    return version


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("ref", help="the version in a GITHUB_REF that names a release tag")
    p.add_argument("ref")

    p = sub.add_parser("head", help="the version in the release tag on HEAD, with a clean, pushed checkout")
    p.add_argument("--repo", required=True, help="the folder the build reads")
    p.add_argument("--remote", required=True, help="the remote the Windows workflow builds from")

    p = sub.add_parser("check", help="fail unless a version has the form a release has")
    p.add_argument("version")

    args = parser.parse_args(argv)
    try:
        if args.command == "ref":
            print(version_of_ref(args.ref))
        elif args.command == "head":
            print(version_at_head(args.repo, args.remote))
        else:
            print(release_version(args.version))
    except NotARelease as e:
        fail(str(e))


if __name__ == "__main__":
    main()
