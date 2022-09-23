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
using System.Text.RegularExpressions;

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
            // get commandline arguments based on CliOptions, otherwise fail and print help.
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
            var url =
                $"https://api.trello.com/1/members/me/boards?"
                + $"key={_apiKey}"
                + $"&token={_apiToken}"
                // we can only grab 1000 at a time. After that we need to paginate. not paginating
                // for boards. If I need to in the future, generalise the code from
                // GetBoardCommentsAsync.
                + $"&limit=1000";
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

            // quick testing
            //trelloApiBoards = trelloApiBoards.Where(board => board.Name == "Test\\Board").ToList();

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
            AnsiConsole.MarkupLine("[magenta]Boards to back up:[/]");
            foreach (TrelloApiBoardModel trelloApiBoard in trelloApiBoards)
            {
                AnsiConsole.MarkupLine($"    [blue]{trelloApiBoard.Name}[/]");
            }

            // process each board asynchronously. The downloading, writing, as much of the process
            // as possible.
            AnsiConsole.MarkupLine("[magenta]Processing each board (phase 1):[/]");
            var boardTasks = new List<Task<TrelloBoardModel>>();
            foreach (TrelloApiBoardModel trelloApiBoard in trelloApiBoards)
            {
                // Starting each board with Task.Run is consistently faster than just async/await
                // within the board, even though it's I/O bound. Probably because the JSON parsing
                // is CPU bound and it's doing enough of it per board.
                boardTasks.Add(Task.Run(() => ProcessTrelloBoardAsync(trelloApiBoard, options)));
            }
            await Task.WhenAll(boardTasks).ConfigureAwait(false);
            IEnumerable<TrelloBoardModel> trelloBoards = boardTasks.Select(task => task.Result);

            // replace links to cards with links to their now-on-disk files.
            AnsiConsole.MarkupLine("[magenta]Replacing links (phase 2):[/]");

            // create an efficient data structure to look up cards from their URL codes
            Dictionary<string, TrelloCardModel> urlCardMap = trelloBoards
                .SelectMany(board => board.Cards)
                .ToDictionary(card => card.ShortUrl);

            var boardReplacementTasks = new List<Task>();
            foreach (TrelloBoardModel trelloBoard in trelloBoards)
            {
                // Starting each board with Task.Run is consistently faster than just async/await
                // within the board, even though it's I/O bound. Probably because the JSON parsing
                // is CPU bound and it's doing enough of it per board.
                boardReplacementTasks.Add(
                    Task.Run(() => ReplaceLinksBoardAsync(trelloBoard, options, urlCardMap))
                );
            }
            await Task.WhenAll(boardReplacementTasks).ConfigureAwait(false);

            // if specified, clean up empty folders. Mainly archive and list ones.
            if (options.RemoveEmptyFolders)
            {
                RemoveEmptyFolders(_outputPath);
            }
        }

        /// <summary>
        /// Removes all empty folders (no files) in the tree.
        /// from https://stackoverflow.com/a/2811746
        /// </summary>
        /// <param name="RootFolderPath"></param>
        private static void RemoveEmptyFolders(string RootFolderPath)
        {
            if (string.IsNullOrEmpty(RootFolderPath))
                throw new ArgumentException(
                    "Starting directory is a null reference or an empty string",
                    "RootFolderPath"
                );

            try
            {
                foreach (string path in Directory.EnumerateDirectories(RootFolderPath))
                {
                    RemoveEmptyFolders(path);
                }

                var entries = Directory.EnumerateFileSystemEntries(RootFolderPath);

                if (!entries.Any())
                {
                    try
                    {
                        Directory.Delete(RootFolderPath);
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (DirectoryNotFoundException) { }
                }
            }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// The per-board code.
        /// </summary>
        /// <param name="trelloApiBoard">Model of the board generated from the API call that
        /// enumerates them, not the downloaded json</param>
        /// <param name="options">The parsed options we received on the commandline from the
        /// user</param>
        /// <returns></returns>
        private static async Task<TrelloBoardModel> ProcessTrelloBoardAsync(
            TrelloApiBoardModel trelloApiBoard,
            CliOptions options
        )
        {
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

            // get all board comments from the API because the json export only contains the last
            // 1000 actions.
            List<TrelloActionModel> boardComments = await GetBoardCommentsAsync(trelloApiBoard.ID);

            // write the json to file (overwrite):

            // without this linux will happily write /'s
            string usableBoardName = GetUsableBoardName(trelloApiBoard, options);

            string boardOutputFilePath = Path.Combine(_outputPath, $"{usableBoardName}.json");
            using FileStream fileStream = File.Create(boardOutputFilePath);
            using Stream contentStream = await response.Content
                .ReadAsStreamAsync()
                .ConfigureAwait(false);
            await contentStream.CopyToAsync(fileStream).ConfigureAwait(false);

            // create a folder for each board
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

            // differentiate duplicate list names
            Dictionary<ITrelloCommon, string> duplicateSuffixes = GetDuplicateSuffixes(
                orderedLists,
                options
            );

            // create folders for each list.
            // optionally number them so they're shown in order
            var nonArchivedListIndex = 0;
            var archivedListIndex = 0;
            foreach (TrelloListModel trelloList in orderedLists)
            {
                ProcessTrelloList(
                    trelloList,
                    archivedListPath,
                    boardPath,
                    ref archivedListIndex,
                    ref nonArchivedListIndex,
                    options,
                    duplicateSuffixes[trelloList]
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

            // differentiate duplicate cards. Do it per list because lists will become folders,
            // cards will become files and that makes the most sense for the user
            Dictionary<ITrelloCommon, string> duplicateCardSuffixes = GetDuplicateSuffixes(
                orderedCards,
                options
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
                    ProcessTrelloCardAsync(
                        trelloCard,
                        cardIndex,
                        listPath,
                        trelloBoard,
                        options,
                        boardComments,
                        duplicateCardSuffixes[trelloCard]
                    )
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
            return trelloBoard;
        }

        /// <summary>
        /// Returns the name of the board with any filesystem-incompatible characters removed.
        /// <para />
        /// Trims preceding and trailing whitespace to avoid Windows being unable to use the folder
        /// (and to neaten things up). <para />
        /// Replaces multiple whitespace with a single space (usually from removing emoji). <para />
        /// If specified in options, emoji are removed here as well.
        /// </summary>
        public static string GetUsableBoardName(
            TrelloApiBoardModel trelloApiBoard,
            CliOptions options
        )
        {
            string usableBoardName = FileSystem.SanitiseForPath(trelloApiBoard.Name);
            if (options.RemoveEmoji)
            {
                usableBoardName = Emoji.ReplaceEmoji(usableBoardName, "");
            }
            usableBoardName = usableBoardName.Trim();
            Regex multipleSpaces = new Regex("\\s+");
            usableBoardName = multipleSpaces.Replace(usableBoardName, " ");
            return usableBoardName;
        }

        /// <summary>
        /// Returns the commentCard action models for a board. <para />
        /// Retrieves them from the API because the json export only contains the last 1000 actions.
        /// <para />
        /// Retrieves them per board not per card because an API call per card is way too slow.
        /// </summary>
        /// <param name="boardID">The ID of the board to retrieve comments for.</param>
        /// <returns></returns>
        private static async Task<List<TrelloActionModel>> GetBoardCommentsAsync(string boardID)
        {
            var boardComments = new List<TrelloActionModel>();

            // page to get all results.
            bool first = true;
            while (true)
            {
                var url =
                    $"https://api.trello.com/1/boards/{boardID}/actions?"
                    + $"key={_apiKey}"
                    + $"&token={_apiToken}"
                    // get only comment actions for this board
                    + $"&filter=commentCard"
                    // we can only grab 1000 at a time. After that we need to paginate
                    + $"&limit=1000";
                // comments are sorted newest to oldest. The paging method is getting the date of
                // the oldest comment and requesting the next x comments `before` that date. In the
                // first loop we don't do this.
                if (!first && boardComments.Count > 0)
                {
                    string pageDate = boardComments.Last().Date;
                    url += $"&before={pageDate}";
                }
                string textResponse = await _httpClient.GetStringAsync(url).ConfigureAwait(false);

                var result = JsonSerializer.Deserialize<List<TrelloActionModel>>(
                    textResponse,
                    _jsonDeserializeOptions
                );

                if (result == null)
                {
                    throw new Exception(
                        $"Failed to parse comments from actions of board {boardID}"
                    );
                }

                // empty array indicates there are no more results
                if (result.Count == 0)
                {
                    break;
                }

                boardComments.AddRange(result);
                first = false;
            }

            // no actions = no comments
            if (boardComments == null)
            {
                return new List<TrelloActionModel>();
            }

            // already filtered via the api call, no need to `Where` filter again
            return boardComments.ToList();
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
        /// <param name="options">The parsed options we received on the commandline from the
        /// user</param>
        /// <param name="duplicateDifferentiator">If we're not numbering (options.NoNumbering)
        /// and this isn't empty, we'll append it to the card name to differentiate us. </param>
        private static void ProcessTrelloList(
            TrelloListModel trelloList,
            string archivedListPath,
            string boardPath,
            ref int archivedListIndex,
            ref int nonArchivedListIndex,
            CliOptions options,
            string duplicateDifferentiator
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
            string usableListName = GetUsableListName(trelloList, options);
            if (options.NoNumbering && !string.IsNullOrEmpty(duplicateDifferentiator))
            {
                usableListName += $" {duplicateDifferentiator}";
            }
            string listFolderName = options.NoNumbering
                ? usableListName
                : $"{listIndex} {usableListName}";
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
        /// Returns the name of the list with any filesystem-incompatible characters removed.
        /// <para />
        /// Trims preceding and trailing whitespace to avoid Windows being unable to use the folder
        /// (and to neaten things up). <para />
        /// Replaces multiple whitespace with a single space (usually from removing emoji). <para />
        /// If specified in options, emoji are removed here as well.
        /// </summary>
        public static string GetUsableListName(TrelloListModel trelloList, CliOptions options)
        {
            string usableListName = FileSystem.SanitiseForPath(trelloList.Name);
            if (options.RemoveEmoji)
            {
                usableListName = Emoji.ReplaceEmoji(usableListName, "");
            }
            usableListName = usableListName.Trim();
            Regex multipleSpaces = new Regex("\\s+");
            usableListName = multipleSpaces.Replace(usableListName, " ");
            return usableListName;
        }

        /// <summary>
        /// Creates a markdown file each for the board's description, comments, checklists and list
        /// of attachments. If specified in `options`, only a singe card is created. <para/>
        /// Downloads all attachments. <para/>
        /// Replaces references to the attachment URLs with the new relative ones in the
        /// description and comments. <para/>
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
        /// <param name="boardActions">The list of all comments for the board so we can retrieve
        /// ours.</param>
        /// <param name="duplicateDifferentiator">If we're not numbering (options.NoNumbering)
        /// and this isn't empty, we'll append it to the card name to differentiate us. </param>
        /// <returns></returns>
        private static async Task ProcessTrelloCardAsync(
            TrelloCardModel trelloCard,
            int cardIndex,
            string cardFolderPath,
            TrelloBoardModel trelloBoard,
            CliOptions options,
            List<TrelloActionModel> boardComments,
            string duplicateDifferentiator
        )
        {
            // record our board to make phase 2 processing easier
            trelloCard.Board = trelloBoard;

            // get a version of the card title with user-limited length that's safe to use for a
            // filename
            string usableCardName = GetUsableCardName(trelloCard, options);

            if (options.NoNumbering && !string.IsNullOrEmpty(duplicateDifferentiator))
            {
                usableCardName += $" {duplicateDifferentiator}";
            }

            // save the path of the files so we can
            // find/replace URLs in them
            string descriptionPath,
                commentsPath,
                checklistsPath;
            descriptionPath = commentsPath = checklistsPath = String.Empty;

            // write the description to a markdown file
            Task<(string, string)> WriteCardDescriptionTask = WriteCardDescriptionAsync(
                trelloCard,
                cardFolderPath,
                cardIndex,
                usableCardName,
                options
            );

            // if we're writing to only one file ensure only one thread is writing to the file at
            // the same time so wait for the description to finish
            if (options.SingleFile)
            {
                (_, descriptionPath) = await WriteCardDescriptionTask;
            }

            // download uploaded attachments if there are any and list them in a markdown file. Only
            // direct file uploads or image data pastes, not e.g. pasted http links
            IEnumerable<TrelloAttachmentModel> uploadedAttachments = trelloCard.Attachments.Where(
                attachment => attachment.IsUpload
            );
            Task? downloadAttachmentsTask = null;
            if (uploadedAttachments.Count() > 0)
            {
                downloadAttachmentsTask = DownloadTrelloCardAttachmentsAsync(
                    uploadedAttachments,
                    trelloCard,
                    cardIndex,
                    usableCardName,
                    cardFolderPath,
                    options.IgnoreFailedAttachmentDownloads,
                    options.AlwaysUseForwardSlashes,
                    trelloBoard.Name,
                    options,
                    descriptionPath
                );
            }

            if (options.SingleFile && downloadAttachmentsTask != null)
            {
                await downloadAttachmentsTask;
            }

            // write the checklists to a file if there are any
            Task<(string, string)> WriteCardChecklistsTask = WriteCardChecklistsAsync(
                trelloCard,
                trelloBoard,
                cardFolderPath,
                cardIndex,
                usableCardName,
                options,
                descriptionPath
            );

            if (options.SingleFile)
            {
                (_, checklistsPath) = await WriteCardChecklistsTask;
            }

            // get only this card's comments
            List<TrelloActionModel> cardComments = boardComments
                .Where(action => action.Data.Card.ID == trelloCard.ID)
                .ToList();

            // write comments to a markdown file
            Task<(string, string)> WriteCardCommentsTask = WriteCardCommentsAsync(
                trelloCard,
                trelloBoard,
                cardFolderPath,
                cardIndex,
                usableCardName,
                options,
                cardComments,
                descriptionPath
            );

            if (options.SingleFile)
            {
                (_, commentsPath) = await WriteCardCommentsTask;
            }

            // if we're writing to separate files, allow them all to write at once above and wait
            // for them all here
            if (!options.SingleFile)
            {
                Task.WaitAll(
                    WriteCardDescriptionTask,
                    WriteCardCommentsTask,
                    WriteCardChecklistsTask
                );
                (_, descriptionPath) = WriteCardDescriptionTask.Result;
                (_, commentsPath) = WriteCardCommentsTask.Result;
                (_, checklistsPath) = WriteCardChecklistsTask.Result;

                if (downloadAttachmentsTask != null)
                {
                    await downloadAttachmentsTask;
                }
            }

            if (downloadAttachmentsTask != null)
            {
                // replace full http attachment URLs with local relative paths so the description
                // and comments now link to the downloaded copies.
                await UpdateAttachmentReferencesAsync(
                    uploadedAttachments,
                    descriptionPath,
                    commentsPath
                );
            }

            // record the file paths so we can iterate over and replace links in them later
            trelloCard.DescriptionPath = descriptionPath;
            trelloCard.CommentsPath = commentsPath;
            trelloCard.ChecklistsPath = checklistsPath;
        }

        /// <summary>
        /// Returns the name of the card limited to the length specified by the user and with any
        /// filesystem-incompatible characters removed. <para />
        /// Trims preceding and trailing whitespace to avoid Windows being unable to use the folder
        /// (and to neaten things up). <para />
        /// Replaces multiple whitespace with a single space (usually from removing emoji). <para />
        /// If specified in options, emoji are removed here as well.
        /// </summary>
        public static string GetUsableCardName(TrelloCardModel trelloCard, CliOptions options)
        {
            int actualOrRestrictedLength = Math.Min(
                trelloCard.Name.Length,
                options.MaxCardFilenameTitleLength
            );
            string usableCardName = trelloCard.Name.Substring(0, actualOrRestrictedLength);
            // remove special filesystem characters
            usableCardName = FileSystem.SanitiseForPath(usableCardName);
            // remove emoji if specified
            if (options.RemoveEmoji)
            {
                usableCardName = Emoji.ReplaceEmoji(usableCardName, "");
            }
            usableCardName = usableCardName.Trim();
            Regex multipleSpaces = new Regex("\\s+");
            usableCardName = multipleSpaces.Replace(usableCardName, " ");
            return usableCardName;
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
            // inject the full title, and short url, into its output. Add a prefix to the short URL
            // to make the original more easily greppable: a search of just the URL will show all
            // files that reference it.
            var descriptionContents =
                $"# {trelloCard.Name}\n"
                + $"\n"
                + $"Original URL: {trelloCard.ShortUrl}\n"
                + $"\n"
                + $"---\n"
                + $"\n"
                + $"{trelloCard.Desc}";
            // sort the cards in order unless specified otherwise
            var descriptionFilename = options.NoNumbering
                ? $"{usableCardName}.md"
                : $"{cardIndex} {usableCardName}.md";
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
        /// <param name="descriptionFilePath">If options.SingleFile is specified, the checklists
        /// content is instead appended to this file.</param>
        /// <returns></returns>
        private static async Task<(string, string)> WriteCardChecklistsAsync(
            TrelloCardModel trelloCard,
            TrelloBoardModel trelloBoard,
            string cardFolderPath,
            int cardIndex,
            string usableCardName,
            CliOptions options,
            string descriptionFilePath
        )
        {
            // checklists are under the board so retrieve the ones for this card
            IEnumerable<TrelloChecklistModel> cardChecklists = trelloBoard.Checklists.Where(
                checklist => trelloCard.IDChecklists.Contains(checklist.ID)
            );
            if (cardChecklists.Count() > 0)
            {
                string checklistsContents;
                if (options.SingleFile)
                {
                    // if we're appending to the description file nest it as a second level heading.
                    // Put it on a separated new line as the description contents might not end with
                    // one. Add a line separator to try and separate it further
                    checklistsContents = $"\n\n---\n\n## Checklists\n\n";
                }
                else
                {
                    // if it's a separate file,  start with a modified title
                    checklistsContents = $"# {trelloCard.Name} - Checklists\n\n";
                }
                // maintain the checklist order in the card
                IOrderedEnumerable<TrelloChecklistModel> orderedCardChecklists =
                    cardChecklists.OrderBy(checklist => checklist.Pos);
                foreach (TrelloChecklistModel trelloChecklist in orderedCardChecklists)
                {
                    // write each checklist title as the next subheading
                    string headingPrefix = options.SingleFile ? "###" : "##";
                    checklistsContents += $"{headingPrefix} {trelloChecklist.Name}\n\n";
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
                string outputPath;
                if (options.SingleFile)
                {
                    outputPath = descriptionFilePath;
                }
                else
                {
                    var checklistsFilename = options.NoNumbering
                        ? $"{usableCardName} - Checklists.md"
                        : $"{cardIndex} {usableCardName} - Checklists.md";
                    outputPath = Path.Join(cardFolderPath, checklistsFilename);
                }
                await File.AppendAllTextAsync(outputPath, checklistsContents).ConfigureAwait(false);

                return (checklistsContents, outputPath);
            }
            // else
            return ("", "");
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
        /// <param name="descriptionFilePath">If options.SingleFile is specified, the checklists
        /// content is instead appended to this file.</param>
        /// <param name="descriptionFilePath">If options.SingleFile is specified, the checklists
        /// content is instead appended to this file.</param>
        /// <returns>Returns the markdown contents of the comments file in the first tuple member
        /// and the path to it in the second.</returns>
        private static async Task<(string, string)> WriteCardCommentsAsync(
            TrelloCardModel trelloCard,
            TrelloBoardModel trelloBoard,
            string cardFolderPath,
            int cardIndex,
            string usableCardName,
            CliOptions options,
            List<TrelloActionModel> cardComments,
            string descriptionFilePath
        )
        {
            if (cardComments.Count() > 0)
            {
                string commentsContents;
                if (options.SingleFile)
                {
                    // if we're appending to the description file nest it as a second level heading.
                    // Put it on a separated new line as the description contents might not end with
                    // one. Add a line separator to try and separate it further
                    commentsContents = $"\n\n---\n\n## Comments\n\n";
                }
                else
                {
                    // if it's a separate file, start with a modified title
                    commentsContents = $"# {trelloCard.Name} - Comments\n\n";
                }

                // order the comments by date. ISO 8601 dates can be sorted as a string
                IOrderedEnumerable<TrelloActionModel> orderedCardComments = cardComments.OrderBy(
                    comment => comment.Date
                );
                foreach (TrelloActionModel trelloComment in orderedCardComments)
                {
                    // separate each card's contents
                    string headingPrefix = options.SingleFile ? "###" : "##";
                    commentsContents += $"{headingPrefix} " + new string('-', 40) + "\n\n";
                    //commentsContents += "## " + new string('-', 10) + $"Comment on
                    //{trelloComment.Date}" + new string('-', 10) + "\n\n";

                    commentsContents += trelloComment.Data.Text;
                    commentsContents += "\n\n";
                }

                // write the file
                string outputPath;
                if (options.SingleFile)
                {
                    outputPath = descriptionFilePath;
                }
                else
                {
                    var commentsFilename = options.NoNumbering
                        ? $"{usableCardName} - Comments.md"
                        : $"{cardIndex} {usableCardName} - Comments.md";
                    outputPath = Path.Join(cardFolderPath, commentsFilename);
                }
                await File.AppendAllTextAsync(outputPath, commentsContents).ConfigureAwait(false);
                return (commentsContents, outputPath);
            }
            // else
            return ("", "");
        }

        /// <summary>
        /// Download attachments for this card if there are any and record them in a markdown table.
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
        /// <param name="descriptionFilePath">If options.SingleFile is specified, the checklists
        /// content is instead appended to this file.</param>
        /// <returns></returns>
        private static async Task DownloadTrelloCardAttachmentsAsync(
            IEnumerable<TrelloAttachmentModel> uploadedAttachments,
            TrelloCardModel trelloCard,
            int cardIndex,
            string usableCardName,
            string cardFolderPath,
            bool ignoreFailedAttachmentDownloads,
            bool alwaysUseForwardSlashes,
            string boardName,
            CliOptions options,
            string descriptionFilePath
        )
        {
            if (uploadedAttachments.Count() > 0)
            {
                // create a folder for this card's attachments
                var attachmentFolderName = options.NoNumbering
                    ? $"{usableCardName} - Attachments"
                    : $"{cardIndex} {usableCardName} - Attachments";
                string attachmentFolderPath = Path.Join(cardFolderPath, attachmentFolderName);
                Directory.CreateDirectory(attachmentFolderPath);

                string attachmentListContents;
                if (options.SingleFile)
                {
                    // if we're appending to the description file nest it as a second level heading.
                    // Put it on a separated new line as the description contents might not end with
                    // one
                    attachmentListContents = $"\n\n---\n\n## Attachments\n\n";
                }
                else
                {
                    // if it's a separate file, start with a modified title
                    attachmentListContents = $"# {trelloCard.Name} - Attachments\n\n";
                }

                // start listing all the attachments, their actual names and paths. a
                // functional but unformatted markdown table for now
                attachmentListContents += $"id | original fileName | link\n" + $"---|---|---\n";

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
                            usableCardName,
                            options
                        )
                    );
                }
                string[] AttachmentTableLines = await Task.WhenAll(AttachmentDownloadTasks);

                // record all lines in the file
                attachmentListContents += String.Join("\n", AttachmentTableLines);

                // write the file listing all the attachments
                string outputPath;
                if (options.SingleFile)
                {
                    outputPath = descriptionFilePath;
                }
                else
                {
                    var attachmentListFilename = options.NoNumbering
                        ? $"{usableCardName} - Attachments.md"
                        : $"{cardIndex} {usableCardName} - Attachments.md";
                    outputPath = Path.Join(cardFolderPath, attachmentListFilename);
                }
                await File.AppendAllTextAsync(outputPath, attachmentListContents)
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
            string cardName,
            CliOptions options
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
                //
                // csharpier-ignore-start
                string tableRow =
                    $"{attachment.ID} | "
                    + $"{attachment.FileName} | "
                    // a markdown image within a markdown link so the image is clickable. the
                    // obsidian image size separator is escaped because | is also a table column
                    // separator. [![alt-text|width](link)](link)
                    + $"["
                        + $"!["
                            + $"{relativeAttachmentPath}"
                            + $"\\|{options.ObsidianAttachmentPreviewWidth}"
                        + $"]"
                        + $"({relativeAttachmentPathSpacesReplaced})"
                    + $"]({relativeAttachmentPathSpacesReplaced})";
                // csharpier-ignore-end

                return tableRow;
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
        /// <param name="descriptionPath">Full path on disk to the description markdown
        /// file.</param>
        /// <param name="commentsPath">Full path on disk to the comments markdown file.</param>
        /// <returns></returns>
        private static async Task UpdateAttachmentReferencesAsync(
            IEnumerable<TrelloAttachmentModel> uploadedAttachments,
            string descriptionPath,
            string commentsPath
        )
        {
            if (uploadedAttachments.Count() > 0)
            {
                string descriptionContents = "";
                string commentsContents = "";
                // read the contents from disk again instead of passing in the contents to this
                // function to support single-file mode
                if (descriptionPath != "")
                {
                    descriptionContents = await File.ReadAllTextAsync(descriptionPath);
                }
                if (commentsPath != "")
                {
                    commentsContents = await File.ReadAllTextAsync(commentsPath);
                }

                // Replace in a new variable so we can check if we made any changes
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
                if (replacedDescriptionContents != descriptionContents && descriptionPath != "")
                {
                    await File.WriteAllTextAsync(descriptionPath, replacedDescriptionContents)
                        .ConfigureAwait(false);
                }
                if (
                    replacedCommentsContents != commentsContents
                    && commentsPath != ""
                    && commentsPath != descriptionPath
                )
                {
                    await File.WriteAllTextAsync(commentsPath, replacedCommentsContents)
                        .ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Given a list of Trello models with Name properties that might be duplicates, returns a
        /// Dict containing each input entry as a key, and a unique incrementing (per duplicate
        /// name, not total) number in a string as their value if they had a duplicate name, or an
        /// empty string if they didn't. <para />
        /// Cards will be de-duplicated per list.
        /// E.g.<br />
        ///     ["Card 1", "Card 2", "Card 1"]<br />
        /// would return<br />
        ///     {"Card 1": "1", "Card 2"; "", "Card 1": "2"}. <para />
        /// </summary>
        /// <param name="potentialDuplicates"></param>
        /// <returns></returns>
        public static Dictionary<ITrelloCommon, string> GetDuplicateSuffixes(
            IEnumerable<ITrelloCommon> potentialDuplicates,
            CliOptions options
        )
        {
            var output = new Dictionary<ITrelloCommon, string>();

            // track how many times we've seen each name
            var occurrences = new Dictionary<string, int>();

            foreach (ITrelloCommon potentialDuplicate in potentialDuplicates)
            {
                string name = GetDuplicateNameKey(potentialDuplicate, options);
                // increment how many times we've seen this name.
                int count;
                // if in there, grab the current value, then increment it.
                // argh: TryGetValue() sets the output variable to default (0) here if not found.
                if (occurrences.TryGetValue(name, out count))
                {
                    count++;
                }
                else
                {
                    // default if not already in there
                    count = 1;
                }
                // update it
                occurrences[name] = count;

                // note in the output. for now specify initial 1s as well as we don't know if
                // they'll be duplicates yet
                output[potentialDuplicate] = count.ToString();
            }

            // clear suffixes of those that arent duplicate
            string oneAsString = 1.ToString();
            foreach ((ITrelloCommon entry, string suffix) in output)
            {
                string name = GetDuplicateNameKey(entry, options);
                if (suffix == oneAsString && occurrences[name] == 1)
                {
                    output[entry] = "";
                }
            }

            return output;
        }

        /// <summary>
        /// Generates the card/list name key used in GetDuplicateSuffixes(). <para/>
        /// Card names are differentiated within each list. <para />
        /// </summary>
        private static string GetDuplicateNameKey(
            ITrelloCommon potentialDuplicate,
            CliOptions options
        )
        {
            // archived and non-archived are in separate folders so we don't want to deduplicate
            // between archived and non-archived cards with the same name
            string archivedString = potentialDuplicate.Closed ? "archived" : "";

            // notes: `as` returns null if the cast fails, casting (prefixing with `(type)`) throws
            // an exception if the cast fails
            var card = potentialDuplicate as TrelloCardModel;
            if (card != null)
            {
                // important:
                // - check duplicity via the actual card name we'll write to disk. Without this
                //   there were a few cards that were unique at full length but duplicate when
                //   truncated, that weren't picked up.
                // - compare case insensitive. We want to write the original case to disk (so it's
                //   not in `GetUsableCardName()`) but still avoid overwriting a different one
                //   that's already been written on case-insensitive systems
                string usableCardName = GetUsableCardName(card, options).ToLower();
                return usableCardName + card.IDList + archivedString;
            }

            var list = potentialDuplicate as TrelloListModel;
            if (list != null)
            {
                string usableListName = GetUsableListName(list, options).ToLower();
                return usableListName + archivedString;
            }

            // else
            return potentialDuplicate.Name.ToLower() + archivedString;
        }

        /// <summary>
        /// Replaces links to trello card URLs with relative folder links in all cards on the board.
        /// </summary>
        /// <param name="trelloBoard">The board to replace links on. This needs to have card models
        /// with filled out description (and other if used) file paths. i.e. this function needs to
        /// be called after the initial population work.</param>
        /// <param name="options">cli options from the user.</param>
        /// <param name="urlCardMap">A mapping of card short URLs to card models.</param>
        private static async Task ReplaceLinksBoardAsync(
            TrelloBoardModel trelloBoard,
            CliOptions options,
            Dictionary<string, TrelloCardModel> urlCardMap
        )
        {
            AnsiConsole.MarkupLine($"    [blue]Starting {trelloBoard.Name}[/]");

            var cardReplacementTasks = new List<Task>();
            foreach (TrelloCardModel trelloCard in trelloBoard.Cards)
            {
                cardReplacementTasks.Add(ReplaceLinksCardAsync(trelloCard, options, urlCardMap));
            }
            await Task.WhenAll(cardReplacementTasks);

            AnsiConsole.MarkupLine($"    [green]Finished {trelloBoard.Name}[/]");
        }

        /// <summary>
        /// Replaces links to trello card URLs with relative folder links in this card's files.
        /// </summary>
        /// <param name="trelloCard">The card to replace links in. This needs to have filled out
        /// description (and other if used) file paths. i.e. this function needs to be called after
        /// the initial population work.</param>
        /// <param name="options">cli options from the user.</param>
        /// <param name="urlCardMap">A mapping of card short URLs to card models.</param>
        private static async Task ReplaceLinksCardAsync(
            TrelloCardModel trelloCard,
            CliOptions options,
            Dictionary<string, TrelloCardModel> urlCardMap
        )
        {
            // unique paths only to avoid processing the same file more than once (and at the same
            // time causing exceptions) in single-file mode
            var paths = new string[]
            {
                trelloCard.DescriptionPath,
                trelloCard.CommentsPath,
                trelloCard.ChecklistsPath
            }.Distinct();

            var fileTasks = new List<Task>();
            foreach (string path in paths)
            {
                fileTasks.Add(
                    ReplaceLinksFileAsync(path, options, urlCardMap, trelloCard.ShortUrl)
                );
            }
            await Task.WhenAll(fileTasks);
        }

        /// <summary>
        /// Replaces links to trello card URLs with relative folder links in the contents of the
        /// file at `path`.
        /// </summary>
        /// <param name="path">The path to a card file on disk, potentially with Trello card URLs in
        /// it.</param>
        /// <param name="options">cli options from the user.</param>
        /// <param name="urlCardMap">A mapping of card short URLs to card models.</param>
        /// <param name="thisCardShortUrl">The short URL of the current card we're replacing content
        /// for (i.e. of the card `path` is for). We won't replace references to this URL, to avoid
        /// overwriting the "Original URL" line.</param>
        private static async Task ReplaceLinksFileAsync(
            string path,
            CliOptions options,
            Dictionary<string, TrelloCardModel> urlCardMap,
            string thisCardShortUrl
        )
        {
            if (path != "")
            {
                string content = await File.ReadAllTextAsync(path);
                string replacedContent = content;

                IEnumerable<string> foundUrls = GetTrelloCardUrlsFromText(content);
                foreach (string url in foundUrls)
                {
                    // don't change the Original URL line at the top of the source card.
                    if (url.Contains(thisCardShortUrl))
                    {
                        continue;
                    }

                    // extract the short code in the format the json delivers it in
                    Match shortUrlMatch = Regex.Match(
                        url,
                        @"https:\/\/trello\.com\/c\/[a-zA-Z0-9]+"
                    );
                    // all the foundUrls should contain the short URL. If not, something is wrong
                    if (!shortUrlMatch.Success)
                    {
                        throw new Exception(
                            $"Couldn't extract a short Trello card URL from \"{url}\""
                        );
                    }

                    TrelloCardModel destinationCard = urlCardMap[shortUrlMatch.Value];

                    // don't change links to excluded boards
                    if (
                        destinationCard.Board != null
                        && options.ReplaceLinksDestinationBoardsToExclude.Contains(
                            destinationCard.Board.Name
                        )
                    )
                    {
                        continue;
                    }

                    // find the local path of the target card
                    string destinationCardPath = destinationCard.DescriptionPath;

                    // get the path from the current file to the target.
                    // we need to use the current card's folder path, not its file path
                    string? sourcePath = Path.GetDirectoryName(path);
                    if (sourcePath == null)
                    {
                        throw new Exception("Path.GetDirectoryName(path) returned null");
                    }
                    string relativePath = Path.GetRelativePath(sourcePath, destinationCardPath);
                    relativePath = relativePath.Replace(" ", "%20");
                    if (options.AlwaysUseForwardSlashes)
                    {
                        relativePath = relativePath.Replace("\\", "/");
                    }

                    // replace the links
                    string localMarkdownLink = $"[{destinationCard.Name}]({relativePath})";
                    replacedContent = replacedContent.Replace(url, localMarkdownLink);
                }

                // write it back to disk if we made any changes
                if (replacedContent != content)
                {
                    await File.WriteAllTextAsync(path, replacedContent);
                }
            }
        }

        /// <summary>
        /// Matches trello card URLs in card contents that link to other cards. <para />
        /// Returns a list of these so they can be looked up and replaced. <para />
        /// Finds URLs with or without trailing / and titles. E.g <para />
        /// <c>https://trello.com/c/aa11BB22</c> <para />
        /// <c>https://trello.com/c/aa11BB22/</c> <para />
        /// <c>https://trello.com/c/aa11BB22/card-title</c> <para />
        /// <c>https://trello.com/c/aa11BB22/card-title/</c> <para />
        /// <c>https://trello.com/c/aa11BB22/card-title-emoji-%E2%9D%A4/</c> <para />
        /// </summary>
        /// <param name="input">The string potentially containing the URLs.</param>
        /// <returns>All the found URLs.</returns>
        public static IEnumerable<string> GetTrelloCardUrlsFromText(string input)
        {
            // equivalent of `(?x)`. That works in the online testers but doesn't in code.
            // without this we were returning 7 matches instead of 12.
            // a C# gotcha. Found by Retax on the C# Discord.
            RegexOptions options = RegexOptions.IgnorePatternWhitespace;

            var regex = new Regex(
                @"
                    (?x) # allow splitting the regex over multiple lines
                         # and using comments.
                    https:\/\/ # https://
                    trello\.com # trello.com
                    \/c # /c
                    \/[a-zA-Z0-9]+ # shortcode, e.g. /aa11BB22
                    # at this point we have a minimum valid URL so everything
                    # else has to be optional.
                    (
                      \/ # /. Keep this in here so we don't match a sentence instead
                         # of the title
                      ( # however still make the card title optional so we can capture
                        # just a trailing / (line above) too 
                        (\w|-|%)+ # capture letters, numbers, hyphens and what emoji
                                  # look like when copied and pasted from Chrome URLs
                        \/? # another optional trailing /
                        )?
                    )?
                ",
                options
            );

            MatchCollection results = regex.Matches(input);
            var test = regex.Replace(input, "_");
            return results.Select(match => match.Value);
        }
    }
}
