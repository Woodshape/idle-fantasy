#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT_BIN="${GODOT_BIN:-/home/woodshape/software/godot/Godot_v4.6.2-stable_mono_linux.x86_64}"
SESSION_DIR="${SESSION_DIR:-$(mktemp -d /tmp/idle-fantasy-target-risk.XXXXXX)}"
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
	--test-bridge-scene-tag=target_choice_risk \
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
{"id":1,"cmd":"wait_for_state","name":"game_loop","timeout_ms":5000}
{"id":2,"cmd":"damage_adventurer","name":"Warrior","amount":30}
{"id":3,"cmd":"set_time_scale","scale":4.0}
{"id":4,"cmd":"wait_for_event","event":"adventurer_retreat_chosen","timeout_ms":10000}
{"id":5,"cmd":"wait_for_event","event":"adventurer_target_selected","timeout_ms":10000}
{"id":6,"cmd":"quit"}
JSONL

if ! wait "${GODOT_PID}"; then
	echo "Godot exited with failure. Session: ${SESSION_DIR}" >&2
	exit 1
fi
trap - EXIT

if grep -q '"type":"command_failed"' "${EVENTS_FILE}"; then
	echo "A bridge command failed during target-risk verification. Session: ${SESSION_DIR}" >&2
	exit 1
fi

required_events=(
	"adventurer_target_scored"
	"adventurer_target_selected"
	"adventurer_retreat_chosen"
	"bridge_stopped"
)

for event_type in "${required_events[@]}"; do
	if ! grep -q "\"type\":\"${event_type}\"" "${EVENTS_FILE}"; then
		echo "Missing required event '${event_type}'. Session: ${SESSION_DIR}" >&2
		exit 1
	fi
done

scored_count="$(grep -c '"type":"adventurer_target_scored"' "${EVENTS_FILE}" || true)"

if (( scored_count < 2 )); then
	echo "Expected at least two target score events; saw ${scored_count}. Session: ${SESSION_DIR}" >&2
	exit 1
fi

dire_line="$(grep '"type":"adventurer_target_scored"' "${EVENTS_FILE}" | grep '"monster":"Dire Slime"' | grep '"selected":false' | head -n 1 || true)"
selected_slime_line="$(grep '"type":"adventurer_target_scored"' "${EVENTS_FILE}" | grep '"monster":"Slime"' | grep '"selected":true' | head -n 1 || true)"
selected_event_line="$(grep '"type":"adventurer_target_selected"' "${EVENTS_FILE}" | grep '"monster":"Slime"' | head -n 1 || true)"

if [[ -z "${dire_line}" || -z "${selected_slime_line}" || -z "${selected_event_line}" ]]; then
	echo "Missing evidence that an over-tough Dire Slime was rejected in favor of a normal slime. Session: ${SESSION_DIR}" >&2
	exit 1
fi

dire_distance="$(printf '%s\n' "${dire_line}" | sed -n 's/.*"distance":\([0-9.]*\).*/\1/p')"
dire_score="$(printf '%s\n' "${dire_line}" | sed -n 's/.*"score":\(-*[0-9.]*\).*/\1/p')"
dire_reward="$(printf '%s\n' "${dire_line}" | sed -n 's/.*"reward_value":\([0-9.]*\).*/\1/p')"
dire_danger="$(printf '%s\n' "${dire_line}" | sed -n 's/.*"danger_penalty":\([0-9.]*\).*/\1/p')"
dire_level="$(printf '%s\n' "${dire_line}" | sed -n 's/.*"monster_level":\([0-9]*\).*/\1/p')"
dire_health_gap="$(printf '%s\n' "${dire_line}" | sed -n 's/.*"health_gap":\(-*[0-9.]*\).*/\1/p')"
dire_health_gap_penalty="$(printf '%s\n' "${dire_line}" | sed -n 's/.*"health_gap_penalty":\([0-9.]*\).*/\1/p')"
dire_combat_margin="$(printf '%s\n' "${dire_line}" | sed -n 's/.*"combat_ticks_margin":\(-*[0-9.]*\).*/\1/p')"
dire_combat_penalty="$(printf '%s\n' "${dire_line}" | sed -n 's/.*"combat_deficit_penalty":\([0-9.]*\).*/\1/p')"
slime_distance="$(printf '%s\n' "${selected_slime_line}" | sed -n 's/.*"distance":\([0-9.]*\).*/\1/p')"
slime_score="$(printf '%s\n' "${selected_slime_line}" | sed -n 's/.*"score":\(-*[0-9.]*\).*/\1/p')"
slime_reward="$(printf '%s\n' "${selected_slime_line}" | sed -n 's/.*"reward_value":\([0-9.]*\).*/\1/p')"
slime_danger="$(printf '%s\n' "${selected_slime_line}" | sed -n 's/.*"danger_penalty":\([0-9.]*\).*/\1/p')"
slime_level="$(printf '%s\n' "${selected_slime_line}" | sed -n 's/.*"monster_level":\([0-9]*\).*/\1/p')"

