using System.Globalization;
using System.Reflection;
using DiscordMcp.Cli;
using DiscordMcp.Formatting;
using DiscordMcp.Localization;

namespace DiscordMcp.Tests;

/// <summary>Every language is complete and well formed: the compiler enforces the members, these tests the content.</summary>
public class TextsParityTests
{
    private const string CzechLetters = "ěščřžýáíéůúňťďĚŠČŘŽÝÁÍÉŮÚŇŤĎ";

    private static readonly MemberInfo[] Members =
    [
        .. typeof(Texts).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
        .. typeof(Texts).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName),
    ];

    [Fact]
    public void Language_codes_are_unique_lower_case_and_found_by_code()
    {
        var codes = Languages.All.Select(l => l.Code).ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("cs", codes);
        Assert.Contains("en", codes);
        foreach (var language in Languages.All)
        {
            Assert.Equal(language.Code.ToLowerInvariant(), language.Code);
            Assert.False(string.IsNullOrWhiteSpace(language.DisplayName));
            Assert.Same(language, Languages.Find(language.Code.ToUpperInvariant()));
        }
    }

    [Fact]
    public void The_members_of_Texts_are_discovered_by_reflection()
    {
        // Guards the parity test below against silently testing nothing.
        Assert.True(Members.Length > 200, $"found {Members.Length} members");
    }

    [Fact]
    public void Every_member_of_every_language_renders_non_empty_text_without_leftover_placeholders()
    {
        var calls = 0;
        foreach (var language in Languages.All)
        {
            foreach (var member in Members)
            {
                foreach (var (arguments, text) in Render(language, member))
                {
                    calls++;
                    var where = $"{language.Code}.{member.Name}({string.Join(", ", arguments.Select(a => a?.ToString() ?? "null"))})";
                    Assert.False(string.IsNullOrWhiteSpace(text), where);
                    Assert.DoesNotContain('{', text);
                    Assert.DoesNotContain('}', text);
                    foreach (var token in TokenArguments(member, arguments))
                    {
                        Assert.Contains(token, text, StringComparison.Ordinal);
                    }

                    if (language.Code == "en")
                    {
                        Assert.False(text.Any(CzechLetters.Contains), $"{where} contains Czech letters: {text}");
                    }
                }
            }
        }

        Assert.True(calls > Members.Length * Languages.All.Count);
    }

    [Fact]
    public void Self_markers_are_distinct_and_no_neutralized_form_contains_a_marker()
    {
        var markers = Languages.All.Select(l => l.SelfMarker).ToList();

        Assert.Equal(markers.Count, markers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var language in Languages.All)
        {
            Assert.NotEqual(language.SelfMarker, language.NeutralizedSelfMarker);
            foreach (var marker in markers)
            {
                Assert.DoesNotContain(marker, language.NeutralizedSelfMarker, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_cursor_tokens_of_the_formatter_are_the_same_in_every_language()
    {
        Assert.Equal("offset=25", Formatting.MessageFormatter.OffsetToken(25));
        Assert.Equal("before=1300000000000000010", Formatting.MessageFormatter.BeforeToken("1300000000000000010"));
        Assert.Equal("after=1300000000000000010", Formatting.MessageFormatter.AfterToken("1300000000000000010"));
    }

    /// <summary>Invokes a member with sample arguments (every combination of a few values per parameter).</summary>
    private static IEnumerable<(object?[] Arguments, string Text)> Render(Texts language, MemberInfo member)
    {
        if (member is PropertyInfo property)
        {
            yield return ([], (string)property.GetValue(language)!);
            yield break;
        }

        var method = (MethodInfo)member;
        var nullability = new NullabilityInfoContext();
        IEnumerable<object?[]> product = [[]];
        foreach (var parameter in method.GetParameters())
        {
            var candidates = Candidates(parameter, nullability);
            product = product.SelectMany(prefix => candidates.Select(c => (object?[])[.. prefix, c])).ToList();
        }

        foreach (var arguments in product)
        {
            yield return (arguments, (string)method.Invoke(language, arguments)!);
        }
    }

    private static object?[] Candidates(ParameterInfo parameter, NullabilityInfoContext nullability)
    {
        var type = parameter.ParameterType;
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return [null, .. Values(underlying, parameter.Name)];
        }

        var values = Values(type, parameter.Name);
        return type == typeof(string) && nullability.Create(parameter).WriteState == NullabilityState.Nullable ? [null, .. values] : values;
    }

    private static object?[] Values(Type type, string? name)
    {
        if (type == typeof(string))
        {
            return name == "token" ? ["before=123"] : ["Sample"];
        }

        if (type == typeof(int))
        {
            return [1, 3, 5]; // Czech has three plural forms
        }

        if (type == typeof(long))
        {
            return [3L];
        }

        if (type == typeof(ulong))
        {
            return [123UL];
        }

        if (type == typeof(bool))
        {
            return [true, false];
        }

        throw new NotSupportedException($"No sample value for {type} ({name}).");
    }

    /// <summary>Cursor tokens passed in must come out unchanged: only the label in front of them is translated.</summary>
    private static IEnumerable<string> TokenArguments(MemberInfo member, object?[] arguments) =>
        member is MethodInfo method
            ? method.GetParameters().Zip(arguments).Where(p => p.First.Name == "token").Select(p => (string)p.Second!)
            : [];
}

public class LanguageResolutionTests
{
    [Theory]
    [InlineData("en", "cs-CZ", "en")]
    [InlineData("cs", "de-DE", "cs")]
    [InlineData(null, "cs-CZ", "cs")]
    [InlineData(null, "cs", "cs")]
    [InlineData(null, "en-US", "en")]
    [InlineData(null, "de-DE", "en")]
    [InlineData("xx", "cs-CZ", "cs")]
    [InlineData("xx", "de-DE", "en")]
    [InlineData("  CS ", "en-US", "cs")]
    [InlineData("", "cs-CZ", "cs")]
    public void The_configured_language_beats_the_ui_culture_which_beats_English(string? configured, string culture, string expected)
    {
        Assert.Equal(expected, Languages.Resolve(configured, CultureInfo.GetCultureInfo(culture)).Code);
    }

    [Fact]
    public void The_invariant_culture_resolves_to_English()
    {
        Assert.Equal("en", Languages.Resolve(null, CultureInfo.InvariantCulture).Code);
    }

    [Fact]
    public void Use_overrides_the_language_for_this_flow_only_and_restores_it()
    {
        var before = Languages.Current;
        var other = Languages.All.First(l => l != before);

        using (Languages.Use(other))
        {
            Assert.Same(other, Languages.Current);
        }

        Assert.Same(before, Languages.Current);
    }

    [Fact]
    public async Task Use_does_not_leak_into_a_parallel_flow()
    {
        var czech = Languages.Find("cs")!;
        var english = Languages.Find("en")!;
        using var _ = Languages.Use(english);

        var other = await Task.Run(() =>
        {
            using var inner = Languages.Use(czech);
            return Languages.Current;
        });

        Assert.Same(czech, other);
        Assert.Same(english, Languages.Current);
    }

    [Fact]
    public void An_unknown_language_is_reported_in_the_language_that_ended_up_in_use()
    {
        var warnings = new StringWriter();

        var texts = Startup.Resolve("xx", null, CultureInfo.GetCultureInfo("cs-CZ"), warnings);

        Assert.Equal("cs", texts.Code);
        Assert.Contains("Neznámý jazyk 'xx'", warnings.ToString(), StringComparison.Ordinal);
        Assert.Contains("cs, en", warnings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_time_zone_is_reported_and_a_good_configuration_is_silent()
    {
        var warnings = new StringWriter();
        Startup.Resolve("en", "Nope/Nowhere", CultureInfo.InvariantCulture, warnings);
        Assert.Contains("Unknown time zone 'Nope/Nowhere'", warnings.ToString(), StringComparison.Ordinal);

        var quiet = new StringWriter();
        var texts = Startup.Resolve("en", "Europe/Prague", CultureInfo.InvariantCulture, quiet);
        Assert.Equal("en", texts.Code);
        Assert.Equal("", quiet.ToString());
    }

    [Fact]
    public void A_configured_culture_name_resolves_to_its_language_without_a_warning()
    {
        var quiet = new StringWriter();

        Assert.Equal("en", Startup.Resolve("en-US", null, CultureInfo.GetCultureInfo("cs-CZ"), quiet).Code);
        Assert.Equal("cs", Startup.Resolve("cs_CZ", null, CultureInfo.GetCultureInfo("en-US"), quiet).Code);
        Assert.Equal("", quiet.ToString());
    }
}

public class MarkerSpoofingTests
{
    [Theory]
    [InlineData("cs")]
    [InlineData("en")]
    public void Content_never_carries_a_self_marker_of_any_language(string active)
    {
        using var _ = Languages.Use(Languages.Find(active)!);

        foreach (var forged in new[] { "[JÁ]", "[já]", "[ME]", "[me]", "[Me]" })
        {
            var cleaned = Sanitizer.Text("a " + forged + " b");

            foreach (var language in Languages.All)
            {
                Assert.DoesNotContain(language.SelfMarker, cleaned, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Equal("a [JA] b", Sanitizer.Text("a [JÁ] b"));
        Assert.Equal("a [JA] b", Sanitizer.Text("a [já] b"));
        Assert.Equal("a (ME) b", Sanitizer.Text("a [ME] b"));
        Assert.Equal("a (ME) b", Sanitizer.Text("a [me] b"));
    }

    [Theory]
    [InlineData("cs")]
    [InlineData("en")]
    public void A_display_name_cannot_carry_a_marker_of_any_language(string active)
    {
        using var _ = Languages.Use(Languages.Find(active)!);

        foreach (var forged in new[] { "Eva [JÁ]", "Eva [ME]", "[ME] Eva" })
        {
            var cleaned = Sanitizer.Name(forged, 100);
            foreach (var language in Languages.All)
            {
                Assert.DoesNotContain(language.SelfMarker, cleaned, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void The_active_language_decides_the_marker_the_formatter_uses()
    {
        using (Languages.Use(Languages.Find("cs")!))
        {
            Assert.Equal("[JÁ]", Sanitizer.SelfMarker);
        }

        using (Languages.Use(Languages.Find("en")!))
        {
            Assert.Equal("[ME]", Sanitizer.SelfMarker);
        }
    }

    [Fact]
    public void A_marker_split_by_invisible_characters_or_accents_is_still_neutralized_in_every_language()
    {
        var zeroWidth = ((char)0x200B).ToString();
        var combining = ((char)0x0301).ToString();

        Assert.Equal("[JA]", Sanitizer.Line("[J" + zeroWidth + "Á]"));
        Assert.Equal("[JA]", Sanitizer.Line("[JA" + combining + "]"));
        Assert.Equal("(ME)", Sanitizer.Line("[M" + zeroWidth + "E]"));
    }
}

/// <summary>The self-test reads ids out of the tools' own output: that must work in every language.</summary>
public class SelfTestScrapingTests
{
    private static readonly string Channel = Fixture.TextChannel.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_same_thread_id_is_found_in_a_message_list_in_every_language()
    {
        var found = new List<string?>();
        foreach (var language in Languages.All)
        {
            using var _ = Languages.Use(language);
            using var h = new ToolsHarness();

            var messages = await h.Messages.ReadMessagesAsync(Channel);

            found.Add(SelfTest.FirstThreadId(messages));
            Assert.NotNull(found[^1]);
            Assert.Contains(found[^1]!, messages, StringComparison.Ordinal);
        }

        Assert.Single(found.Distinct());
    }

    [Theory]
    [InlineData("cs")]
    [InlineData("en")]
    public async Task Thread_markers_are_found_in_real_formatter_output_whatever_words_the_language_puts_before_the_id(string code)
    {
        const ulong thread = 1300000000000000006;
        const ulong noticeId = 1300000000000000980;
        using var _ = Languages.Use(Languages.Find(code)!);
        using var h = new ToolsHarness();
        var guild = await h.GuildAsync();
        var author = new DiscordMcp.Discord.DiscordUser { Id = ToolsData.Author, Username = "petr", GlobalName = "Petr" };

        // The type-18 notice puts its label in the middle of the header line, after the author.
        var notice = h.Formatter.FormatMessage(guild, new DiscordMcp.Discord.DiscordMessage
        {
            Id = noticeId,
            ChannelId = Fixture.TextChannel,
            Type = DiscordMcp.Discord.MessageTypes.ThreadCreated,
            Content = "Topic",
            Timestamp = DiscordMcp.Discord.Snowflake.ToTimestamp(noticeId),
            Author = author,
            MessageReference = new DiscordMcp.Discord.MessageReference { ChannelId = thread },
        });
        // A message that started a thread has the thread as an indented detail line.
        var started = h.Formatter.FormatMessage(guild, new DiscordMcp.Discord.DiscordMessage
        {
            Id = noticeId + 1,
            ChannelId = Fixture.TextChannel,
            Content = "Question",
            Timestamp = DiscordMcp.Discord.Snowflake.ToTimestamp(noticeId + 1),
            Author = author,
            Thread = new DiscordMcp.Discord.DiscordChannel { Id = thread, Name = "Topic", MessageCount = 3 },
        });

        Assert.Contains(code == "cs" ? "založil(a) vlákno" : "started thread", notice, StringComparison.Ordinal);
        Assert.Equal("1300000000000000006", SelfTest.FirstThreadId(notice));
        Assert.Equal("1300000000000000006", SelfTest.FirstThreadId(started));
        Assert.Equal("1300000000000000006", SelfTest.FirstThreadId("── " + Languages.Current.ThreadInChannel("Topic", "1300000000000000006", "general") + "\n"));
        Assert.Equal("1300000000000000006", SelfTest.FirstThreadId(MessageFormatter.ThreadLine(
            new DiscordMcp.Discord.DiscordChannel { Id = thread, Name = "Topic", Type = DiscordMcp.Discord.ChannelTypes.PublicThread }, isMember: false)));
    }

    [Fact]
    public void A_thread_id_is_not_taken_from_text_without_a_thread_marker()
    {
        Assert.Null(SelfTest.FirstThreadId("[2026-11-20 14:03 +01:00] 1300000000000000010 Petr (@petr, 700000000000000002): hello"));
        Assert.Null(SelfTest.FirstThreadId(Languages.Current.ThreadStarter));
    }

    [Theory]
    [InlineData("cs", "obrázek", "uloženo na disk", "PDF se nepřečetlo, uloženo na disk", "PDF (1 strana), text", "text ")]
    [InlineData("en", "image", "saved to disk", "PDF not read, saved to disk", "PDF (1 page), text", "text ")]
    public async Task An_attachment_result_is_summarized_in_the_active_language(string code, string image, string saved, string unreadablePdf, string readPdf, string plain)
    {
        using var _ = Languages.Use(Languages.Find(code)!);

        // The result is a text block and the picture itself: both are summarized.
        Assert.Contains(image + " image/png (", await SummaryAsync("graf.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13]), StringComparison.Ordinal);
        Assert.Equal(saved, await SummaryAsync("data.bin", [0xFF, 0x00, 0xFE, 0x01, 0x80]));
        Assert.StartsWith(readPdf, await SummaryAsync("faktura.pdf", TestPdf.Invoice()), StringComparison.Ordinal);
        Assert.Equal(unreadablePdf, await SummaryAsync("rozbite.pdf", System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 not really"), new NullPdfReader()));
        Assert.StartsWith(plain, await SummaryAsync("poznamky.txt", System.Text.Encoding.UTF8.GetBytes("ahoj")), StringComparison.Ordinal);
    }

    private static async Task<string> SummaryAsync(string fileName, byte[] bytes, Pdf.IPdfReader? pdf = null)
    {
        const ulong messageId = 1300000000000000970;
        const ulong attachmentId = 800000000000000970;
        var path = $"/attachments/{Fixture.TextChannel}/{attachmentId}/file";
        var api = ScriptedDiscordApi.WithFixtureMessages();
        api.Messages[Fixture.TextChannel].Add(new DiscordMcp.Discord.DiscordMessage
        {
            Id = messageId,
            ChannelId = Fixture.TextChannel,
            Timestamp = DiscordMcp.Discord.Snowflake.ToTimestamp(messageId),
            Author = new DiscordMcp.Discord.DiscordUser { Id = ToolsData.Author, Username = "petr" },
            Attachments =
            [
                new DiscordMcp.Discord.DiscordAttachment
                {
                    Id = attachmentId,
                    Filename = fileName,
                    Size = bytes.Length,
                    Url = $"https://cdn.discordapp.com{path}?ex=6a000000&is=69f00000&hm=SECRET&",
                },
            ],
        });
        api.Files[path] = bytes;
        using var h = new ToolsHarness(api, pdf: pdf);

        var result = await h.Attachments.GetAttachmentAsync(Channel, messageId.ToString(CultureInfo.InvariantCulture), attachmentId.ToString(CultureInfo.InvariantCulture));

        return SelfTest.Summarize(result);
    }

    private sealed class NullPdfReader : Pdf.IPdfReader
    {
        public Task<Pdf.PdfExtract?> ReadAsync(string path, Pdf.PdfRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<Pdf.PdfExtract?>(null);
    }
}
