#!/bin/sh
# OFFLINE PREPARATION ONLY; running this on a VPS requires separate approval.
# Existing ring must already be recovered by its custodian; NEVER initialize here.
set -eu
: "${APP43B_SEPARATE_APPROVAL:?Separate live approval required}"
: "${DP_BACKING_DIRECTORY:?Approved dedicated local PV backing directory required}"
test -d "$DP_BACKING_DIRECTORY"
test ! -L "$DP_BACKING_DIRECTORY"
case "$DP_BACKING_DIRECTORY" in /var/lib/rancher/k3s/storage/*) ;; *) exit 1;; esac
# Fail rather than treating an empty/new directory as preserved legacy material.
find "$DP_BACKING_DIRECTORY" -maxdepth 1 -type f -name 'key-*.xml' | grep -q .
# Root ONLY: no recursive modification of existing keys/database files.
chown 1654:1654 "$DP_BACKING_DIRECTORY"
chmod 0700 "$DP_BACKING_DIRECTORY"
stat -c '%u:%g %a %n' "$DP_BACKING_DIRECTORY"
# Separately verify mounted effective UID1654 can read existing keys and can
# create/rename/delete a synthetic permission-probe file; no XML in diagnostics.
