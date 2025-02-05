// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Utilities;
using Tool.ServicePipDaemon;

namespace Tool.DropDaemon
{
    /// <summary>
    /// Functions used for Generating Build Manifest file and Catalog Signing.
    /// </summary>
    public static class BuildManifestHelper
    {
        // Files associated with Build Manifest SBOMs are stored inside '_manifest' folder, placed directly under the drop root.
        /// <nodoc/>
        public const string BuildManifestFilename = "manifest.json";
        /// <nodoc/>
        public const string BsiFilename = "bsi.json";
        /// <nodoc/>
        public const string ManifestFileDestination = "/_manifest/";
        /// <nodoc/>
        public const string DropBuildManifestPath = ManifestFileDestination + BuildManifestFilename;
        /// <nodoc/>
        public const string DropBsiPath = ManifestFileDestination + BsiFilename;
        /// <nodoc/>
        public const string CatalogFilename = "manifest.cat";
        /// <nodoc/>
        public const string DropCatalogFilePath = ManifestFileDestination + CatalogFilename;

        private const int ExecutableMaxRuntimeInMinute = 3;

        /// <summary>
        /// Generates a local catalog file and signs it using EsrpManifestSign.exe from CloudBuild.
        /// </summary>
        /// <param name="makeCatToolPath"></param>
        /// <param name="esrpSignToolPath"></param>
        /// <param name="buildManifestLocalPath"></param>
        /// <param name="bsiFileLocalPath"></param>
        /// <returns>Payload contains errorMessage if !Success, else contains local path to cat file</returns>
        public async static Task<(bool Success, string Payload)> GenerateSignedCatalogAsync(
            string makeCatToolPath,
            string esrpSignToolPath,
            string buildManifestLocalPath,
            string bsiFileLocalPath)
        {
            // Details about the [CatalogFiles] section: https://stackoverflow.com/questions/52285385/makecat-failure-no-members-found/53205550#53205550
            var catFileSb = Pools.StringBuilderPool.GetInstance().Instance;
            catFileSb.Append("[CatalogFiles]");

            catFileSb.Append($@"{Environment.NewLine}<HASH>{BuildManifestFilename}={buildManifestLocalPath}");
            catFileSb.Append($@"{Environment.NewLine}<HASH>{BuildManifestFilename}ATTR1=0x11010001:File:{BuildManifestFilename}");

            catFileSb.Append($@"{Environment.NewLine}<HASH>{BsiFilename}={bsiFileLocalPath}");
            catFileSb.Append($@"{Environment.NewLine}<HASH>{BsiFilename}ATTR1=0x11010001:File:{BsiFilename}");

            string cdfPath = Path.GetTempFileName();
            string tempDir = Path.GetDirectoryName(cdfPath);
            string catPath = Path.Combine(tempDir, CatalogFilename);

            try
            {
                File.WriteAllText(cdfPath, $@"
[CatalogHeader]
Name={CatalogFilename}
CatalogVersion=2
HashAlgorithms=SHA256

{catFileSb}
");

                // Run makecat.exe on the CDF to produce a .cat file.
                var makeCatExecutionResult = await TryExecuteProcessAsync(makeCatToolPath, $@"-v ""{cdfPath}""", tempDir);
                if (!makeCatExecutionResult.Succeeded || !File.Exists(catPath))
                {
                    return (false, $"Failure occured during Build Manifest CAT file generation at path '{catPath}'. Failure: {makeCatExecutionResult.Failure.DescribeIncludingInnerFailures()}");
                }

                // Sign the .cat file using EsrpManifestSign.exe from CloudBuild
                var esrpSignExecutionResult = await TryExecuteProcessAsync(esrpSignToolPath,
                    $@"-s ""{catPath}"" -o ""{tempDir}""",
                    tempDir);
                if (!esrpSignExecutionResult.Succeeded || !File.Exists(catPath))
                {
                    return (false, $"Unable to sign Manifest.cat at path '{catPath}' using EsrpManifestSign.exe. Failure: {esrpSignExecutionResult.Failure.DescribeIncludingInnerFailures()}");
                }
            }
            catch (IOException e)
            {
                return (false, $"Exception occured during GenerateSignedCatalogAsync :{e.Message}");
            }
            finally
            {
                // Delete temporary file created during Build Manifest signing
                try
                {
                    File.Delete(cdfPath);
                }
                catch (IOException)
                {
                    // Can be ignored
                }
            }

            return (true, catPath);
        }

        /// <summary>
        /// Executes a Process
        /// </summary>
        /// <param name="exePath">Path to exe file</param>
        /// <param name="args">Args to be passed to exe</param>
        /// <param name="tempDir">Working directory for exe</param>
        /// <returns>(<see cref="Process.ExitCode"/> == 0 AND !timeOut, StdErr, StdOut)</returns>
        private async static Task<Possible<bool>> TryExecuteProcessAsync(string exePath, string args, string tempDir)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(exePath, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = tempDir
                },
                EnableRaisingEvents = true
            };

