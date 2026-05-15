#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GODOT_BIN="${GODOT_BIN:-/home/woodshape/software/godot/Godot_v4.6.2-stable_mono_linux.x86_64}"
TIMEOUT_SECONDS="${TIMEOUT_SECONDS:-45}"

run_scene() {
	local scene_path="$1"
	local scene_tag="$2"
	local expected_adventurers="$3"
	local expected_monsters="$4"
	local expected_started_count="$5"
	local session_dir="${SESSION_DIR:-$(mktemp -d "/tmp/idle-fantasy-${scene_tag}.XXXXXX")}"
	local home_dir="${IDLE_FANTASY_TEST_HOME:-/tmp/idle-fantasy-home}"
	local xdg_dir="${IDLE_FANTASY_TEST_XDG:-/tmp/idle-fantasy-xdg}"
	local commands_file="${session_dir}/commands.jsonl"
	local events_file="${session_dir}/events.jsonl"

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
{"id":1,"cmd":"set_time_scale","scale":4.0}
{"id":2,"cmd":"wait_for_event","event":"combat_action_resolved","timeout_ms":10000}
{"id":3,"cmd":"quit"}
JSONL

	if ! wait "${godot_pid}"; then
		echo "Godot exited with failure in ${scene_tag}. Session: ${session_dir}" >&2
		return 1
	fi

	trap - RETURN

	for event_type in \
		"combat_started" \
		"combat_tick_started" \
		"combat_action_queued" \
		"combat_action_order_rolled" \
		"combat_action_resolved" \
		"combat_tick_completed" \
		"bridge_stopped"; do
		if ! grep -q "\"type\":\"${event_type}\"" "${events_file}"; then
			echo "Missing required event '${event_type}' in ${scene_tag}. Session: ${session_dir}" >&2
			return 1
		fi
	done

	local started_line
	started_line="$(grep '"type":"combat_started"' "${events_file}" | head -n 1 || true)"

	if ! printf '%s\n' "${started_line}" | grep -q "\"adventurer_count\":${expected_adventurers}"; then
		echo "Unexpected adventurer count in ${scene_tag}. Expected ${expected_adventurers}. Session: ${session_dir}" >&2
		return 1
	fi

	if ! printf '%s\n' "${started_line}" | grep -q "\"monster_count\":${expected_monsters}"; then
		echo "Unexpected monster count in ${scene_tag}. Expected ${expected_monsters}. Session: ${session_dir}" >&2
		return 1
	fi

	local started_count
	started_count="$(grep -c '"type":"combat_started"' "${events_file}")"
	if [[ "${started_count}" != "${expected_started_count}" ]]; then
		echo "Expected ${expected_started_count} active encounter start(s) in ${scene_tag}, got ${started_count}. Session: ${session_dir}" >&2
		return 1
	fi

	echo "${scene_tag} verification passed. Session: ${session_dir}"
}

dotnet build "${PROJECT_ROOT}/Idle Fantasy.csproj" >/tmp/idle-fantasy-combat-architecture-build.log
run_scene "res://scenes/tests/TestCombat1v1.tscn" "combat_1v1" 1 1 1
run_scene "res://scenes/tests/TestCombat1v2.tscn" "combat_1v2" 1 2 1
run_scene "res://scenes/tests/TestCombat2v1.tscn" "combat_2v1" 2 1 1
run_scene "res://scenes/tests/TestCombat2v2.tscn" "combat_2v2" 2 2 1

echo "Combat architecture verification passed."
