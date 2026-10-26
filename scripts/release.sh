#!/usr/bin/env bash
# Cut a plugin release: scripts/release.sh X.Y.Z.W
#
# On a clean dev that matches origin/dev, this sets <Version> in Directory.Build.props,
# turns CHANGELOG.md's Unreleased section into "## [X.Y.Z.W] - <today>", runs the tests,
# commits "release: vX.Y.Z.W", tags vX.Y.Z.W, and pushes dev, main (a fast-forward to the
# release commit) and the tag in one atomic push. The tag starts
# .github/workflows/release.yml: build, test, the zip on a GitHub Release, and the
# repository manifest on GitHub Pages.
#
#   --dry-run   do every check and show the changes, then put the files back
#               without committing, tagging or pushing.
set -euo pipefail

die() { echo "release: $*" >&2; exit 1; }

dry_run=0
args=()
for a in "$@"; do
  case "$a" in
    --dry-run) dry_run=1 ;;
    -h|--help) sed -n '2,12s/^# \{0,1\}//p' "$0"; exit 0 ;;
    *) args+=("$a") ;;
  esac
done
[ "${#args[@]}" -eq 1 ] || die "usage: scripts/release.sh [--dry-run] X.Y.Z.W"
version=${args[0]#v}
[[ "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] \
  || die "not a version: ${args[0]} (want X.Y.Z.W, Jellyfin's four parts)"
tag="v$version"

cd "$(git rev-parse --show-toplevel)"

# --- checks -----------------------------------------------------------------
branch=$(git symbolic-ref --quiet --short HEAD || true)
[ "$branch" = dev ] || die "releases are cut from dev (you are on ${branch:-a detached HEAD})"
[ -z "$(git status --porcelain)" ] || die "the working tree is not clean"

git fetch --quiet --tags origin dev main
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/dev)" ] \
  || die "dev is not the same as origin/dev (pull or push first)"
git merge-base --is-ancestor origin/main HEAD \
  || die "origin/main is not an ancestor of dev, so main can't fast-forward"
if git rev-parse -q --verify "refs/tags/$tag" >/dev/null \
  || git ls-remote --exit-code --tags origin "refs/tags/$tag" >/dev/null; then
  die "$tag already exists"
fi

current=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' Directory.Build.props | head -n1)
newest=$(printf '%s\n%s\n' "$current" "$version" | sort -t. -k1,1n -k2,2n -k3,3n -k4,4n | tail -n1)
[ "$version" != "$current" ] && [ "$newest" = "$version" ] \
  || die "$version is not newer than the current $current"

grep -q '^## \[Unreleased\]' CHANGELOG.md || die "CHANGELOG.md has no ## [Unreleased] section"
today=$(date +%Y-%m-%d)

# --- changes ----------------------------------------------------------------
# Until the release commit exists, any exit puts the two files back.
trap 'git checkout --quiet -- Directory.Build.props CHANGELOG.md' EXIT
sed -i.bak -E "s|<Version>[^<]*</Version>|<Version>$version</Version>|" Directory.Build.props
rm -f Directory.Build.props.bak

python3 - "$version" "$today" <<'PY'
import re, sys
version, today = sys.argv[1], sys.argv[2]
text = open("CHANGELOG.md").read()
m = re.search(r"^## \[Unreleased\]\n(.*?)(?=^## \[|\Z)", text, re.M | re.S)
if not m or not m.group(1).strip():
    sys.exit("release: CHANGELOG.md's Unreleased section is empty: nothing to release")
head = f"## [Unreleased]\n\n## [{version}] - {today}\n\n{m.group(1).strip()}\n\n"
open("CHANGELOG.md", "w").write(text[:m.start()] + head + text[m.end():])
PY

grep -q "<Version>$version</Version>" Directory.Build.props || die "failed to bump Directory.Build.props"
grep -q "^## \[$version\] - $today" CHANGELOG.md || die "failed to date CHANGELOG.md"

dotnet test --configuration Release --nologo --verbosity quiet || die "the tests failed"

git --no-pager diff --stat
if [ "$dry_run" = 1 ]; then
  git --no-pager diff
  echo "release: dry run of $tag ok; nothing committed or pushed"
  exit 0
fi

# --- commit, tag, push ------------------------------------------------------
git commit --quiet -m "release: $tag" -- Directory.Build.props CHANGELOG.md
trap - EXIT
git tag -a "$tag" -m "jellyfin-plugin-quipclipper $version"
if ! git push --atomic origin HEAD:refs/heads/dev HEAD:refs/heads/main "refs/tags/$tag"; then
  die "the push failed and nothing was published. Retry it with
  git push --atomic origin HEAD:refs/heads/dev HEAD:refs/heads/main refs/tags/$tag
or undo the local release with
  git tag -d $tag && git reset --hard origin/dev"
fi

echo "release: $tag pushed (dev, main, tag)."
echo "  Release workflow: https://github.com/weckere/jellyfin-plugin-quipclipper/actions/workflows/release.yml"
