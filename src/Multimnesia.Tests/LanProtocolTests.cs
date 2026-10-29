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
    public async Task Writing_and_reading_report_the_framed_size_including_the_length_prefix()
    {
        await using var stream = new MemoryStream();
        var message = new LanMessage.ChatEntry("Bob", "hello");

        var written = await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var frame = await LanProtocol.ReadFrameAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(stream.Length, written);
        Assert.Equal(new LanFrame(message, written), frame);
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

    [Theory]
    [InlineData(null)]
    [InlineData("FF00FF")]
    public async Task A_chat_entry_round_trips_with_and_without_an_author_chat_color(string? authorColor)
    {
        var message = new LanMessage.ChatEntry("Alice", "hi", authorColor);
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(message, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_author_chat_color_crosses_the_wire_in_one_case()
    {
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream, new LanMessage.ChatEntry("Alice", "hi", "ff00Ff"), TestContext.Current.CancellationToken);

        var bytes = stream.ToArray();
        Assert.Contains("\"authorColor\":\"FF00FF\"", Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4));
    }

    [Theory]
    [InlineData("{\"type\":\"chat-entry\",\"author\":\"Alice\",\"message\":\"hi\"}", null)]
    [InlineData("{\"type\":\"chat-entry\",\"author\":\"Alice\",\"message\":\"hi\",\"authorColor\":null}", null)]
    [InlineData("{\"type\":\"chat-entry\",\"author\":\"Alice\",\"message\":\"hi\",\"authorColor\":\"ff00ff\"}", "FF00FF")]
    public async Task An_absent_or_null_author_chat_color_reads_as_none(string json, string? expected)
    {
        await using var stream = Frame(json);

        var entry = Assert.IsType<LanMessage.ChatEntry>(await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));

        Assert.Equal(expected, entry.AuthorColor);
    }

    [Theory]
    [InlineData("\"FF00\"")]
    [InlineData("\"#FF00FF\"")]
    [InlineData("\"FF00FFAA\"")]
    [InlineData("\"ZZZZZZ\"")]
    [InlineData("\"\"")]
    [InlineData("16711935")]
    public async Task A_malformed_author_chat_color_is_rejected_on_read(string authorColor)
    {
        await using var stream = Frame($"{{\"type\":\"chat-entry\",\"author\":\"Alice\",\"message\":\"hi\",\"authorColor\":{authorColor}}}");

        await Assert.ThrowsAsync<LanProtocolException>(() => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData("FF00")]
    [InlineData("#FF00FF")]
    [InlineData("FF00FFAA")]
    [InlineData("ZZZZZZ")]
    [InlineData("")]
    public async Task A_malformed_author_chat_color_is_never_written(string authorColor)
    {
        await using var stream = new MemoryStream();

        var error = await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.WriteAsync(stream, new LanMessage.ChatEntry("Alice", "hi", authorColor), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("Invalid Chat Entry.", error.Message);
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public void Protocol_version_is_13_for_enemy_door_breaks_and_auto_disables()
    {
        Assert.Equal(13, LanProtocol.CurrentVersion);
    }

    private static MemoryStream Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        payload.CopyTo(bytes.AsSpan(4));
        return new MemoryStream(bytes);
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

    private static readonly BodyState Thrown = new(1.25, -2.5, 3.75, 0, 0.6, 0, 0.8, 0.5, 0, -1, 0, 90, 0);
    private static readonly BodyState AtRest = new(0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0);
    private const string HoldMap = "custom_stories/My Story: Part 2/maps/cellar one.map";

    public static TheoryData<LanMessage> HoldMessages => new()
    {
        new LanMessage.Claim(HoldMap, 12, ClaimReason.Interact),
        new LanMessage.Claim("maps/a.map", int.MinValue, ClaimReason.Contact),
        new LanMessage.ClaimDenied(HoldMap, int.MaxValue),
        new LanMessage.Interaction(HoldMap, 12, 3, true),
        new LanMessage.Interaction("maps/a.map", -7, 0, false),
        new LanMessage.Broke(HoldMap, 12, Thrown),
        new LanMessage.Settled(HoldMap, -7),
        new LanMessage.Bodies(123456, HoldMap, [new(12, 3, Thrown), new(-7, 0, AtRest)]),
        new LanMessage.Bodies(ulong.MaxValue, "maps/a.map", []),
        new LanMessage.Bodies(0, "maps/a.map",
            [.. Enumerable.Range(0, LanProtocol.MaximumBodies).Select(index => new BodyEntry(index, index, AtRest))]),
    };

    [Theory]
    [MemberData(nameof(HoldMessages))]
    public async Task Hold_messages_round_trip(LanMessage message)
    {
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(message, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    public static TheoryData<LanMessage, string> HoldWireNames => new()
    {
        { new LanMessage.Claim("maps/a.map", 12, ClaimReason.Interact), "{\"type\":\"claim\",\"map\":\"maps/a.map\",\"propId\":12,\"reason\":\"interact\"}" },
        { new LanMessage.Claim("maps/a.map", 12, ClaimReason.Contact), "{\"type\":\"claim\",\"map\":\"maps/a.map\",\"propId\":12,\"reason\":\"contact\"}" },
        { new LanMessage.ClaimDenied("maps/a.map", 12), "{\"type\":\"claim-denied\",\"map\":\"maps/a.map\",\"propId\":12}" },
        { new LanMessage.Interaction("maps/a.map", 12, 3, true), "{\"type\":\"interaction\",\"map\":\"maps/a.map\",\"propId\":12,\"bodyId\":3,\"active\":true}" },
        { new LanMessage.Settled("maps/a.map", -7), "{\"type\":\"settled\",\"map\":\"maps/a.map\",\"propId\":-7}" },
        {
            new LanMessage.Broke("maps/a.map", 12, Thrown),
            "{\"type\":\"broke\",\"map\":\"maps/a.map\",\"propId\":12,\"position\":[1.25,-2.5,3.75],\"orientation\":[0,0.6,0,0.8]," +
            "\"linearVelocity\":[0.5,0,-1],\"angularVelocity\":[0,90,0]}"
        },
        {
            new LanMessage.Bodies(1000, "maps/a.map", [new(12, 3, Thrown)]),
            "{\"type\":\"bodies\",\"timeMs\":1000,\"map\":\"maps/a.map\",\"entries\":[{\"propId\":12,\"bodyId\":3,\"position\":[1.25,-2.5,3.75]," +
            "\"orientation\":[0,0.6,0,0.8],\"linearVelocity\":[0.5,0,-1],\"angularVelocity\":[0,90,0]}]}"
        },
    };

    [Theory]
    [MemberData(nameof(HoldWireNames))]
    public async Task Hold_messages_use_their_wire_names(LanMessage message, string json)
    {
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);

        Assert.Equal(json, Encoding.UTF8.GetString(stream.ToArray(), 4, (int)stream.Length - 4));
    }

    public static TheoryData<LanMessage> InvalidHoldMessages => new()
    {
        new LanMessage.Claim("", 12, ClaimReason.Interact),
        new LanMessage.Claim("maps/a\n.map", 12, ClaimReason.Interact),
        new LanMessage.Claim(new string('m', LanProtocol.MaximumPoseMapScalars + 1), 12, ClaimReason.Interact),
        new LanMessage.Claim("maps/a.map", 12, (ClaimReason)99),
        new LanMessage.ClaimDenied("", 12),
        new LanMessage.Interaction("maps/a\t.map", 12, 3, true),
        new LanMessage.Settled("", 12),
        new LanMessage.Broke("maps/a.map", 12, Thrown with { X = double.NaN }),
        new LanMessage.Broke("maps/a.map", 12, Thrown with { Vy = double.PositiveInfinity }),
        new LanMessage.Broke("maps/a.map", 12, Thrown with { Wz = LanProtocol.MaximumPoseMagnitude * 2 }),
        new LanMessage.Broke("maps/a.map", 12, Thrown with { Qw = 0.98 }),
        new LanMessage.Broke("maps/a.map", 12, Thrown with { Qx = 0.5 }),
        new LanMessage.Broke("", 12, Thrown),
        new LanMessage.Bodies(1, "maps/a.map", [new(12, 3, Thrown with { Z = -LanProtocol.MaximumPoseMagnitude * 2 })]),
        new LanMessage.Bodies(1, "maps/a.map", [new(12, 3, AtRest with { Qw = 0 })]),
        new LanMessage.Bodies(1, "", [new(12, 3, Thrown)]),
        new LanMessage.Bodies(1, "maps/a.map",
            [.. Enumerable.Range(0, LanProtocol.MaximumBodies + 1).Select(index => new BodyEntry(index, 0, AtRest))]),
    };

    [Theory]
    [MemberData(nameof(InvalidHoldMessages))]
    public async Task Invalid_Hold_messages_are_rejected_when_written(LanMessage message)
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(stream.ToArray());
    }

    [Fact]
    public void Body_states_within_their_bounds_and_a_unit_quaternion_tolerance_are_valid()
    {
        Assert.True(LanProtocol.IsValid(new BodyState(
            LanProtocol.MaximumPoseMagnitude, -LanProtocol.MaximumPoseMagnitude, 0, 0, 0, 0, 0.995, 0, 0, 0, 0, 0, 0)));
        Assert.True(LanProtocol.IsValid(AtRest with { Qw = 1.009 }));
        Assert.False(LanProtocol.IsValid(AtRest with { Qw = 1.011 }));
        Assert.False(LanProtocol.IsValid(AtRest with { Qw = double.NaN }));
    }

    private const string ThrownJson =
        "\"position\":[1.25,-2.5,3.75],\"orientation\":[0,0.6,0,0.8],\"linearVelocity\":[0.5,0,-1],\"angularVelocity\":[0,90,0]";

    public static TheoryData<string> InvalidHoldFrames => new()
    {
        "{\"type\":\"claim\",\"map\":\"maps/a.map\",\"propId\":12}",
        "{\"type\":\"claim\",\"map\":\"maps/a.map\",\"propId\":12,\"reason\":\"grab\"}",
        "{\"type\":\"claim\",\"map\":\"maps/a.map\",\"propId\":12,\"reason\":\"Interact\"}",
        "{\"type\":\"claim\",\"map\":\"maps/a.map\",\"propId\":12,\"reason\":0}",
        "{\"type\":\"claim\",\"map\":\"maps/a.map\",\"propId\":2147483648,\"reason\":\"interact\"}",
        "{\"type\":\"claim\",\"map\":\"maps/a.map\",\"propId\":1.5,\"reason\":\"interact\"}",
        "{\"type\":\"claim\",\"map\":\"maps/a.map\",\"propId\":\"12\",\"reason\":\"interact\"}",
        "{\"type\":\"claim\",\"propId\":12,\"reason\":\"interact\"}",
        "{\"type\":\"claim\",\"map\":\"\",\"propId\":12,\"reason\":\"interact\"}",
        "{\"type\":\"claim\",\"map\":\"maps/a\\u0007.map\",\"propId\":12,\"reason\":\"interact\"}",
        "{\"type\":\"claim\",\"map\":\"" + new string('m', LanProtocol.MaximumPoseMapScalars + 1) + "\",\"propId\":12,\"reason\":\"interact\"}",
        "{\"type\":\"claim-denied\",\"map\":\"maps/a.map\"}",
        "{\"type\":\"claim-denied\",\"map\":42,\"propId\":12}",
        "{\"type\":\"interaction\",\"map\":\"maps/a.map\",\"propId\":12,\"bodyId\":3}",
        "{\"type\":\"interaction\",\"map\":\"maps/a.map\",\"propId\":12,\"bodyId\":3,\"active\":1}",
        "{\"type\":\"interaction\",\"map\":\"maps/a.map\",\"propId\":12,\"bodyId\":-2147483649,\"active\":true}",
        "{\"type\":\"settled\",\"propId\":12}",
        "{\"type\":\"settled\",\"map\":\"maps/a.map\",\"propId\":null}",
        "{\"type\":\"broke\",\"map\":\"maps/a.map\",\"propId\":12}",
        "{\"type\":\"broke\",\"map\":\"maps/a.map\",\"propId\":12,\"position\":[1,2],\"orientation\":[0,0,0,1],\"linearVelocity\":[0,0,0],\"angularVelocity\":[0,0,0]}",
        "{\"type\":\"broke\",\"map\":\"maps/a.map\",\"propId\":12,\"position\":[1,2,3,4],\"orientation\":[0,0,0,1],\"linearVelocity\":[0,0,0],\"angularVelocity\":[0,0,0]}",
        "{\"type\":\"broke\",\"map\":\"maps/a.map\",\"propId\":12,\"position\":[1e20,2,3],\"orientation\":[0,0,0,1],\"linearVelocity\":[0,0,0],\"angularVelocity\":[0,0,0]}",
        "{\"type\":\"broke\",\"map\":\"maps/a.map\",\"propId\":12,\"position\":[1e400,2,3],\"orientation\":[0,0,0,1],\"linearVelocity\":[0,0,0],\"angularVelocity\":[0,0,0]}",
        "{\"type\":\"broke\",\"map\":\"maps/a.map\",\"propId\":12,\"position\":[\"1\",2,3],\"orientation\":[0,0,0,1],\"linearVelocity\":[0,0,0],\"angularVelocity\":[0,0,0]}",
        "{\"type\":\"broke\",\"map\":\"maps/a.map\",\"propId\":12,\"position\":[1,2,3],\"orientation\":[0,0,0,2],\"linearVelocity\":[0,0,0],\"angularVelocity\":[0,0,0]}",
        "{\"type\":\"broke\",\"map\":\"maps/a.map\",\"propId\":12,\"position\":[1,2,3],\"orientation\":[0,0,0],\"linearVelocity\":[0,0,0],\"angularVelocity\":[0,0,0]}",
        "{\"type\":\"broke\",\"map\":\"maps/a.map\",\"propId\":12,\"position\":[1,2,3],\"orientation\":[0,0,0,1],\"linearVelocity\":{},\"angularVelocity\":[0,0,0]}",
        "{\"type\":\"bodies\",\"map\":\"maps/a.map\",\"entries\":[]}",
        "{\"type\":\"bodies\",\"timeMs\":-1,\"map\":\"maps/a.map\",\"entries\":[]}",
        "{\"type\":\"bodies\",\"timeMs\":1,\"map\":\"maps/a.map\"}",
        "{\"type\":\"bodies\",\"timeMs\":1,\"map\":\"maps/a.map\",\"entries\":{}}",
        "{\"type\":\"bodies\",\"timeMs\":1,\"entries\":[]}",
        "{\"type\":\"bodies\",\"timeMs\":1,\"map\":\"maps/a.map\",\"entries\":[{\"propId\":12," + ThrownJson + "}]}",
        "{\"type\":\"bodies\",\"timeMs\":1,\"map\":\"maps/a.map\",\"entries\":[{\"propId\":12,\"bodyId\":3,\"position\":[0,0,0],\"orientation\":[0,0,0,0],\"linearVelocity\":[0,0,0],\"angularVelocity\":[0,0,0]}]}",
        "{\"type\":\"bodies\",\"timeMs\":1,\"map\":\"maps/a.map\",\"entries\":[" +
            string.Join(',', Enumerable.Repeat("{\"propId\":12,\"bodyId\":3," + ThrownJson + "}", LanProtocol.MaximumBodies + 1)) + "]}",
    };

    [Theory]
    [MemberData(nameof(InvalidHoldFrames))]
    public async Task Invalid_Hold_messages_are_rejected_when_read(string json)
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
    public async Task The_largest_bodies_message_fits_in_one_frame_whatever_its_numbers_and_map_characters()
    {
        await using var stream = new MemoryStream();
        // The longest JSON numbers within the bounds: 17 significant digits, a sign, and an exponent.
        const double longest = -1.2345678901234567E-300;
        var state = new BodyState(longest, longest, longest, longest, longest, longest, -1, longest, longest, longest, longest, longest, longest);
        var bodies = new LanMessage.Bodies(ulong.MaxValue, string.Concat(Enumerable.Repeat("👻", LanProtocol.MaximumPoseMapScalars)),
            [.. Enumerable.Range(0, LanProtocol.MaximumBodies).Select(_ => new BodyEntry(int.MinValue, int.MinValue, state))]);

        await LanProtocol.WriteAsync(stream, bodies, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(bodies, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    public static TheoryData<LanMessage, string> WorldMessageWireNames => new()
    {
        {
            new LanMessage.PlayerCollide("maps/a.map", 30, Entered: true),
            "{\"type\":\"shared-callback\",\"map\":\"maps/a.map\",\"kind\":\"player-collide\",\"entityId\":30,\"state\":1}"
        },
        {
            new LanMessage.PlayerCollide("maps/a.map", 30, Entered: false),
            "{\"type\":\"shared-callback\",\"map\":\"maps/a.map\",\"kind\":\"player-collide\",\"entityId\":30,\"state\":-1}"
        },
        {
            new LanMessage.EntityCollide("maps/a.map", 30, 12, Entered: true),
            "{\"type\":\"shared-callback\",\"map\":\"maps/a.map\",\"kind\":\"entity-collide\",\"parentId\":30,\"entityId\":12,\"state\":1}"
        },
        {
            new LanMessage.Interact("maps/a.map", -7),
            "{\"type\":\"shared-callback\",\"map\":\"maps/a.map\",\"kind\":\"interact\",\"entityId\":-7}"
        },
        {
            new LanMessage.UseItem("maps/a.map", 12, "key_study"),
            "{\"type\":\"shared-callback\",\"map\":\"maps/a.map\",\"kind\":\"use-item\",\"entityId\":12,\"item\":\"key_study\"}"
        },
        {
            new LanMessage.Pickup("maps/a.map", 12),
            "{\"type\":\"shared-callback\",\"map\":\"maps/a.map\",\"kind\":\"pickup\",\"entityId\":12}"
        },
        {
            new LanMessage.Flashback("maps/a.map", -17),
            "{\"type\":\"shared-callback\",\"map\":\"maps/a.map\",\"kind\":\"flashback\",\"entityId\":-17}"
        },
        {
            new LanMessage.Combine("maps/a.map", "hollow_needle_1", "stone_hammer_1"),
            "{\"type\":\"shared-callback\",\"map\":\"maps/a.map\",\"kind\":\"combine\",\"itemA\":\"hollow_needle_1\",\"itemB\":\"stone_hammer_1\"}"
        },
        {
            new LanMessage.StickyAttach("maps/a.map", 21, 30, -7),
            "{\"type\":\"shared-callback\",\"map\":\"maps/a.map\",\"kind\":\"sticky-attach\",\"areaId\":21,\"entityId\":30,\"bodyId\":-7}"
        },
        {
            new LanMessage.AutoDisabled("maps/a.map", 139),
            "{\"type\":\"shared-callback\",\"map\":\"maps/a.map\",\"kind\":\"auto-disabled\",\"entityId\":139}"
        },
        { new LanMessage.ButtonState("maps/a.map", 12, SwitchedOn: true), "{\"type\":\"button-state\",\"map\":\"maps/a.map\",\"entityId\":12,\"switchedOn\":true}" },
        { new LanMessage.LampState("maps/a.map", 13, Lit: false), "{\"type\":\"lamp-state\",\"map\":\"maps/a.map\",\"entityId\":13,\"lit\":false}" },
    };

    [Theory]
    [MemberData(nameof(WorldMessageWireNames))]
    public async Task World_messages_use_their_wire_names_and_round_trip(LanMessage message, string json)
    {
        await using var stream = new MemoryStream();

        await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);
        Assert.Equal(json, Encoding.UTF8.GetString(stream.ToArray(), 4, (int)stream.Length - 4));
        stream.Position = 0;

        Assert.Equal(message, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task World_messages_keep_long_maps_extreme_identifiers_and_the_longest_item_name()
    {
        await using var stream = new MemoryStream();
        var message = new LanMessage.UseItem(HoldMap, int.MinValue, "!" + new string('~', LanProtocol.MaximumItemNameLength - 1));

        await LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken);
        stream.Position = 0;

        Assert.Equal(message, await LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    public static TheoryData<LanMessage> InvalidWorldMessages => new()
    {
        new LanMessage.PlayerCollide("", 30, true),
        new LanMessage.EntityCollide("maps/a\n.map", 30, 12, true),
        new LanMessage.Interact(new string('m', LanProtocol.MaximumPoseMapScalars + 1), 12),
        new LanMessage.UseItem("maps/a.map", 12, ""),
        new LanMessage.UseItem("maps/a.map", 12, "rusty key"),
        new LanMessage.UseItem("maps/a.map", 12, "keyé"),
        new LanMessage.UseItem("maps/a.map", 12, new string('k', LanProtocol.MaximumItemNameLength + 1)),
        new LanMessage.Combine("maps/a.map", "", "stone_hammer_1"),
        new LanMessage.Combine("maps/a.map", "hollow_needle_1", "stone hammer"),
        new LanMessage.Combine("maps/a.map", new string('k', LanProtocol.MaximumItemNameLength + 1), "stone_hammer_1"),
        new LanMessage.Combine("", "hollow_needle_1", "stone_hammer_1"),
        new LanMessage.StickyAttach("", 21, 30, 7),
        new LanMessage.ButtonState("", 12, true),
        new LanMessage.LampState("", 13, true),
    };

    [Theory]
    [MemberData(nameof(InvalidWorldMessages))]
    public async Task Invalid_world_messages_are_rejected_when_written(LanMessage message)
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken).AsTask());
        Assert.Empty(stream.ToArray());
    }

    private const string SharedCallbackOn = "{\"type\":\"shared-callback\",\"map\":\"maps/a.map\",";

    public static TheoryData<string> InvalidWorldFrames => new()
    {
        SharedCallbackOn + "\"entityId\":30,\"state\":1}",
        SharedCallbackOn + "\"kind\":\"look-at\",\"entityId\":30}",
        SharedCallbackOn + "\"kind\":\"PlayerCollide\",\"entityId\":30,\"state\":1}",
        SharedCallbackOn + "\"kind\":\"player-collide\",\"state\":1}",
        SharedCallbackOn + "\"kind\":\"player-collide\",\"entityId\":30}",
        SharedCallbackOn + "\"kind\":\"player-collide\",\"entityId\":30,\"state\":0}",
        SharedCallbackOn + "\"kind\":\"player-collide\",\"entityId\":30,\"state\":2}",
        SharedCallbackOn + "\"kind\":\"player-collide\",\"entityId\":2147483648,\"state\":1}",
        SharedCallbackOn + "\"kind\":\"entity-collide\",\"entityId\":12,\"state\":1}",
        SharedCallbackOn + "\"kind\":\"interact\"}",
        SharedCallbackOn + "\"kind\":\"interact\",\"entityId\":\"12\"}",
        "{\"type\":\"shared-callback\",\"kind\":\"interact\",\"entityId\":12}",
        "{\"type\":\"shared-callback\",\"map\":\"\",\"kind\":\"interact\",\"entityId\":12}",
        SharedCallbackOn + "\"kind\":\"use-item\",\"entityId\":12}",
        SharedCallbackOn + "\"kind\":\"use-item\",\"entityId\":12,\"item\":\"\"}",
        SharedCallbackOn + "\"kind\":\"use-item\",\"entityId\":12,\"item\":\"rusty key\"}",
        SharedCallbackOn + "\"kind\":\"use-item\",\"entityId\":12,\"item\":\"key\\u0007\"}",
        SharedCallbackOn + "\"kind\":\"use-item\",\"entityId\":12,\"item\":\"" + new string('k', LanProtocol.MaximumItemNameLength + 1) + "\"}",
        SharedCallbackOn + "\"kind\":\"use-item\",\"entityId\":12,\"item\":42}",
        SharedCallbackOn + "\"kind\":\"pickup\"}",
        SharedCallbackOn + "\"kind\":\"pickup\",\"entityId\":\"12\"}",
        SharedCallbackOn + "\"kind\":\"flashback\"}",
        SharedCallbackOn + "\"kind\":\"flashback\",\"entityId\":2147483648}",
        SharedCallbackOn + "\"kind\":\"combine\",\"itemA\":\"hollow_needle_1\"}",
        SharedCallbackOn + "\"kind\":\"combine\",\"itemB\":\"stone_hammer_1\"}",
        SharedCallbackOn + "\"kind\":\"combine\",\"itemA\":\"hollow needle\",\"itemB\":\"stone_hammer_1\"}",
        SharedCallbackOn + "\"kind\":\"combine\",\"itemA\":\"hollow_needle_1\",\"itemB\":\"" + new string('k', LanProtocol.MaximumItemNameLength + 1) + "\"}",
        SharedCallbackOn + "\"kind\":\"combine\",\"itemA\":\"hollow_needle_1\",\"itemB\":7}",
        SharedCallbackOn + "\"kind\":\"sticky-attach\",\"entityId\":30,\"bodyId\":7}",
        SharedCallbackOn + "\"kind\":\"sticky-attach\",\"areaId\":21,\"bodyId\":7}",
        SharedCallbackOn + "\"kind\":\"sticky-attach\",\"areaId\":21,\"entityId\":30}",
        SharedCallbackOn + "\"kind\":\"sticky-attach\",\"areaId\":\"21\",\"entityId\":30,\"bodyId\":7}",
        SharedCallbackOn + "\"kind\":\"sticky-attach\",\"areaId\":21,\"entityId\":30,\"bodyId\":2147483648}",
        SharedCallbackOn + "\"kind\":\"auto-disabled\"}",
        SharedCallbackOn + "\"kind\":\"auto-disabled\",\"entityId\":\"139\"}",
        "{\"type\":\"button-state\",\"map\":\"maps/a.map\",\"entityId\":12}",
        "{\"type\":\"button-state\",\"map\":\"maps/a.map\",\"entityId\":12,\"switchedOn\":1}",
        "{\"type\":\"button-state\",\"map\":\"maps/a.map\",\"switchedOn\":true}",
        "{\"type\":\"lamp-state\",\"map\":\"maps/a.map\",\"entityId\":13}",
        "{\"type\":\"lamp-state\",\"entityId\":13,\"lit\":true}",
    };

    [Theory]
    [MemberData(nameof(InvalidWorldFrames))]
    public async Task Invalid_world_messages_are_rejected_when_read(string json)
    {
        await using var stream = Frame(json);

        await Assert.ThrowsAsync<LanProtocolException>(
            () => LanProtocol.ReadAsync(stream, TestContext.Current.CancellationToken).AsTask());
    }
}
