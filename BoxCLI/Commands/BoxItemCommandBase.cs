using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Box.V2;
using Box.V2.Models;
using BoxCLI.BoxHome;
using BoxCLI.BoxPlatform.Service;
using BoxCLI.CommandUtilities;
using BoxCLI.CommandUtilities.CommandModels;
using BoxCLI.CommandUtilities.CommandOptions;
using BoxCLI.CommandUtilities.CsvModels;
using BoxCLI.CommandUtilities.Globalization;
using CsvHelper;
using Microsoft.Extensions.CommandLineUtils;
using Newtonsoft.Json;

namespace BoxCLI.Commands
{
    public class BoxItemCommandBase : BoxBaseCommand
    {
        protected CommandOption _asUser;
        protected CommandOption _json;
        private CommandLineApplication _app;
        protected const long MINIUMUM_CHUNKED_UPLOAD_FILE_SIZE = 50000000;
        public BoxItemCommandBase(IBoxPlatformServiceBuilder boxPlatformBuilder, IBoxHome boxHome, LocalizedStringsResource names)
            : base(boxPlatformBuilder, boxHome, names)
        {
        }
        public override void Configure(CommandLineApplication command)
        {
            _app = command;
            _asUser = AsUserOption.ConfigureOption(command);
            _json = OutputJsonOption.ConfigureOption(command);
            base.Configure(command);
        }

        protected virtual void CheckForParentId(string parentId, CommandLineApplication app)
        {
            if (string.IsNullOrEmpty(parentId))
            {
                app.ShowHelp();
                throw new Exception("A Parent ID is required for this command.");
            }
        }

        protected BoxFileRequest ConfigureFileRequest(string fileId = "", string parentId = "",
           string fileName = "", string description = "")
        {
            var fileRequest = new BoxFileRequest();
            if (!string.IsNullOrEmpty(fileId))
            {
                fileRequest.Id = fileId;
            }
            if (!string.IsNullOrEmpty(fileName))
            {
                fileRequest.Name = fileName;
            }
            if (!string.IsNullOrEmpty(description))
            {
                fileRequest.Description = description;
            }
            if (!string.IsNullOrEmpty(parentId))
            {
                fileRequest.Parent = new BoxItemRequest();
                fileRequest.Parent.Id = parentId;
            }
            return fileRequest;
        }



        protected void PrintFile(BoxFile file)
        {
            Reporter.WriteInformation($"File ID: {file.Id}");
            Reporter.WriteInformation($"File Name: {file.Name}");
            Reporter.WriteInformation($"File Size: {file.Size}");
        }
        protected void PrintFolder(BoxFolder folder)
        {
            Reporter.WriteInformation($"Folder ID: {folder.Id}");
            Reporter.WriteInformation($"Folder Name: {folder.Name}");
            if (folder.FolderUploadEmail != null)
            {
                Reporter.WriteInformation($"Folder Upload Email Access: {folder.FolderUploadEmail.Acesss}");
                Reporter.WriteInformation($"Folder Upload Email Address: {folder.FolderUploadEmail.Address}");
            }
            if (folder.Parent != null)
            {
                Reporter.WriteInformation($"Folder Parent:");
                this.PrintFolder(folder.Parent);
            }
        }

        protected void PrintFileLock(BoxFileLock fileLock)
        {
            Reporter.WriteInformation($"File Lock ID: {fileLock.Id}");
            Reporter.WriteInformation($"Is download prevented: {fileLock.IsDownloadPrevented}");
            Reporter.WriteInformation($"Expires at: {fileLock.ExpiresAt}");
            Reporter.WriteInformation($"Created at: {fileLock.CreatedAt}");
            base.PrintMiniUser(fileLock.CreatedBy);
        }

        protected virtual void PrintTaskAssignment(BoxTaskAssignment taskAssignment)
        {
            Reporter.WriteInformation($"Task Assignment ID: {taskAssignment.Id}");
            Reporter.WriteInformation($"Task Assignment Message: {taskAssignment.Message}");
            Reporter.WriteInformation($"Task Assignment Resolution State: {taskAssignment.ResolutionState.Value}");
        }

        protected virtual void PrintTask(BoxTask task)
        {
            Reporter.WriteInformation($"Task ID: {task.Id}");
            Reporter.WriteInformation($"Task Action: {task.Action}");
            Reporter.WriteInformation($"Task Message: {task.Message}");
        }

