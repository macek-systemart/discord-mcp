using System.Globalization;
using System.Net;
using System.Text.Json;
using DiscordMcp.Discord;
using DiscordMcp.Http;
using DiscordMcp.Localization;

namespace DiscordMcp.Cli;

/// <summary>
/// <c>check &lt;guildId&gt; --probe</c>: one GET per row of the endpoint table, paced like the server, printing only
/// status codes, counts and yes/no shape facts — never message content or names. Channels used for the per-channel
/// rows are chosen with the local permission calculation, so no request targets a channel the user cannot read.
/// Stops at the first 401, 429 or verification demand.
/// </summary>
internal sealed class Probe(HttpClient api, ulong guildId, ulong myUserId, TextWriter output)
{
    private static readonly HashSet<int> TextTypes = [0, 5];
    private static readonly HashSet<int> ForumTypes = [15, 16];
    private static readonly HashSet<int> VoiceTypes = [2, 13];

    private int _requests;
    private bool _aborted;

    private static Texts T => Languages.Current;

    private sealed record Reply(int Status, JsonElement? Body, string? Note);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var g = guildId.ToString(CultureInfo.InvariantCulture);
        output.WriteLine(T.ProbeIntro(g, HttpRegistration.UserAgent));
        output.WriteLine();

        var me = await GetAsync("users/@me", "users/@me", ct);
        Line(me, b => $"id={YesNo(b.Has("id"))}");

        var guild = await GetAsync($"guilds/{g}", "guilds/{g}", ct);
        Line(guild, b => $"roles={Describe(b.Arr("roles"))} owner_id={YesNo(b.Has("owner_id"))}");

        var roles = await GetAsync($"guilds/{g}/roles", "guilds/{g}/roles", ct);
        Line(roles, b => $"{T.CountLabel}={Count(b)}");

        var channels = await GetAsync($"guilds/{g}/channels", "guilds/{g}/channels", ct);
        var member = await GetAsync($"users/@me/guilds/{g}/member", "users/@me/guilds/{g}/member", ct);
        Line(member, b => $"roles={YesNo(b.Arr("roles") is not null)}");
        if (_aborted)
        {
            return Finish();
        }

        var context = BuildContext(guild?.Body, roles?.Body, member?.Body);
        var picked = channels?.Body is { ValueKind: JsonValueKind.Array } list ? Summarize(channels, list, context) : default;

        ulong? threadId = null;
        if (picked.Text is { } text)
        {
            var c = text.ToString(CultureInfo.InvariantCulture);
            var info = await GetAsync($"channels/{c}", "channels/{text}", ct);
            Line(info, b => $"{T.GuildIdMatches}={YesNo(b.U64("guild_id") == guildId)} type={b.I32("type")}");

            var page = await GetAsync($"channels/{c}/messages?limit=5", "channels/{text}/messages?limit=5", ct);
            Line(page, b => $"{T.MessagesLabel}={Count(b)}");
            var ids = page?.Body is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray().Select(m => m.U64("id")).OfType<ulong>().ToList() : [];
            if (ids.Count > 0)
            {
                var target = ids[ids.Count / 2];
                var around = await GetAsync($"channels/{c}/messages?around={target}&limit=5", "channels/{text}/messages?around={m}&limit=5", ct);
                Line(around, b => $"{T.MessagesLabel}={Count(b)} {T.ContainsTarget}={YesNo(b.ValueKind == JsonValueKind.Array && b.EnumerateArray().Any(m => m.U64("id") == target))}");
            }
            else
            {
                Skip("channels/{text}/messages?around={m}", T.ProbeReasonNoMessages);
            }

            var pins = await GetAsync($"channels/{c}/messages/pins?limit=1", "channels/{text}/messages/pins?limit=1", ct);
            Line(pins, b => $"items={YesNo(b.Arr("items") is not null)} has_more={YesNo(b.Has("has_more"))}");

            var search = await GetAsync($"channels/{c}/threads/search?limit=1", "channels/{text}/threads/search?limit=1", ct);
            Line(search, b => $"threads={YesNo(b.Arr("threads") is not null)} total_results={YesNo(b.Has("total_results"))}");

            var archived = await GetAsync($"channels/{c}/threads/archived/public?limit=2", "channels/{text}/threads/archived/public?limit=2", ct);
            Line(archived, b => $"{T.ThreadsLabel}={b.Arr("threads").Count()} has_more={YesNo(b.Has("has_more"))}");
            threadId ??= FirstThread(archived?.Body);

            var mine = await GetAsync($"channels/{c}/users/@me/threads/archived/private?limit=2", "channels/{text}/users/@me/threads/archived/private?limit=2", ct);
            Line(mine, b => $"{T.ThreadsLabel}={b.Arr("threads").Count()}");

            if (picked.TextAccess.ManageThreads)
            {
                var all = await GetAsync($"channels/{c}/threads/archived/private?limit=2", "channels/{text}/threads/archived/private?limit=2", ct);
                Line(all, b => $"{T.ThreadsLabel}={b.Arr("threads").Count()}");
            }
            else
            {
                Skip("channels/{text}/threads/archived/private", T.ProbeReasonNoManageThreads);
            }

            var messages = await GetAsync($"guilds/{g}/messages/search?channel_id={c}&limit=1", "guilds/{g}/messages/search?channel_id={text}&limit=1", ct);
            Line(messages, b =>
            {
                var nested = b.Arr("messages") is { } m && m.GetArrayLength() > 0 && m[0].ValueKind == JsonValueKind.Array;
                return $"total_results={YesNo(b.Has("total_results"))} messages={T.ArrayOfArrays}:{YesNo(nested)} threads={YesNo(b.Has("threads"))}";
            });
        }
        else
        {
            Skip("channels/{text}/…", T.ProbeReasonNoTextChannel);
        }

