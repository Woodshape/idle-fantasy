#nullable enable

using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GArray = Godot.Collections.Array;
using GDict = Godot.Collections.Dictionary;

public partial class TestBridge : Node
{
	public static TestBridge? Instance { get; private set; }

	public bool IsActive { get; private set; }
	public string SceneTag => _sceneTag;

	private string _sessionDir = string.Empty;
	private string _commandsPath = string.Empty;
	private string _eventsPath = string.Empty;
	private string _statePath = string.Empty;
	private string _metaPath = string.Empty;
	private string _sceneTag = string.Empty;
	private int _pollMs = 50;
	private bool _verbose;
	private bool _quitWhenIdle;
	private double _pollAccumulatorMs;
	private double _lastActivityTs;
	private long _commandOffset;
	private long _lastProcessedCommandId;
	private string _pendingCommandFragment = string.Empty;
	private long? _activeCommandId;
	private PendingWaitCommand? _pendingWait;
	private bool _bridgeStartedEmitted;
	private readonly List<BridgeEventRecord> _eventHistory = new();
	private readonly Dictionary<string, GDict> _namedStates = new(StringComparer.Ordinal);
	private readonly Queue<string> _queuedCommandLines = new();
	private GDict? _lastSnapshot;

	public override void _EnterTree()
	{
		Instance = this;
	}

	public override void _Ready()
	{
		BridgeConfig config = ParseConfig(OS.GetCmdlineUserArgs());

		if (!config.Enabled)
		{
			return;
		}

		_pollMs = Math.Max(1, config.PollMs);
		_quitWhenIdle = config.QuitWhenIdle;
		_verbose = config.Verbose;
		_sceneTag = config.SceneTag;
		_sessionDir = config.SessionDir;
		_commandsPath = Path.Combine(_sessionDir, "commands.jsonl");
		_eventsPath = Path.Combine(_sessionDir, "events.jsonl");
		_statePath = Path.Combine(_sessionDir, "state.json");
		_metaPath = Path.Combine(_sessionDir, "meta.json");

		try
		{
			InitializeSessionFiles();
			IsActive = true;
			_lastActivityTs = GetNow();
			EmitBridgeEvent("bridge_started", new GDict
			{
				{ "session_dir", _sessionDir },
				{ "scene_tag", _sceneTag },
				{ "poll_ms", _pollMs },
				{ "quit_when_idle", _quitWhenIdle },
				{ "current_scene", GetCurrentScenePath() }
			});
			_bridgeStartedEmitted = true;
			WriteStateFile(BuildBridgeState());
		}
		catch (Exception exception)
		{
			IsActive = false;
			GD.PushError($"TestBridge failed to initialize: {exception.Message}");
		}
	}

	public override void _Process(double delta)
	{
		if (!IsActive)
		{
			return;
		}

		UpdatePendingWait();

		_pollAccumulatorMs += delta * 1000.0;

		if (_pollAccumulatorMs >= _pollMs)
		{
			_pollAccumulatorMs = 0.0;
			PollCommands();
		}

		if (_quitWhenIdle &&
			_pendingWait is null &&
			_lastProcessedCommandId > 0 &&
			GetNow() - _lastActivityTs >= Math.Max(1.0, _pollMs / 1000.0 * 4.0))
		{
			EmitBridgeEvent("bridge_idle", new GDict());
			GetTree().Quit();
		}
	}

	public override void _ExitTree()
	{
		if (_bridgeStartedEmitted)
		{
			try
			{
				EmitBridgeEvent("bridge_stopped", new GDict
				{
					{ "last_command_id", _lastProcessedCommandId }
				});
			}
			catch
			{
				// Exit should not fail because of bridge file I/O.
			}
		}

		IsActive = false;
		_activeCommandId = null;
		_pendingWait = null;
		RefreshStateFile();

		if (ReferenceEquals(Instance, this))
		{
			Instance = null;
		}
	}

	public void EmitEvent(string type, GDict payload)
	{
		if (!IsActive)
		{
			return;
		}

		string source = ExtractSource(payload, "gameplay");
		long? commandId = _activeCommandId;
		GDict eventPayload = SanitizeDictionary(payload);
		WriteEventRecord(type, source, eventPayload, commandId);
	}

	public void EmitState(string name, GDict payload)
	{
		if (!IsActive)
		{
			return;
		}

		GDict sanitized = SanitizeDictionary(payload);
		_namedStates[name] = sanitized;
		RefreshStateFile();
	}

