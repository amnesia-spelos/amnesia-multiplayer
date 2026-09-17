using System.Text;
using Multimnesia.Contracts;

namespace Multimnesia.Client;

public sealed record ChatEntry(string Author, string Message)
{
    private const string LocalSubmissionPrefix = "EVENT:CHAT:";

    public static bool TryParseLocalSubmission(string line, out ChatEntry entry)
    {
        entry = default!;
        if (!line.StartsWith(LocalSubmissionPrefix, StringComparison.Ordinal)) return false;

        var authorEnd = line.IndexOf(':', LocalSubmissionPrefix.Length);
        if (authorEnd < 0) return false;
        var author = line[LocalSubmissionPrefix.Length..authorEnd].Trim();
        var message = line[(authorEnd + 1)..].Trim();
        if (author.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
            !IsValid(author, 32, rejectColon: true) || !IsValid(message, 256, rejectColon: false)) return false;

        entry = new(author, message);
        return true;
    }

    private static bool IsValid(string value, int maximumScalars, bool rejectColon)
    {
        if (string.IsNullOrWhiteSpace(value) || (rejectColon && value.Contains(':'))) return false;
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control) return false;
            if (++count > maximumScalars) return false;
        }
        return true;
    }
}

public static class GameInteractionProtocol
{
    private const string CustomStoryStartedPrefix = "EVENT:CustomStoryStarted:";
    private const string StartCustomStoryResponsePrefix = "RESPONSE:startcustomstory:";
    private const string UnknownCommandWarning = "WARNING:Unknown command";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static StreamReader CreateReader(Stream stream) =>
        new(stream, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

    public static StreamWriter CreateWriter(Stream stream) =>
        new(stream, Utf8, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

    public static GameEvent ParseEvent(string? line)
    {
        if (line is null) return new GameEvent.Unknown(string.Empty);
        if (ChatEntry.TryParseLocalSubmission(line, out var entry)) return new GameEvent.ChatSubmitted(entry);
        if (line.StartsWith(CustomStoryStartedPrefix, StringComparison.Ordinal))
        {
            var identifier = line[CustomStoryStartedPrefix.Length..];
            return CustomStoryIdentifier.IsValid(identifier)
                ? new GameEvent.CustomStoryStarted(identifier)
                : new GameEvent.Unknown(line);
        }
        if (line.StartsWith(StartCustomStoryResponsePrefix, StringComparison.Ordinal))
            return new GameEvent.StartCustomStoryResponded(line[StartCustomStoryResponsePrefix.Length..] switch
            {
                "starting" => StartCustomStoryOutcome.Starting,
                "not found" => StartCustomStoryOutcome.NotFound,
                "invalid" => StartCustomStoryOutcome.Invalid,
                "not in main menu" => StartCustomStoryOutcome.NotInMainMenu,
                _ => StartCustomStoryOutcome.Unrecognized
            });
        if (line == UnknownCommandWarning) return new GameEvent.UnknownCommandWarned();
        return new GameEvent.Unknown(line);
    }

    public static string Display(ChatEntry entry) => $"chat:{entry.Author}:{entry.Message}";

    public static string StartCustomStory(string identifier) => $"startcustomstory:{identifier}";
}

public enum StartCustomStoryOutcome { Starting, NotFound, Invalid, NotInMainMenu, Unrecognized }

public abstract record GameEvent
{
    public sealed record ChatSubmitted(ChatEntry Entry) : GameEvent;
    public sealed record CustomStoryStarted(string Identifier) : GameEvent;
    public sealed record StartCustomStoryResponded(StartCustomStoryOutcome Outcome) : GameEvent;
    public sealed record UnknownCommandWarned : GameEvent;
    public sealed record Unknown(string Line) : GameEvent;
}
