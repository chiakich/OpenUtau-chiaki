#!/usr/bin/env bash
# Merge upstream (OpenUtau Lunai) into a new branch and auto-resolve the conflicts
# that recur every sync: files this fork deleted on purpose but upstream still edits.
set -euo pipefail

remote="${UPSTREAM_REMOTE:-upstream}"
branch="${1:-merge/upstream-$(date +%Y-%m-%d)}"

if [ -n "$(git status --porcelain)" ]; then
  echo "Working tree is not clean." >&2
  exit 1
fi

git config rerere.enabled true
git fetch "$remote"
git switch -c "$branch"

if git merge --no-ff --no-commit "$remote/master"; then
  echo "Merged without conflicts. Review, build, test, then: git commit"
  exit 0
fi

# "UD": deleted by us, modified by them. Keep it deleted.
git status --porcelain | awk '$1 == "UD" { print substr($0, 4) }' | while read -r path; do
  echo "Keeping fork deletion: $path"
  git rm -q -- "$path"
done

echo
echo "Remaining conflicts:"
git diff --name-only --diff-filter=U || true
