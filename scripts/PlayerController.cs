using Godot;
using System.Globalization;
using GDict = Godot.Collections.Dictionary;

public partial class PlayerController : Node2D
{
	[Export]
	public float Speed { get; set; } = 200.0f;

	[Export]
	public float StopDistance { get; set; } = 4.0f;

	private Vector2? _targetPosition;
	private int _movementLogCountdown;
	private bool _movingEventEmitted;

	public override void _Ready()
	{
		PublishState();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mouseButton &&
			mouseButton.ButtonIndex == MouseButton.Left &&
			mouseButton.Pressed)
		{
			Vector2 worldTarget = GetViewport().GetCanvasTransform().AffineInverse() * mouseButton.Position;
			SetTarget(worldTarget, $"mouse_click viewport={mouseButton.Position}");
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_targetPosition is null)
		{
			return;
		}

		Vector2 toTarget = _targetPosition.Value - GlobalPosition;
		float distance = toTarget.Length();

		if (distance <= StopDistance)
		{
			GlobalPosition = _targetPosition.Value;
			GD.Print($"PLAYER_ARRIVED position={FormatVector(GlobalPosition)}");
			_targetPosition = null;
			_movingEventEmitted = false;
			EmitBridgeEvent("player_arrived", new GDict
			{
				{ "source", nameof(PlayerController) },
				{ "position", VectorToArray(GlobalPosition) }
			});
			PublishState();

			return;
		}

		Vector2 movement = toTarget.Normalized() * Speed * (float)delta;
		GlobalPosition += movement.Length() >= distance ? toTarget : movement;

		if (!_movingEventEmitted)
		{
			_movingEventEmitted = true;
			EmitBridgeEvent("player_moving", new GDict
			{
				{ "source", nameof(PlayerController) },
				{ "position", VectorToArray(GlobalPosition) },
				{ "target", VectorToArray(_targetPosition.Value) }
			});
		}

		if (_movementLogCountdown <= 0)
		{
			GD.Print($"PLAYER_MOVING position={FormatVector(GlobalPosition)} target={FormatVector(_targetPosition.Value)}");
			_movementLogCountdown = 10;
		}

		_movementLogCountdown--;
		PublishState();
	}

	private void SetTarget(Vector2 targetPosition, string source)
	{
		_targetPosition = targetPosition;
		_movementLogCountdown = 0;
		_movingEventEmitted = false;
		GD.Print($"PLAYER_TARGET source={source} target={FormatVector(targetPosition)}");
		EmitBridgeEvent("player_target_set", new GDict
		{
			{ "source", nameof(PlayerController) },
			{ "target", VectorToArray(targetPosition) }
		});
		PublishState();
	}

	private void EmitBridgeEvent(string type, GDict payload)
	{
		TestBridge.Instance?.EmitEvent(type, payload);
	}

	private void PublishState()
	{
		if (TestBridge.Instance?.IsActive != true)
		{
			return;
		}

		GDict state = new()
		{
			{ "source", nameof(PlayerController) },
			{ "position", VectorToArray(GlobalPosition) },
			{ "moving", _targetPosition is not null },
			{ "has_target", _targetPosition is not null }
		};

		if (_targetPosition is Vector2 target)
		{
			state["target"] = VectorToArray(target);
		}

		TestBridge.Instance.EmitState("player", state);
	}

	private static string FormatVector(Vector2 value)
	{
		return $"{value.X.ToString("0.00", CultureInfo.InvariantCulture)},{value.Y.ToString("0.00", CultureInfo.InvariantCulture)}";
	}

	private static Godot.Collections.Array VectorToArray(Vector2 value)
	{
		return new Godot.Collections.Array
		{
			value.X,
			value.Y
		};
	}
}
