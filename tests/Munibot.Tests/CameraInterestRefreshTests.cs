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

    [Fact]
    public void AimsAtAHighCornerTargetBeforeSendingAgentUpdate()
    {
        var client = new GridClient();
        var camera = new Vector3(1.2f, 1.1f, 4003);
        var target = new Vector3(1, 1, 4000);
        var updates = 0;

        CameraInterestRefresh.AimAtAndSend(client, camera, target, () =>
        {
            Assert.Equal(camera, client.Self.Movement.Camera.Position);
            var expected = Vector3.Normalize(target - camera);
            Assert.True(Vector3.Dot(expected, client.Self.Movement.Camera.AtAxis) > 0.99f);
            updates++;
        });

        Assert.Equal(1, updates);
        var offset = CameraInterestRefresh.OffsetViewOf(target);
        Assert.InRange(offset.X, 2f, 254f);
        Assert.InRange(offset.Y, 2f, 254f);
        Assert.True(Vector3.Distance(offset, target) > 4f);
    }

    [Fact]
    public void UsesAnOffsetViewWhenAvatarAndTargetPositionsCoincide()
    {
        var client = new GridClient();
        var target = new Vector3(1, 1, 4000);
        var updates = 0;

        CameraInterestRefresh.AimAtAndSend(client, target, target, () => updates++);

        Assert.Equal(1, updates);
        Assert.Equal(CameraInterestRefresh.OffsetViewOf(target), client.Self.Movement.Camera.Position);
        Assert.True(Vector3.Dot(Vector3.Normalize(target - client.Self.Movement.Camera.Position),
            client.Self.Movement.Camera.AtAxis) > 0.99f);
    }
}
