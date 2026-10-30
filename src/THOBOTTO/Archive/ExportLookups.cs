namespace THOBOTTO.Archive;

// What a writer looks up while writing a message: the member as the archive last saw them (nickname, roles, avatar)
// when the message's own JSON carries no member, a role by id, and where an image of the server or a member (an
// emoji, a sticker, an avatar; by its kind, the thing's id and the image's hash) was put in the export.
public sealed record ExportLookups(
    Func<ulong, ArchivedMember?> Member,
    Func<ulong, ArchivedRole?> Role,
    Func<string, ulong, string, string?> Asset);
