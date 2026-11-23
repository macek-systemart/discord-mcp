namespace DiscordMcp.Localization;

/// <summary>
/// Every text this server shows to the user or to Claude, in one language: tool output, error messages, CLI output.
/// One abstract member per text (a property for fixed text, a method with typed parameters for parameterized text), so
/// the compiler enforces that a language is complete. Numbers and dates are passed in already formatted (invariant);
/// only labels and sentences live here. Model-facing tool descriptions and the server instructions are not part of this:
/// they stay English in every language. To add a language: copy <c>English.cs</c>, translate it and add one line to
/// <see cref="Languages"/>.
/// </summary>
public abstract class Texts
{
    // ── Identity ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Language code as used in <c>Discord:Language</c> (<c>cs</c>, <c>en</c>), lower-case.</summary>
    public abstract string Code { get; }

    /// <summary>Name of the language in that language.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Marks messages written by the token's own account.</summary>
    public abstract string SelfMarker { get; }

    /// <summary>What a literal <see cref="SelfMarker"/> in Discord content is turned into. Must not contain any language's marker.</summary>
    public abstract string NeutralizedSelfMarker { get; }

    // ── Startup warnings (stderr) ────────────────────────────────────────────────────────────────────────────────

    public abstract string UnknownLanguageWarning(string code, string supported);

    public abstract string UnknownTimeZoneWarning(string id);

    // ── Message formatter ────────────────────────────────────────────────────────────────────────────────────────

    public abstract string UntrustedHeader { get; }

    public abstract string Legend { get; }

    public abstract string Edited { get; }

    /// <summary>Marks a bot account in the author part of a message header.</summary>
    public abstract string BotMarker { get; }

    public abstract string NoText { get; }

    /// <summary>Wraps a name or title in this language's quotation marks.</summary>
    public abstract string Quote(string text);

    public abstract string ChannelFallback(string id);

    public abstract string ThreadInChannel(string name, string id, string parent);

    public abstract string MessagesTitle(string where);

    public abstract string MessageDetailTitle(string where);

    public abstract string PinsTitle(string where);

    public abstract string NoMessagesInChannel { get; }

    public abstract string NoMessagesInDirection { get; }

    public abstract string ShownOf(int shown, int total);

    public abstract string CutByOutputLimit { get; }

    public abstract string OlderNone { get; }

    public abstract string NewerNone { get; }

    /// <param name="token">The cursor token (<c>before=…</c>), identical in every language.</param>
    public abstract string OlderCursor(string token);

    /// <param name="token">The cursor token (<c>after=…</c>), identical in every language.</param>
    public abstract string NewerCursor(string token);

    /// <param name="token">The cursor token (<c>offset=N</c>), identical in every language.</param>
    public abstract string NextCursor(string token);

    public abstract string EndOfResults { get; }

    public abstract string EndOfList { get; }

    public abstract string EndOfText { get; }

    public abstract string SearchHeader(int total, bool chronological);

    public abstract string NothingFound { get; }

    public abstract string NothingAtOffset(int offset);

    public abstract string SearchShown(int shown, int from, int to, int total);

    public abstract string NoPins { get; }

    public abstract string PinnedAt(string time);

    public abstract string OlderPinsCursor(string token);

    public abstract string NoMorePins { get; }

    // Thread flags
    public abstract string ThreadPrivateMember { get; }

    public abstract string ThreadPrivate { get; }

    public abstract string ThreadArchived { get; }

    public abstract string ThreadLocked { get; }

    public abstract string MessageCount(int count);

    public abstract string LastActivity(string time);

    // Message labels (system messages, replies)
    public abstract string ReplyTo(string id, string author);

    public abstract string ReplyToDeleted { get; }

    public abstract string ThreadStarter { get; }

    public abstract string ThreadStarterDeleted { get; }

    public abstract string ThreadCreated(string id, string name);

    public abstract string PinNotice(string id);

    public abstract string UserJoined { get; }

    public abstract string Boosted { get; }

    public abstract string BoostedTier(int tier);

    public abstract string PollResultLabel { get; }

    public abstract string CommandReply { get; }