            StringBuilder stdOutBuilder = new StringBuilder();
            StringBuilder stdErrBuilder = new StringBuilder();

            using (var executor = new AsyncProcessExecutor(
                process,
                TimeSpan.FromMinutes(ExecutableMaxRuntimeInMinute),
                line => { if (line != null) { stdOutBuilder.AppendLine(line); } },
                line => { if (line != null) { stdErrBuilder.AppendLine(line); } }))
            {
                executor.Start();
                await executor.WaitForExitAsync();
                await executor.WaitForStdOutAndStdErrAsync();

                if (executor.Process.ExitCode == 0 && !executor.TimedOut)
                {
                    return true;
                }

                string errorReturn = $"StdErr: { stdErrBuilder }, StdOut: { stdOutBuilder }";

                return new Failure<string>(errorReturn);
            }
        }

        /// <summary>
        /// Checks if provided <see cref="DropConfig"/> contains all required fields for Build Manifest generation and signing.
        /// </summary>
        /// <returns>True if dropConfig contains all required fields</returns>
        public static bool VerifyBuildManifestRequirements(DropConfig dropConfig, out string error)
        {
            if (dropConfig.GenerateSignedManifest == true)
            {
                List<string> missingFields = new List<string>();

                if (string.IsNullOrEmpty(dropConfig.Repo))
                {
                    missingFields.Add("repo");
                }

                if (string.IsNullOrEmpty(dropConfig.Branch))
                {
                    missingFields.Add("branch");
                }

                if (string.IsNullOrEmpty(dropConfig.CommitId))
                {
                    missingFields.Add("commitId");
                }

                if (string.IsNullOrEmpty(dropConfig.CloudBuildId))
                {
                    missingFields.Add("cloudBuildId");
                }

                if (string.IsNullOrEmpty(dropConfig.BsiFileLocation))
                {
                    missingFields.Add("BsiFileLocation");
                }

                if (string.IsNullOrEmpty(dropConfig.MakeCatToolPath))
                {
                    missingFields.Add("MakeCatToolPath");
                }

                if (string.IsNullOrEmpty(dropConfig.EsrpManifestSignToolPath))
                {
                    missingFields.Add("EsrpManifestSignToolPath");
                }

                if (missingFields.Count != 0)
                {
                    error = $"GenerateSignedManifest = true, but the following required fields are missing: {string.Join(", ", missingFields)}";
                    return false;
                }

                List<string> missingFiles = new List<string>();

                if (!File.Exists(dropConfig.MakeCatToolPath))
                {
                    missingFiles.Add($"MakeCatTool does not exist at : '{dropConfig.MakeCatToolPath}'");
                }

                if (!File.Exists(dropConfig.EsrpManifestSignToolPath))
                {
                    missingFiles.Add($"EsrpManifestSignTool does not exist at : '{dropConfig.EsrpManifestSignToolPath}'");
                }

                if (missingFiles.Count != 0)
                {
                    error = $"GenerateSignedManifest = true, but the the following file(s) are missing on disk: {string.Join(", ", missingFiles)}";
                    return false;
                }
            }

            error = null;
            return true;
        }
    }
}
