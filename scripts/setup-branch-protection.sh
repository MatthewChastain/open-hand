#!/usr/bin/env bash
# Applies the branch protection rules for the public repository.
#
# Branch protection requires the repo to be public (or GitHub Pro), so run
# this once right after switching the repository to public:
#
#   bash scripts/setup-branch-protection.sh
#
# Rules:
#   main    - stable release branch. Changes land only via pull request from
#             develop (self-merge allowed, no approvals required). The CI
#             "state-tests" check must pass. Force pushes and deletion blocked.
#   develop - main working branch. Direct pushes allowed, but the CI
#             "state-tests" check must pass and force pushes are blocked.
set -euo pipefail

REPO="${GH_REPO:-MatthewChastain/open-hand}"

apply() {
    local branch="$1" require_reviews="$2"
    local payload
    payload=$(cat <<JSON
{
  "required_pull_request_reviews": {"required_approving_review_count": 0, "dismiss_stale_reviews": false},
  "required_status_checks": {"strict": false, "contexts": ["state-tests"]},
  "enforce_admins": false,
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false
}
JSON
    )
    if [[ "$require_reviews" != "true" ]]; then
        # Empty object removes the PR requirement (direct pushes allowed).
        payload=$(jq '.required_pull_request_reviews = null' <<< "$payload")
    fi

    gh api "repos/$REPO/branches/$branch/protection" -X PUT --input <<< "$payload" > /dev/null
    echo "Protected $branch (PR required: $require_reviews, checks: state-tests)"
}

apply main true
apply develop false