        protected List<T> ReadCustomFile<T, M>(string path)
        {
            var fileFormat = base.ProcessFileFormatFromPath(path);
            System.Console.WriteLine($"File is {fileFormat}");
            if (fileFormat == base._settings.FILE_FORMAT_JSON)
            {
                using (var fs = File.OpenText(path))
                {
                    var serializer = new JsonSerializer();
                    return (List<T>)serializer.Deserialize(fs, typeof(List<T>));
                }
            }
            else if (fileFormat == base._settings.FILE_FORMAT_CSV)
            {
                System.Console.WriteLine("Found csv file...");
                using (var fs = File.OpenText(path))
                using (var csv = new CsvReader(fs))
                {
                    System.Console.WriteLine("Processing csv...");
                    csv.Configuration.RegisterClassMap(typeof(M));
                    return csv.GetRecords<T>().ToList();
                }
            }
            else
            {
                throw new Exception($"File format {fileFormat} is not currently supported.");
            }
        }

        protected async Task<BoxFile> UploadFile(string path, string parentId = "", string fileName = "", string fileId = "", bool isNewVersion = false)
        {
            var boxClient = base.ConfigureBoxClient(this._asUser.Value());
            path = GeneralUtilities.TranslatePath(path);

            var file = new FileInfo(path);
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = file.Name;
            }
            if (string.IsNullOrEmpty(parentId))
            {
                parentId = "0";
            }

            if (file.Length >= MINIUMUM_CHUNKED_UPLOAD_FILE_SIZE && isNewVersion)
            {
                return await this.ChunkedUpload(path, fileName, parentId, file.Length, fileId, true);
            }
            else if (file.Length >= MINIUMUM_CHUNKED_UPLOAD_FILE_SIZE)
            {
                return await this.ChunkedUpload(path, fileName, parentId, file.Length);
            }
            else
            {
                using (var fileStream = File.Open(path, FileMode.Open))
                {
                    Reporter.WriteData("creating file request...");
                    var fileRequest = this.ConfigureFileRequest(parentId: parentId, fileName: fileName);
                    Reporter.WriteData("File request created...");
                    if (isNewVersion)
                    {
                        if (string.IsNullOrEmpty(fileId))
                        {
                            throw new Exception("A file ID is required for this command.");
                        }
                        Reporter.WriteInformation("Uploading new version...");
                        Reporter.WriteInformation(fileName);
                        var preflightCheckRequest = new BoxPreflightCheckRequest()
                        {
                            Name = fileName,
                            Size = file.Length,
                            Parent = new BoxItemRequest()
                            {
                                Id = parentId
                            }
                        };
                        var preflight = await boxClient.FilesManager.PreflightCheckNewVersion(fileId, preflightCheckRequest);
                        if (preflight.Success)
                        {
                            using (var sha1 = SHA1.Create())
                            {
                                var checksum = sha1.ComputeHash(fileStream);
                                return await boxClient.FilesManager.UploadNewVersionAsync(fileName, fileId, fileStream, uploadUri: preflight.UploadUri, contentMD5: checksum);
                            }
                        }
                        else
                        {
                            throw new Exception("Preflight check failed.");
                        }
                    }
                    else
                    {
                        var preflightCheckRequest = new BoxPreflightCheckRequest()
                        {
                            Name = fileName,
                            Size = file.Length,
                            Parent = new BoxItemRequest()
                            {
                                Id = parentId
                            }
                        };
                        var preflight = await boxClient.FilesManager.PreflightCheck(preflightCheckRequest);
                        if (preflight.Success)
                        {
                            using (var sha1 = SHA1.Create())
                            {
                                var checksum = sha1.ComputeHash(fileStream);
                                return await boxClient.FilesManager.UploadAsync(fileRequest, fileStream, uploadUri: preflight.UploadUri, contentMD5: checksum);
                            }
                        }
                        else
                        {
                            throw new Exception("Preflight check failed.");
                        }
                    }
                }
            }
        }

