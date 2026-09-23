using OpenMetaverse;
using System.Diagnostics;

namespace Munibot;

internal static class CameraInterestRefresh
{
    internal static async Task<bool> WaitForMovementCompleteAsync(Func<bool> isComplete,
        TimeSpan timeout, TimeSpan pollInterval, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        while (!isComplete())
        {
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= timeout) return false;
            await Task.Delay(pollInterval, token);
        }
        return true;
    }

    internal static void AlignAndSend(GridClient client, Vector3 avatarPosition, Action sendUpdate)
    {
        // AgentUpdate advertises this camera origin to the simulator's object interest list.
        client.Self.Movement.Camera.Position = avatarPosition;
        sendUpdate();
    }

    internal static void AimAtAndSend(GridClient client, Vector3 cameraPosition, Vector3 targetPosition,
        Action sendUpdate)
    {
        var direction = targetPosition - cameraPosition;
        if (direction.LengthSquared() < 0.01f)
        {
            cameraPosition = OffsetViewOf(targetPosition);
            direction = targetPosition - cameraPosition;
        }
        direction.Normalize();
        var up = Math.Abs(direction.Z) > 0.95f ? Vector3.UnitY : Vector3.UnitZ;
        var left = Vector3.Normalize(Vector3.Cross(up, direction));
        client.Self.Movement.Camera.Position = cameraPosition;
        client.Self.Movement.Camera.AtAxis = direction;
        client.Self.Movement.Camera.LeftAxis = left;
        client.Self.Movement.Camera.UpAxis = Vector3.Cross(direction, left);
        sendUpdate();
    }

    internal static Vector3 OffsetViewOf(Vector3 targetPosition)
        => new(Math.Clamp(targetPosition.X + 3f, 2f, 254f),
            Math.Clamp(targetPosition.Y + 3f, 2f, 254f), targetPosition.Z + 3f);
}
