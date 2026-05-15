#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT_BIN="${GODOT_BIN:-/home/woodshape/software/godot/Godot_v4.6.2-stable_mono_linux.x86_64}"
SESSION_DIR="${SESSION_DIR:-$(mktemp -d /tmp/idle-fantasy-social-aggro.XXXXXX)}"
HOME_DIR="${IDLE_FANTASY_TEST_HOME:-/tmp/idle-fantasy-home}"
XDG_DIR="${IDLE_FANTASY_TEST_XDG:-/tmp/idle-fantasy-xdg}"
COMMANDS_FILE="${SESSION_DIR}/commands.jsonl"
EVENTS_FILE="${SESSION_DIR}/events.jsonl"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-30}"

mkdir -p "${SESSION_DIR}" "${HOME_DIR}" "${XDG_DIR}"

HOME="${HOME_DIR}" XDG_DATA_HOME="${XDG_DIR}" "${GODOT_BIN}" \
	--headless \
	--path "${PROJECT_ROOT}" \
	-- \
	--test-bridge-dir="${SESSION_DIR}" \
	--test-bridge-scene-tag=enemy_group_social_aggro \
	> "${SESSION_DIR}/godot.log" 2>&1 &

GODOT_PID=$!

cleanup() {
	if kill -0 "${GODOT_PID}" 2>/dev/null; then
		kill "${GODOT_PID}" 2>/dev/null || true
		wait "${GODOT_PID}" 2>/dev/null || true
	fi
}
trap cleanup EXIT

deadline=$((SECONDS + TIMEOUT_SECONDS))
until [[ -f "${EVENTS_FILE}" ]] && grep -q '"type":"bridge_started"' "${EVENTS_FILE}"; do
	if (( SECONDS >= deadline )); then
		echo "Timed out waiting for bridge_started. Session: ${SESSION_DIR}" >&2
		exit 1
	fi
	sleep 0.1
done

cat >> "${COMMANDS_FILE}" <<'JSONL'
{"id":1,"cmd":"set_time_scale","scale":4.0}
{"id":2,"cmd":"wait_for_event","event":"enemy_group_social_aggro","timeout_ms":15000}
{"id":3,"cmd":"wait_for_event","event":"monster_joined_encounter","timeout_ms":5000}
{"id":4,"cmd":"wait_for_event","event":"monster_wave_cleared","timeout_ms":45000}
{"id":5,"cmd":"quit"}
JSONL

if ! wait "${GODOT_PID}"; then
	echo "Godot exited with failure. Session: ${SESSION_DIR}" >&2
	exit 1
fi
trap - EXIT

required_events=(
	"content_validation_completed"
	"combat_started"
	"combat_action_resolved"
	"enemy_group_social_aggro"
	"monster_aggro_target_set"
	"monster_joined_encounter"
	"loot_collected"
	"monster_wave_cleared"
	"bridge_stopped"
)

for event_type in "${required_events[@]}"; do
	if ! grep -q "\"type\":\"${event_type}\"" "${EVENTS_FILE}"; then
		echo "Missing required event '${event_type}'. Session: ${SESSION_DIR}" >&2
		exit 1
	fi
done

python3 - "$EVENTS_FILE" <<'PY'
import json
import sys

events_path = sys.argv[1]
with open(events_path, "r", encoding="utf-8") as handle:
    events = [json.loads(line) for line in handle if line.strip()]

social_events = [event for event in events if event.get("type") == "enemy_group_social_aggro"]
if not social_events:
    raise SystemExit("Missing enemy_group_social_aggro event.")

social = social_events[0]
if social.get("enemy_group_id") != "slime_pack":
    raise SystemExit(f"Expected slime_pack social aggro, saw {social.get('enemy_group_id')!r}.")
if float(social.get("social_radius", 0)) != 115.0:
    raise SystemExit(f"Expected social radius 115.0, saw {social.get('social_radius')!r}.")
if social.get("source_monster") == social.get("monster"):
    raise SystemExit("Social aggro cannot target the source monster.")
if float(social.get("distance_to_source", 9999)) > float(social.get("social_radius", 0)):
    raise SystemExit("Social aggro target was outside the configured radius.")

aggro = [
    event for event in events
    if event.get("type") == "monster_aggro_target_set"
    and event.get("aggro_trigger") == "social_aggro"
    and event.get("enemy_group_id") == "slime_pack"
    and event.get("monster") == social.get("monster")
]
if not aggro:
    raise SystemExit("Grouped monster did not receive a social aggro target.")

social_monsters = {event.get("monster") for event in social_events}
joined = [
    event for event in events
    if event.get("type") == "monster_joined_encounter"
    and event.get("aggro_trigger") == "social_aggro"
    and event.get("monster") in social_monsters
]
if not joined:
    raise SystemExit("No grouped monster joined an active encounter.")

rewarded_social_join = None
for joined_event in joined:
    encounter_id = joined_event.get("encounter_id")
    joined_monster = joined_event.get("monster")
    loot = [
        event for event in events
        if event.get("type") == "loot_collected"
        and event.get("encounter_id") == encounter_id
    ]
    if joined_monster in {event.get("monster") for event in loot}:
        rewarded_social_join = joined_event
        break

if rewarded_social_join is None:
    raise SystemExit("No socially joined monster was rewarded through its joined encounter.")
PY

echo "Enemy group social aggro verification passed."
echo "Session: ${SESSION_DIR}"