        protected async Task ProcessFileUploadsFromFile(string path, string asUser = "", bool isNewVersion = false)
        {
            try
            {
                path = GeneralUtilities.TranslatePath(path);
                System.Console.WriteLine($"Path: {path}");
                System.Console.WriteLine("Reading file...");
                var fileRequests = this.ReadCustomFile<BoxFileUpload, BoxFileUploadMap>(path);
                System.Console.WriteLine(fileRequests.Count);
                if (!isNewVersion)
                {
                    foreach (var fileRequest in fileRequests)
                    {
                        System.Console.WriteLine($"Processing a file request");
                        System.Console.WriteLine($"File Path: {fileRequest.Path}");
                        try
                        {
                            var uploadedFile = await this.UploadFile(path: fileRequest.Path, parentId: fileRequest.Parent.Id, fileName: fileRequest.Name);
                            this.PrintFile(uploadedFile);
                        }
                        catch (Exception e)
                        {
                            Reporter.WriteError(e.Message);
                        }
                    }
                }
                else
                {
                    foreach (var fileRequest in fileRequests)
                    {
                        System.Console.WriteLine($"Processing a new file version request");
                        System.Console.WriteLine($"File Path: {fileRequest.Path}");
                        try
                        {
                            var uploadedFile = await this.UploadFile(path: fileRequest.Path, fileId: fileRequest.Id, fileName: fileRequest.Name, isNewVersion: true);
                            this.PrintFile(uploadedFile);
                        }
                        catch (Exception e)
                        {
                            Reporter.WriteError(e.Message);
                        }
                    }
                }
                System.Console.WriteLine("Uploaded all files...");
            }
            catch (Exception e)
            {
                Reporter.WriteError(e.Message);
            }
        }

        private async Task<BoxFile> ChunkedUpload(string path, string fileName, string parentFolderId, long fileSize, string fileId = "", bool isNewVersion = false)
        {
            var boxClient = base.ConfigureBoxClient(this._asUser.Value());
            using (var fileInMemoryStream = File.Open(path, FileMode.Open))
            {
                System.Console.WriteLine($"File name: {fileName}");
                BoxFileUploadSession boxFileUploadSession;
                if (isNewVersion)
                {
                    System.Console.WriteLine(fileId);
                    // TODO: Correct after SDK is fixed.
                    boxClient.AddResourcePlugin<BoxFileManagerCommand>();
                    var command = boxClient.ResourcePlugins.Get<BoxFileManagerCommand>();
                    boxFileUploadSession = await command.CreateNewVersionUploadSessionAsync(fileId, fileSize);
                }
                else
                {
                    var boxFileUploadSessionRequest = new BoxFileUploadSessionRequest()
                    {
                        FolderId = parentFolderId,
                        FileName = fileName,
                        FileSize = fileSize
                    };
                    boxFileUploadSession = await boxClient.FilesManager.CreateUploadSessionAsync(boxFileUploadSessionRequest);
                }
                System.Console.WriteLine("Requested for an Upload Session...");
                System.Console.WriteLine($"ID: {boxFileUploadSession.Id}");
                System.Console.WriteLine($"Parts Processed: {boxFileUploadSession.NumPartsProcessed}");
                System.Console.WriteLine($"Part Size: {boxFileUploadSession.PartSize}");
                System.Console.WriteLine($"Abort: {boxFileUploadSession.SessionEndpoints.Abort}");
                System.Console.WriteLine($"Commit: {boxFileUploadSession.SessionEndpoints.Commit}");
                System.Console.WriteLine($"List Parts: {boxFileUploadSession.SessionEndpoints.ListParts}");
                System.Console.WriteLine($"Log Event: {boxFileUploadSession.SessionEndpoints.LogEvent}");
                System.Console.WriteLine($"Status: {boxFileUploadSession.SessionEndpoints.Status}");
                System.Console.WriteLine($"Upload Part: {boxFileUploadSession.SessionEndpoints.UploadPart}");
                System.Console.WriteLine($"Type: {boxFileUploadSession.Type}");
                System.Console.WriteLine($"Total Parts: {boxFileUploadSession.TotalParts}");
                System.Console.WriteLine($"Expires: {boxFileUploadSession.SessionExpiresAt}");
                var completeFileSha = await Task.Run(() =>
                {
                    return Box.V2.Utility.Helper.GetSha1Hash(fileInMemoryStream);
                });
                var boxSessionEndpoint = boxFileUploadSession.SessionEndpoints;
                var uploadPartUri = new Uri(boxSessionEndpoint.UploadPart);
                var commitUri = new Uri(boxSessionEndpoint.Commit);
                var partSize = boxFileUploadSession.PartSize;
                long partSizeLong;
                long.TryParse(partSize, out partSizeLong);
                var numberOfParts = this.GetUploadPartsCount(fileSize, partSizeLong);
                ProgressBar.UpdateProgress($"Processing {fileName}", 0, numberOfParts);
                var boxSessionParts = await UploadPartsInSessionAsync(uploadPartUri, numberOfParts, partSizeLong, fileInMemoryStream, client: boxClient, fileSize: fileSize);

                BoxSessionParts sessionPartsForCommit = new BoxSessionParts() { Parts = boxSessionParts };
                Reporter.WriteInformation("Attempting to commit...");
                const int retryCount = 5;
                var retryInterval = boxSessionParts.Count() * 100;

                // Commit
                if (!string.IsNullOrEmpty(fileId))
                {
                    //TODO: Fix after SDK update
                    var command = boxClient.ResourcePlugins.Get<BoxFileManagerCommand>();
                    var response =
                    await Box.V2.Utility.Retry.ExecuteAsync(
                        async () =>
                            await command.CommitSessionAsync(commitUri, completeFileSha, sessionPartsForCommit),
                        TimeSpan.FromMilliseconds(retryInterval), retryCount);

                    return response;
                }
                else
                {
                    var response =
                    await Box.V2.Utility.Retry.ExecuteAsync(
                        async () =>
                            await boxClient.FilesManager.CommitSessionAsync(commitUri, completeFileSha, sessionPartsForCommit),
                        TimeSpan.FromMilliseconds(retryInterval), retryCount);

                    return response;
                }
            }
        }