        if (picked.Forum is { } forum)
        {
            var f = forum.ToString(CultureInfo.InvariantCulture);
            var posts = await GetAsync($"channels/{f}/threads/search?limit=1", "channels/{forum}/threads/search?limit=1", ct);
            Line(posts, b => $"{T.ThreadsLabel}={b.Arr("threads").Count()} first_messages={YesNo(b.Has("first_messages"))}");
            threadId ??= FirstThread(posts?.Body);
        }
        else
        {
            Skip("channels/{forum}/threads/search", T.ProbeReasonNoForum);
        }

        if (threadId is { } thread)
        {
            var t = thread.ToString(CultureInfo.InvariantCulture);
            var info = await GetAsync($"channels/{t}", "channels/{thread}", ct);
            Line(info, b => $"{T.GuildIdMatches}={YesNo(b.U64("guild_id") == guildId)} parent_id={YesNo(b.Has("parent_id"))} type={b.I32("type")}");
            var inThread = await GetAsync($"channels/{t}/messages?limit=1", "channels/{thread}/messages?limit=1", ct);
            Line(inThread, b => $"{T.MessagesLabel}={Count(b)}");
        }
        else
        {
            Skip("channels/{thread}", T.ProbeReasonNoThread);
        }

        if (picked.Voice is { } voice)
        {
            var v = voice.ToString(CultureInfo.InvariantCulture);
            var chat = await GetAsync($"channels/{v}/messages?limit=1", "channels/{voice}/messages?limit=1", ct);
            Line(chat, b => $"{T.MessagesLabel}={Count(b)}");
        }
        else
        {
            Skip("channels/{voice}/messages", T.ProbeReasonNoVoiceChat);
        }

        var active = await GetAsync($"guilds/{g}/threads/active", "guilds/{g}/threads/active (" + T.ProbeExpectedRefusal + ")", ct);
        Line(active, b => $"threads={YesNo(b.Arr("threads") is not null)}");

