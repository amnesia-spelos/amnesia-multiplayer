using System.Buffers.Binary;
using System.Text;
using Multimnesia.Contracts;

namespace Multimnesia.Tests;

public sealed class LanProtocolTests
{
    [Fact]
    public async Task Messages_are_length_prefixed_UTF8_JSON_and_round_trip()
    {
        await using var stream = new MemoryStream();
        var message = new LanMessage.JoinRequest(LanProtocol.CurrentVersion, Guid.Parse("11111111-1111-1111-1111-111111111111"));

        await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);

        var bytes = stream.ToArray();
        Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32BigEndian(bytes));
        Assert.StartsWith("{\"type\":\"join-request\"", Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4));
        stream.Position = 0;
        Assert.Equal(message, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Oversized_frames_are_rejected_before_the_payload_is_read()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, LanProtocol.MaximumFrameBytes + 1);
        await using var stream = new MemoryStream(header);

        var error = await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("LAN message exceeds the maximum frame size.", error.Message);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task Unknown_message_types_are_rejected()
    {
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"surprise\"}");
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        payload.CopyTo(bytes.AsSpan(4));
        await using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Missing_required_fields_are_reported_as_malformed_protocol_input()
    {
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"join-request\"}");
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        payload.CopyTo(bytes.AsSpan(4));
        await using var stream = new MemoryStream(bytes);

        var error = await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("Malformed LAN message.", error.Message);
    }

    [Fact]
    public async Task Chat_limits_count_Unicode_scalars_and_preserve_the_entry_whole()
    {
        var message = new LanMessage.ChatEntry(string.Concat(Enumerable.Repeat("👩‍🚀", 10)) + "ab", new string('m', 255) + "👋");
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(message, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    public static TheoryData<string, string> InvalidChat => new()
    {
        { new string('a', 33), "hello" },
        { "Alice", new string('m', 257) },
        { "SYSTEM", "forged feedback" },
        { "Alice", "/leave" },
        { "Alice", "line\nbreak" },
    };

    [Theory]
    [MemberData(nameof(InvalidChat))]
    public async Task Invalid_chat_is_rejected_whole_at_the_LAN_boundary(string author, string message)
    {
        await using var stream = new MemoryStream();

        var error = await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.WriteAsync(stream, new LanMessage.ChatEntry(author, message), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("Invalid Chat Entry.", error.Message);
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public void Protocol_version_is_4_for_the_raised_lantern_in_the_Shared_Pose()
    {
        Assert.Equal(4, LanProtocol.CurrentVersion);
    }

    public static TheoryData<LanMessage.Pose> Poses => new()
    {
        new LanMessage.Pose(123456, 3, 1.25, -2.5, 3.75, 90, -45, true, false, "custom_stories/My Story: Part 2/maps/cellar one.map"),
        new LanMessage.Pose(ulong.MaxValue, uint.MaxValue, 0, 0, 0, 0, 0, false, true, "maps/a.map "),
        new LanMessage.Pose(0, 0, -LanProtocol.MaximumPoseMagnitude, LanProtocol.MaximumPoseMagnitude, 0.1, -179.9999, 89.5, false, false,
            "custom_stories/Příběh 👻/maps/" + new string('m', LanProtocol.MaximumPoseMapScalars - 29)),
    };

    [Theory]
    [MemberData(nameof(Poses))]
    public async Task Poses_round_trip(LanMessage.Pose pose)
    {
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream, pose, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(pose, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Poses_use_their_wire_names()
    {
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream,
            new LanMessage.Pose(1000, 2, 1.5, -2, 3, 90, -45, true, false, "maps/a.map"), TestContext.Current.CancellationToken);

        Assert.Equal(
            "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2,\"x\":1.5,\"y\":-2,\"z\":3,\"yaw\":90,\"pitch\":-45,\"crouch\":true,\"lantern\":false,\"map\":\"maps/a.map\"}",
            Encoding.UTF8.GetString(stream.ToArray(), 4, (int)stream.Length - 4));
    }

    private static LanMessage.Pose ValidPose => new(1000, 2, 1.5, -2, 3, 90, -45, true, false, "maps/a.map");

    public static TheoryData<LanMessage.Pose> InvalidPoses => new()
    {
        ValidPose with { X = double.NaN },
        ValidPose with { Y = double.PositiveInfinity },
        ValidPose with { Z = double.NegativeInfinity },
        ValidPose with { Yaw = LanProtocol.MaximumPoseMagnitude * 2 },
        ValidPose with { Pitch = -LanProtocol.MaximumPoseMagnitude * 2 },
        ValidPose with { Map = "" },
        ValidPose with { Map = "maps/a\n.map" },
        ValidPose with { Map = "maps/a\t.map" },
        ValidPose with { Map = new string('m', LanProtocol.MaximumPoseMapScalars + 1) },
    };

    [Theory]
    [MemberData(nameof(InvalidPoses))]
    public async Task Invalid_Poses_are_rejected_when_written(LanMessage.Pose pose)
    {
        await using var stream = new MemoryStream();

        Assert.False(LanProtocol.IsValid(pose));
        await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.WriteAsync(stream, pose, TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(stream.ToArray());
    }

    private const string PoseFields = "\"x\":1.5,\"y\":-2,\"z\":3,\"yaw\":90,\"pitch\":-45,\"crouch\":true,\"lantern\":false";

    public static TheoryData<string> InvalidPoseFrames => new()
    {
        "{\"type\":\"pose\",\"teleportCounter\":2," + PoseFields + ",\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":-1,\"teleportCounter\":2," + PoseFields + ",\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":18446744073709551616,\"teleportCounter\":2," + PoseFields + ",\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1.5,\"teleportCounter\":2," + PoseFields + ",\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":\"1000\",\"teleportCounter\":2," + PoseFields + ",\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":4294967296," + PoseFields + ",\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":-1," + PoseFields + ",\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2,\"x\":1e400,\"y\":-2,\"z\":3,\"yaw\":90,\"pitch\":-45,\"crouch\":true,\"lantern\":false,\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2,\"x\":1e20,\"y\":-2,\"z\":3,\"yaw\":90,\"pitch\":-45,\"crouch\":true,\"lantern\":false,\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2,\"x\":\"NaN\",\"y\":-2,\"z\":3,\"yaw\":90,\"pitch\":-45,\"crouch\":true,\"lantern\":false,\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2,\"y\":-2,\"z\":3,\"yaw\":90,\"pitch\":-45,\"crouch\":true,\"lantern\":false,\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2,\"x\":1.5,\"y\":-2,\"z\":3,\"yaw\":90,\"pitch\":-45,\"crouch\":1,\"lantern\":false,\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2,\"x\":1.5,\"y\":-2,\"z\":3,\"yaw\":90,\"pitch\":-45,\"crouch\":true,\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2,\"x\":1.5,\"y\":-2,\"z\":3,\"yaw\":90,\"pitch\":-45,\"crouch\":true,\"lantern\":1,\"map\":\"maps/a.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2," + PoseFields + "}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2," + PoseFields + ",\"map\":\"\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2," + PoseFields + ",\"map\":\"maps/a\\u0007.map\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2," + PoseFields + ",\"map\":\"" + new string('m', LanProtocol.MaximumPoseMapScalars + 1) + "\"}",
        "{\"type\":\"pose\",\"timeMs\":1000,\"teleportCounter\":2," + PoseFields + ",\"map\":42}",
    };

    [Theory]
    [MemberData(nameof(InvalidPoseFrames))]
    public async Task Invalid_Poses_are_rejected_when_read(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        payload.CopyTo(bytes.AsSpan(4));
        await using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task The_largest_Pose_fits_in_one_frame_whatever_its_map_characters()
    {
        await using var stream = new MemoryStream();
        const double extreme = -LanProtocol.MaximumPoseMagnitude;
        var pose = new LanMessage.Pose(ulong.MaxValue, uint.MaxValue, extreme, extreme, extreme, extreme, extreme, true, true,
            string.Concat(Enumerable.Repeat("👻", LanProtocol.MaximumPoseMapScalars)));

        await LanProtocol.WriteAsync(stream, pose, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(pose, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    public static TheoryData<LanMessage> CustomStoryMessages => new()
    {
        new LanMessage.CustomStoryStarted("mp-test-cs"),
        new LanMessage.CustomStoryStartOutcome("mp-test-cs", SharedCustomStoryStartOutcome.Started),
        new LanMessage.CustomStoryStartOutcome("Příběh 👻", SharedCustomStoryStartOutcome.NotFound),
        new LanMessage.CustomStoryStartOutcome("mp-test-cs", SharedCustomStoryStartOutcome.Invalid),
        new LanMessage.CustomStoryStartOutcome("mp-test-cs", SharedCustomStoryStartOutcome.NotInMainMenu),
        new LanMessage.CustomStoryStartOutcome("mp-test-cs", SharedCustomStoryStartOutcome.Unavailable),
    };

    [Theory]
    [MemberData(nameof(CustomStoryMessages))]
    public async Task Custom_story_messages_round_trip(LanMessage message)
    {
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(message, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("started", SharedCustomStoryStartOutcome.Started)]
    [InlineData("not-found", SharedCustomStoryStartOutcome.NotFound)]
    [InlineData("invalid", SharedCustomStoryStartOutcome.Invalid)]
    [InlineData("not-in-main-menu", SharedCustomStoryStartOutcome.NotInMainMenu)]
    [InlineData("unavailable", SharedCustomStoryStartOutcome.Unavailable)]
    public async Task Custom_story_start_outcomes_use_their_wire_names(string wireName, SharedCustomStoryStartOutcome outcome)
    {
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream,
            new LanMessage.CustomStoryStartOutcome("mp-test-cs", outcome), TestContext.Current.CancellationToken);

        Assert.Equal(
            $"{{\"type\":\"custom-story-start-outcome\",\"identifier\":\"mp-test-cs\",\"outcome\":\"{wireName}\"}}",
            Encoding.UTF8.GetString(stream.ToArray(), 4, (int)stream.Length - 4));
    }

    [Fact]
    public async Task Custom_story_started_uses_its_wire_name()
    {
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream, new LanMessage.CustomStoryStarted("mp-test-cs"), TestContext.Current.CancellationToken);

        Assert.Equal(
            "{\"type\":\"custom-story-started\",\"identifier\":\"mp-test-cs\"}",
            Encoding.UTF8.GetString(stream.ToArray(), 4, (int)stream.Length - 4));
    }

    public static TheoryData<string> InvalidCustomStoryIdentifiers => new()
    {
        "",
        new string('s', CustomStoryIdentifier.MaximumScalars + 1),
        "mp|test",
        "mp:test",
        "mp\ntest",
    };

    [Theory]
    [MemberData(nameof(InvalidCustomStoryIdentifiers))]
    public async Task Invalid_custom_story_identifiers_are_rejected_when_written(string identifier)
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<LanProtocolException>(() => LanProtocol.WriteAsync(
            stream, new LanMessage.CustomStoryStarted(identifier), TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<LanProtocolException>(() => LanProtocol.WriteAsync(
            stream, new LanMessage.CustomStoryStartOutcome(identifier, SharedCustomStoryStartOutcome.Started),
            TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public async Task Unknown_custom_story_start_outcomes_are_rejected_when_written()
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<LanProtocolException>(() => LanProtocol.WriteAsync(
            stream, new LanMessage.CustomStoryStartOutcome("mp-test-cs", (SharedCustomStoryStartOutcome)99),
            TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(stream.ToArray());
    }

    public static TheoryData<string> InvalidCustomStoryFrames => new()
    {
        "{\"type\":\"custom-story-started\"}",
        "{\"type\":\"custom-story-started\",\"identifier\":\"\"}",
        "{\"type\":\"custom-story-started\",\"identifier\":\"mp:test\"}",
        "{\"type\":\"custom-story-started\",\"identifier\":\"mp|test\"}",
        "{\"type\":\"custom-story-started\",\"identifier\":\"mp\\u0007test\"}",
        "{\"type\":\"custom-story-started\",\"identifier\":\"" + new string('s', CustomStoryIdentifier.MaximumScalars + 1) + "\"}",
        "{\"type\":\"custom-story-started\",\"identifier\":42}",
        "{\"type\":\"custom-story-start-outcome\",\"identifier\":\"mp-test-cs\"}",
        "{\"type\":\"custom-story-start-outcome\",\"outcome\":\"started\"}",
        "{\"type\":\"custom-story-start-outcome\",\"identifier\":\"mp-test-cs\",\"outcome\":\"exploded\"}",
        "{\"type\":\"custom-story-start-outcome\",\"identifier\":\"mp-test-cs\",\"outcome\":\"Started\"}",
        "{\"type\":\"custom-story-start-outcome\",\"identifier\":\"mp-test-cs\",\"outcome\":\"0\"}",
        "{\"type\":\"custom-story-start-outcome\",\"identifier\":\"mp-test-cs\",\"outcome\":0}",
        "{\"type\":\"custom-story-start-outcome\",\"identifier\":\"mp:test\",\"outcome\":\"started\"}",
    };

    [Theory]
    [MemberData(nameof(InvalidCustomStoryFrames))]
    public async Task Invalid_custom_story_messages_are_rejected_when_read(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        payload.CopyTo(bytes.AsSpan(4));
        await using var stream = new MemoryStream(bytes);

        await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }
}
