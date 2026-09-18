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
}