        return Finish();
    }

    private (ulong? Text, ChannelAccess TextAccess, ulong? Forum, ulong? Voice) Summarize(Reply reply, JsonElement list, MemberContext? context)
    {
        var all = list.EnumerateArray().ToList();
        var types = all.GroupBy(c => c.I32("type") ?? -1).OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Count()}");
        var withLast = all.Count(c => c.Has("last_message_id"));
        var obfuscated = all.Count(c => ((c.U64("flags") ?? 0) & Visibility.ChannelObfuscatedFlag) != 0);
        var hasOverwrites = all.Count(c => c.Arr("permission_overwrites") is not null);
        Line(reply, _ => $"{T.ChannelsLabel}={all.Count} {T.TypesLabel}[{string.Join(' ', types)}] last_message_id={withLast} permission_overwrites={hasOverwrites} obfuscated={obfuscated}");

        if (context is null)
        {
            output.WriteLine("        " + T.ProbeCannotComputePermissions);
            return default;
        }

        var access = all.Select(c => (Channel: c, Access: Visibility.ForChannel(context, Overwrites(c), c.U64("flags") ?? 0))).ToList();
        output.WriteLine("        " + T.ProbeAccessSummary(access.Count(a => a.Access.View), access.Count(a => a.Access.History), access.Count(a => !a.Access.View)));

        static ulong Last(JsonElement c) => c.U64("last_message_id") ?? 0;
        var text = access.Where(a => a.Access.History && TextTypes.Contains(a.Channel.I32("type") ?? -1))
            .OrderByDescending(a => Last(a.Channel)).FirstOrDefault();
        var forum = access.Where(a => a.Access.History && ForumTypes.Contains(a.Channel.I32("type") ?? -1))
            .OrderByDescending(a => Last(a.Channel)).FirstOrDefault();
        var voice = access.Where(a => a.Access.History && VoiceTypes.Contains(a.Channel.I32("type") ?? -1) && Last(a.Channel) > 0)
            .OrderByDescending(a => Last(a.Channel)).FirstOrDefault();

        return (text.Channel.U64("id"), text.Access, forum.Channel.U64("id"), voice.Channel.U64("id"));
    }

    private MemberContext? BuildContext(JsonElement? guild, JsonElement? roles, JsonElement? member)
    {
        var roleList = guild?.Arr("roles") ?? (roles is { ValueKind: JsonValueKind.Array } r ? r : null);
        if (guild?.U64("owner_id") is not { } owner || roleList is not { } list || member?.Arr("roles") is not { } mine)
        {
            return null;
        }

        var permissions = new Dictionary<ulong, ulong>();
        foreach (var role in list.EnumerateArray())
        {
            if (role.U64("id") is { } id)
            {
                permissions[id] = role.U64("permissions") ?? 0;
            }
        }

        var memberRoles = mine.EnumerateArray().Select(x => x.AsU64()).OfType<ulong>().ToHashSet();
        return new MemberContext(guildId, owner, myUserId, permissions, memberRoles);
    }

    private static List<PermissionOverwrite> Overwrites(JsonElement channel)
    {
        var result = new List<PermissionOverwrite>();
        if (channel.Arr("permission_overwrites") is not { } list)
        {
            return result;
        }

        foreach (var o in list.EnumerateArray())
        {
            var type = o.TryGetProperty("type", out var t) && (t.ValueKind == JsonValueKind.Number ? t.GetInt32() == 1 : t.GetString() == "member")
                ? OverwriteType.Member
                : OverwriteType.Role;
            if (o.U64("id") is { } id)
            {
                result.Add(new PermissionOverwrite(id, type, o.U64("allow") ?? 0, o.U64("deny") ?? 0));
            }
        }

        return result;
    }

    private static ulong? FirstThread(JsonElement? body) =>
        body?.Arr("threads") is { } threads && threads.GetArrayLength() > 0 ? threads[0].U64("id") : null;

    private async Task<Reply?> GetAsync(string path, string label, CancellationToken ct)
    {
        if (_aborted)
        {
            return null;
        }

        HttpResponseMessage response;
        try
        {
            _requests++;
            response = await api.GetAsync(path, ct);
        }
        catch (DiscordApiException e)
        {
            output.WriteLine($"  {T.ProbeStopTag}  {label}: {e.Message}");
            _aborted = true;
            return null;
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            JsonElement? body = null;
            string? note = null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length is > 0 and < 4 * 1024 * 1024)
            {
                try
                {
                    using var doc = JsonDocument.Parse(bytes);
                    body = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    note = T.ProbeBodyNotJson;
                }
            }

            if (!response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Accepted)
            {
                var code = body?.I32("code");
                var retry = body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("retry_after", out var r) ? r.ToString() : null;
                note = $"code={code?.ToString(CultureInfo.InvariantCulture) ?? "-"}" + (retry is null ? "" : $" retry_after={retry}");
            }

            output.WriteLine($"  [{status}] {label}{(note is null ? "" : $"  ({note})")}");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
            {
                output.WriteLine($"  {T.ProbeStopTag}  {T.ProbeEndsOnRefusal}");
                _aborted = true;
            }

            return new Reply(status, response.StatusCode == HttpStatusCode.OK ? body : null, note);
        }
    }

    private void Line(Reply? reply, Func<JsonElement, string> describe)
    {
        if (reply?.Body is { } body)
        {
            output.WriteLine($"        {describe(body)}");
        }
    }

    private void Skip(string label, string reason) => output.WriteLine(T.ProbeSkip(label, reason));

    private int Finish()
    {
        output.WriteLine();
        output.WriteLine(T.ProbeDone(_requests, _aborted));
        return _aborted ? 1 : 0;
    }

    private static string YesNo(bool value) => value ? T.Yes : T.No;

    private static int Count(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;

    private static string Describe(JsonElement? array) => array is { } a ? a.GetArrayLength().ToString(CultureInfo.InvariantCulture) : T.Missing;
}
