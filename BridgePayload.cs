using Godot;
using GArray = Godot.Collections.Array;

public static class BridgePayload
{
	public static GArray VectorToArray(Vector2 value)
	{
		return new GArray
		{
			value.X,
			value.Y
		};
	}
}
