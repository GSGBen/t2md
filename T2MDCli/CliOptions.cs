/*
 * Code and classes for commandline options the program uses.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommandLine;

namespace GoldenSyrupGames.T2MD
{
    // commandline arguments
    public class CliOptions
    {
        // The path under which the backups will be stored and the markdown folder structure
        // created. Will be created if it doesn't exist.
        [Option(
            "output-folder",
            Required = true,
            HelpText = "The path under which the backups will be stored and the markdown folder "
                + "structure created.\n"
                + "Will be created if it doesn't exist."
        )]
        public string OutputPath { get; set; } = "";

        // The path under which the backups will be stored and the markdown folder structure
        // created. Will be created if it doesn't exist.
        [Option(
            "config",
            Default = "t2md.json",
            HelpText = "The path to the configuration file containing the Trello API key and token.\n"
                + "If not specified will default to t2md.json in the current (shell's) folder.\n"
                + "If the file doesn't exist a template will be created there for you."
        )]
        public string ConfigFilePath { get; set; } = "";

        // The path under which the backups will be stored and the markdown folder structure
        // created. Will be created if it doesn't exist.
        [Option(
            "max-card-filename-title-length",
            Default = 40,
            HelpText = "The maximum number of characters that can be in the title in each card's "
                + "files' filenames.\n"
                + "Regardless of this the full title will be in the description card."
        )]
        public int MaxCardFilenameTitleLength { get; set; } = 0;

        // https://github.com/GSGBen/t2md/issues/4. Trello can have attachment errors
        [Option(
            "ignore-failed-attachment-downloads",
            Default = false,
            HelpText = "If specified, exceptions when downloading attachments will print a warning "
                + "instead of stopping the program."
        )]
        public bool IgnoreFailedAttachmentDownloads { get; set; } = false;

        // Obsidian doesn't work with back slashes. Allow replacing with forward slashes regardless
        // of platform
        [Option(
            "always-use-forward-slashes",
            Default = false,
            HelpText = "Obsidian doesn't work with back slashes. "
                + "If specified, always uses forward slashes for paths regardless of platform."
        )]
        public bool AlwaysUseForwardSlashes { get; set; } = false;

        /// <summary>Disable ordering of cards and lists using numbers to match the source order.
        /// <para />
        /// Using a negative bool because it's a user-facing switch and we want the default
        /// enabled.
        /// </summary>
        [Option(
            "no-numbering",
            Default = false,
            HelpText = "For exporting to systems like Obsidian, you probably don't want numbered "
                + "prefixes.\n If specified, this doesn't prefix numbers to maintain list and "
                + "card ordering."
        )]
        public bool NoNumbering { get; set; } = false;

        /// <summary>
        /// Replace emoji with _.
        /// </summary>
        [Option(
            "remove-emoji",
            Default = false,
            HelpText = "Dropbox doesn't support emoji in filenames.\n" + "This switch removes them."
        )]
        public bool RemoveEmoji { get; set; } = false;

        /// <summary>
        /// Write checklists, comments and attachments to the description file.
        /// </summary>
        [Option(
            "single-file",
            Default = false,
            HelpText = "If specified, checklists, comments and attachments are written to the same"
                + "file as the description."
        )]
        public bool SingleFile { get; set; } = false;

        public static Task PrintUsage(IEnumerable<Error> errors)
        {
            Console.WriteLine(
                @"
Usage:

- Run `t2md.exe --output-path <backup destination folder>` once to generate t2md.json in the current directory
- Browse to https://trello.com/app-key and copy your Key
- Replace <key> with it at the end of the following URL, browse to it and continue to retrieve your token
  - https://trello.com/1/authorize?name=Trello%20To%20Markdown&expiration=never&scope=read&response_type=token&key=<key>
- Put them both in t2md.json
- Run `t2md.exe --output-path <backup destination folder>` again.
  A t2md subfolder will be created under this and all Trello boards will be backed up to it.
  - **WARNING:** The entire t2md subfolder will be deleted and recreated each time.
"
            );
            return Task.CompletedTask;
        }

        // override with no parameters. Keep it here so that callers don't have to check or guess
        // that an empty list is fine
        public static void PrintUsage()
        {
            PrintUsage(new List<Error>());
        }
    }
}
