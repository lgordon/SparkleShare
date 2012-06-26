//   SparkleShare, a collaboration and sharing tool.
//   Copyright (C) 2010  Hylke Bons <hylkebons@gmail.com>
//
//   This program is free software: you can redistribute it and/or modify
//   it under the terms of the GNU General Public License as published by
//   the Free Software Foundation, either version 3 of the License, or
//   (at your option) any later version.
//
//   This program is distributed in the hope that it will be useful,
//   but WITHOUT ANY WARRANTY; without even the implied warranty of
//   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//   GNU General Public License for more details.
//
//   You should have received a copy of the GNU General Public License
//   along with this program. If not, see <http://www.gnu.org/licenses/>.


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

using SparkleLib;

namespace SparkleLib.Gitbin {

    // Sets up a fetcher that can get remote folders
    public class SparkleFetcher : SparkleFetcherBase {
	//needs options for amazon s3 bucket
	//best not to have these in git config --global
	//need to have the list of files to pass through git bin written to .gitattributes

        private SparkleGit git;
        private string crypto_salt = "e0d592768d7cf99a"; // TODO: Make unique per repo

        public SparkleFetcher (string server, string required_fingerprint, string remote_path,
            string target_folder, bool fetch_prior_history) : base (server, required_fingerprint, remote_path,
                target_folder, fetch_prior_history)
        {
            Uri uri = RemoteUrl;

            if (!uri.Scheme.Equals ("ssh") &&
                !uri.Scheme.Equals ("https") &&
                !uri.Scheme.Equals ("http") &&
                !uri.Scheme.Equals ("git")) {

                uri = new Uri ("ssh://" + uri);
            }

            if (uri.Host.Equals ("gitorious.org")) {
                if (!uri.AbsolutePath.Equals ("/") &&
                    !uri.AbsolutePath.EndsWith (".git")) {

                    uri = new Uri ("ssh://git@gitorious.org" + uri.AbsolutePath + ".git");

                } else {
                    uri = new Uri ("ssh://git@gitorious.org" + uri.AbsolutePath);
                }

            } else if (uri.Host.Equals ("github.com")) {
                uri = new Uri ("ssh://git@github.com" + uri.AbsolutePath);

            } else if (uri.Host.Equals ("gnome.org")) {
                uri = new Uri ("ssh://git@gnome.org/git" + uri.AbsolutePath);

            } else {
                if (string.IsNullOrEmpty (uri.UserInfo) &&
                    !uri.Scheme.Equals ("https") &&
                    !uri.Scheme.Equals ("http")) {

                    if (uri.Port == -1)
                        uri = new Uri (uri.Scheme + "://git@" + uri.Host + uri.AbsolutePath);
                    else
                        uri = new Uri (uri.Scheme + "://git@" + uri.Host + ":" + uri.Port + uri.AbsolutePath);
                }
            }

            TargetFolder = target_folder;
            RemoteUrl    = uri;
        }


