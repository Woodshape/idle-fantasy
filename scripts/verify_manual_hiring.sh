#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT_BIN="${GODOT_BIN:-/home/woodshape/software/godot/Godot_v4.6.2-stable_mono_linux.x86_64}"
SESSION_DIR="${SESSION_DIR:-$(mktemp -d /tmp/idle-fantasy-hiring.XXXXXX)}"
HOME_DIR="${IDLE_FANTASY_TEST_HOME:-/tmp/idle-fantasy-home}"
XDG_DIR="${IDLE_FANTASY_TEST_XDG:-/tmp/idle-fantasy-xdg}"
COMMANDS_FILE="${SESSION_DIR}/commands.jsonl"
EVENTS_FILE="${SESSION_DIR}/events.jsonl"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-20}"

mkdir -p "${SESSION_DIR}" "${HOME_DIR}" "${XDG_DIR}"

HOME="${HOME_DIR}" XDG_DATA_HOME="${XDG_DIR}" "${GODOT_BIN}" \
	--headless \
	--path "${PROJECT_ROOT}" \
	-- \
	--test-bridge-dir="${SESSION_DIR}" \
	--test-bridge-scene-tag=manual_hiring \
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
{"id":2,"cmd":"set_gold","amount":60}
{"id":3,"cmd":"click_node","path":"Hud/Panel/VBoxContainer/HiringRow/HireAdventurerButton"}
{"id":4,"cmd":"click_node","path":"Hud/Panel/VBoxContainer/HiringRow/HireAdventurerButton"}
{"id":5,"cmd":"hire_adventurer"}
{"id":6,"cmd":"quit"}
JSONL

if ! wait "${GODOT_PID}"; then
	echo "Godot exited with failure. Session: ${SESSION_DIR}" >&2
	exit 1
fi
trap - EXIT

if grep -q '"type":"command_failed"' "${EVENTS_FILE}"; then
	echo "A bridge command failed during hiring verification. Session: ${SESSION_DIR}" >&2
	exit 1
fi

required_events=(
	"player_gold_set"
	"hire_requested"
	"adventurer_hired"
	"gold_spent"
	"hire_failed"
	"bridge_stopped"
)

for event_type in "${required_events[@]}"; do
	if ! grep -q "\"type\":\"${event_type}\"" "${EVENTS_FILE}"; then
		echo "Missing required event '${event_type}'. Session: ${SESSION_DIR}" >&2
		exit 1
	fi
done

gold_set_line="$(grep '"type":"player_gold_set"' "${EVENTS_FILE}" | head -n 1 || true)"

if [[ -z "${gold_set_line}" ]] \
	|| ! printf '%s\n' "${gold_set_line}" | grep -q '"gold_after":60'; then
	echo "set_gold did not set player gold to 60. Session: ${SESSION_DIR}" >&2
	exit 1
fi

hire_requested_count="$(grep -c '"type":"hire_requested"' "${EVENTS_FILE}" || true)"

if (( hire_requested_count != 3 )); then
	echo "Expected 3 hire requests; saw ${hire_requested_count}. Session: ${SESSION_DIR}" >&2
	exit 1
fi

hired_count="$(grep -c '"type":"adventurer_hired"' "${EVENTS_FILE}" || true)"
first_hired_line="$(grep '"type":"adventurer_hired"' "${EVENTS_FILE}" | sed -n '1p' || true)"
second_hired_line="$(grep '"type":"adventurer_hired"' "${EVENTS_FILE}" | sed -n '2p' || true)"
first_gold_spent_line="$(grep '"type":"gold_spent"' "${EVENTS_FILE}" | grep '"service":"hire_adventurer"' | sed -n '1p' || true)"
second_gold_spent_line="$(grep '"type":"gold_spent"' "${EVENTS_FILE}" | grep '"service":"hire_adventurer"' | sed -n '2p' || true)"
failed_line="$(grep '"type":"hire_failed"' "${EVENTS_FILE}" | head -n 1 || true)"