    public abstract string AutoModAction { get; }

    public abstract string Crosspost { get; }

    public abstract string SystemMessage(int type);

    // Message details
    public abstract string Forwarded(string time);

    public abstract string Sticker(string name);

    public abstract string ThreadInline(string id, string name, int? messageCount);

    public abstract string Reactions(string list);

    public abstract string EmbedFallback { get; }

    public abstract string PollLine(string question, string answers, string state, bool multiselect);

    public abstract string PollMyVote { get; }

    public abstract string PollFinished { get; }

    public abstract string PollEnds(string time);

    public abstract string PollNoEnd { get; }

    // ── Server overview and thread list ──────────────────────────────────────────────────────────────────────────

    public abstract string OverviewIntro(string name, string id);

    public abstract string NoCategory { get; }

    public abstract string OtherTypes { get; }

    public abstract string OtherTypeSuffix(int type);

    public abstract string HiddenChannels(int count);

    public abstract string Nsfw { get; }

    public abstract string NoHistoryFlag { get; }

    public abstract string Topic(string topic);

    public abstract string TypeText { get; }

    public abstract string TypeVoice { get; }

    public abstract string TypeAnnouncement { get; }

    public abstract string TypeStage { get; }

    public abstract string TypeForum { get; }

    public abstract string TypeMedia { get; }

    public abstract string ThreadsHeader(string where, int? total);

    public abstract string NoThreads { get; }

    // ── Tool arguments ───────────────────────────────────────────────────────────────────────────────────────────

    public abstract string IdParameterInvalid(string name);

    public abstract string CursorParameterInvalid(string name);

    public abstract string OffsetNegative { get; }

    public abstract string PageFromOne { get; }

    public abstract string PageOnlyForPdf { get; }

    public abstract string OffsetRange { get; }

    public abstract string CursorsExclusive { get; }

    public abstract string SortInvalid { get; }

    public abstract string HasInvalid(string allowed);

    public abstract string QueryTooLong { get; }

    // ── Channel and message errors ───────────────────────────────────────────────────────────────────────────────

    public abstract string ChannelNotVisible(ulong id);

    public abstract string NotInChannelList(ulong id, int minutes);

    public abstract string ThreadHidden(ulong id);

    public abstract string OutsideServer(ulong id);

    public abstract string PostNotInForum(ulong post, ulong forum);

    public abstract string ChannelIsForum(ulong id);

    public abstract string ChannelHasNoMessages(ulong id, int type);

    public abstract string NoHistoryAccess(ulong id);

    public abstract string ChannelHasNoThreads(ulong id);

    public abstract string CannotSearchChannel(ulong id);

    public abstract string MessageNotFound(ulong messageId, ulong channelId);

    public abstract string MessageHasNoAttachment(ulong messageId, ulong attachmentId);

    // ── Attachments ──────────────────────────────────────────────────────────────────────────────────────────────

    public abstract string AttachmentAbout(string attachmentId, string name, string size, string messageId);

    public abstract string AttachmentGone { get; }

    public abstract string AttachmentTooLargeSkipped(long megabytes);

    public abstract string InvalidAttachmentName { get; }

    public abstract string OffsetBeyondText(int length);

    public abstract string FenceIntro(string about, int from, int to, int total);

    public abstract string NextPart(string token);

    public abstract string SavedFile(string reason, string path, bool marked);

    public abstract string ImageInline(string about, string mime);

    public abstract string ImageTooBigForInline(string about, string limit);

    public abstract string ImagePreview(string attachmentId, string name, string messageId, string mime, string dimensions, string originalSize, string previewSize, string previewMime);

    public abstract string LegacyOffice(string about);

    public abstract string NotImageOrText(string about);

    // Office and ZIP
    public abstract string OfficeUnreadable(string about);

    public abstract string OfficeTruncated { get; }

    public abstract string NoTextInFile { get; }

    public abstract string ExcelNote { get; }

    public abstract string OpenDocumentSpreadsheetNote { get; }

    public abstract string ZipNote { get; }

    public abstract string KindWord { get; }

    public abstract string KindExcel { get; }