        public override bool Fetch ()
        {
            //need to figure out how cloning a repo that contains git-bin hash files will work
            if (FetchPriorHistory) {
                this.git = new SparkleGit (SparkleConfig.DefaultConfig.TmpPath,
                    "clone " +
                    "--progress " +
                    "--no-checkout " +
                    "\"" + RemoteUrl + "\" \"" + TargetFolder + "\"");

            } else {
                    this.git = new SparkleGit (SparkleConfig.DefaultConfig.TmpPath,
                    "clone " +
                    "--progress " +
                    "--no-checkout " +
                    "--depth=1 " +
                    "\"" + RemoteUrl + "\" \"" + TargetFolder + "\"");
            }

            this.git.StartInfo.RedirectStandardError = true;
            this.git.Start ();

            double percentage = 1.0;
            Regex progress_regex = new Regex (@"([0-9]+)%", RegexOptions.Compiled);

            DateTime last_change     = DateTime.Now;
            TimeSpan change_interval = new TimeSpan (0, 0, 0, 1);

            while (!this.git.StandardError.EndOfStream) {
                string line = this.git.StandardError.ReadLine ();
                Match match = progress_regex.Match (line);
                
                double number = 0.0;
                if (match.Success) {
                    number = double.Parse (match.Groups [1].Value);
                    
                    // The cloning progress consists of two stages: the "Compressing 
                    // objects" stage which we count as 20% of the total progress, and 
                    // the "Receiving objects" stage which we count as the last 80%
                    if (line.Contains ("|"))
                        // "Receiving objects" stage
                        number = (number / 100 * 80 + 20);
                    else
                        // "Compressing objects" stage
                        number = (number / 100 * 20);

                } else {
                    SparkleHelpers.DebugInfo ("Fetcher", line);

                    if (line.StartsWith ("fatal:", true, null) ||
                        line.StartsWith ("error:", true, null)) {

                        base.errors.Add (line);
                    }
                }


                if (number >= percentage) {
                    percentage = number;

                    if (DateTime.Compare (last_change, DateTime.Now.Subtract (change_interval)) < 0) {
                        base.OnProgressChanged (percentage);
                        last_change = DateTime.Now;
                    }
                }
            }
            
            this.git.WaitForExit ();

            SparkleHelpers.DebugInfo ("Git", "Exit code: " + this.git.ExitCode);

            if (this.git.ExitCode == 0) {
                while (percentage < 100) {
                    percentage += 25;

                    if (percentage >= 100)
                        break;

                    Thread.Sleep (500);
                    base.OnProgressChanged (percentage);
                }

                base.OnProgressChanged (100);

                InstallConfiguration ();
                InstallExcludeRules ();

                AddWarnings ();

                return true;

            } else {
                return false;
            }
        }


        public override bool IsFetchedRepoEmpty {
            get {
                SparkleGit git = new SparkleGit (TargetFolder, "rev-parse HEAD");
                git.Start ();

                // Reading the standard output HAS to go before
                // WaitForExit, or it will hang forever on output > 4096 bytes
                git.StandardOutput.ReadToEnd ();
                git.WaitForExit ();

                return (git.ExitCode != 0);
            }
        }


        public override void EnableFetchedRepoCrypto (string password)
        {
            // Define the crypto filter in the config
            string repo_config_file_path = SparkleHelpers.CombineMore (TargetFolder, ".git", "config");
            string config = File.ReadAllText (repo_config_file_path);

            string n = Environment.NewLine;

            config += "[filter \"crypto\"]" + n +
                "\tsmudge = openssl enc -d -aes-256-cbc -base64 -S " + this.crypto_salt + " -pass file:.git/password" + n +
                "\tclean  = openssl enc -e -aes-256-cbc -base64 -S " + this.crypto_salt + " -pass file:.git/password" + n;

            File.WriteAllText (repo_config_file_path, config);


            // Pass all files through the crypto filter
            string git_attributes_file_path = SparkleHelpers.CombineMore (
                TargetFolder, ".git", "info", "attributes");

            File.WriteAllText (git_attributes_file_path, "* filter=crypto");


            // Store the password
            string password_file_path = SparkleHelpers.CombineMore (TargetFolder, ".git", "password");
            File.WriteAllText (password_file_path, password.Trim ());
        }


        public override bool IsFetchedRepoPasswordCorrect (string password)
        {
            string password_check_file_path = Path.Combine (TargetFolder, ".sparkleshare");

            if (!File.Exists (password_check_file_path)) {
                SparkleGit git = new SparkleGit (TargetFolder, "show HEAD:.sparkleshare");
                git.Start ();

                // Reading the standard output HAS to go before
                // WaitForExit, or it will hang forever on output > 4096 bytes
                string output = git.StandardOutput.ReadToEnd ();
                git.WaitForExit ();

                if (git.ExitCode != 0) {
                    return false;

                } else {
                    File.WriteAllText (password_check_file_path, output);
                }
            }

            Process process = new Process () {
                EnableRaisingEvents = true
            };

            process.StartInfo.WorkingDirectory       = TargetFolder;
            process.StartInfo.UseShellExecute        = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow         = true;

            process.StartInfo.FileName  = "openssl";
            process.StartInfo.Arguments = "enc -d -aes-256-cbc -base64 -S " + this.crypto_salt +
                " -pass pass:\"" + password + "\" -in " + password_check_file_path;

            process.Start ();

            // Reading the standard output HAS to go before
            // WaitForExit, or it will hang forever on output > 4096 bytes
            process.StandardOutput.ReadToEnd ();
            process.WaitForExit ();

            if (process.ExitCode == 0) {
                File.Delete (password_check_file_path);
                return true;

            } else {
                return false;
            }
        }


