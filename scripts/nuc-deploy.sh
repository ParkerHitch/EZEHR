#!/usr/bin/env bash
# CI's only way in to the NUC: the forced command of the deploy key (.github/workflows/deploy.yml). Brings this
# checkout up to the commit CI just tested, then runs nuc-setup.sh. The commit arrives as the SSH command, which
# is checked and never run:
#
#   ssh -i <deploy key> colin-meyer@colin-nuc.tail51bf60.ts.net <commit sha>
#
# nuc-setup.sh runs as root and comes from the commit being deployed, so anyone who can push to main can run code
# as root here. The key itself can do nothing but deploy a commit that is already on main.
set -euo pipefail

commit=${SSH_ORIGINAL_COMMAND:-}
if [[ ! "$commit" =~ ^[0-9a-f]{40}$ ]]; then
  echo "expected a full commit sha, got '$commit'" >&2
  exit 1
fi
cd "$(dirname "$0")/.."

# The public URL, so the fetch needs no GitHub credentials on this machine.
git fetch --quiet https://github.com/colinmeyer0/EZEHR.git main
if ! git merge-base --is-ancestor "$commit" FETCH_HEAD; then
  echo "$commit isn't on main" >&2
  exit 1
fi
# Fails, and deploys nothing, if this checkout has local commits or changes that are in the way.
git merge --quiet --ff-only "$commit"
echo "deploying $(git log -1 --format='%h %s')"

exec sudo -n "$(pwd)/scripts/nuc-setup.sh"
