// shared code helpers

using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GoldenSyrupGames.T2MD
{
    public static class Extensions
    {
        /// <summary>
        /// Retrieves Json from a web API URL and converts it to a JsonDocument class for reading.
        /// Raises an exception if unsuccessful.
        /// </summary>
        /// <exception cref="EnsureSuccessStatusCode "></exception>
        public static async Task<JsonDocument> GetJsonDocumentAsync(this HttpClient httpClient, string url)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await httpClient.SendAsync(httpRequest).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            string responseString;
            using (var streamReader = new StreamReader(responseStream))
            {
                responseString = await streamReader.ReadToEndAsync().ConfigureAwait(false);
            }
            return JsonDocument.Parse(responseString);
        }

        /// <summary>
        /// Retrieves Json from a web API HttpRequestMessage (URL and other options) and converts it to a JsonDocument class for reading
        /// Raises an exception if unsuccessful.
        /// </summary>
        /// <exception cref="EnsureSuccessStatusCode "></exception>
        public static async Task<JsonDocument> GetJsonDocumentAsync(this HttpClient httpClient, HttpRequestMessage httpRequest)
        {
            using var response = await httpClient.SendAsync(httpRequest).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            string responseString;
            using (var streamReader = new StreamReader(responseStream))
            {
                responseString = await streamReader.ReadToEndAsync().ConfigureAwait(false);
            }
            return JsonDocument.Parse(responseString);
        }

        /// <summary>
        /// Retrieves text from a web API URL response
        /// Raises an exception if unsuccessful.
        /// </summary>
        /// <exception cref="EnsureSuccessStatusCode "></exception>
        public static async Task<string> GetStringAsync(this HttpClient httpClient, string url)
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await httpClient.SendAsync(httpRequest).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            string responseString;
            using (var streamReader = new StreamReader(responseStream))
            {
                responseString = await streamReader.ReadToEndAsync().ConfigureAwait(false);
            }
            return responseString;
        }

        /// <summary>
        /// Retrieves text from a web API URL response
        /// Raises an exception if unsuccessful.
        /// </summary>
        /// <exception cref="EnsureSuccessStatusCode "></exception>
        public static async Task<string> GetStringAsync(this HttpClient httpClient, HttpRequestMessage httpRequest)
        {
            using var response = await httpClient.SendAsync(httpRequest).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            string responseString;
            using (var streamReader = new StreamReader(responseStream))
            {
                responseString = await streamReader.ReadToEndAsync().ConfigureAwait(false);
            }
            return responseString;
        }
    }

    public static class FileSystem
    {
        /// <summary>
        /// Hopefully a fix for folder-in-use errors when deleting within Dropbox.
        /// Depth-first recursive delete, with handling for descendant 
        /// directories open in Windows Explorer.
        /// Modified from https://stackoverflow.com/a/1703799.
        /// WARNING: runs recursively so can worst-case delay for total folders * retries * delay, but this is unlikely.
        /// </summary>
        public static void DeleteDirectoryRecursivelyWithRetriesAndDelay(string path, int maxRetries, int delayMilliseconds)
        {
            foreach (string directory in Directory.GetDirectories(path))
            {
                DeleteDirectoryRecursivelyWithRetriesAndDelay(directory, maxRetries, delayMilliseconds);
            }

            int currentRetries = 0;
            Exception? lastException = null;
            while (currentRetries < maxRetries)
            {
                try
                {
                    Directory.Delete(path, true);
                    lastException = null;
                    break;
                }
                catch (IOException e)
                {
                    Thread.Sleep(delayMilliseconds);
                    currentRetries++;
                    lastException = e;
                }
                catch (UnauthorizedAccessException e)
                {
                    Thread.Sleep(delayMilliseconds);
                    currentRetries++;
                    lastException = e;
                }
            }
            // rethrow the final exception if we didn't succeed
            if (lastException != null)
            {
                Console.WriteLine($"Failed to delete {path} after all retries. Last exception:");
                throw lastException;
            }
        }

        /// <summary>
        /// Returns FolderOrFileName with any unsafe path characters replaced with _.
        /// </summary>
        /// <param name="FolderOrFileName">The folder or file name with potentially unsafe characters</param>
        /// <returns></returns>
        public static string SanitiseForPath(string FolderOrFileName)
        {
            // remove special characters
            char[] unusableCharacters = Path.GetInvalidFileNameChars();
            return String.Join("_", FolderOrFileName.Split(unusableCharacters, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        }
    }
}