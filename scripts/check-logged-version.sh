#!/usr/bin/env bash
#
# Reads the stamped version back OFF a built binary, on the platforms where the OS provides no
# version resource to inspect.
#
# Windows has one (the PE VERSIONINFO block, which the workflow reads directly), but an ELF binary
# and a Mach-O one do not. What both do have is the app's own startup line, which osu!framework
# writes as "Running <name> <version>" from the entry assembly — so the check is: run the smoke
# test first, then read what the app said about itself.
#
# This is the point of stamping. A release that builds, launches and reports 0.0.0.0 has failed at
# the one job the tag was for.
#
# Usage: check-logged-version.sh <log-search-root> <expected-version>
set -uo pipefail

LOG_ROOT="${1:?directory to search for runtime logs}"
EXPECTED="${2:?expected version}"

LOG="$(find "$LOG_ROOT" -name '*runtime.log' -print0 2>/dev/null | xargs -0 ls -t 2>/dev/null | head -1)"

if [[ -z "$LOG" ]]; then
    echo "::error::no runtime log under '$LOG_ROOT' — run the smoke test before this check"
    exit 1
fi

LINE="$(grep -am1 '^Running ' "$LOG")"

if [[ -z "$LINE" ]]; then
    echo "::error::no 'Running …' line in '$LOG'"
    exit 1
fi

echo "$LINE"

# "Running JukeBox 1.0.0.0 on .NET 10.0.10" -> the third field.
REPORTED="$(echo "$LINE" | awk '{print $3}')"

if [[ "$REPORTED" != "$EXPECTED" ]]; then
    echo "::error::the binary reports version '$REPORTED', expected '$EXPECTED' — the version was not stamped"
    exit 1
fi

echo "ok: the binary reports $REPORTED"