if ! awk \
	-v dire_distance="${dire_distance}" \
	-v slime_distance="${slime_distance}" \
	-v dire_score="${dire_score}" \
	-v slime_score="${slime_score}" \
	-v dire_reward="${dire_reward}" \
	-v slime_reward="${slime_reward}" \
	-v dire_danger="${dire_danger}" \
	-v slime_danger="${slime_danger}" \
	-v dire_level="${dire_level}" \
	-v slime_level="${slime_level}" \
	-v dire_health_gap="${dire_health_gap}" \
	-v dire_health_gap_penalty="${dire_health_gap_penalty}" \
	-v dire_combat_margin="${dire_combat_margin}" \
	-v dire_combat_penalty="${dire_combat_penalty}" \
	'BEGIN { exit !(dire_distance > slime_distance && dire_score < slime_score && dire_reward > slime_reward && dire_danger > slime_danger && dire_level > slime_level && dire_health_gap > 1000 && dire_health_gap_penalty > 0 && dire_combat_margin < 0 && dire_combat_penalty > 0) }'; then
	echo "Target scoring did not reject the farther, higher-reward, over-tough Dire Slime. Session: ${SESSION_DIR}" >&2
	exit 1
fi

for field in \
	'"reward_value"' \
	'"distance_penalty"' \
	'"danger_penalty"' \
	'"health_gap"' \
	'"health_gap_penalty"' \
	'"adventurer_expected_damage_per_tick"' \
	'"monster_expected_damage_per_tick"' \
	'"expected_ticks_to_kill_monster"' \
	'"expected_ticks_to_kill_adventurer"' \
	'"combat_ticks_margin"' \
	'"combat_deficit_penalty"' \
	'"wounded_caution_adjustment"' \
	'"adventurer_hp_ratio"'; do
	if ! printf '%s\n' "${dire_line}" | grep -q "${field}"; then
		echo "Target score event is missing field ${field}. Session: ${SESSION_DIR}" >&2
		exit 1
	fi
done

if printf '%s\n' "${dire_line}" | grep -q '"level_fit_bonus"'; then
	echo "Target score event still includes removed level_fit_bonus. Session: ${SESSION_DIR}" >&2
	exit 1
fi

retreat_line="$(grep '"type":"adventurer_retreat_chosen"' "${EVENTS_FILE}" | grep '"adventurer":"Warrior"' | head -n 1 || true)"

if [[ -z "${retreat_line}" ]]; then
	echo "Missing Warrior retreat evidence after damage. Session: ${SESSION_DIR}" >&2
	exit 1
fi

retreat_health_ratio="$(printf '%s\n' "${retreat_line}" | sed -n 's/.*"health_ratio":\([0-9.]*\).*/\1/p')"
retreat_threshold="$(printf '%s\n' "${retreat_line}" | sed -n 's/.*"rest_health_ratio":\([0-9.]*\).*/\1/p')"

if ! awk -v health_ratio="${retreat_health_ratio}" -v threshold="${retreat_threshold}" \
	'BEGIN { exit !(health_ratio < 0.50 && threshold >= 0.50 && health_ratio < threshold) }'; then
	echo "Hurt adventurer did not retreat at the earlier wounded threshold. health_ratio=${retreat_health_ratio:-missing} threshold=${retreat_threshold:-missing}. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if grep '"type":"adventurer_target_selected"' "${EVENTS_FILE}" | grep -q '"monster":"Dire Slime"'; then
	echo "Dire Slime was selected despite a negative combat-risk score. Session: ${SESSION_DIR}" >&2
	exit 1
fi

echo "Target choice and risk verification passed."
echo "Session: ${SESSION_DIR}"
