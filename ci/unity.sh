#!/bin/bash
# Runs the Unity editor (Apple-silicon build) with a watchdog so a hung editor cannot stall the job.
#   ci/unity.sh <log file> <timeout seconds> <done regex or ""> <unity args...>
# Returns Unity's exit code; 0 if the done regex appeared in the log (Unity is then killed if it lingers);
# 124 on timeout. The log is followed live so the job output shows progress.
LOG="$1"; LIMIT="$2"; DONE="$3"; shift 3
UNITY=/Applications/Unity/Unity.app/Contents/MacOS/Unity
rm -f "$LOG"; touch "$LOG"
"$UNITY" -batchmode -nographics -logFile "$LOG" "$@" &
PID=$!
tail -n +1 -f "$LOG" > >(grep --line-buffered -iE "licens|error|exception|\[IosShellBuild|build|compil|exiting|il2cpp" | cut -c1-300) &
TAILPID=$!
start=$SECONDS; rc=""
while kill -0 $PID 2>/dev/null; do
  if [ -n "$DONE" ] && grep -qE "$DONE" "$LOG"; then
    echo "[unity.sh] done marker seen after $((SECONDS-start))s"
    for i in $(seq 1 30); do kill -0 $PID 2>/dev/null || break; sleep 1; done
    kill -9 $PID 2>/dev/null; rc=0; break
  fi
  if [ $((SECONDS-start)) -ge "$LIMIT" ]; then
    echo "[unity.sh] TIMEOUT after ${LIMIT}s - killing Unity"; kill -9 $PID 2>/dev/null; rc=124; break
  fi
  sleep 5
done
if [ -z "$rc" ]; then wait $PID; rc=$?; fi
if [ -n "$DONE" ] && [ "$rc" != 0 ] && grep -qE "$DONE" "$LOG"; then rc=0; fi
sleep 1; kill $TAILPID 2>/dev/null
echo "[unity.sh] exit $rc after $((SECONDS-start))s"
exit $rc