    public abstract string KindPowerPoint { get; }

    public abstract string KindOpenDocumentText { get; }

    public abstract string KindOpenDocumentSpreadsheet { get; }

    public abstract string KindOpenDocumentPresentation { get; }

    public abstract string KindZip { get; }

    // Labels the office-extract child writes into the extracted text (wrapped as <c>--- label ---</c> by the extractor)
    public abstract string HeaderLabel { get; }

    public abstract string FooterLabel { get; }

    public abstract string SlideLabel(int number);

    public abstract string SheetLabel(string name);

    public abstract string DefaultSheetName(string number);

    public abstract string TableLabel(string? name);

    public abstract string EntryList(int count);

    // PDF
    public abstract string PagesLabel(int count);

    public abstract string PdfUnreadable(string about);

    public abstract string PdfEncrypted(string about);

    public abstract string PdfPageOutOfRange(int count);

    public abstract string PdfPageLabel(int number, int count);

    public abstract string PdfDescription(string about, int pageCount, int? onlyPage);

    public abstract string PdfTruncated(int maxPages, int maxChars);

    public abstract string PdfGarbled(string pages);

    public abstract string PdfImageBelow(int page, int width, int height, string size);

    public abstract string PdfImageUnavailable(int page, string reason);

    public abstract string ImageProblemTooLarge { get; }

    public abstract string ImageProblemUnsupported { get; }

    public abstract string ImageProblemCorrupt { get; }

    public abstract string PdfPicturePages(string pages);

    public abstract string PdfNoContent { get; }

    // ── Discord API and network errors ───────────────────────────────────────────────────────────────────────────

    public abstract string GuildIdMissing(string variable);

    public abstract string GuildIdSingleOnly(string variable);

    public abstract string GuildIdInvalid(string variable);

    /// <summary>The API client's fallback when options carry no guild id and no error text.</summary>
    public abstract string GuildIdNotConfigured(string variable);

    public abstract string GuildIdUnknown { get; }

    public abstract string InvalidReadParameters { get; }

    public abstract string InvalidPinsParameters { get; }

    public abstract string InvalidThreadSearchParameters { get; }

    public abstract string InvalidSearchParameters(string hasValues);

    public abstract string AttachmentUnavailable { get; }

    public abstract string DownloadTooLarge(string megabytes);

    public abstract string DownloadFailed { get; }

    public abstract string DownloadTimeout { get; }

    public abstract string IndexNotReady { get; }

    public abstract string RateLimited429 { get; }

    public abstract string UnexpectedResponse { get; }

    public abstract string NetworkError { get; }

    public abstract string NoResponseInTime { get; }

    public abstract string Unauthorized401 { get; }

    public abstract string Forbidden403 { get; }

    public abstract string NotFound404 { get; }

    public abstract string BadRequest400(string? detail);

    public abstract string ServerTrouble(int status);

    public abstract string UnexpectedStatus(int status);

    public abstract string NoToken { get; }

    public abstract string NoTokenRetry { get; }

    // ── Request governor, state, tool runner ─────────────────────────────────────────────────────────────────────

    public abstract string RequestWouldMissDeadline { get; }

    public abstract string HardStopped(string reason);

    public abstract string HardStopUnauthorized { get; }

    public abstract string HardStopCaptcha { get; }

    /// <summary>Reason shown for a stored stop whose code is unknown (written by an older version, or edited by hand).</summary>
    public abstract string HardStopUnknown { get; }

    public abstract string BreakerBlocked(string clock);

    public abstract string RateLimitedUntil(string clock);

    public abstract string DailyBudgetExhausted(int limit, string retryAt);

    public abstract string TenMinuteBudgetExhausted(int limit, string clock);

    public abstract string ToolBudgetExceeded(int maxCalls);

    public abstract string ToolTimeout(int seconds);

    public abstract string StateLockedByOtherProcess { get; }

    public abstract string StateFileUnavailable { get; }

    public abstract string GuardBlocked { get; }

    public abstract string ToolFailed { get; }

    // ── Command line ─────────────────────────────────────────────────────────────────────────────────────────────

