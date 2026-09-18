using OpenMetaverse;

namespace Munibot.Tests;

public sealed class CameraInterestRefreshTests
{
    [Fact]
    public void AlignsAStaleGroundLevelCameraBeforeSendingAgentUpdate()
    {
        var client = new GridClient();
        client.Self.Movement.Camera.Position = new Vector3(128, 128, 20);
        var avatarPosition = new Vector3(253, 4, 4001);
        var updates = 0;

        CameraInterestRefresh.AlignAndSend(client, avatarPosition, () =>
        {
            Assert.Equal(avatarPosition, client.Self.Movement.Camera.Position);
            updates++;
        });

        Assert.Equal(avatarPosition, client.Self.Movement.Camera.Position);
        Assert.Equal(1, updates);
    }

    [Fact]
    public void RefreshesAgainAfterTheAvatarMovesWithinTheSameRegion()
    {
        var client = new GridClient();
        var initialPosition = new Vector3(253, 4, 4001);
        var movedPosition = new Vector3(248, 9, 4001);
        var advertisedPositions = new List<Vector3>();

        CameraInterestRefresh.AlignAndSend(client, initialPosition,
            () => advertisedPositions.Add(client.Self.Movement.Camera.Position));
        CameraInterestRefresh.AlignAndSend(client, movedPosition,
            () => advertisedPositions.Add(client.Self.Movement.Camera.Position));

        Assert.Equal([initialPosition, movedPosition], advertisedPositions);
    }

    [Fact]
    public async Task WaitsForMovementCompletionBeforeCameraRefresh()
    {
        var client = new GridClient();
        var movementComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = CameraInterestRefresh.WaitForMovementCompleteAsync(() => movementComplete.Task.IsCompleted,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5), default);

        await Task.Delay(25);
        Assert.False(wait.IsCompleted);
        Assert.NotEqual(new Vector3(253, 4, 4001), client.Self.Movement.Camera.Position);

        movementComplete.SetResult();
        Assert.True(await wait);
        CameraInterestRefresh.AlignAndSend(client, new Vector3(253, 4, 4001),
            () => Assert.Equal(new Vector3(253, 4, 4001), client.Self.Movement.Camera.Position));
    }

    [Fact]
    public async Task MovementCompletionWaitIsBounded()
    {
        var completed = await CameraInterestRefresh.WaitForMovementCompleteAsync(() => false,
            TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(5), default);

        Assert.False(completed);
    }
}
