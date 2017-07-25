using System.IO;
using System.Threading.Tasks;
using BoxCLI.BoxHome;
using BoxCLI.BoxPlatform.Service;
using BoxCLI.CommandUtilities;
using BoxCLI.CommandUtilities.CommandOptions;
using BoxCLI.CommandUtilities.Globalization;
using Microsoft.Extensions.CommandLineUtils;

namespace BoxCLI.Commands.FileSubCommand
{
    public class FileDownloadCommand : FileSubCommandBase
    {
        private CommandArgument _fileId;
        private CommandLineApplication _app;
        public FileDownloadCommand(IBoxPlatformServiceBuilder boxPlatformBuilder, IBoxHome boxHome, LocalizedStringsResource names) 
            : base(boxPlatformBuilder, boxHome, names)
        {
        }
        public override void Configure(CommandLineApplication command)
        {
            _app = command;
            command.Description = "Download a file.";
            _fileId = command.Argument("fileId",
                               "Id of file to download");
            command.OnExecute(async () =>
            {
                return await this.Execute();
            });
            base.Configure(command);
        }

        protected async override Task<int> Execute()
        {
            await this.RunDownload();
            return await base.Execute();
        }

        private async Task RunDownload()
        {
            base.CheckForFileId(this._fileId.Value, this._app);
            await base.DownloadFile(this._fileId.Value);
        }
    }
}