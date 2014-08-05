using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Xml;

namespace FeedBuilder
{
    [Flags]
    enum ExitCodes
    {
        Success = 0,
        Failure = 1,
        FileNotFound = 2,
        InvalidFeedFileLocation = 4
    }

    class Program
    {
        private static ArgumentsParser _argParser;
        private static IDictionary<string, FileInfoEx> _files = new Dictionary<string, FileInfoEx>();

        #region " Properties"

        public static string FileName { get; set; }
        public bool ShowGui { get; set; }

        #endregion

        static int Main(string[] args)
        {
            _argParser = new ArgumentsParser(args);
            if (!_argParser.HasArgs)
            {
                ConsoleHelper.Warn("No arguments provided. Exiting.");
                return (int)ExitCodes.Success;
            }
            FileName = _argParser.FileName;
            if (!string.IsNullOrEmpty(FileName))
            {
                if (File.Exists(FileName))
                {
                    var p = new FeedBuilderSettingsProvider();
                    p.LoadFrom(FileName);
                    ReadFiles();
                }
                else
                {
                    ConsoleHelper.Warn("Unable to locate file {0}", FileName);
                    return (int)(ExitCodes.Failure | ExitCodes.FileNotFound);
                }
            }

            if (_argParser.Build) return Build();
            return (int)ExitCodes.Success;
        }

        private static void ReadFiles()
        {
            if (string.IsNullOrEmpty(Settings.Default.OutputFolder.Trim()) || !Directory.Exists(Settings.Default.OutputFolder.Trim())) return;

            var outputDir = Settings.Default.OutputFolder.Trim();
            var outputDirLength = outputDir.Length;

            var enumerator = new FileSystemEnumerator(Settings.Default.OutputFolder.Trim(), "*.*", true);
            foreach (var fi in enumerator.Matches())
            {
                var thisFile = fi.FullName;
                if ((IsIgnorable(thisFile)))
                {
                    ConsoleHelper.Warn("Skipping {0}", thisFile);
                    continue; //skip files that we can ignore
                }
                
                var thisInfo = new FileInfoEx(thisFile, outputDirLength);
                var thisItem = new KeyValuePair<string, FileInfoEx>(thisInfo.RelativeName, thisInfo);
                _files.Add(thisItem);
                ConsoleHelper.Success("Added {0} to file list", thisItem.Key);
            }
        }

        private static bool IsIgnorable(string filename)
        {
            string ext = Path.GetExtension(filename);
            if ((Settings.Default.IgnoreDebugSymbols && ext == ".pdb")) return true;
            return (Settings.Default.IgnoreVsHosting && filename.ToLower().Contains("vshost.exe"));
        }

