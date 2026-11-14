#!/usr/bin/env bash
# Local coop test on this PC (run from WSL): relay on Windows localhost + two ONI windows side by side,
# A = host (loads a save), B = guest (gets the world from A over a simulated long-distance line).
#
#   tools/coop-local-test.sh                       defaults below
#   tools/coop-local-test.sh --sim off             B on a perfect line
#   tools/coop-local-test.sh --quit-after 150      both games quit by themselves (B after half the time)
#   tools/coop-local-test.sh --save 'C:\path\x.sav'
#   tools/coop-local-test.sh --server-mode         A is the dedicated server instead of a player host: no window
#                                                  (-batchmode -nographics), -coop-role server, named "Server"
#   tools/coop-local-test.sh --remote s --sim off  both games use the relay on ssh host "s" (~/oni-coop) through an SSH
#                                                  tunnel (127.0.0.1:47811 here), instead of a local relay
#
# Needs: ./build.sh OniCoop (mod installed + enabled), dist/coop-relay/win-x64 (dotnet publish), steam_appid.txt in
# the game folder and C:\ONI-dev\oni-multilaunch.exe (see docs/architecture.md, local test plan).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEV=/mnt/c/ONI-dev
DEV_WIN='C:\ONI-dev'
TESTDIR=/mnt/c/Users/DELL/Documents/ONI-local-test
SAVE='C:\Users\DELL\Documents\ONI-local-test\saves\c02_coop.sav'
SIM='delay=150,jitter=15,loss=0.02'   # ~300 ms round trip, 2 % UDP loss each way (the user's line)
QUIT=0
TOKEN=devtoken
HOSTX=''          # extra -coop-* options for A only, e.g. --host-args -coop-savebench
BOTHX=''          # extra -coop-* options for both, e.g. --both-args "-coop-autotest cmds"
WANDER='-coop-wander'

PORT=47810
SERVERMODE=''    # A = dedicated server (headless, -coop-role server)
REMOTE=''        # ssh host running the relay in ~/oni-coop (see docs/coop-skeleton.md)

while [[ $# -gt 0 ]]; do
  case "$1" in
    --sim) SIM="$2"; shift 2 ;;
    --quit-after) QUIT="$2"; shift 2 ;;
    --save) SAVE="$2"; shift 2 ;;
    --host-args) HOSTX="$2"; shift 2 ;;
    --both-args) BOTHX="$2"; shift 2 ;;
    --no-wander) WANDER=''; shift ;;
    --remote) REMOTE="$2"; shift 2 ;;
    --server-mode) SERVERMODE=1; shift ;;
    *) echo "unknown option $1" >&2; exit 1 ;;
  esac
done
[[ "$SIM" == off ]] && SIM=''

mkdir -p "$DEV" "$TESTDIR"
# a relay left running (a previous test) keeps its exe locked: stop it before copying a new one
for pid in $(tasklist.exe /FI "IMAGENAME eq coop-relay.exe" /FO CSV /NH 2>/dev/null | grep coop-relay | cut -d'"' -f4); do
  taskkill.exe /PID "$pid" /F >/dev/null 2>&1 || true
done
sleep 1
cp -u "$ROOT/dist/coop-relay/win-x64/coop-relay.exe" "$DEV/"

relay_log() { if [[ -n "$REMOTE" ]]; then ssh -o BatchMode=yes "$REMOTE" "tail -n +$((REMOTE_LOG_START + 1)) ~/oni-coop/relay.log"; else cat "$DEV/relay.log"; fi; }

if [[ -n "$REMOTE" ]]; then
  # the relay on the server, through an SSH tunnel (TCP only: the games fall back from UDP by themselves)
  PORT=47811
  TOKEN=$(ssh -o BatchMode=yes "$REMOTE" 'cat ~/oni-coop/token')
  REMOTE_LOG_START=$(ssh -o BatchMode=yes "$REMOTE" 'wc -l < ~/oni-coop/relay.log')
  if ! powershell.exe -NoProfile -Command "(Test-NetConnection 127.0.0.1 -Port $PORT -WarningAction SilentlyContinue).TcpTestSucceeded" | grep -q True; then
    ssh -f -N -o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -L 127.0.0.1:$PORT:127.0.0.1:47810 "$REMOTE"
    echo "SSH tunnel 127.0.0.1:$PORT -> $REMOTE:47810 opened"
  fi
else
# A fresh relay (and log) per test: stop the one a previous test left running.
for pid in $(tasklist.exe /FI "IMAGENAME eq coop-relay.exe" /FO CSV /NH 2>/dev/null | grep coop-relay | cut -d'"' -f4); do
  taskkill.exe /PID "$pid" /F >/dev/null 2>&1 || true
done
sleep 1
rm -f "$DEV/relay.log"
# Started by Windows itself (not as a WSL child), so it does not hold this script's output pipe open.
powershell.exe -NoProfile -Command "Start-Process -WindowStyle Hidden -WorkingDirectory '$DEV_WIN' -FilePath '$DEV_WIN\\coop-relay.exe' \
  -ArgumentList 'serve','--bind','127.0.0.1','--port','$PORT','--token','$TOKEN','--data','$DEV_WIN\\relay-data','--log','$DEV_WIN\\relay.log'"
sleep 2
head -1 "$DEV/relay.log"
fi

QA=(); QB=()
ANAME=Alice; AWIN=(-coop-window 0,60,1280,800)
if [[ -n "$SERVERMODE" ]]; then ANAME=Server; AWIN=(-batchmode -nographics -coop-role server); fi
if [[ "$QUIT" != 0 ]]; then QA=(-coop-quit-after "$QUIT"); QB=(-coop-quit-after "$((QUIT / 2))"); fi
SIMARG=(); [[ -n "$SIM" ]] && SIMARG=(-coop-sim "$SIM")

rm -rf /mnt/c/Users/DELL/Documents/Klei/OxygenNotIncluded/coop/shots
# Output goes to a file: the game processes inherit the launcher's stdout, and a pipe back into WSL would keep
# this script waiting until both games exit.
(cd "$DEV" && ./oni-multilaunch.exe two \
  -- -coop-server 127.0.0.1:$PORT -coop-token $TOKEN -coop-shots 10 $WANDER $BOTHX \
  --a -coop-role host -coop-name $ANAME -coop-load "$SAVE" "${AWIN[@]}" "${QA[@]}" $HOSTX \
  --b -coop-role guest -coop-name Bob -coop-window 1280,60,1280,800 "${SIMARG[@]}" "${QB[@]}" \
  > "$TESTDIR/multilaunch.txt" 2>&1)
grep -E "^instance|both|exited|ready|not seen" "$TESTDIR/multilaunch.txt" | cut -c1-60

echo "waiting for Bob to get the world and enter the game (up to 5 min)..."
export -f relay_log; export REMOTE REMOTE_LOG_START DEV
if timeout 300 bash -c "until relay_log 2>/dev/null | grep -q 'Bob(Guest): .* -> InGame'; do sleep 3; done"; then
  echo "OK"
else
  echo "TIMEOUT"
fi
relay_log
echo
echo "game logs: $TESTDIR/instanceA.log, instanceB.log (grep '\[Coop\]')"
echo "screenshots: C:\\Users\\DELL\\Documents\\Klei\\OxygenNotIncluded\\coop\\shots"
echo "stop: taskkill.exe /PID <pid> /F  (pids above; relay: tasklist.exe | grep coop-relay)"
