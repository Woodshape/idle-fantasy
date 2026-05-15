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
{"id":2,"cmd":"click_world","x":160,"y":272}
{"id":3,"cmd":"wait_for_event","event":"character_selected","timeout_ms":5000}
{"id":4,"cmd":"wait_for_event","event":"game_loop_completed","timeout_ms":30000}
{"id":5,"cmd":"wait_for_event","event":"monster_wave_respawned","timeout_ms":15000}
{"id":6,"cmd":"quit"}
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
	"combat_tick_completed"
	"loot_collected"
	"adventurer_level_up"
	"character_selected"
	"game_loop_completed"
	"monster_wave_cleared"
	"monster_wave_respawned"
	"adventurer_recovery_tick"
	"bridge_stopped"
)

for event_type in "${required_events[@]}"; do
	if ! grep -q "\"type\":\"${event_type}\"" "${EVENTS_FILE}"; then
		echo "Missing required event '${event_type}'. Session: ${SESSION_DIR}" >&2
		exit 1
	fi
done

loot_collected_count="$(grep -c '"type":"loot_collected"' "${EVENTS_FILE}" || true)"

if (( loot_collected_count < 3 )); then
	echo "Expected all three starter slimes to be looted; saw ${loot_collected_count}. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if ! grep -q '"living_adventurers":2' "${SESSION_DIR}/state.json"; then
	echo "Expected both starter adventurers to be alive after the completed loop. Session: ${SESSION_DIR}" >&2
	exit 1
fi

for state_field in '"level"' '"experience"' '"current_experience"' '"xp_to_next_level"' '"total_experience"'; do
	if ! grep -q "${state_field}" "${SESSION_DIR}/state.json"; then
		echo "Missing adventurer progression state field ${state_field}. Session: ${SESSION_DIR}" >&2
		exit 1
	fi
done

level_up_line="$(grep '"type":"adventurer_level_up"' "${EVENTS_FILE}" | head -n 1 || true)"

if [[ -z "${level_up_line}" ]]; then
	echo "Missing adventurer level-up event. Session: ${SESSION_DIR}" >&2
	exit 1
fi

level_up_role="$(printf '%s\n' "${level_up_line}" | sed -n 's/.*"role":"\([^"]*\)".*/\1/p')"
level_up_definition_id="$(printf '%s\n' "${level_up_line}" | sed -n 's/.*"definition_id":"\([^"]*\)".*/\1/p')"
level_up_old_level="$(printf '%s\n' "${level_up_line}" | sed -n 's/.*"old_level":\([0-9]*\).*/\1/p')"
level_up_new_level="$(printf '%s\n' "${level_up_line}" | sed -n 's/.*"new_level":\([0-9]*\).*/\1/p')"
level_up_threshold="$(printf '%s\n' "${level_up_line}" | sed -n 's/.*"threshold":\([0-9]*\).*/\1/p')"
level_up_xp_to_next="$(printf '%s\n' "${level_up_line}" | sed -n 's/.*"xp_to_next_level":\([0-9]*\).*/\1/p')"
level_up_max_health_delta="$(printf '%s\n' "${level_up_line}" | sed -n 's/.*"changed_stats":{[^}]*"max_health":\([0-9]*\).*/\1/p')"
level_up_attack_delta="$(printf '%s\n' "${level_up_line}" | sed -n 's/.*"changed_stats":{[^}]*"attack":\([0-9]*\).*/\1/p')"
level_up_defense_delta="$(printf '%s\n' "${level_up_line}" | sed -n 's/.*"changed_stats":{[^}]*"defense":\([0-9]*\).*/\1/p')"

if ! awk -v old_level="${level_up_old_level}" -v new_level="${level_up_new_level}" -v threshold="${level_up_threshold}" -v next_threshold="${level_up_xp_to_next}" \
	'BEGIN { exit !(old_level == 1 && new_level == 2 && threshold == 20 && next_threshold == 35) }'; then
	echo "Level-up event did not expose the expected deterministic threshold progression. Session: ${SESSION_DIR}" >&2
	exit 1
fi

case "${level_up_role}" in
	Tank)
		expected_max_health_delta=6
		expected_attack_delta=1
		expected_defense_delta=1
		;;
	DamageDealer)
		expected_max_health_delta=4
		expected_attack_delta=2
		expected_defense_delta=1
		;;
	Support)
		expected_max_health_delta=5
		expected_attack_delta=1
		expected_defense_delta=1
		;;
	*)
		echo "Level-up event did not expose an AdventurerDefinition role. role=${level_up_role:-missing}. Session: ${SESSION_DIR}" >&2
		exit 1
		;;
esac

if [[ "${level_up_max_health_delta}" != "${expected_max_health_delta}" \
	|| "${level_up_attack_delta}" != "${expected_attack_delta}" \
	|| "${level_up_defense_delta}" != "${expected_defense_delta}" ]]; then
	echo "Level-up stat growth did not match role ${level_up_role}. max_health=${level_up_max_health_delta:-missing} attack=${level_up_attack_delta:-missing} defense=${level_up_defense_delta:-missing}. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if [[ -z "${level_up_definition_id}" ]] \
	|| ! grep -q "\"adventurer:${level_up_definition_id}\":{.*\"level\":${level_up_new_level}" "${SESSION_DIR}/state.json"; then
	echo "Leveled adventurer state was not published with the new level. definition_id=${level_up_definition_id:-missing} level=${level_up_new_level:-missing}. Session: ${SESSION_DIR}" >&2
	exit 1
