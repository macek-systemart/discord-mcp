using DiscordMcp.Discord;

namespace DiscordMcp.Formatting;

/// <summary>
/// Where a message's files live: its own <c>attachments</c>, then forwarded snapshots, then (thread starter, type 21
/// only) the original message in the parent channel. The formatter lists them and <c>get_attachment</c> finds them.
/// </summary>
public static class AttachmentLocator
{
    public static IEnumerable<DiscordAttachment> All(DiscordMessage message)
    {
        var snapshots = message.MessageSnapshots?.SelectMany(s => s.Message.Attachments) ?? [];
        var starter = message.Type == MessageTypes.ThreadStarterMessage ? message.ReferencedMessage?.Attachments ?? [] : [];
        return message.Attachments.Concat(snapshots).Concat(starter);
    }

    public static DiscordAttachment? Find(DiscordMessage message, ulong attachmentId) =>
        All(message).FirstOrDefault(a => a.Id == attachmentId);
}