        private static int Build()
        {
            ConsoleHelper.Info("Building NAppUpdater feed '{0}'", Settings.Default.BaseURL.Trim());
            if (string.IsNullOrEmpty(Settings.Default.FeedXML.Trim()))
            {
                const string msg = "The feed file location needs to be defined.\n" + "The outputs cannot be generated without this.";
                ConsoleHelper.Error(msg);
                return (int)(ExitCodes.Failure | ExitCodes.InvalidFeedFileLocation);
            }
            // If the target folder doesn't exist, create a path to it
            string dest = Settings.Default.FeedXML.Trim();
            var destDir = Directory.GetParent(new FileInfo(dest).FullName);
            if (!Directory.Exists(destDir.FullName)) Directory.CreateDirectory(destDir.FullName);

            XmlDocument doc = new XmlDocument();
            XmlDeclaration dec = doc.CreateXmlDeclaration("1.0", "utf-8", null);

            doc.AppendChild(dec);
            XmlElement feed = doc.CreateElement("Feed");
            if (!string.IsNullOrEmpty(Settings.Default.BaseURL.Trim())) feed.SetAttribute("BaseUrl", Settings.Default.BaseURL.Trim());
            doc.AppendChild(feed);

            XmlElement tasks = doc.CreateElement("Tasks");

            ConsoleHelper.Info("Processing feed items");
            int itemsCopied = 0;
            int itemsCleaned = 0;
            int itemsSkipped = 0;
            int itemsFailed = 0;
            int itemsMissingConditions = 0;
            foreach (var thisItem in _files)
            {
                string destFile = "";
                string folder = "";
                string filename = "";
                try
                {
                    folder = Path.GetDirectoryName(Settings.Default.FeedXML.Trim());
                    filename = thisItem.Value.RelativeName;
                    if (folder != null) destFile = Path.Combine(folder, filename);
                }
                catch { }
                if (destFile == "" || folder == "" || filename == "")
                {
                    var msg = string.Format("The file could not be pathed:\nFolder:'{0}'\nFile:{1}", folder, filename);
                    ConsoleHelper.Warn(msg);
                    continue;
                }

                    var fileInfoEx = thisItem.Value;
                    XmlElement task = doc.CreateElement("FileUpdateTask");
                    task.SetAttribute("localPath", fileInfoEx.RelativeName);

                    // generate FileUpdateTask metadata items
                    task.SetAttribute("lastModified", fileInfoEx.FileInfo.LastWriteTime.ToFileTime().ToString(CultureInfo.InvariantCulture));
                    task.SetAttribute("fileSize", fileInfoEx.FileInfo.Length.ToString(CultureInfo.InvariantCulture));
                    if (!string.IsNullOrEmpty(fileInfoEx.FileVersion)) task.SetAttribute("version", fileInfoEx.FileVersion);

                    XmlElement conds = doc.CreateElement("Conditions");
                    XmlElement cond;
                    bool hasFirstCondition = false;

                    //File Exists
                    cond = doc.CreateElement("FileExistsCondition");
                    cond.SetAttribute("type", "or");
                    conds.AppendChild(cond);


                    //Version
                    if (Settings.Default.CompareVersion && !string.IsNullOrEmpty(fileInfoEx.FileVersion))
                    {
                        cond = doc.CreateElement("FileVersionCondition");
                        cond.SetAttribute("what", "below");
                        cond.SetAttribute("version", fileInfoEx.FileVersion);
                        conds.AppendChild(cond);
                        hasFirstCondition = true;
                    }

                    //Size
                    if (Settings.Default.CompareSize)
                    {
                        cond = doc.CreateElement("FileSizeCondition");
                        cond.SetAttribute("type", hasFirstCondition ? "or-not" : "not");
                        cond.SetAttribute("what", "is");
                        cond.SetAttribute("size", fileInfoEx.FileInfo.Length.ToString(CultureInfo.InvariantCulture));
                        conds.AppendChild(cond);
                    }

                    //Date
                    if (Settings.Default.CompareDate)
                    {
                        cond = doc.CreateElement("FileDateCondition");
                        if (hasFirstCondition) cond.SetAttribute("type", "or");
                        cond.SetAttribute("what", "older");
                        // local timestamp, not UTC
                        cond.SetAttribute("timestamp", fileInfoEx.FileInfo.LastWriteTime.ToFileTime().ToString(CultureInfo.InvariantCulture));
                        conds.AppendChild(cond);
                    }

                    //Hash
                    if (Settings.Default.CompareHash)
                    {
                        cond = doc.CreateElement("FileChecksumCondition");
                        cond.SetAttribute("type", hasFirstCondition ? "or-not" : "not");
                        cond.SetAttribute("checksumType", "sha256");
                        cond.SetAttribute("checksum", fileInfoEx.Hash);
                        conds.AppendChild(cond);
                    }

                    if (conds.ChildNodes.Count == 0) itemsMissingConditions++;
                    task.AppendChild(conds);
                    tasks.AppendChild(task);

                    ConsoleHelper.Success("Added tasks for {0}", fileInfoEx.RelativeName);

                    if (Settings.Default.CopyFiles)
                    {
                        if (CopyFile(fileInfoEx.FileInfo.FullName, destFile)) itemsCopied++;
                        else itemsFailed++;
                    }
            }
            feed.AppendChild(tasks);
            doc.Save(Settings.Default.FeedXML.Trim());

            // open the outputs folder if we're running from the GUI or 
            // we have an explicit command line option to do so
            if (!_argParser.HasArgs || _argParser.OpenOutputsFolder) OpenOutputsFolder();
            ConsoleHelper.Success("Done building feed.");
            if (itemsCopied > 0) ConsoleHelper.Success("{0,5} items copied", itemsCopied);
            if (itemsCleaned > 0) ConsoleHelper.Success("{0,5} items cleaned", itemsCleaned);
            if (itemsSkipped > 0) ConsoleHelper.Success("{0,5} items skipped", itemsSkipped);
            if (itemsFailed > 0) ConsoleHelper.Success("{0,5} items failed", itemsFailed);
            if (itemsMissingConditions > 0) ConsoleHelper.Success("{0,5} items without any conditions", itemsMissingConditions);

            return (int)ExitCodes.Success;
        }

        private static bool CopyFile(string sourceFile, string destFile)
        {
            // If the target folder doesn't exist, create the path to it
            var fi = new FileInfo(destFile);
            var d = Directory.GetParent(fi.FullName);
            if (!Directory.Exists(d.FullName)) CreateDirectoryPath(d.FullName);

            // Copy with delayed retry
            int retries = 3;
            while (retries > 0)
            {
                try
                {
                    if (File.Exists(destFile)) File.Delete(destFile);
                    File.Copy(sourceFile, destFile);
                    retries = 0; // success
                    return true;
                }
                catch (IOException)
                {
                    // Failed... let's try sleeping a bit (slow disk maybe)
                    if (retries-- > 0) Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    // same handling as IOException
                    if (retries-- > 0) Thread.Sleep(200);
                }
            }
            return false;
        }

        private static void CreateDirectoryPath(string directoryPath)
        {
            // Create the folder/path if it doesn't exist, with delayed retry
            int retries = 3;
            while (retries > 0 && !Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
                if (retries-- < 3) Thread.Sleep(200);
            }
        }

        private static void OpenOutputsFolder()
        {
            string dir = Path.GetDirectoryName(Settings.Default.FeedXML.Trim());
            if (dir == null) return;
            CreateDirectoryPath(dir);
            Process process = new Process
            {
                StartInfo =
                {
                    UseShellExecute = true,
                    FileName = dir
                }
            };
            process.Start();
        }
    }
}