        public override void Stop ()
        {
            try {
                this.git.Close ();
                this.git.Kill ();
                this.git.Dispose ();

            } catch (Exception e) {
                SparkleHelpers.DebugInfo ("Fetcher", "Failed to dispose properly: " + e.Message);
            }
        }


        public override void Complete ()
        {
            if (IsFetchedRepoEmpty)
                return;

            SparkleGit git = new SparkleGit (TargetFolder, "checkout HEAD");
            git.Start ();

            // Reading the standard output HAS to go before
            // WaitForExit, or it will hang forever on output > 4096 bytes
            git.StandardOutput.ReadToEnd ();
            git.WaitForExit ();
        }


        // Install the user's name and email and some config into
        // the newly cloned repository
        private void InstallConfiguration ()
        {
            string repo_config_file_path = SparkleHelpers.CombineMore (TargetFolder, ".git", "config");
            string config = File.ReadAllText (repo_config_file_path);

            string n = Environment.NewLine;

            config = config.Replace ("[core]" + n,
                "[core]" + n +
                "\tquotepath = false" + n + // Show special characters in the logs
                "\tpackedGitLimit = 128m" + n +
                "\tautocrlf = false" + n +
                "\tsafecrlf = false" + n +
                "\bigFileThreshold = 2g" + n +
                "\tpackedGitWindowSize = 128m" + n);

            config = config.Replace ("[remote \"origin\"]" + n,
                "[pack]" + n +
                "\tdeltaCacheSize = 128m" + n +
                "\tpackSizeLimit = 128m" + n +
                "\twindowMemory = 128m" + n +
                "[remote \"origin\"]" + n);
            
            // git-bin filter    
            config = config.Replace ("[remote \"origin\"]" + n,
                "[filter \"bin\"]" + n +
                "\tclean = \"git bin clean %f\"" + n +
                "\tsmudge = \"git bin smudge"\" + n +
                "[remote \"origin\"]" + n);   
            
            // git-bin settings
            // need a way to input the S3 information until 
            // git-bin supports SCP
            config = config.Replace ("[remote \"origin\"]" + n,
                "[git-bin]" + n +    
                "\ts3bucket = \"your bucket name\"" + n +
                "\ts3key = \"your key"" + n +
                "\ts3secretKey = \"your secret key"" + n +
                "\tchunkSize = 1m" + n +
                "[remote \"origin\"]" + n);    

            // Be case sensitive explicitly to work on Mac
            config = config.Replace ("ignorecase = true", "ignorecase = false");

            // Ignore permission changes
            config = config.Replace ("filemode = true", "filemode = false");

            // Write the config to the file
            File.WriteAllText (repo_config_file_path, config);
            SparkleHelpers.DebugInfo ("Fetcher", "Added configuration to '" + repo_config_file_path + "'");
        }


