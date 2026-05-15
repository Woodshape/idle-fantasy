#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT_BIN="${GODOT_BIN:-/home/woodshape/software/godot/Godot_v4.6.2-stable_mono_linux.x86_64}"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-60}"

run_scene() {
	local scene_path="$1"
	local scene_tag="$2"
	local expected_adventurers="$3"
	local expected_monsters="$4"
	local session_dir="${SESSION_DIR:-$(mktemp -d "/tmp/idle-fantasy-${scene_tag}.XXXXXX")}"
	local home_dir="${IDLE_FANTASY_TEST_HOME:-/tmp/idle-fantasy-home}"
	local xdg_dir="${IDLE_FANTASY_TEST_XDG:-/tmp/idle-fantasy-xdg}"
	local commands_file="${session_dir}/commands.jsonl"
	local events_file="${session_dir}/events.jsonl"
	local state_file="${session_dir}/state.json"

	mkdir -p "${session_dir}" "${home_dir}" "${xdg_dir}"

	HOME="${home_dir}" XDG_DATA_HOME="${xdg_dir}" "${GODOT_BIN}" \
		--headless \
		--path "${PROJECT_ROOT}" \
		"${scene_path}" \
		-- \
		--test-bridge-dir="${session_dir}" \
		--test-bridge-scene-tag="${scene_tag}" \
		> "${session_dir}/godot.log" 2>&1 &

	local godot_pid=$!

	cleanup_scene() {
		if kill -0 "${godot_pid}" 2>/dev/null; then
			kill "${godot_pid}" 2>/dev/null || true
			wait "${godot_pid}" 2>/dev/null || true
		fi
	}
	trap cleanup_scene RETURN

	local deadline=$((SECONDS + TIMEOUT_SECONDS))
	until [[ -f "${events_file}" ]] && grep -q '"type":"bridge_started"' "${events_file}"; do
		if (( SECONDS >= deadline )); then
			echo "Timed out waiting for bridge_started in ${scene_tag}. Session: ${session_dir}" >&2
			return 1
		fi
		sleep 0.1
	done

cat >> "${commands_file}" <<'JSONL'
{"id":1,"cmd":"set_time_scale","scale":6.0}
{"id":2,"cmd":"wait_for_event","event":"loot_collected","timeout_ms":40000}
{"id":3,"cmd":"quit"}
JSONL

	if ! wait "${godot_pid}"; then
		echo "Godot exited with failure in ${scene_tag}. Session: ${session_dir}" >&2
		return 1
	fi

	trap - RETURN

	python3 - "$events_file" "$state_file" "$scene_tag" "$expected_adventurers" "$expected_monsters" <<'PY'
import json
import sys
from collections import defaultdict

events_path, state_path, scene_tag, expected_adventurers, expected_monsters = sys.argv[1:6]
expected_adventurers = int(expected_adventurers)
expected_monsters = int(expected_monsters)

with open(events_path, "r", encoding="utf-8") as handle:
    events = [json.loads(line) for line in handle if line.strip()]

with open(state_path, "r", encoding="utf-8") as handle:
    state = json.load(handle)

def fail(message: str) -> None:
    raise SystemExit(f"{scene_tag}: {message}")

started = [event for event in events if event.get("type") == "combat_started"]
if len(started) != 1:
    fail(f"expected exactly one combat_started event, saw {len(started)}")

start = started[0]
if start.get("adventurer_count") != expected_adventurers:
    fail(f"expected {expected_adventurers} adventurers in the encounter, saw {start.get('adventurer_count')}")
if start.get("monster_count") != expected_monsters:
    fail(f"expected {expected_monsters} monsters in the encounter, saw {start.get('monster_count')}")

encounter_id = start.get("encounter_id")
loot = [event for event in events if event.get("type") == "loot_collected" and event.get("encounter_id") == encounter_id]
expected_loot_events = expected_adventurers * expected_monsters
if len(loot) != expected_loot_events:
    fail(f"expected {expected_loot_events} loot_collected events for split membership, saw {len(loot)}")

started_monsters = set(start.get("monsters", []))
started_adventurers = set(start.get("adventurers", []))

loot_by_monster = defaultdict(list)
for event in loot:
    loot_by_monster[event.get("monster")].append(event)
    if event.get("adventurer") not in started_adventurers:
        fail(f"unexpected reward recipient {event.get('adventurer')}")
    if event.get("recipient_count") != expected_adventurers:
        fail(f"loot event for {event.get('monster')} had recipient_count={event.get('recipient_count')}")
    if bool(event.get("split_reward")) != (expected_adventurers > 1):
        fail(f"loot event for {event.get('monster')} had incorrect split_reward flag")

if set(loot_by_monster) != started_monsters:
    fail(f"rewarded monsters {sorted(loot_by_monster)} did not match encounter monsters {sorted(started_monsters)}")

for monster_name, monster_loot in loot_by_monster.items():
    recipients = {event.get("adventurer") for event in monster_loot}
    if recipients != started_adventurers:
        fail(f"{monster_name} rewarded {sorted(recipients)} instead of {sorted(started_adventurers)}")
    total_gold = sum(int(event.get("gold", 0)) for event in monster_loot)
    total_experience = sum(int(event.get("experience", 0)) for event in monster_loot)
    reward_gold = int(monster_loot[0].get("total_gold_reward", -1))
    reward_experience = int(monster_loot[0].get("total_experience_reward", -1))
    if total_gold != reward_gold:
        fail(f"{monster_name} split gold total {total_gold} did not conserve reward {reward_gold}")
    if total_experience != reward_experience:
        fail(f"{monster_name} split XP total {total_experience} did not conserve reward {reward_experience}")

combat_state = state.get("states", {}).get("combat_encounter", {})
if combat_state.get("encounter_id") != encounter_id:
    fail("final combat_encounter state did not expose the verified encounter id")
if len(combat_state.get("adventurer_ids", [])) != expected_adventurers:
    fail("combat_encounter state did not expose the expected adventurer membership ids")
if len(combat_state.get("monster_ids", [])) != expected_monsters:
    fail("combat_encounter state did not expose the expected monster membership ids")
if len(combat_state.get("defeated_monster_ids", [])) != expected_monsters:
    fail("combat_encounter state did not expose all defeated monster ids")
if len(combat_state.get("eligible_reward_recipient_ids", [])) != expected_adventurers:
    fail("combat_encounter state did not expose all eligible reward recipients")
if len(combat_state.get("rewarded_monster_ids", [])) != expected_monsters:
    fail("combat_encounter state did not expose all rewarded monster ids")

print(f"{scene_tag} verification passed.")
PY

	echo "${scene_tag} session: ${session_dir}"
}

dotnet build "${PROJECT_ROOT}/Idle Fantasy.csproj" >/tmp/idle-fantasy-encounter-party-build.log
run_scene "res://scenes/tests/TestCombat1v2.tscn" "combat_1v2" 1 2
run_scene "res://scenes/tests/TestCombat2v1.tscn" "combat_2v1" 2 1
run_scene "res://scenes/tests/TestCombat2v2.tscn" "combat_2v2" 2 2

echo "Encounter party hardening verification passed."
