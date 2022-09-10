using System;
using CommandLine;
using Spectre.Console;
using System.Text.Json;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace GoldenSyrupGames.T2MD
{
    public class Cli
    {
        // create the HttpClient we'll use for all our requests. see
        // https://www.aspnetmonsters.com/2016/08/2016-08-27-httpclientwrong/
        private static HttpClient _httpClient = new HttpClient();

        // Trello credentials
        private static string _apiKey = "";
        private static string _apiToken = "";

        // the path to the t2md folder inside the user-specified folder we write
        // to
        private static string _outputPath = "";

        // shared options for json work
        private static JsonSerializerOptions _jsonDeserializeOptions = new JsonSerializerOptions
        {
            // match camelCase json to PascalCase C# class
            PropertyNameCaseInsensitive = true,
            // some users have boards (in the per-board processing later, but set all together here)
            // with pos values as strings, e.g. "123.45". Support those
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            // Trello also (used to?) encode some positions as "bottom". Handle that
            Converters = { new TrelloDoubleJsonConverter() }
        };

        static async Task Main(string[] args)
        {
            // get commandline arguments based on Options, otherwise fail and print help.
            await Parser.Default
                .ParseArguments<CliOptions>(args)
                .MapResult(
                    // when we have all valid options
                    RunAsync,
                    // when something's missing or wrong
                    CliOptions.PrintUsage
                )
                .ConfigureAwait(false);
        }

        // Main() with parsed arguments.
        // guaranteed to have the required arguments
        private static async Task RunAsync(CliOptions options)
        {
            AnsiConsole.MarkupLine($"[cyan]Output path: {options.OutputPath}[/]");
            AnsiConsole.MarkupLine($"[cyan]Config file: {options.ConfigFilePath}[/]");

            // ensure our output folder exists.
            // use a subfolder of the user-specified path for safety
            _outputPath = Path.Combine(options.OutputPath, "t2md");
            // recreate it every time
            if (Directory.Exists(_outputPath))
            {
                // give explorer and maybe Dropbox time to close their handles. `Directory.Delete`
                // was throwing "file in use"
                FileSystem.DeleteDirectoryRecursivelyWithRetriesAndDelay(_outputPath, 10, 100);
            }
            Directory.CreateDirectory(_outputPath);

            // read from the json file if it exists
            if (File.Exists(options.ConfigFilePath))
            {
                string jsonString = File.ReadAllText(options.ConfigFilePath);
                var configFileOptions = JsonSerializer.Deserialize<ConfigFileOptions>(jsonString);

                // ensure it contains what we need or get them to reenter it
                if (configFileOptions == null)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Can't read data from {options.ConfigFilePath}. "
                            + $"Please correct the file or delete it and run again to have it "
                            + $"recreated.[/]"
                    );
                    Environment.Exit(1);
                }
                else if (
                    string.IsNullOrEmpty(configFileOptions.ApiKey)
                    || string.IsNullOrEmpty(configFileOptions.ApiToken)
                )
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Can't find ApiKey or ApiToken in {options.ConfigFilePath}. "
                            + $"Please correct the file or delete it and run again to have it "
                            + $"recreated.[/]"
                    );
                    Environment.Exit(1);
                }
                else if (configFileOptions.ApiKey == "key" || configFileOptions.ApiToken == "token")
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Please fill out ApiKey and ApiToken in {options.ConfigFilePath}.[/]"
                    );
                    CliOptions.PrintUsage();
                    Environment.Exit(1);
                }
                else
                {
                    _apiKey = configFileOptions.ApiKey;
                    _apiToken = configFileOptions.ApiToken;
                    //AnsiConsole.MarkupLine($"[magenta]API key: {_apiKey}[/]");
                }
            }
            // otherwise create the template json file if it doesn't exist
            else
            {
                var configTemplate = new ConfigFileOptions { ApiKey = @"key", ApiToken = @"token" };
                // pretty print
                var serializerOptions = new JsonSerializerOptions { WriteIndented = true };
                string jsonString = JsonSerializer.Serialize(configTemplate, serializerOptions);
                File.WriteAllText(options.ConfigFilePath, jsonString);
                AnsiConsole.MarkupLine(
                    $"[magenta]Created config file template {options.ConfigFilePath}. "
                        + $"Please enter your API key and token there then run again.[/]"
                );
                Environment.Exit(0);
            }

            // get a list of all boards
            var url = $"https://api.trello.com/1/members/me/boards?key={_apiKey}&token={_apiToken}";
            string textResponse = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            var trelloApiBoards = new List<TrelloApiBoardModel>();
            trelloApiBoards = JsonSerializer.Deserialize<List<TrelloApiBoardModel>>(
                textResponse,
                _jsonDeserializeOptions
            );
            if (trelloApiBoards == null)
            {
                throw new Exception(
                    "No boards retrieved from https://api.trello.com/1/members/me/boards"
                );
            }

            // ensure they all have the required properties (not natively possible with
            // System.Text.Json)
            foreach (TrelloApiBoardModel trelloApiBoard in trelloApiBoards)
            {
                if (!trelloApiBoard.AreAllRequiredFieldsFilled())
                {
                    throw new Exception(
                        "Boards retrieved from https://api.trello.com/1/members/me/boards missing "
                            + "required properties"
                    );
                }
            }
            ;

            // list them
            AnsiConsole.MarkupLine("[blue]Boards to back up:[/]");
            foreach (TrelloApiBoardModel trelloApiBoard in trelloApiBoards)
            {
                AnsiConsole.MarkupLine($"    [blue]{trelloApiBoard.Name}[/]");
            }

            // process each board asynchronously
            AnsiConsole.MarkupLine("[blue]Processing each board:[/]");
            var boardTasks = new List<Task>();
            foreach (TrelloApiBoardModel trelloApiBoard in trelloApiBoards)
            {
                // Starting each board with Task.Run is consistently faster than just async/await
                // within the board, even though it's I/O bound. Probably because the JSON parsing
                // is CPU bound and it's doing enough of it per board.
                boardTasks.Add(Task.Run(() => ProcessTrelloBoardAsync(trelloApiBoard, options)));
            }
            await Task.WhenAll(boardTasks).ConfigureAwait(false);
        }

        /// <summary>
        /// The per-board code.
        /// </summary>
        /// <param name="trelloApiBoard">Model of the board generated from the API call that
        /// enumerates them, not the downloaded json</param>
        /// <param name="options">The parsed options we received on the commandline from the
        /// user</param>
        /// <returns></returns>
        private static async Task ProcessTrelloBoardAsync(
            TrelloApiBoardModel trelloApiBoard,
            CliOptions options
        )
        {
            // quick testing
            //if (trelloApiBoard.Name != "Computers")
            //{
            //    return;
            //}

            AnsiConsole.MarkupLine($"    [blue]Starting {trelloApiBoard.Name}[/]");

            // - retrieve the full backup of each, the same as "Menu > more > print and export >
            //   JSON" in the web UI.
            // - the backup URL is just the board with .json: https://trello.com/b/<boardID>.json
            // - this grabs everything without having to specify everything we want via the API,
            //   which may change on us in the future.
            // - also as a quick check the (formatted) output of this has more lines than the output
            //   of trello-backup.php for the same board.
            var backupUrl = $"https://trello.com/b/{trelloApiBoard.ShortLink}.json";
            using var request = new HttpRequestMessage(HttpMethod.Get, backupUrl);
            // don't auth with parameters, authorize with the weird header like the S3 requests:
            //     Authorization: OAuth oauth_consumer_key="<api key>", oauth_token="<api token>"
            request.Headers.Add(
                "Authorization",
                $"OAuth oauth_consumer_key=\"{_apiKey}\", oauth_token=\"{_apiToken}\""
            );
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // write the json to file (overwrite)
            string usableBoardName = FileSystem.SanitiseForPath(trelloApiBoard.Name);
            string boardOutputFilePath = Path.Combine(_outputPath, $"{usableBoardName}.json");
            using FileStream fileStream = File.Create(boardOutputFilePath);
            using Stream contentStream = await response.Content
                .ReadAsStreamAsync()
                .ConfigureAwait(false);
            await contentStream.CopyToAsync(fileStream).ConfigureAwait(false);

            // create a folder for each board.
            // without this linux will happily write /'s
            string boardPath = Path.Combine(_outputPath, usableBoardName);
            Directory.CreateDirectory(boardPath);
            // do the same for a subfolder for archived lists
            string archivedListPath = Path.Combine(boardPath, "archived");
            Directory.CreateDirectory(archivedListPath);

            // parse the board's json. first reset the stream as we read it above
            contentStream.Position = 0;
            using var boardJsonStreamReader = new StreamReader(contentStream);
            var trelloBoard = JsonSerializer.Deserialize<TrelloBoardModel>(
                await boardJsonStreamReader.ReadToEndAsync().ConfigureAwait(false),
                _jsonDeserializeOptions
            );
            if (trelloBoard == null)
            {
                throw new Exception($"Failed to parse {boardOutputFilePath}");
            }

            // ensure we have required properties
            if (!trelloBoard.AreAllRequiredFieldsFilled())
            {
                throw new Exception($"{boardOutputFilePath} missing required properties.");
            }

            // sort the lists by their position in the board so we order the same way as the GUI
            IOrderedEnumerable<TrelloListModel> orderedLists = trelloBoard.Lists.OrderBy(
                list => list.Pos
            );

            // create folders for each list.
            // number them so they're shown in order
            var nonArchivedListIndex = 0;
            var archivedListIndex = 0;
            foreach (TrelloListModel trelloList in orderedLists)
            {
                ProcessTrelloList(
                    trelloList,
                    archivedListPath,
                    boardPath,
                    ref archivedListIndex,
                    ref nonArchivedListIndex
                );
            }

            // - sort the cards by their position in their list so we order by list order.
            // - we don't care about cross-list position, just the position relative to other cards
            //   in the list.
            // - fun fact: Trello doesn't use contiguous ints for their position: they use
            //   large-ranging floats so that card positions can be updated without recalculating
            //   all.
            IOrderedEnumerable<TrelloCardModel> orderedCards = trelloBoard.Cards.OrderBy(
                card => card.Pos
            );

            // process each card
            var CardTasks = new List<Task>();
            foreach (TrelloCardModel trelloCard in orderedCards)
            {
                TrelloListModel parentList = trelloBoard.Lists
                    .Where(list => list.ID == trelloCard.IDList)
                    .First();

                // give the card the right index and path depending on whether it's archived or not.
                // outside the function because they're all running in parallel and would read the
                // wrong index
                int cardIndex = 0;
                string listPath = "";
                if (trelloCard.Closed)
                {
                    cardIndex = parentList.ArchivedCardIndex;
                    listPath = parentList.ArchiveFolderPath;
                }
                else
                {
                    cardIndex = parentList.NonArchivedCardIndex;
                    listPath = parentList.FolderPath;
                }

                // run them in parallel
                CardTasks.Add(
                    ProcessTrelloCardAsync(trelloCard, cardIndex, listPath, trelloBoard, options)
                );

                // again outside the function because they're all running in parallel
                if (trelloCard.Closed)
                {
                    parentList.ArchivedCardIndex++;
                }
                else
                {
                    parentList.NonArchivedCardIndex++;
                }
            }
            await Task.WhenAll(CardTasks);

            AnsiConsole.MarkupLine($"    [green]Finished {trelloApiBoard.Name}[/]");
        }

        /// <summary>
        /// Creates a numbered folder for the list in the right subdirectory (based on whether it's
        /// archived or not) and records the path in its model object. <para/>
        /// Updates the list index to be passed to the next index. <para/>
        /// No async because windows doesn't have an async CreateDirectory. <para/>
        /// </summary>
        /// <param name="trelloList">The model of the list parsed from the json backup</param>
        /// <param name="archivedListPath">the full path to the outer folder for archived
        /// lists</param>
        /// <param name="boardPath">the full path to the folder for this board, where non-archived
        /// lists are stored</param>
        /// <param name="archivedListIndex">index to use for this board if it's archived</param>
        /// <param name="nonArchivedListIndex">index to use for this board if it's not
        /// archived</param>
        private static void ProcessTrelloList(
            TrelloListModel trelloList,
            string archivedListPath,
            string boardPath,
            ref int archivedListIndex,
            ref int nonArchivedListIndex
        )
        {
            if (!trelloList.AreAllRequiredFieldsFilled())
            {
                throw new Exception($"a list is missing required properties");
            }

            // put archived lists in a subfolder
            string outerPath = trelloList.Closed ? archivedListPath : boardPath;
            int listIndex = trelloList.Closed ? archivedListIndex : nonArchivedListIndex;

            // create a folder for each list.
            // remove special characters
            string usableListName = FileSystem.SanitiseForPath(trelloList.Name);
            var listFolderName = $"{listIndex} {usableListName}";
            string listPath = Path.Combine(outerPath, listFolderName);
            Directory.CreateDirectory(listPath);

            // record it as we'll need it for the cards and we don't do cards per-list
            trelloList.FolderPath = listPath;

            // do the same for a subfolder for archived cards
            string archivedCardPath = Path.Combine(listPath, "archived");
            Directory.CreateDirectory(archivedCardPath);
            trelloList.ArchiveFolderPath = archivedCardPath;

            if (trelloList.Closed)
            {
                archivedListIndex++;
            }
            else
            {
                nonArchivedListIndex++;
            }
        }

        /// <summary>
        /// Creates a markdown file each for the board's description, comments, checklists and list
        /// of attachments. <para/>
        /// Downloads all attachments. <para/>
        /// Replaces references to the attachment URLs with the new relative ones in the description
        /// and comments. <para/>
        /// </summary>
        /// <param name="trelloCard">The model of the card parsed from the json backup</param>
        /// <param name="cardIndex">The order of this card in the list, depending on whether it's
        /// archived or not. Archived and unarchived cards should have unique contiguous
        /// numbering.</param>
        /// <param name="trelloBoard">The model of the board parsed from the json backup. Card
        /// features like lists and comments are actually stored all together at a board
        /// level.</param>
        /// <param name="options">The parsed options we received on the commandline from the
        /// user</param>
        /// <returns></returns>
        private static async Task ProcessTrelloCardAsync(
            TrelloCardModel trelloCard,
            int cardIndex,
            string cardFolderPath,
            TrelloBoardModel trelloBoard,
            CliOptions options
        )
        {
            // restrict the maximum filename length for all files. Just via the title, not any
            // suffix or prefix
            int actualOrRestrictedLength = Math.Min(
                trelloCard.Name.Length,
                options.MaxCardFilenameTitleLength
            );
            string usableCardName = trelloCard.Name.Substring(0, actualOrRestrictedLength);
            // remove special characters
            usableCardName = FileSystem.SanitiseForPath(usableCardName);

            // write the description to a markdown file
            Task<(string, string)> WriteCardDescriptionTask = WriteCardDescriptionAsync(
                trelloCard,
                cardFolderPath,
                cardIndex,
                usableCardName,
                options
            );

            // write the checklists to a file if there are any
            Task WriteCardChecklistsTask = WriteCardChecklistsAsync(
                trelloCard,
                trelloBoard,
                cardFolderPath,
                cardIndex,
                usableCardName,
                options
            );

            // download uploaded attachments if there are any. Only direct file uploads or image
            // data pastes, not e.g. pasted http links
            IEnumerable<TrelloAttachmentModel> uploadedAttachments = trelloCard.Attachments.Where(
                attachment => attachment.IsUpload
            );
            if (uploadedAttachments.Count() > 0)
            {
                await DownloadTrelloCardAttachmentsAsync(
                    uploadedAttachments,
                    trelloCard,
                    cardIndex,
                    usableCardName,
                    cardFolderPath,
                    options.IgnoreFailedAttachmentDownloads,
                    options.AlwaysUseForwardSlashes,
                    trelloBoard.Name
                );
            }

            List<TrelloActionModel> cardComments = await GetCardCommentsAsync(trelloCard.ID);

            // save the path and contents of the description and comment files so we can
            // find/replace URLs in them
            (string commentsContents, string commentsPath) = await WriteCardCommentsAsync(
                trelloCard,
                trelloBoard,
                cardFolderPath,
                cardIndex,
                usableCardName,
                options,
                cardComments
            );
            (string descriptionContents, string descriptionPath) = await WriteCardDescriptionTask;
            await WriteCardChecklistsTask;

            // replace full http attachment URLs with local relative paths so the description and
            // comments now link to the downloaded copies.
            await UpdateAttachmentReferencesAsync(
                uploadedAttachments,
                descriptionContents,
                descriptionPath,
                commentsContents,
                commentsPath
            );
        }

        /// <summary>
        /// Write the card description to a markdown file.
        /// </summary>
        /// <param name="trelloCard">The model of the card parsed from the json backup</param>
        /// <param name="cardFolderPath">The archived or non-archived folder items in this card
        /// write to.</param>
        /// <param name="cardIndex">The order of this card in the list, depending on whether it's
        /// archived or not.</param>
        /// <param name="usableCardName">The length-limited and usable version of the card title -
        /// special characters should be removed already.</param>
        /// <param name="options">The parsed options we received on the commandline from the
        /// user</param>
        /// <returns>Returns the markdown contents of the description in the first tuple member and
        /// the path to the description file in the second.</returns>
        private static async Task<(string, string)> WriteCardDescriptionAsync(
            TrelloCardModel trelloCard,
            string cardFolderPath,
            int cardIndex,
            string usableCardName,
            CliOptions options
        )
        {
            // inject the full title into its output
            var descriptionContents = $"# {trelloCard.Name}\n\n{trelloCard.Desc}";
            // sort the cards in order
            var descriptionFilename = $"{cardIndex} {usableCardName}.md";
            // put archived cards in a subfolder
            string descriptionPath = Path.Join(cardFolderPath, descriptionFilename);
            await File.WriteAllTextAsync(descriptionPath, descriptionContents)
                .ConfigureAwait(false);
            return (descriptionContents, descriptionPath);
        }

        /// <summary>
        /// Write the card checklists to a markdown file if the card has any.
        /// </summary>
        /// <param name="trelloCard">The model of the card parsed from the json backup</param>
        /// <param name="trelloBoard">The model of  the board parsed from the json backup. Card
        /// features like lists and comments are actually stored all together at a board
        /// level.</param>
        /// <param name="cardFolderPath">The archived or non-archived folder items in this card
        /// write to.</param>
        /// <param name="cardIndex">The order of this card in the list, depending on whether it's
        /// archived or not.</param>
        /// <param name="usableCardName">The length-limited and usable version of the card title -
        /// special characters should be removed already.</param>
        /// <param name="options">The parsed options we received on the commandline from the
        /// user</param>
        /// <returns></returns>
        private static async Task WriteCardChecklistsAsync(
            TrelloCardModel trelloCard,
            TrelloBoardModel trelloBoard,
            string cardFolderPath,
            int cardIndex,
            string usableCardName,
            CliOptions options
        )
        {
            // checklists are under the board so retrieve the ones for this card
            IEnumerable<TrelloChecklistModel> cardChecklists = trelloBoard.Checklists.Where(
                checklist => trelloCard.IDChecklists.Contains(checklist.ID)
            );
            if (cardChecklists.Count() > 0)
            {
                // start with a modified title for the whole file
                var checklistsContents = $"# {trelloCard.Name} - Checklists\n\n";
                // maintain the checklist order in the card
                IOrderedEnumerable<TrelloChecklistModel> orderedCardChecklists =
                    cardChecklists.OrderBy(checklist => checklist.Pos);
                foreach (TrelloChecklistModel trelloChecklist in orderedCardChecklists)
                {
                    // write each checklist title as the next subheading
                    checklistsContents += $"## {trelloChecklist.Name}\n\n";
                    // write each entry in the checklist
                    foreach (TrelloCheckItemModel checkItem in trelloChecklist.CheckItems)
                    {
                        // use the [x] or [ ] format for checklist items
                        var checkContents = checkItem.State == "complete" ? "x" : " ";
                        checklistsContents += $"- [{checkContents}] {checkItem.Name}\n";
                    }
                    checklistsContents += "\n";
                }
                // write the file
                var checklistsFilename = $"{cardIndex} {usableCardName} - Checklists.md";
                string checklistsPath = Path.Join(cardFolderPath, checklistsFilename);
                await File.WriteAllTextAsync(checklistsPath, checklistsContents)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Returns the commentCart action models for a card. <para />
        /// Retrieves them from the API because the json export only contains the last 1000 actions.
        /// </summary>
        /// <param name="cardID">The ID of the card to retrieve comments for.</param>
        /// <returns></returns>
        private static async Task<List<TrelloActionModel>> GetCardCommentsAsync(string cardID)
        {
            // get all actions for this card, including comments
            var url =
                $"https://api.trello.com/1/cards/{cardID}/actions?key={_apiKey}&token={_apiToken}";
            string textResponse = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            var cardActions = new List<TrelloActionModel>();
            cardActions = JsonSerializer.Deserialize<List<TrelloActionModel>>(
                textResponse,
                _jsonDeserializeOptions
            );

            // no actions = no comments
            if (cardActions == null)
            {
                return new List<TrelloActionModel>();
            }

            // return comment actions only
            return cardActions.Where(cardAction => cardAction.Type == "commentCard").ToList();
        }

        /// <summary>
        /// Write the card comments to a markdown file if the card has any.
        /// </summary>
        /// <param name="trelloCard">The model of the card parsed from the json backup</param>
        /// <param name="trelloBoard">The model of  the board parsed from the json backup. Card
        /// features like lists and comments are actually stored all together at a board
        /// level.</param>
        /// <param name="cardFolderPath">The archived or non-archived folder items in this card
        /// write to.</param>
        /// <param name="cardIndex">The order of this card in the list, depending on whether it's
        /// archived or not.</param>
        /// <param name="usableCardName">The length-limited and usable version of the card title -
        /// special characters should be removed already.</param>
        /// <param name="options">The parsed options we received on the commandline from the
        /// user</param>
        /// <param name="commentActions">The comments to write to the file.</param>
        /// <returns>Returns the markdown contents of the comments file in the first tuple member
        /// and the path to it in the second.</returns>
        private static async Task<(string, string)> WriteCardCommentsAsync(
            TrelloCardModel trelloCard,
            TrelloBoardModel trelloBoard,
            string cardFolderPath,
            int cardIndex,
            string usableCardName,
            CliOptions options,
            List<TrelloActionModel> cardComments
        )
        {
            if (cardComments.Count() > 0)
            {
                // start with a modified title for the whole file
                var commentsContents = $"# {trelloCard.Name} - Comments\n\n";
                // order the comments by date. ISO 8601 dates can be sorted as a string
                IOrderedEnumerable<TrelloActionModel> orderedCardComments = cardComments.OrderBy(
                    comment => comment.Date
                );
                foreach (TrelloActionModel trelloComment in orderedCardComments)
                {
                    // separate each card's contents
                    commentsContents += "## " + new string('-', 40) + "\n\n";
                    //commentsContents += "## " + new string('-', 10) + $"Comment on
                    //{trelloComment.Date}" + new string('-', 10) + "\n\n";

                    commentsContents += trelloComment.Data.Text;
                    commentsContents += "\n\n";
                }
                // write the file
                var commentsFilename = $"{cardIndex} {usableCardName} - Comments.md";
                var commentsPath = Path.Join(cardFolderPath, commentsFilename);
                await File.WriteAllTextAsync(commentsPath, commentsContents).ConfigureAwait(false);
                return (commentsContents, commentsPath);
            }
            // else
            return ("", "");
        }

        /// <summary>
        /// Download attachments for this card if there are any.
        /// </summary>
        /// <param name="uploadedAttachments">The models of the attachments for this card parsed
        /// from the json backup.</param>
        /// <param name="trelloCard">The model of the card parsed from the json backup</param>
        /// <param name="cardIndex">The order of this card in the list, depending on whether it's
        /// archived or not.</param>
        /// <param name="usableCardName">The length-limited and usable version of the card title -
        /// special characters should be removed already.</param>
        /// <param name="cardFolderPath">The archived or non-archived folder items in this card
        /// write to.</param>
        /// <param name="ignoreFailedAttachmentDownloads">If specified, print a warning when an
        /// exception is encountered instead of propagating.</param>
        /// <param name="alwaysUseForwardSlashes">If specified, replace back slashes in paths with
        /// forward slashes.</param>
        /// <param name="boardName">The name of the board this attachment is in a card under, for
        /// logging.</param>
        /// <returns></returns>
        private static async Task DownloadTrelloCardAttachmentsAsync(
            IEnumerable<TrelloAttachmentModel> uploadedAttachments,
            TrelloCardModel trelloCard,
            int cardIndex,
            string usableCardName,
            string cardFolderPath,
            bool ignoreFailedAttachmentDownloads,
            bool alwaysUseForwardSlashes,
            string boardName
        )
        {
            if (uploadedAttachments.Count() > 0)
            {
                // create a folder for this card's attachments
                var attachmentFolderName = $"{cardIndex} {usableCardName} - Attachments";
                string attachmentFolderPath = Path.Join(cardFolderPath, attachmentFolderName);
                Directory.CreateDirectory(attachmentFolderPath);

                // start creating a new markdown file listing all the
                // attachments, their actual names and paths. a functional but
                // unformatted markdown table for now
                var attachmentListContents =
                    $"# {trelloCard.Name} - Attachments\n\n"
                    + $"id | original fileName | relative downloaded path\n"
                    + $"---|---|---\n";

                // download all uploaded attachments into that folder
                var AttachmentDownloadTasks = new List<Task<string>>();
                foreach (TrelloAttachmentModel attachment in uploadedAttachments)
                {
                    AttachmentDownloadTasks.Add(
                        DownloadTrelloCardAttachmentAsync(
                            attachment,
                            attachmentFolderPath,
                            cardFolderPath,
                            ignoreFailedAttachmentDownloads,
                            alwaysUseForwardSlashes,
                            boardName,
                            usableCardName
                        )
                    );
                }
                string[] AttachmentTableLines = await Task.WhenAll(AttachmentDownloadTasks);

                // record all lines in the file
                attachmentListContents += String.Join("\n", AttachmentTableLines);

                // write the file listing all the attachments
                var attachmentListFilename = $"{cardIndex} {usableCardName} - Attachments.md";
                string attachmentListPath = Path.Join(cardFolderPath, attachmentListFilename);
                await File.WriteAllTextAsync(attachmentListPath, attachmentListContents)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Download a single attachment.
        /// </summary>
        /// <param name="attachment">The models of the attachment parsed from the json
        /// backup.</param>
        /// <param name="attachmentFolderPath">The folder we save attachments into for this
        /// card.</param>
        /// <param name="cardFolderPath">The archived or non-archived folder items in this card
        /// write to.</param>
        /// <param name="IgnoreFailedAttachmentDownloads">If specified, print a warning when an
        /// exception is encountered instead of propagating.</param>
        /// <param name="alwaysUseForwardSlashes">If specified, replace back slashes in paths with
        /// forward slashes.</param>
        /// <param name="boardName">The name of the board this attachment is in a card under, for
        /// logging.</param>
        /// <param name="cardName">The name of the card this attachment is attached to, for
        /// logging.</returns>
        public static async Task<string> DownloadTrelloCardAttachmentAsync(
            TrelloAttachmentModel attachment,
            string attachmentFolderPath,
            string cardFolderPath,
            bool ignoreFailedAttachmentDownloads,
            bool alwaysUseForwardSlashes,
            string boardName,
            string cardName
        )
        {
            try
            {
                // download the attachment
                using var attachmentRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    attachment.Url
                );
                attachmentRequest.Headers.Add(
                    "Authorization",
                    $"OAuth oauth_consumer_key=\"{_apiKey}\", oauth_token=\"{_apiToken}\""
                );
                using HttpResponseMessage attachmentResponse = await _httpClient
                    .SendAsync(attachmentRequest)
                    .ConfigureAwait(false);
                attachmentResponse.EnsureSuccessStatusCode();

                string attachmentFileExtension = Path.GetExtension(attachment.FileName);
                // use the ID to create a unique filename
                string attachmentPath = Path.Combine(
                    attachmentFolderPath,
                    $"{attachment.ID}{attachmentFileExtension}"
                );
                // write the attachment to disk
                using FileStream attachmentFileStream = File.Create(attachmentPath);
                using Stream attachmentContentStream = await attachmentResponse.Content
                    .ReadAsStreamAsync()
                    .ConfigureAwait(false);
                await attachmentContentStream
                    .CopyToAsync(attachmentFileStream)
                    .ConfigureAwait(false);

                // calculate the new URL, from where the markdown files will be to the attachment
                // file. Markdown supports local relative paths
                string relativeAttachmentPath =
                    ".\\" + Path.GetRelativePath(cardFolderPath, attachmentPath);
                // prepare the line to add to the file
                string relativeAttachmentPathSpacesReplaced = relativeAttachmentPath.Replace(
                    " ",
                    "%20"
                );

                // backslashes don't work in Obsidian. If specified convert them to forward slashes
                if (alwaysUseForwardSlashes)
                {
                    relativeAttachmentPath = relativeAttachmentPath.Replace("\\", "/");
                    relativeAttachmentPathSpacesReplaced =
                        relativeAttachmentPathSpacesReplaced.Replace("\\", "/");
                }

                // update the model so the replacement works
                attachment.RelativeAttachmentPathSpacesReplaced =
                    relativeAttachmentPathSpacesReplaced;

                return $"{attachment.ID} | "
                    + $"{attachment.FileName} | "
                    + $"[{relativeAttachmentPath}]({relativeAttachmentPathSpacesReplaced})";
            }
            catch (Exception exception)
            {
                if (ignoreFailedAttachmentDownloads)
                {
                    // print a warning instead
                    AnsiConsole.MarkupLine(
                        $"[yellow]Failed to download attachment {attachment.FileName} "
                            + $"from {attachment.Url}"
                            + $"    Board: \"{boardName}\""
                            + $"    Card: {cardName}"
                            +
                            // using in interpolation automatically calls exception.ToString()
                            $"    Exception: {exception}[/]"
                    );

                    return $"{attachment.ID} | {attachment.FileName} | **failed to download**";
                }
                else
                {
                    // this is the proper way to rethrow the exception - you don't need to specify
                    // the caught one
                    throw;
                }
            }
        }

        /// <summary>
        /// Replace references to attachment URLs with relative local paths in a card's description
        /// and comment files.
        /// </summary>
        /// <param name="uploadedAttachments">The models of the attachments for this card parsed
        /// from the json backup.</param>
        /// <param name="descriptionContents">Original contents of the markdown file for the
        /// description.</param>
        /// <param name="descriptionPath">Full path on disk to the description markdown
        /// file.</param>
        /// <param name="commentsContents">Original contents of the markdown file for the
        /// comments.</param>
        /// <param name="commentsPath">Full path on disk to the comments markdown file.</param>
        /// <returns></returns>
        private static async Task UpdateAttachmentReferencesAsync(
            IEnumerable<TrelloAttachmentModel> uploadedAttachments,
            string descriptionContents,
            string descriptionPath,
            string commentsContents,
            string commentsPath
        )
        {
            // debug
            //if (descriptionPath.Contains("nftables"))
            //{
            //    Console.WriteLine("nftables");
            //}

            if (uploadedAttachments.Count() > 0)
            {
                // we're going to and replace and URL references. Do it in a new variable so we can
                // check if we made any changes
                string replacedDescriptionContents = descriptionContents;
                string replacedCommentsContents = commentsContents;

                foreach (TrelloAttachmentModel attachment in uploadedAttachments)
                {
                    // find and replace attachment URLs in descriptions and comments with the new
                    // relative URLs
                    replacedDescriptionContents = replacedDescriptionContents.Replace(
                        attachment.Url,
                        attachment.RelativeAttachmentPathSpacesReplaced
                    );
                    replacedCommentsContents = replacedCommentsContents.Replace(
                        attachment.Url,
                        attachment.RelativeAttachmentPathSpacesReplaced
                    );
                }

                // if we replaced any URLs in the description or comments, update them
                if (replacedDescriptionContents != descriptionContents)
                {
                    await File.WriteAllTextAsync(descriptionPath, replacedDescriptionContents)
                        .ConfigureAwait(false);
                }
                if (replacedCommentsContents != commentsContents)
                {
                    await File.WriteAllTextAsync(commentsPath, replacedCommentsContents)
                        .ConfigureAwait(false);
                }
            }
        }
    }
}