fi

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

if ! awk -v distance="${spark_distance}" -v range="${spark_range}" 'BEGIN { exit !(distance > 48 && range >= 160 && distance <= range) }'; then
	echo "Spark did not cast from medium range. distance=${spark_distance:-missing} range=${spark_range:-missing}. Session: ${SESSION_DIR}" >&2
	exit 1
fi

cast_pause_line="$(grep '"type":"adventurer_cast_movement_paused"' "${EVENTS_FILE}" | grep '"action_id":"spark"' | head -n 1 || true)"

if [[ -z "${cast_pause_line}" ]]; then
	echo "Missing adventurer movement pause during spark cast. Session: ${SESSION_DIR}" >&2
	exit 1
fi

cast_pause_distance="$(printf '%s\n' "${cast_pause_line}" | sed -n 's/.*"distance_to_monster":\([0-9.]*\).*/\1/p')"

if ! awk -v distance="${cast_pause_distance}" 'BEGIN { exit !(distance > 48 && distance <= 160) }'; then
	echo "Spark cast pause did not happen inside ranged distance. distance=${cast_pause_distance:-missing}. Session: ${SESSION_DIR}" >&2
	exit 1
fi

spark_target="$(printf '%s\n' "${spark_cast_line}" | sed -n 's/.*"target":"\([^"]*\)".*/\1/p')"
spark_adventurer="$(printf '%s\n' "${spark_cast_line}" | sed -n 's/.*"combatant":"\([^"]*\)".*/\1/p')"
spark_resolved_numbered_line="$(grep -n '"type":"combat_action_resolved"' "${EVENTS_FILE}" | grep '"combatant_kind":"adventurer"' | grep '"action_id":"spark"' | grep "\"combatant\":\"${spark_adventurer}\"" | grep "\"target\":\"${spark_target}\"" | head -n 1 || true)"
spark_resolved_line_number="$(printf '%s\n' "${spark_resolved_numbered_line}" | cut -d: -f1)"

if [[ -z "${spark_resolved_line_number}" ]]; then
	echo "Missing spark action resolution. Session: ${SESSION_DIR}" >&2
	exit 1
fi

monster_action_resolution_count="$(grep '"type":"combat_action_resolved"' "${EVENTS_FILE}" | grep -c '"combatant_kind":"monster"' || true)"

if [[ "${monster_action_resolution_count}" == "0" ]]; then
	echo "Missing monster attack resolution after ranged opener. Session: ${SESSION_DIR}" >&2
	exit 1
fi

slime_basic_queue_count="$(grep '"type":"combat_action_queued"' "${EVENTS_FILE}" | grep '"combatant_kind":"monster"' | grep -c '"action_id":"basic_attack"' || true)"
slime_basic_resolution_count="$(grep '"type":"combat_action_resolved"' "${EVENTS_FILE}" | grep '"combatant_kind":"monster"' | grep -c '"action_id":"basic_attack"' || true)"
slime_basic_attack_roll_count="$(grep '"type":"attack_roll_resolved"' "${EVENTS_FILE}" | grep '"attacker_kind":"monster"' | grep '"defender_kind":"adventurer"' | grep -c '"action_id":"basic_attack"' || true)"
slime_basic_distance_line="$(grep '"type":"combat_action_queued"' "${EVENTS_FILE}" | grep '"combatant_kind":"monster"' | grep '"action_id":"basic_attack"' | awk '/"distance_to_target":([0-9]|[1-3][0-9]|4[0-8])(\.[0-9]*)?/{ print; exit }' || true)"

if [[ "${slime_basic_queue_count}" == "0" || "${slime_basic_resolution_count}" == "0" ]]; then
	echo "Missing slime basic_attack queue/resolution evidence. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if [[ "${slime_basic_attack_roll_count}" == "0" ]]; then
	echo "Missing slime basic_attack attack-roll evidence. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if [[ -z "${slime_basic_distance_line}" ]]; then
	echo "Missing slime melee attack queued within melee distance. Session: ${SESSION_DIR}" >&2
	exit 1
fi

heavy_strike_resolution_count="$(grep '"type":"combat_action_resolved"' "${EVENTS_FILE}" | grep '"combatant_kind":"adventurer"' | grep -c '"action_id":"heavy_strike"' || true)"

if [[ "${heavy_strike_resolution_count}" == "0" ]]; then
	echo "Missing adventurer melee action after ranged opener. Session: ${SESSION_DIR}" >&2
	exit 1
fi

aggro_line="$(grep '"type":"monster_aggro_target_set"' "${EVENTS_FILE}" | grep '"aggro_trigger":"ability_resolved"' | head -n 1 || true)"

if [[ -z "${aggro_line}" ]]; then
	echo "Missing ability-resolved monster aggro target event. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if ! printf '%s\n' "${aggro_line}" | grep -q '"aggro_range":48.0'; then
	echo "Monster aggro range was not preserved as authored 48.0. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if ! printf '%s\n' "${aggro_line}" | grep -q '"desired_combat_distance":42.0'; then
	echo "Monster aggro event did not expose shared melee combat distance. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if ! grep -q '"skill_cooldowns"' "${SESSION_DIR}/state.json"; then
	echo "Missing skill cooldown state. Session: ${SESSION_DIR}" >&2
	exit 1
fi

echo "Combat main loop verification passed."
echo "Session: ${SESSION_DIR}"
