#!/usr/bin/env bash
#
# Proves a packaged build actually RUNS, which is the thing single-file publishing breaks and a
# successful `dotnet publish` says nothing about.
#
# osu!framework carries native dependencies — BASS/BASS_FX/BASSmix for audio, SDL2 for the window,
# FFmpeg for video, veldrid/MoltenVK for rendering. Single-file publishing embeds them and extracts
# them to a temp directory on first launch, and the loader has to find them there. When that goes
# wrong the app dies with "Unable to load shared library", which is invisible to a build-only check
# and extremely visible to a user.
#
# So: launch the real binary, let it get through startup, then read its own log for the lines that
# only appear once the native libraries are loaded and initialised. The process is killed by the
# timeout — it is a GUI app with no exit path, and a timeout kill is the SUCCESS case here.
#
# Usage: smoke-run.sh <executable> <log-search-root> [seconds]
set -uo pipefail

EXE="${1:?executable}"
LOG_ROOT="${2:?directory to search for runtime logs}"
SECONDS_TO_RUN="${3:-75}"

if [[ ! -x "$EXE" ]]; then
    echo "::error::'$EXE' is not executable — the artifact would not run for a user either"
    exit 1
fi

echo "running '$EXE' for ${SECONDS_TO_RUN}s"

# Only logs written AFTER this point count. A CI runner starts clean, but a local run against an
# app that has been launched before would otherwise happily pass on a stale log from last time.
MARKER=/tmp/smoke-marker
: > "$MARKER"

# Backgrounded and killed by hand rather than run under `timeout`, which macOS does not ship —
# a GNU-coreutils-only tool here cost a CI round trip, failing with status 127 ("command not
# found") that read like the app itself refusing to start.
#
# xvfb where there is no display (Linux CI); macOS always has one.
if [[ "$(uname)" == "Linux" ]] && command -v xvfb-run >/dev/null; then
    xvfb-run -a "$EXE" >/tmp/smoke-stdout.txt 2>&1 &
else
    "$EXE" >/tmp/smoke-stdout.txt 2>&1 &
fi

APP_PID=$!
sleep "$SECONDS_TO_RUN"

fail=0

# The strongest signal available, and the reason it is checked FIRST: the app is a GUI with no exit
# path, so it should still be running when the watch window ends. Having quit on its own means it
# crashed.
#
# This check exists because marker-scraping alone is not enough. A build published with
# -p:EnableCompressionInSingleFile=true logged all three markers below and THEN died with an
# AccessViolationException inside the crypto native library while hashing a font — the log looked
# perfect and the app was unusable.
if kill -0 "$APP_PID" 2>/dev/null; then
    echo "ok: still running after ${SECONDS_TO_RUN}s"
    kill "$APP_PID" 2>/dev/null || true
    wait "$APP_PID" 2>/dev/null || true
else
    wait "$APP_PID" 2>/dev/null
    echo "::error::the app exited on its own with status $? — it should still have been running"
    fail=1
fi

if grep -qaE 'Fatal error|Unhandled exception|AccessViolationException' /tmp/smoke-stdout.txt; then
    echo "::error::the app printed a fatal error"
    echo "---- stdout ----"
    cat /tmp/smoke-stdout.txt
    fail=1
fi

LOG="$(find "$LOG_ROOT" -name '*runtime.log' -newer "$MARKER" -print0 2>/dev/null | xargs -0 ls -t 2>/dev/null | head -1)"

if [[ -z "$LOG" ]]; then
    echo "::error::no runtime log written under '$LOG_ROOT' — the app did not get far enough to open one"
    echo "---- stdout ----"
    cat /tmp/smoke-stdout.txt
    exit 1
fi

echo "---- $LOG ----"
cat "$LOG"
echo "---- end of log ----"

require() {
    if grep -qa "$1" "$LOG"; then
        echo "ok: $2"
    else
        echo "::error::$2 — expected '$1' in the runtime log"
        fail=1
    fi
}

# The window and renderer came up: SDL2 and veldrid (plus MoltenVK on macOS) all loaded.
require 'Renderer initialised' 'renderer started'
# The audio stack came up: BASS, BASS_FX and BASSmix all loaded.
require 'BASS initialised' 'audio started'
# And the game itself got past its own startup rather than dying on the first screen.
require 'ScreenStack' 'the game screen loaded'

# The specific failure single-file packaging causes, called out by name so a future breakage is
# self-explaining in the job log rather than just a missing marker.
if grep -qaE 'Unable to load shared library|DllNotFoundException' "$LOG" /tmp/smoke-stdout.txt; then
    echo "::error::a native library failed to load — the single-file extraction is missing something"
    fail=1
fi

exit $fail