        private int GetUploadPartsCount(long totalSize, long partSize)
        {
            if (partSize == 0)
                throw new Exception("Part Size cannot be 0");
            int numberOfParts = 1;
            if (partSize != totalSize)
            {
                numberOfParts = Convert.ToInt32(totalSize / partSize);
                numberOfParts += 1;
            }
            return numberOfParts;
        }

        private Stream GetFilePart(Stream stream, long partSize, long partOffset)
        {
            // Default the buffer size to 4K.
            const int bufferSize = 4096;

            byte[] buffer = new byte[bufferSize];
            int bytesRead = 0;
            stream.Position = partOffset;
            var partStream = new MemoryStream();
            do
            {
                bytesRead = stream.Read(buffer, 0, 4096);
                if (bytesRead > 0)
                {
                    long bytesToWrite = bytesRead;
                    bool shouldBreak = false;
                    if (partStream.Length + bytesRead >= partSize)
                    {
                        bytesToWrite = partSize - partStream.Length;
                        shouldBreak = true;
                    }

                    partStream.Write(buffer, 0, Convert.ToInt32(bytesToWrite));

                    if (shouldBreak)
                    {
                        break;
                    }
                }
            } while (bytesRead > 0);

            return partStream;
        }

        private async Task<IEnumerable<BoxSessionPartInfo>> UploadPartsInSessionAsync(
            Uri uploadPartsUri, int numberOfParts, long partSize, Stream stream, BoxClient client,
            long fileSize, TimeSpan? timeout = null)
        {
            var maxTaskNum = Environment.ProcessorCount + 1;

            // Retry 5 times for 10 seconds
            const int retryMaxCount = 5;
            const int retryMaxInterval = 10;

            var ret = new List<BoxSessionPartInfo>();

            using (SemaphoreSlim concurrencySemaphore = new SemaphoreSlim(maxTaskNum))
            {
                var postTaskTasks = new List<Task>();
                int taskCompleted = 0;

                var tasks = new List<Task<BoxUploadPartResponse>>();
                for (var i = 0; i < numberOfParts; i++)
                {
                    await concurrencySemaphore.WaitAsync();

                    // Split file as per part size
                    var partOffset = partSize * i;

                    // Retry
                    var uploadPartWithRetryTask = Box.V2.Utility.Retry.ExecuteAsync(async () =>
                    {
                        // Release the memory when done
                        using (var partFileStream = this.GetFilePart(stream, partSize,
                                    partOffset))
                        {
                            var sha = Box.V2.Utility.Helper.GetSha1Hash(partFileStream);
                            partFileStream.Position = 0;
                            var uploadPartResponse = await client.FilesManager.UploadPartAsync(
                                uploadPartsUri, sha, partOffset, fileSize, partFileStream,
                                timeout);

                            return uploadPartResponse;
                        }
                    }, TimeSpan.FromSeconds(retryMaxInterval), retryMaxCount);

                    // Have each task notify the Semaphore when it completes so that it decrements the number of tasks currently running.
                    postTaskTasks.Add(uploadPartWithRetryTask.ContinueWith(tsk =>
                        {
                            concurrencySemaphore.Release();
                            ++taskCompleted;
                            ProgressBar.UpdateProgress($"Processing...", taskCompleted, numberOfParts);
                        }
                    ));

                    tasks.Add(uploadPartWithRetryTask);
                }

                var results = await Task.WhenAll(tasks);
                ret.AddRange(results.Select(elem => elem.Part));
            }

            return ret;
        }
    }
}