        // Add a .gitignore file to the repo
        private void InstallExcludeRules ()
        {
            DirectoryInfo info = Directory.CreateDirectory (
                SparkleHelpers.CombineMore (TargetFolder, ".git", "info"));

            // File that lists the files we want git to ignore
            string exclude_rules_file_path = Path.Combine (info.FullName, "exclude");
            TextWriter writer = new StreamWriter (exclude_rules_file_path);

            foreach (string exclude_rule in ExcludeRules)
                writer.WriteLine (exclude_rule);

            writer.Close ();

            // File that lists the files we want don't want git to compress.
            // Not compressing the already compressed files saves us memory
            // usage and increases speed
            
            //send all binaries to git-bin
            string no_compression_rules_file_path = Path.Combine (info.FullName, "attributes");
            writer = new StreamWriter (no_compression_rules_file_path);

                // Images
                writer.WriteLine ("*.jpg filter=bin binary");
                writer.WriteLine ("*.jpeg filter=bin binary");
                writer.WriteLine ("*.JPG filter=bin binary");
                writer.WriteLine ("*.JPEG filter=bin binary");

                writer.WriteLine ("*.png filter=bin binary");
                writer.WriteLine ("*.PNG filter=bin binary");

                writer.WriteLine ("*.tiff filter=bin binary");
                writer.WriteLine ("*.TIFF filter=bin binary");

                // Audio
                writer.WriteLine ("*.flac filter=bin binary");
                writer.WriteLine ("*.FLAC filter=bin binary");

                writer.WriteLine ("*.mp3 filter=bin binary");
                writer.WriteLine ("*.MP3 filter=bin binary");

                writer.WriteLine ("*.ogg filter=bin binary");
                writer.WriteLine ("*.OGG filter=bin binary");

                writer.WriteLine ("*.oga filter=bin binary");
                writer.WriteLine ("*.OGA filter=bin binary");

                // Video
                writer.WriteLine ("*.avi filter=bin binary");
                writer.WriteLine ("*.AVI filter=bin binary");

                writer.WriteLine ("*.mov filter=bin binary");
                writer.WriteLine ("*.MOV filter=bin binary");

                writer.WriteLine ("*.mpg filter=bin binary");
                writer.WriteLine ("*.MPG filter=bin binary");
                writer.WriteLine ("*.mpeg filter=bin binary");
                writer.WriteLine ("*.MPEG filter=bin binary");

                writer.WriteLine ("*.mkv filter=bin binary");
                writer.WriteLine ("*.MKV filter=bin binary");

                writer.WriteLine ("*.ogv filter=bin binary");
                writer.WriteLine ("*.OGV filter=bin binary");

                writer.WriteLine ("*.ogx filter=bin binary");
                writer.WriteLine ("*.OGX filter=bin binary");

                writer.WriteLine ("*.webm filter=bin binary");
                writer.WriteLine ("*.WEBM filter=bin binary");

                // Archives
                writer.WriteLine ("*.zip filter=bin binary");
                writer.WriteLine ("*.ZIP filter=bin binary");

                writer.WriteLine ("*.gz filter=bin binary");
                writer.WriteLine ("*.GZ filter=bin binary");

                writer.WriteLine ("*.bz filter=bin binary");
                writer.WriteLine ("*.BZ filter=bin binary");

                writer.WriteLine ("*.bz2 filter=bin binary");
                writer.WriteLine ("*.BZ2 filter=bin binary");

                writer.WriteLine ("*.rpm filter=bin binary");
                writer.WriteLine ("*.RPM filter=bin binary");

                writer.WriteLine ("*.deb filter=bin binary");
                writer.WriteLine ("*.DEB filter=bin binary");

                writer.WriteLine ("*.tgz filter=bin binary");
                writer.WriteLine ("*.TGZ filter=bin binary");

                writer.WriteLine ("*.rar filter=bin binary");
                writer.WriteLine ("*.RAR filter=bin binary");

                writer.WriteLine ("*.ace filter=bin binary");
                writer.WriteLine ("*.ACE filter=bin binary");

                writer.WriteLine ("*.7z filter=bin binary");
                writer.WriteLine ("*.7Z filter=bin binary");

                writer.WriteLine ("*.pak filter=bin binary");
                writer.WriteLine ("*.PAK filter=bin binary");

                writer.WriteLine ("*.tar filter=bin binary");
                writer.WriteLine ("*.TAR filter=bin binary");

            writer.Close ();
        }


        private void AddWarnings ()
        {
            /*
            SparkleGit git = new SparkleGit (TargetFolder,
                "config --global core.excludesfile");

            git.Start ();

            // Reading the standard output HAS to go before
            // WaitForExit, or it will hang forever on output > 4096 bytes
            string output = git.StandardOutput.ReadToEnd ().Trim ();
            git.WaitForExit ();

            if (string.IsNullOrEmpty (output))
                return;
            else
                this.warnings.Add ("You seem to have a system wide ‘gitignore’ file, this may affect SparkleShare files.");
            */
        }
    }
}