if (( hired_count != 2 )); then
	echo "Expected 2 successful hires from the HUD button; saw ${hired_count}. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if [[ -z "${first_hired_line}" ]] \
	|| ! printf '%s\n' "${first_hired_line}" | grep -Eq '"definition_id":"(warrior|mage)"' \
	|| ! printf '%s\n' "${first_hired_line}" | grep -Eq '"adventurer":"(Warrior|Mage) Hire 1"' \
	|| ! printf '%s\n' "${first_hired_line}" | grep -q '"cost":20' \
	|| ! printf '%s\n' "${first_hired_line}" | grep -q '"next_hire_cost":40' \
	|| ! printf '%s\n' "${first_hired_line}" | grep -q '"adventurer_count":3'; then
	echo "First HUD hire did not expose a random hired adventurer, cost 20, next cost 40, and count 3. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if [[ -z "${second_hired_line}" ]] \
	|| ! printf '%s\n' "${second_hired_line}" | grep -Eq '"definition_id":"(warrior|mage)"' \
	|| ! printf '%s\n' "${second_hired_line}" | grep -Eq '"adventurer":"(Warrior|Mage) Hire 2"' \
	|| ! printf '%s\n' "${second_hired_line}" | grep -q '"cost":40' \
	|| ! printf '%s\n' "${second_hired_line}" | grep -q '"next_hire_cost":80' \
	|| ! printf '%s\n' "${second_hired_line}" | grep -q '"adventurer_count":4'; then
	echo "Second HUD hire did not expose a random hired adventurer, cost 40, next cost 80, and count 4. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if [[ -z "${first_gold_spent_line}" ]] \
	|| ! printf '%s\n' "${first_gold_spent_line}" | grep -q '"spender_kind":"player"' \
	|| ! printf '%s\n' "${first_gold_spent_line}" | grep -q '"amount":20' \
	|| ! printf '%s\n' "${first_gold_spent_line}" | grep -q '"gold_before":60' \
	|| ! printf '%s\n' "${first_gold_spent_line}" | grep -q '"gold_after":40'; then
	echo "First hire gold spend did not debit player gold from 60 to 40. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if [[ -z "${second_gold_spent_line}" ]] \
	|| ! printf '%s\n' "${second_gold_spent_line}" | grep -q '"spender_kind":"player"' \
	|| ! printf '%s\n' "${second_gold_spent_line}" | grep -q '"amount":40' \
	|| ! printf '%s\n' "${second_gold_spent_line}" | grep -q '"gold_before":40' \
	|| ! printf '%s\n' "${second_gold_spent_line}" | grep -q '"gold_after":0'; then
	echo "Second hire gold spend did not debit player gold from 40 to 0. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if [[ -z "${failed_line}" ]] \
	|| ! printf '%s\n' "${failed_line}" | grep -q '"reason":"insufficient_gold"' \
	|| ! printf '%s\n' "${failed_line}" | grep -q '"cost":80' \
	|| ! printf '%s\n' "${failed_line}" | grep -q '"adventurer_count":4'; then
	echo "Broke random hire attempt did not fail at cost 80 without spawning. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if ! grep -q '"adventurer_count":4' "${SESSION_DIR}/state.json" \
	|| ! grep -q '"living_adventurers":4' "${SESSION_DIR}/state.json" \
	|| ! grep -q '"player_gold":0' "${SESSION_DIR}/state.json" \
	|| ! grep -q '"hire_cost":80' "${SESSION_DIR}/state.json"; then
	echo "Final game_loop state does not show two hires, four living adventurers, spent player gold, and next cost 80. Session: ${SESSION_DIR}" >&2
	exit 1
fi

if ! grep -Eq '"adventurer:(Warrior|Mage) Hire 1"' "${SESSION_DIR}/state.json" \
	|| ! grep -Eq '"adventurer:(Warrior|Mage) Hire 2"' "${SESSION_DIR}/state.json"; then
	echo "Hired adventurer states were not published. Session: ${SESSION_DIR}" >&2
	exit 1
fi

echo "Manual hiring verification passed."
echo "Session: ${SESSION_DIR}"
