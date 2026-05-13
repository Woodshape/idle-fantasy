#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT_BIN="${GODOT_BIN:-/home/woodshape/software/godot/Godot_v4.6.2-stable_mono_linux.x86_64}"
SESSION_DIR="${SESSION_DIR:-$(mktemp -d /tmp/idle-fantasy-combat.XXXXXX)}"
HOME_DIR="${IDLE_FANTASY_TEST_HOME:-/tmp/idle-fantasy-home}"
XDG_DIR="${IDLE_FANTASY_TEST_XDG:-/tmp/idle-fantasy-xdg}"
COMMANDS_FILE="${SESSION_DIR}/commands.jsonl"
EVENTS_FILE="${SESSION_DIR}/events.jsonl"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-45}"

mkdir -p "${SESSION_DIR}" "${HOME_DIR}" "${XDG_DIR}"

HOME="${HOME_DIR}" XDG_DATA_HOME="${XDG_DIR}" "${GODOT_BIN}" \
	--headless \
	--path "${PROJECT_ROOT}" \
	-- \
	--test-bridge-dir="${SESSION_DIR}" \
	--test-bridge-scene-tag=combat_main_loop \
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
{"id":2,"cmd":"wait_for_event","event":"game_loop_completed","timeout_ms":30000}
{"id":3,"cmd":"quit"}
JSONL

if ! wait "${GODOT_PID}"; then
	echo "Godot exited with failure. Session: ${SESSION_DIR}" >&2
	exit 1
fi
trap - EXIT

required_events=(
	"simulation_tick"
	"combat_tick_started"
	"combat_action_queued"
	"combat_action_order_rolled"
	"combat_action_resolved"
	"combat_action_cooldown_started"
	"combat_action_cooldown_ready"
	"combat_cast_started"
	"adventurer_cast_movement_paused"
	"combat_cast_completed"
	"monster_aggro_target_set"
	"monster_aggro_moving"
	"combat_tick_completed"
	"loot_collected"
	"game_loop_completed"
	"bridge_stopped"
)

for event_type in "${required_events[@]}"; do
	if ! grep -q "\"type\":\"${event_type}\"" "${EVENTS_FILE}"; then
		echo "Missing required event '${event_type}'. Session: ${SESSION_DIR}" >&2
		exit 1
	fi
done

if ! grep -q '"action_id":"spark"' "${EVENTS_FILE}"; then
	echo "Missing spark action evidence. Session: ${SESSION_DIR}" >&2
	exit 1
fi

spark_cast_line="$(grep '"type":"combat_cast_started"' "${EVENTS_FILE}" | grep '"action_id":"spark"' | head -n 1 || true)"

if [[ -z "${spark_cast_line}" ]]; then
	echo "Missing spark cast-start evidence. Session: ${SESSION_DIR}" >&2
	exit 1
fi

spark_distance="$(printf '%s\n' "${spark_cast_line}" | sed -n 's/.*"distance_to_target":\([0-9.]*\).*/\1/p')"
spark_range="$(printf '%s\n' "${spark_cast_line}" | sed -n 's/.*"range":\([0-9.]*\).*/\1/p')"

if ! awk -v distance="${spark_distance}" -v range="${spark_range}" 'BEGIN { exit !(distance >= 150 && range >= 160 && distance <= range) }'; then
	echo "Spark did not cast from medium range. distance=${spark_distance:-missing} range=${spark_range:-missing}. Session: ${SESSION_DIR}" >&2
	exit 1
fi

cast_pause_line="$(grep '"type":"adventurer_cast_movement_paused"' "${EVENTS_FILE}" | grep '"action_id":"spark"' | head -n 1 || true)"

if [[ -z "${cast_pause_line}" ]]; then
	echo "Missing adventurer movement pause during spark cast. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if ! printf '%s\n' "${cast_pause_line}" | grep -q '"distance_to_monster":1[5-6][0-9]'; then
	echo "Spark cast pause did not happen at ranged distance. Session: ${SESSION_DIR}" >&2
	exit 1
fi

spark_resolved_numbered_line="$(grep -n '"type":"combat_action_resolved"' "${EVENTS_FILE}" | grep '"combatant_kind":"adventurer"' | grep '"action_id":"spark"' | head -n 1 || true)"
spark_resolved_line_number="$(printf '%s\n' "${spark_resolved_numbered_line}" | cut -d: -f1)"
spark_resolved_line="$(printf '%s\n' "${spark_resolved_numbered_line}" | cut -d: -f2-)"
first_monster_attack_line_number="$(grep -n '"type":"combat_action_resolved"' "${EVENTS_FILE}" | grep '"combatant_kind":"monster"' | head -n 1 | cut -d: -f1 || true)"

if [[ -z "${spark_resolved_line_number}" ]]; then
	echo "Missing spark action resolution. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if ! grep '"type":"combat_action_resolved"' "${EVENTS_FILE}" | grep -q '"combatant_kind":"monster"'; then
	echo "Missing monster attack resolution after ranged opener. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if (( first_monster_attack_line_number <= spark_resolved_line_number )); then
	echo "Monster attacked before spark resolved. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if ! grep '"type":"combat_action_resolved"' "${EVENTS_FILE}" | grep '"combatant_kind":"adventurer"' | grep -q '"action_id":"heavy_strike"'; then
	echo "Missing adventurer melee action after ranged opener. Session: ${SESSION_DIR}" >&2
	exit 1
fi

aggro_numbered_line="$(grep -n '"type":"monster_aggro_target_set"' "${EVENTS_FILE}" | head -n 1 || true)"
aggro_line="$(printf '%s\n' "${aggro_numbered_line}" | cut -d: -f2-)"
aggro_line_number="$(printf '%s\n' "${aggro_numbered_line}" | cut -d: -f1)"

if [[ -z "${aggro_line}" ]]; then
	echo "Missing monster aggro target event. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if (( aggro_line_number <= spark_resolved_line_number )); then
	echo "Monster aggro triggered before spark resolved. Session: ${SESSION_DIR}" >&2
	exit 1
fi

aggro_distance="$(printf '%s\n' "${aggro_line}" | sed -n 's/.*"distance_to_target":\([0-9.]*\).*/\1/p')"

if ! printf '%s\n' "${aggro_line}" | grep -q '"aggro_trigger":"ability_resolved"'; then
	echo "Spark resolved, but first combat aggro was not caused by the targeted spell resolution. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if ! awk -v distance="${aggro_distance}" 'BEGIN { exit !(distance >= 150) }'; then
	echo "Targeted spell aggro was not triggered from ranged spark distance. distance=${aggro_distance:-missing}. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if ! grep -q '"skill_cooldowns"' "${SESSION_DIR}/state.json"; then
	echo "Missing skill cooldown state. Session: ${SESSION_DIR}" >&2
	exit 1
fi

echo "Combat main loop verification passed."
echo "Session: ${SESSION_DIR}"