    public abstract string Usage { get; }

    public abstract string UnknownCommand(string verb);

    public abstract string NetworkErrorCli { get; }

    public abstract string Cancelled { get; }

    public abstract string SetTokenNeedsConsole { get; }

    public abstract string SetTokenHowToFirst { get; }

    public abstract string SetTokenHowToSecond { get; }

    public abstract string SetTokenPrompt { get; }

    public abstract string TokenRead(int length);

    public abstract string TokenNotUserToken { get; }

    public abstract string TokenHasWhitespace { get; }

    public abstract string TokenBadLength { get; }

    public abstract string TokenRejected { get; }

    public abstract string DiscordReturnedNothingSaved(int status);

    public abstract string UnexpectedResponseNothingSaved { get; }

    public abstract string TokenSaved(string user, ulong userId);

    public abstract string TokenCleared { get; }

    public abstract string NoTokenWasStored { get; }

    public abstract string DiscordReturned(int status);

    public abstract string ConfigSnippetIntro { get; }

    public abstract string GuildIdPlaceholder { get; }

    public abstract string CheckUsage(string detail);

    public abstract string NoStoredToken { get; }

    public abstract string TokenCheckFailed(int status);

    public abstract string TokenOk(string user, ulong userId);

    public abstract string ServerCheckFailed(ulong guild, int status);

    public abstract string ServerOk(string name, ulong guild);

    // ── Probe (check --probe) ────────────────────────────────────────────────────────────────────────────────────

    public abstract string ProbeIntro(string guild, string userAgent);

    public abstract string Yes { get; }

    public abstract string No { get; }

    public abstract string Missing { get; }

    public abstract string CountLabel { get; }

    public abstract string MessagesLabel { get; }

    public abstract string ThreadsLabel { get; }

    public abstract string ChannelsLabel { get; }

    public abstract string TypesLabel { get; }

    public abstract string GuildIdMatches { get; }

    public abstract string ContainsTarget { get; }

    public abstract string ArrayOfArrays { get; }

    public abstract string ProbeCannotComputePermissions { get; }

    public abstract string ProbeAccessSummary(int visible, int withHistory, int hidden);

    public abstract string ProbeExpectedRefusal { get; }

    public abstract string ProbeSkip(string label, string reason);

    public abstract string ProbeReasonNoMessages { get; }

    public abstract string ProbeReasonNoManageThreads { get; }

    public abstract string ProbeReasonNoTextChannel { get; }

    public abstract string ProbeReasonNoForum { get; }

    public abstract string ProbeReasonNoThread { get; }

    public abstract string ProbeReasonNoVoiceChat { get; }

    /// <summary>Tag of a probe line that ends the run.</summary>
    public abstract string ProbeStopTag { get; }

    public abstract string ProbeEndsOnRefusal { get; }

    public abstract string ProbeBodyNotJson { get; }

    public abstract string ProbeDone(int requests, bool aborted);

    // ── Self-test (check --selftest) ─────────────────────────────────────────────────────────────────────────────

    public abstract string SelfTestIntro { get; }

    public abstract string SelfTestNoTextChannel { get; }

    public abstract string SelfTestNoThread { get; }

    public abstract string SelfTestNoForum { get; }

    public abstract string SelfTestNoAttachment(string step);

    public abstract string SelfTestAttachmentError { get; }

    public abstract string SelfTestImage(string mime, int kilobytes, int base64Kilobytes);

    public abstract string SelfTestSavedPdf { get; }

    public abstract string SelfTestSaved { get; }

    public abstract string SelfTestPdf(int pages, int chars);

    public abstract string SelfTestText(int chars);

    public abstract string SelfTestCounts(int lines, int chars);

    public abstract string SelfTestOkTag { get; }

    public abstract string SelfTestErrorTag { get; }

    public abstract string SelfTestAllOk { get; }

    public abstract string SelfTestFailures(int count);

    public abstract string StepFile { get; }

    public abstract string StepImage { get; }

    public abstract string StepThread { get; }

    public abstract string StepForum { get; }

    public abstract string StepForumPost { get; }
}
