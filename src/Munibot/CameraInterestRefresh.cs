using OpenMetaverse;

namespace Munibot;

internal static class CameraInterestRefresh
{
    internal static void AlignAndSend(GridClient client, Vector3 avatarPosition, Action sendUpdate)
    {
        // AgentUpdate advertises this camera origin to the simulator's object interest list.
        client.Self.Movement.Camera.Position = avatarPosition;
        sendUpdate();
    }
}