	public bool TryGetActiveCommandId(out long commandId)
	{
		if (_activeCommandId is long value)
		{
			commandId = value;
			return true;
		}

		commandId = default;
		return false;
	}

	private void InitializeSessionFiles()
	{
		Directory.CreateDirectory(_sessionDir);

		if (!File.Exists(_commandsPath))
		{
			File.WriteAllText(_commandsPath, string.Empty);
		}

		File.WriteAllText(_eventsPath, string.Empty);
		File.WriteAllText(_statePath, "{}");
		File.WriteAllText(_metaPath, Json.Stringify(new GDict
		{
			{ "session_dir", _sessionDir },
			{ "commands_file", _commandsPath },
			{ "events_file", _eventsPath },
			{ "state_file", _statePath },
			{ "scene_tag", _sceneTag },
			{ "poll_ms", _pollMs },
			{ "pid", OS.GetProcessId() },
			{ "started_at", GetNow() }
		}));
	}

	private void PollCommands()
	{
		if (_pendingWait is not null)
		{
			return;
		}

		while (_queuedCommandLines.Count > 0 && _pendingWait is null)
		{
			ProcessCommandLine(_queuedCommandLines.Dequeue());
		}

		if (_pendingWait is not null)
		{
			return;
		}

		try
		{
			if (!File.Exists(_commandsPath))
			{
				return;
			}

			long fileLength = new FileInfo(_commandsPath).Length;

			if (fileLength < _commandOffset)
			{
				EmitBridgeError("commands_file_truncated", new GDict
				{
					{ "previous_offset", _commandOffset },
					{ "new_length", fileLength }
				});
				_commandOffset = 0;
				_pendingCommandFragment = string.Empty;
			}

			if (fileLength == _commandOffset)
			{
				return;
			}

			using FileStream stream = new(_commandsPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
			stream.Seek(_commandOffset, SeekOrigin.Begin);
			using StreamReader reader = new(stream);
			string chunk = reader.ReadToEnd();
			string combined = _pendingCommandFragment + chunk;
			int consumedChars = 0;
			int cursor = 0;

			while (cursor < combined.Length)
			{
				int newlineIndex = combined.IndexOf('\n', cursor);

				if (newlineIndex < 0)
				{
					break;
				}

				string line = combined.Substring(cursor, newlineIndex - cursor).TrimEnd('\r');
				cursor = newlineIndex + 1;
				consumedChars = cursor;

				if (string.IsNullOrWhiteSpace(line))
				{
					continue;
				}

				ProcessCommandLine(line);

				if (_pendingWait is not null)
				{
					break;
				}
			}

			if (_pendingWait is not null)
			{
				BufferQueuedLines(combined[consumedChars..]);
				_commandOffset = fileLength - GetUtf8ByteCount(_pendingCommandFragment);
			}
			else
			{
				_pendingCommandFragment = combined[consumedChars..];
				_commandOffset = fileLength - GetUtf8ByteCount(_pendingCommandFragment);
			}
		}
		catch (Exception exception)
		{
			EmitBridgeError("command_poll_failed", new GDict
			{
				{ "message", exception.Message }
			});
		}
	}

	private void BufferQueuedLines(string text)
	{
		_pendingCommandFragment = string.Empty;
		int cursor = 0;

		while (cursor < text.Length)
		{
			int newlineIndex = text.IndexOf('\n', cursor);

			if (newlineIndex < 0)
			{
				_pendingCommandFragment = text[cursor..];
				return;
			}

			string line = text.Substring(cursor, newlineIndex - cursor).TrimEnd('\r');

			if (!string.IsNullOrWhiteSpace(line))
			{
				_queuedCommandLines.Enqueue(line);
			}

			cursor = newlineIndex + 1;
		}
	}

	private void ProcessCommandLine(string line)
	{
		Json json = new();
		Error parseResult = json.Parse(line);

		if (parseResult != Error.Ok || json.Data.VariantType != Variant.Type.Dictionary)
		{
			EmitBridgeError("malformed_command", new GDict
			{
				{ "line", line },
				{ "error", parseResult.ToString() }
			});
			return;
		}

		GDict rawCommand = (GDict)json.Data;

		if (!TryGetLong(rawCommand, "id", out long commandId))
		{
			EmitBridgeError("command_missing_id", new GDict
			{
				{ "line", line }
			});
			return;
		}

		if (commandId <= _lastProcessedCommandId)
		{
			EmitBridgeError("command_id_out_of_order", new GDict
			{
				{ "command_id", commandId },
				{ "last_processed_command_id", _lastProcessedCommandId }
			});
			return;
		}

		if (!TryGetString(rawCommand, "cmd", out string commandName) || string.IsNullOrWhiteSpace(commandName))
		{
			EmitCommandFailed(commandId, "unknown", "Command is missing a cmd field.");
			_lastProcessedCommandId = commandId;
			return;
		}

		_lastActivityTs = GetNow();
		_activeCommandId = commandId;
		EmitBridgeEvent("command_received", new GDict
		{
			{ "command", commandName }
		}, commandId);

		try
		{
			if (TryBeginWaitCommand(commandId, commandName, rawCommand))
			{
				return;
			}

			DispatchImmediateCommand(commandId, commandName, rawCommand);
		}
		catch (Exception exception)
		{
			EmitCommandFailed(commandId, commandName, exception.Message);
		}
			finally
			{
				if (_pendingWait is null || _pendingWait.CommandId != commandId)
				{
					_activeCommandId = null;
			}

				if (_pendingWait is null)
				{
					_lastProcessedCommandId = commandId;
				}

				RefreshStateFile();
			}
		}

	private bool TryBeginWaitCommand(long commandId, string commandName, GDict command)
	{
		switch (commandName)
		{
			case "wait_for_event":
			{
				if (!TryGetString(command, "event", out string eventType) || string.IsNullOrWhiteSpace(eventType))
				{
					throw new InvalidOperationException("wait_for_event requires an event field.");
				}

				int timeoutMs = GetTimeoutMs(command);
					_pendingWait = new PendingWaitCommand
				{
					CommandId = commandId,
					CommandName = commandName,
					TimeoutMs = timeoutMs,
					DeadlineTs = GetNow() + timeoutMs / 1000.0,
					EventType = eventType,
					EventStartIndex = _eventHistory.Count
				};
				return true;
			}
			case "wait_for_state":
			{
				if (!TryGetString(command, "name", out string stateName) || string.IsNullOrWhiteSpace(stateName))
				{
					throw new InvalidOperationException("wait_for_state requires a name field.");
				}

				int timeoutMs = GetTimeoutMs(command);
				command.TryGetValue("equals", out Variant expectedValue);
				string path = TryGetString(command, "path", out string explicitPath) ? explicitPath : string.Empty;
				bool exists = !TryGetBool(command, "exists", out bool parsedExists) || parsedExists;
				_pendingWait = new PendingWaitCommand
				{
					CommandId = commandId,
					CommandName = commandName,
					TimeoutMs = timeoutMs,
					DeadlineTs = GetNow() + timeoutMs / 1000.0,
					StateName = stateName,
					StatePath = path,
					StateExists = exists,
					StateExpectedValue = expectedValue
				};
				return true;
			}
			default:
				return false;
		}
	}

	private void DispatchImmediateCommand(long commandId, string commandName, GDict command)
	{
		switch (commandName)
		{
			case "ping":
				EmitCommandCompleted(commandId, commandName, new GDict
				{
					{ "pong", true }
				});
				break;
			case "quit":
				EmitCommandCompleted(commandId, commandName, new GDict());
				GetTree().Quit();
				break;
			case "snapshot":
			{
				GDict snapshot = BuildSnapshot(GetRequestedNodePaths(command));
				_lastSnapshot = snapshot;
				RefreshStateFile();
				WriteEventRecord("snapshot", "TestBridge", snapshot, commandId);
				EmitCommandCompleted(commandId, commandName, new GDict
				{
					{ "state_file", _statePath }
				});
				break;
			}
			case "set_time_scale":
			{
				if (!TryGetDouble(command, "scale", out double scale) &&
					!TryGetDouble(command, "value", out scale))
				{
					throw new InvalidOperationException("set_time_scale requires a scale field.");
				}

				Engine.TimeScale = (float)scale;
				EmitCommandCompleted(commandId, commandName, new GDict
				{
					{ "time_scale", Engine.TimeScale }
				});
				break;
			}
			case "click_viewport":
			{
				Vector2 viewportPosition = GetCommandVector2(command);
				InjectViewportClick(viewportPosition);
				EmitCommandCompleted(commandId, commandName, new GDict
				{
					{ "viewport_position", VectorToArray(viewportPosition) }
				});
				break;
			}
			case "click_world":
			{
				Vector2 worldPosition = GetCommandVector2(command);
				Vector2 viewportPosition = GetViewport().GetCanvasTransform() * worldPosition;
				InjectViewportClick(viewportPosition);
				EmitCommandCompleted(commandId, commandName, new GDict
				{
					{ "world_position", VectorToArray(worldPosition) },
					{ "viewport_position", VectorToArray(viewportPosition) }
				});
				break;
			}
			case "click_node":
			{
				if (!TryGetString(command, "path", out string nodePath) || string.IsNullOrWhiteSpace(nodePath))
				{
					throw new InvalidOperationException("click_node requires a path field.");
				}

				Node node = ResolveNode(nodePath) ?? throw new InvalidOperationException($"Node not found: {nodePath}");

				if (node is Control control)
				{
					Vector2 controlPosition = control.GetGlobalRect().GetCenter();
					if (control is BaseButton button)
					{
						if (!button.Disabled)
						{
							button.EmitSignal(BaseButton.SignalName.Pressed);
						}
					}
					else
					{
						InjectViewportClick(controlPosition);
					}
					EmitCommandCompleted(commandId, commandName, new GDict
					{
						{ "path", nodePath },
						{ "viewport_position", VectorToArray(controlPosition) }
					});
					return;
				}

				if (node is Node2D node2D)
				{
					Vector2 worldPosition = node2D.GlobalPosition;
					Vector2 viewportPosition = GetViewport().GetCanvasTransform() * worldPosition;
					InjectViewportClick(viewportPosition);
					EmitCommandCompleted(commandId, commandName, new GDict
					{
						{ "path", nodePath },
						{ "world_position", VectorToArray(worldPosition) },
						{ "viewport_position", VectorToArray(viewportPosition) }
					});
					return;
				}

				throw new InvalidOperationException($"Node is not clickable: {nodePath}");
			}
			case "damage_adventurer":
			{
				if (!TryGetLong(command, "amount", out long rawAmount))
				{
					throw new InvalidOperationException("damage_adventurer requires an amount field.");
				}

				string requestedName = TryGetString(command, "name", out string parsedName) ? parsedName : string.Empty;
				Adventurer adventurer = FindAdventurer(requestedName)
					?? throw new InvalidOperationException($"Adventurer not found: {requestedName}");
				int healthBefore = adventurer.Health;
				int damage = adventurer.ApplyDamage((int)Math.Max(0L, rawAmount));
				EmitCommandCompleted(commandId, commandName, new GDict
				{
					{ "adventurer", adventurer.AdventurerName },
					{ "requested_damage", rawAmount },
					{ "damage", damage },
					{ "health_before", healthBefore },
					{ "health_after", adventurer.Health },
					{ "max_health", adventurer.Stats.MaxHealth },
					{ "gold", adventurer.Gold }
				});
				break;
			}
			case "hire_adventurer":
			{
				string definitionId = TryGetString(command, "definition_id", out string parsedDefinitionId)
					? parsedDefinitionId
					: string.Empty;
				GameController game = GetTree().CurrentScene as GameController
					?? throw new InvalidOperationException("hire_adventurer requires a GameController current scene.");
				GDict result = game.RequestHireAdventurer(definitionId);
				EmitCommandCompleted(commandId, commandName, result);
				break;
			}
			case "set_gold":
			{
				if (!TryGetLong(command, "amount", out long rawAmount) &&
					!TryGetLong(command, "gold", out rawAmount) &&
					!TryGetLong(command, "value", out rawAmount))
				{
					throw new InvalidOperationException("set_gold requires an amount field.");
				}

				GameController game = GetTree().CurrentScene as GameController
					?? throw new InvalidOperationException("set_gold requires a GameController current scene.");
				GDict result = game.SetPlayerGold((int)Math.Max(0L, rawAmount));
				EmitCommandCompleted(commandId, commandName, result);
				break;
			}
			default:
				throw new InvalidOperationException($"Unsupported command: {commandName}");
		}
	}

	private void UpdatePendingWait()
	{
		if (_pendingWait is null)
		{
			return;
		}

		_activeCommandId = _pendingWait.CommandId;

		switch (_pendingWait.CommandName)
		{
			case "wait_for_event":
				UpdateWaitForEvent();
				break;
			case "wait_for_state":
				UpdateWaitForState();
				break;
		}
	}

	private void UpdateWaitForEvent()
	{
		if (_pendingWait is null)
		{
			return;
		}

		BridgeEventRecord? matchedEvent = _eventHistory
			.Skip(_pendingWait.EventStartIndex)
			.FirstOrDefault(record => string.Equals(record.Type, _pendingWait.EventType, StringComparison.Ordinal));

		if (matchedEvent is not null)
		{
			long commandId = _pendingWait.CommandId;
			string commandName = _pendingWait.CommandName;
			string matchedType = matchedEvent.Type;
				_pendingWait = null;
				_lastProcessedCommandId = commandId;
				_activeCommandId = null;
				RefreshStateFile();
				EmitCommandCompleted(commandId, commandName, new GDict
				{
					{ "matched_event", matchedType }
			});
			PollCommands();
			return;
		}

		if (GetNow() >= _pendingWait.DeadlineTs)
		{
			long commandId = _pendingWait.CommandId;
			string commandName = _pendingWait.CommandName;
			string eventType = _pendingWait.EventType;
				_pendingWait = null;
				_lastProcessedCommandId = commandId;
				_activeCommandId = null;
				RefreshStateFile();
				EmitCommandFailed(commandId, commandName, $"Timed out waiting for event '{eventType}'.");
				PollCommands();
		}
	}

	private void UpdateWaitForState()
	{
		if (_pendingWait is null)
		{
			return;
		}

		bool matched = TryMatchState(_pendingWait, out GDict details);

		if (matched)
		{
			long commandId = _pendingWait.CommandId;
			string commandName = _pendingWait.CommandName;
				_pendingWait = null;
				_lastProcessedCommandId = commandId;
				_activeCommandId = null;
				RefreshStateFile();
				EmitCommandCompleted(commandId, commandName, details);
				PollCommands();
			return;
		}

		if (GetNow() >= _pendingWait.DeadlineTs)
		{
			long commandId = _pendingWait.CommandId;
			string commandName = _pendingWait.CommandName;
			string stateName = _pendingWait.StateName;
				_pendingWait = null;
				_lastProcessedCommandId = commandId;
				_activeCommandId = null;
				RefreshStateFile();
				EmitCommandFailed(commandId, commandName, $"Timed out waiting for state '{stateName}'.");
				PollCommands();
		}
	}

	private bool TryMatchState(PendingWaitCommand wait, out GDict details)
	{
		details = new GDict
		{
			{ "name", wait.StateName }
		};

		if (!_namedStates.TryGetValue(wait.StateName, out GDict? state))
		{
			return !wait.StateExists;
		}

		Variant currentValue = state;

		if (!string.IsNullOrEmpty(wait.StatePath))
		{
			if (!TryGetVariantAtPath(currentValue, wait.StatePath, out currentValue))
			{
				return !wait.StateExists;
			}
		}

		if (wait.StateExpectedValue.VariantType == Variant.Type.Nil)
		{
			details["value"] = currentValue;
			return true;
		}

		bool equals = VariantDeepEquals(currentValue, wait.StateExpectedValue);

		if (equals == wait.StateExists)
		{
			details["value"] = currentValue;
			return true;
		}

		return false;
	}

	private void EmitBridgeEvent(string type, GDict payload, long? commandId = null)
	{
		WriteEventRecord(type, "TestBridge", payload, commandId);
	}

	private void EmitBridgeError(string errorType, GDict payload)
	{
		GDict errorPayload = SanitizeDictionary(payload);
		errorPayload["error_type"] = errorType;
		WriteEventRecord("bridge_error", "TestBridge", errorPayload, _activeCommandId);
	}

	private void EmitCommandCompleted(long commandId, string commandName, GDict payload)
	{
		GDict completedPayload = SanitizeDictionary(payload);
		completedPayload["command"] = commandName;
		WriteEventRecord("command_completed", "TestBridge", completedPayload, commandId);
	}

	private void EmitCommandFailed(long commandId, string commandName, string message)
	{
		WriteEventRecord("command_failed", "TestBridge", new GDict
		{
			{ "command", commandName },
			{ "message", message }
		}, commandId);
	}

	private void WriteEventRecord(string type, string source, GDict payload, long? commandId)
	{
		GDict eventData = new()
		{
			{ "ts", GetNow() },
			{ "frame", GetFrameNumber() },
			{ "type", type },
			{ "source", source }
		};

		if (!string.IsNullOrEmpty(_sceneTag))
		{
			eventData["scene_tag"] = _sceneTag;
		}

		if (commandId is long value)
		{
			eventData["cmd_id"] = value;
		}

		foreach (Variant key in payload.Keys)
		{
			eventData[key] = payload[key];
		}

			File.AppendAllText(_eventsPath, Json.Stringify(eventData) + System.Environment.NewLine);
		_eventHistory.Add(new BridgeEventRecord
		{
			Type = type,
			Source = source,
			CommandId = commandId,
			Payload = eventData
		});

		if (_verbose)
		{
			GD.Print($"TEST_BRIDGE_EVENT type={type} source={source}");
		}
	}

	private void WriteStateFile(GDict state)
	{
		File.WriteAllText(_statePath, Json.Stringify(state));
	}

	private void RefreshStateFile()
	{
		if (!_bridgeStartedEmitted && !IsActive)
		{
			return;
		}

		WriteStateFile(BuildBridgeState(_lastSnapshot));
	}

	private GDict BuildBridgeState(GDict? latestSnapshot = null)
	{
		GDict states = new();

		foreach ((string key, GDict value) in _namedStates)
		{
			states[key] = value;
		}

		GDict bridge = new()
		{
			{ "active", IsActive },
			{ "scene_tag", _sceneTag },
			{ "frame", GetFrameNumber() },
			{ "time_scale", Engine.TimeScale },
			{ "current_scene", GetCurrentScenePath() },
			{ "last_processed_command_id", _lastProcessedCommandId }
		};

		if (_activeCommandId is long commandId)
		{
			bridge["active_command_id"] = commandId;
		}

		if (_pendingWait is not null)
		{
			bridge["pending_wait"] = new GDict
			{
				{ "cmd_id", _pendingWait.CommandId },
				{ "command", _pendingWait.CommandName }
			};
		}

		if (latestSnapshot is not null)
		{
			bridge["snapshot"] = latestSnapshot;
		}

		return new GDict
		{
			{ "bridge", bridge },
			{ "states", states }
		};
	}

	private GDict BuildSnapshot(GArray requestedPaths)
	{
		GDict snapshot = new()
		{
			{ "scene", GetCurrentScenePath() },
			{ "frame", GetFrameNumber() },
			{ "time_scale", Engine.TimeScale }
		};

		Camera2D? camera = GetViewport().GetCamera2D();

		if (camera is not null)
		{
			snapshot["camera"] = camera.GetPath().ToString();
		}

		GArray nodes = new();

		foreach (Variant pathVariant in requestedPaths)
		{
			string path = pathVariant.AsString();
			Node? node = ResolveNode(path);
			GDict entry = new()
			{
				{ "path", path },
				{ "found", node is not null }
			};

			if (node is not null)
			{
				entry["type"] = node.GetType().Name;
				entry["state"] = ExtractNodeState(node);
			}

			nodes.Add(entry);
		}

		if (nodes.Count > 0)
		{
			snapshot["nodes"] = nodes;
		}

		return snapshot;
	}

	private GDict ExtractNodeState(Node node)
	{
		GDict state = new()
		{
			{ "name", node.Name.ToString() },
			{ "path", node.GetPath().ToString() }
		};

		if (node is CanvasItem canvasItem)
		{
			state["visible"] = canvasItem.Visible;
		}

		if (node is Node2D node2D)
		{
			state["global_position"] = VectorToArray(node2D.GlobalPosition);
			state["rotation"] = node2D.GlobalRotation;
		}

		if (node is CharacterBody2D characterBody2D)
		{
			state["velocity"] = VectorToArray(characterBody2D.Velocity);
		}

		if (node is Area2D area2D)
		{
			state["monitoring"] = area2D.Monitoring;
			state["monitorable"] = area2D.Monitorable;
		}

		return state;
	}

	private void InjectViewportClick(Vector2 position)
	{
		Input.ParseInputEvent(new InputEventMouseButton
		{
			ButtonIndex = MouseButton.Left,
			Pressed = true,
			Position = position,
			GlobalPosition = position
		});
		Input.ParseInputEvent(new InputEventMouseButton
		{
			ButtonIndex = MouseButton.Left,
			Pressed = false,
			Position = position,
			GlobalPosition = position
		});
	}

	private Node? ResolveNode(string rawPath)
	{
		if (string.IsNullOrWhiteSpace(rawPath))
		{
			return null;
		}

		string absolutePath = rawPath switch
		{
			_ when rawPath.StartsWith("/root", StringComparison.Ordinal) => rawPath,
			_ when rawPath.StartsWith("root/", StringComparison.Ordinal) => $"/root/{rawPath["root/".Length..]}",
			_ when rawPath.StartsWith("/", StringComparison.Ordinal) => rawPath,
			_ => string.Empty
		};

		if (!string.IsNullOrEmpty(absolutePath))
		{
			Node? absoluteNode = GetNodeOrNull(absolutePath);

			if (absoluteNode is not null)
			{
				return absoluteNode;
			}
		}

		Node? currentScene = GetTree().CurrentScene;

		if (currentScene is null)
		{
			return null;
		}

		return currentScene.GetNodeOrNull(rawPath);
	}

	private Adventurer? FindAdventurer(string requestedName)
	{
		if (GetTree().CurrentScene is not GameController game)
		{
			return null;
		}

		if (string.IsNullOrWhiteSpace(requestedName))
		{
			return game.Adventurers.FirstOrDefault();
		}

		return game.Adventurers.FirstOrDefault(adventurer =>
			string.Equals(adventurer.AdventurerName, requestedName, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(adventurer.Name, requestedName, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(adventurer.DefinitionId, requestedName, StringComparison.OrdinalIgnoreCase));
	}

	private static string ExtractSource(GDict payload, string fallback)
	{
		if (payload.ContainsKey("source"))
		{
			string source = payload["source"].AsString();
			payload.Remove("source");
			return string.IsNullOrWhiteSpace(source) ? fallback : source;
		}

		return fallback;
	}

	private static GDict SanitizeDictionary(GDict payload)
	{
		GDict result = new();

		foreach (Variant key in payload.Keys)
		{
			result[key] = SanitizeVariant(payload[key]);
		}

		return result;
	}

	private static Variant SanitizeVariant(Variant value)
	{
		return value.VariantType switch
		{
			Variant.Type.Vector2 => VectorToArray(value.AsVector2()),
			Variant.Type.Dictionary => SanitizeDictionary((GDict)value),
			Variant.Type.Array => SanitizeArray((GArray)value),
			_ => value
		};
	}

	private static GArray SanitizeArray(GArray values)
	{
		GArray result = new();

		foreach (Variant value in values)
		{
			result.Add(SanitizeVariant(value));
		}

		return result;
	}

	private static GArray VectorToArray(Vector2 value)
	{
		return new GArray
		{
			value.X,
			value.Y
		};
	}

	private static Vector2 GetCommandVector2(GDict command)
	{
		if (!TryGetDouble(command, "x", out double x) || !TryGetDouble(command, "y", out double y))
		{
			throw new InvalidOperationException("Command requires numeric x and y fields.");
		}

		return new Vector2((float)x, (float)y);
	}

	private static GArray GetRequestedNodePaths(GDict command)
	{
		GArray paths = new();

		if (!command.TryGetValue("paths", out Variant pathVariant) || pathVariant.VariantType != Variant.Type.Array)
		{
			return paths;
		}

		foreach (Variant item in (GArray)pathVariant)
		{
			paths.Add(item.AsString());
		}

		return paths;
	}

	private static bool TryGetVariantAtPath(Variant root, string path, out Variant value)
	{
		value = root;

		foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
		{
			if (value.VariantType != Variant.Type.Dictionary)
			{
				return false;
			}

			GDict dictionary = (GDict)value;

			if (!dictionary.ContainsKey(segment))
			{
				return false;
			}

			value = dictionary[segment];
		}

		return true;
	}

	private static bool VariantDeepEquals(Variant left, Variant right)
	{
		return Json.Stringify(SanitizeVariant(left)) == Json.Stringify(SanitizeVariant(right));
	}

	private static int GetUtf8ByteCount(string text)
	{
		return System.Text.Encoding.UTF8.GetByteCount(text);
	}

	private static int GetTimeoutMs(GDict command)
	{
		return TryGetLong(command, "timeout_ms", out long timeoutMs)
			? (int)Math.Max(1L, timeoutMs)
			: 5000;
	}

	private static bool TryGetString(GDict dictionary, string key, out string value)
	{
		if (dictionary.TryGetValue(key, out Variant raw))
		{
			value = raw.AsString();
			return true;
		}

		value = string.Empty;
		return false;
	}

	private static bool TryGetBool(GDict dictionary, string key, out bool value)
	{
		if (!dictionary.TryGetValue(key, out Variant raw))
		{
			value = false;
			return false;
		}

		switch (raw.VariantType)
		{
			case Variant.Type.Bool:
				value = raw.AsBool();
				return true;
			case Variant.Type.String:
				return bool.TryParse(raw.AsString(), out value);
			default:
				value = false;
				return false;
		}
	}

	private static bool TryGetLong(GDict dictionary, string key, out long value)
	{
		if (!dictionary.TryGetValue(key, out Variant raw))
		{
			value = default;
			return false;
		}

		switch (raw.VariantType)
		{
			case Variant.Type.Int:
				value = raw.AsInt64();
				return true;
			case Variant.Type.Float:
				value = (long)raw.AsDouble();
				return true;
			case Variant.Type.String:
				return long.TryParse(raw.AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
			default:
				value = default;
				return false;
		}
	}

	private static bool TryGetDouble(GDict dictionary, string key, out double value)
	{
		if (!dictionary.TryGetValue(key, out Variant raw))
		{
			value = default;
			return false;
		}

		switch (raw.VariantType)
		{
			case Variant.Type.Int:
				value = raw.AsInt64();
				return true;
			case Variant.Type.Float:
				value = raw.AsDouble();
				return true;
			case Variant.Type.String:
				return double.TryParse(raw.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
			default:
				value = default;
				return false;
		}
	}

	private static string GetCurrentScenePath()
	{
		return Instance?.GetTree().CurrentScene?.SceneFilePath ?? string.Empty;
	}

	private static double GetNow()
	{
		return Time.GetUnixTimeFromSystem();
	}

	private long GetFrameNumber()
	{
		return GetTree().GetFrame();
	}

	private static BridgeConfig ParseConfig(string[] args)
	{
		BridgeConfig config = new();

		foreach (string arg in args)
		{
			if (arg.StartsWith("--test-bridge-dir=", StringComparison.Ordinal))
			{
				config.Enabled = true;
				config.SessionDir = arg["--test-bridge-dir=".Length..];
			}
			else if (arg.StartsWith("--test-bridge-poll-ms=", StringComparison.Ordinal))
			{
				string rawValue = arg["--test-bridge-poll-ms=".Length..];

				if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pollMs))
				{
					config.PollMs = pollMs;
				}
			}
			else if (arg == "--test-bridge-quit-when-idle")
			{
				config.QuitWhenIdle = true;
			}
			else if (arg == "--test-bridge-verbose")
			{
				config.Verbose = true;
			}
			else if (arg.StartsWith("--test-bridge-scene-tag=", StringComparison.Ordinal))
			{
				config.SceneTag = arg["--test-bridge-scene-tag=".Length..];
			}
		}

		if (!string.IsNullOrWhiteSpace(config.SessionDir))
		{
			config.Enabled = true;
		}

		return config;
	}

	private sealed class BridgeConfig
	{
		public bool Enabled { get; set; }

		public string SessionDir { get; set; } = string.Empty;

		public int PollMs { get; set; } = 50;

		public bool QuitWhenIdle { get; set; }

		public bool Verbose { get; set; }

		public string SceneTag { get; set; } = string.Empty;
	}

	private sealed class PendingWaitCommand
	{
		public long CommandId { get; set; }

		public string CommandName { get; set; } = string.Empty;

		public int TimeoutMs { get; set; }

		public double DeadlineTs { get; set; }

		public string EventType { get; set; } = string.Empty;

		public int EventStartIndex { get; set; }

		public string StateName { get; set; } = string.Empty;

		public string StatePath { get; set; } = string.Empty;

		public bool StateExists { get; set; } = true;

		public Variant StateExpectedValue { get; set; }
	}

	private sealed class BridgeEventRecord
	{
		public string Type { get; set; } = string.Empty;

		public string Source { get; set; } = string.Empty;

		public long? CommandId { get; set; }

		public GDict Payload { get; set; } = new();
	}
}
