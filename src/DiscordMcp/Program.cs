using DiscordMcp;
using DiscordMcp.Cli;

// Claude Desktop starts the exe without arguments: that is the stdio MCP server.
var verb = args.Length > 0 ? args[0] : "serve";
var rest = args.Skip(1).ToArray();

// Language and display time zone are process-wide and must be set before anything produces text (options included).
// The office-extract child also gets its language explicitly from the parent.
Startup.Configure();

return verb switch
{
    "serve" => await AppHost.RunServerAsync(rest),
    "set-token" or "clear-token" or "guilds" or "check" => await Commands.RunAsync(verb, rest),
    DiscordMcp.Pdf.PdfCommand.Verb => DiscordMcp.Pdf.PdfCommand.Run(rest), // internal: the server's PDF child process
    DiscordMcp.Office.OfficeCommand.Verb => DiscordMcp.Office.OfficeCommand.Run(rest), // internal: Office/ZIP child process
    "help" or "--help" or "-h" or "/?" => Commands.PrintUsage(unknownVerb: null),
    _ => Commands.PrintUsage(unknownVerb: verb),
};
