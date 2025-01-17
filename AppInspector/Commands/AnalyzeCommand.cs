﻿// Copyright (C) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE.txt in the project root for license information.

namespace Microsoft.ApplicationInspector.Commands
{
    using GlobExpressions;
    using Microsoft.ApplicationInspector.RulesEngine;
    using Microsoft.CST.RecursiveExtractor;
    using Newtonsoft.Json;
    using ShellProgressBar;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInspector.Common;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;

    /// <summary>
    /// Options specific to analyze operation not to be confused with CLIAnalyzeCmdOptions which include CLI only args
    /// </summary>
    public class AnalyzeOptions
    {
        public IEnumerable<string> SourcePath { get; set; } = Array.Empty<string>();
        public string? CustomRulesPath { get; set; }
        public bool IgnoreDefaultRules { get; set; }
        public IEnumerable<Confidence> ConfidenceFilters { get; set; } = new Confidence[] { Confidence.High, Confidence.Medium };
        public IEnumerable<Severity> SeverityFilters { get; set; } = new Severity[] { Severity.Critical | Severity.Important | Severity.Moderate | Severity.BestPractice | Severity.ManualReview };
        public IEnumerable<string> FilePathExclusions { get; set; } = Array.Empty<string>();
        public bool SingleThread { get; set; } = false;
        /// <summary>
        /// Treat <see cref="LanguageInfo.LangFileType.Build"/> files as if they were <see cref="LanguageInfo.LangFileType.Code"/> when determining if tags should apply.
        /// </summary>
        public bool AllowAllTagsInBuildFiles { get; set; } = false;
        public bool NoShowProgress { get; set; } = true;
        public bool TagsOnly { get; set; } = false;
        public int FileTimeOut { get; set; } = 0;
        public int ProcessingTimeOut { get; set; } = 0;
        public int ContextLines { get; set; } = 3;
        public bool ScanUnknownTypes { get; set; }
        public bool NoFileMetadata { get; set; }
        /// <summary>
        /// If non-zero, and <see cref="TagsOnly"/> is not set, will ignore matches if all of the matches tags have already been found the set value number of times.
        /// </summary>
        public int MaxNumMatchesPerTag { get; set; } = 0;
        public string? CustomCommentsPath { get; set; }
        public string? CustomLanguagesPath { get; set; }
    }

    /// <summary>
    /// Result of Analyze command GetResult() operation
    /// </summary>
    public class AnalyzeResult : Result
    {
        public enum ExitCode
        {
            Success = 0,
            NoMatches = 1,
            CriticalError = Common.Utils.ExitCode.CriticalError, //ensure common value for final exit log mention
            Canceled = 3,
            TimedOut = 4
        }

        [JsonProperty(Order = 2, PropertyName = "resultCode")]
        public ExitCode ResultCode { get; set; }

        /// <summary>
        /// Analyze command result object containing scan properties
        /// </summary>
        [JsonProperty(Order = 3, PropertyName = "metaData")]
        public MetaData Metadata { get; set; }

        public AnalyzeResult()
        {
            Metadata = new MetaData("", "");//needed for serialization for other commands; replaced later
        }
    }

    /// <summary>
    /// Analyze operation for setup and processing of results from Rulesengine
    /// </summary>
    public class AnalyzeCommand
    {
        private readonly ILoggerFactory? _loggerFactory;
        private readonly ILogger _logger;
        private readonly List<string> _srcfileList = new();
        private MetaDataHelper _metaDataHelper; //wrapper containing MetaData object to be assigned to result
        private Languages _languages = new();
        private RuleProcessor _rulesProcessor;
        private const int _sleepDelay = 100;
        private DateTime DateScanned { get; }

        private readonly List<Glob> _fileExclusionList = new();
        private readonly Confidence _confidence = Confidence.Unspecified;
        private readonly AnalyzeOptions _options; //copy of incoming caller options
        private readonly Severity _severity = Severity.Unspecified;

        /// <summary>
        /// Constructor for AnalyzeCommand.
        /// </summary>
        /// <param name="opt">The <see cref="AnalyzeOptions"/> to use for this analysis.</param>
        /// <param name="loggerFactory">The <see cref="ILoggerFactory"/> use for log messages.</param>
        public AnalyzeCommand(AnalyzeOptions opt, ILoggerFactory? loggerFactory = null)
        {
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory?.CreateLogger<AnalyzeCommand>() ?? NullLogger<AnalyzeCommand>.Instance;
            _options = opt;

            if (opt.FilePathExclusions.Any(x => !x.Equals("none"))){
                _fileExclusionList = opt.FilePathExclusions.Select(x => new Glob(x)).ToList();
            }
            else
            {
                _fileExclusionList = new List<Glob>();
            }

            //create metadata helper to wrap and help populate metadata from scan
            _metaDataHelper = new MetaDataHelper(string.Join(',', _options.SourcePath));
            DateScanned = DateTime.Now;
            foreach(Confidence confidence in _options.ConfidenceFilters)
            {
                _confidence |= confidence;
            }
            foreach(Severity severity in _options.SeverityFilters)
            {
                _severity |= severity;
            }
            ConfigSourcetoScan();
            ConfigRules();
        }

        /// <summary>
        /// Simple validation on source path provided for scanning and preparation
        /// </summary>
        private void ConfigSourcetoScan()
        {
            _logger.LogTrace("AnalyzeCommand::ConfigSourcetoScan");

            if (!_options.SourcePath.Any())
            {
                throw new OpException(MsgHelp.FormatString(MsgHelp.ID.CMD_REQUIRED_ARG_MISSING, "SourcePath"));
            }

            foreach(var entry in _options.SourcePath)
            {
                if (Directory.Exists(entry))
                {
                    try
                    {
                        _srcfileList.AddRange(Directory.EnumerateFiles(entry, "*.*", SearchOption.AllDirectories));
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
                else if (File.Exists(entry))
                {
                    _srcfileList.Add(entry);
                }
                else
                {
                    throw new OpException(MsgHelp.FormatString(MsgHelp.ID.CMD_INVALID_FILE_OR_DIR, string.Join(',', _options.SourcePath)));
                }
            }
            if (_srcfileList.Count == 0)
            {
                throw new OpException(MsgHelp.FormatString(MsgHelp.ID.CMD_INVALID_FILE_OR_DIR, string.Join(',', _options.SourcePath)));
            }
        }

        /// <summary>
        /// Add default and/or custom rules paths
        /// Iterate paths and add to ruleset
        /// </summary>
        private void ConfigRules()
        {
            _logger.LogTrace("AnalyzeCommand::ConfigRules");

            if (!string.IsNullOrEmpty(_options.CustomCommentsPath) || !string.IsNullOrEmpty(_options.CustomLanguagesPath))
            {
                _languages = Languages.FromConfigurationFiles(_loggerFactory, _options.CustomCommentsPath, _options.CustomLanguagesPath);
            }

            RuleSet? rulesSet = null;

            if (!_options.IgnoreDefaultRules)
            {
                rulesSet = RuleSetUtils.GetDefaultRuleSet(_loggerFactory);
            }

            if (!string.IsNullOrEmpty(_options.CustomRulesPath))
            {
                if (rulesSet == null)
                {
                    rulesSet = new RuleSet(_loggerFactory);
                }

                if (Directory.Exists(_options.CustomRulesPath))
                {
                    rulesSet.AddDirectory(_options.CustomRulesPath);
                }
                else if (File.Exists(_options.CustomRulesPath)) //verify custom rules before use
                {
                    RulesVerifierOptions rulesVerifierOptions = new RulesVerifierOptions()
                    {
                        FailFast = false,
                        LanguageSpecs = _languages,
                        LoggerFactory = _loggerFactory,
                    };
                    RulesVerifier verifier = new(rulesVerifierOptions);
                    var verification = verifier.Verify(_options.CustomRulesPath);
                    if (!verification.Verified)
                    {
                        throw new OpException(MsgHelp.FormatString(MsgHelp.ID.VERIFY_RULE_LOADFILE_FAILED, _options.CustomRulesPath));
                    }

                    rulesSet.AddRange(verification.CompiledRuleSet);
                }
                else
                {
                    throw new OpException(MsgHelp.FormatString(MsgHelp.ID.CMD_INVALID_RULE_PATH, _options.CustomRulesPath));
                }
            }

            //error check based on ruleset not path enumeration
            if (rulesSet == null || !rulesSet.Any())
            {
                throw new OpException(MsgHelp.GetString(MsgHelp.ID.CMD_NORULES_SPECIFIED));
            }

            //instantiate a RuleProcessor with the added rules and exception for dependency
            var rpo = new RuleProcessorOptions()
            {
                LoggerFactory = _loggerFactory,
                AllowAllTagsInBuildFiles = _options.AllowAllTagsInBuildFiles,
                ConfidenceFilter = _confidence,
                SeverityFilter = _severity,
                Parallel = !_options.SingleThread,
                Languages = _languages
            };

            _rulesProcessor = new RuleProcessor(rulesSet, rpo);
        }

        /// <summary>
        /// Populate the MetaDataHelper with the data from the FileEntries
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <param name="populatedEntries"></param>
        /// <returns></returns>
        public AnalyzeResult.ExitCode PopulateRecords(CancellationToken cancellationToken, IEnumerable<FileEntry> populatedEntries)
        {
            _logger.LogTrace("AnalyzeCommand::PopulateRecords");
            if (_metaDataHelper is null)
            {
                _logger.LogError("MetadataHelper is null");
                throw new NullReferenceException("_metaDataHelper");
            }
            if (_rulesProcessor is null || populatedEntries is null)
            {
                return AnalyzeResult.ExitCode.CriticalError;
            }

            if (_options.SingleThread)
            {
                foreach (var entry in populatedEntries)
                {
                    if (cancellationToken.IsCancellationRequested) { return AnalyzeResult.ExitCode.Canceled; }
                    ProcessAndAddToMetadata(entry);
                }
            }
            else
            {
                try
                {
                    Parallel.ForEach(populatedEntries, new ParallelOptions() { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount * 3 / 2 }, entry =>
                    {
                        try
                        {
                            ProcessAndAddToMetadata(entry);
                        }
                        catch (Exception)
                        {
                            throw;
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    return AnalyzeResult.ExitCode.Canceled;
                }
            }

            return AnalyzeResult.ExitCode.Success;

            void ProcessAndAddToMetadata(FileEntry file)
            {
                var fileRecord = new FileRecord() { FileName = file.FullPath, ModifyTime = file.ModifyTime, CreateTime = file.CreateTime, AccessTime = file.AccessTime };

                var sw = new Stopwatch();
                sw.Start();

                if (_fileExclusionList.Any(x => x.IsMatch(file.FullPath)))
                {
                    _logger.LogDebug(MsgHelp.GetString(MsgHelp.ID.ANALYZE_EXCLUDED_TYPE_SKIPPED), fileRecord.FileName);
                    fileRecord.Status = ScanState.Skipped;
                }
                else
                {
                    if (IsBinary(file.Content))
                    {
                        _logger.LogDebug(MsgHelp.GetString(MsgHelp.ID.ANALYZE_EXCLUDED_BINARY), fileRecord.FileName);
                        fileRecord.Status = ScanState.Skipped;
                    }
                    else
                    {
                        _ = _metaDataHelper.FileExtensions.TryAdd(Path.GetExtension(file.FullPath).Replace('.', ' ').TrimStart(), 0);

                        LanguageInfo languageInfo = new();

                        if (_languages.FromFileName(file.FullPath, ref languageInfo))
                        {
                            _metaDataHelper.AddLanguage(languageInfo.Name);
                        }
                        else
                        {
                            _metaDataHelper.AddLanguage("Unknown");
                            languageInfo = new LanguageInfo() { Extensions = new string[] { Path.GetExtension(file.FullPath) }, Name = "Unknown" };
                            if (!_options.ScanUnknownTypes)
                            {
                                fileRecord.Status = ScanState.Skipped;
                            }
                        }

                        if (fileRecord.Status != ScanState.Skipped)
                        {
                            List<MatchRecord> results = new();

                            void ProcessLambda()
                            {
                                if (_options.TagsOnly)
                                {
                                    results = _rulesProcessor.AnalyzeFile(file, languageInfo, _metaDataHelper.UniqueTags.Keys, -1);
                                }
                                else if (_options.MaxNumMatchesPerTag > 0)
                                {
                                    results = _rulesProcessor.AnalyzeFile(file, languageInfo, _metaDataHelper.UniqueTags.Where(x => x.Value >= _options.MaxNumMatchesPerTag).Select(x => x.Key), _options.ContextLines);
                                }
                                else
                                {
                                    results = _rulesProcessor.AnalyzeFile(file, languageInfo, null, _options.ContextLines);
                                }
                            }

                            if (_options.FileTimeOut > 0)
                            {
                                using var cts = new CancellationTokenSource();
                                var t = Task.Run(() =>
                                {
                                    ProcessLambda();
                                }, cts.Token);
                                try
                                {
                                    if (!t.Wait(new TimeSpan(0, 0, 0, 0, _options.FileTimeOut)))
                                    {
                                        _logger.LogError("{path} timed out.", file.FullPath);
                                        fileRecord.Status = ScanState.TimedOut;
                                        cts.Cancel();
                                    }
                                    else
                                    {
                                        fileRecord.Status = ScanState.Analyzed;
                                    }
                                }
                                catch (Exception e)
                                {
                                    _logger.LogDebug("Failed to analyze file {path}. {type}:{message}. ({stackTrace}), fileRecord.FileName", file.FullPath, e.GetType(), e.Message, e.StackTrace);
                                    fileRecord.Status = ScanState.Error;
                                }
                            }
                            else
                            {
                                ProcessLambda();
                                fileRecord.Status = ScanState.Analyzed;
                            }

                            if (results.Any())
                            {
                                fileRecord.Status = ScanState.Affected;
                                fileRecord.NumFindings = results.Count;
                            }
                            foreach (var matchRecord in results)
                            {
                                if (_options.TagsOnly)
                                {
                                    _metaDataHelper.AddTagsFromMatchRecord(matchRecord);
                                }
                                else if (_options.MaxNumMatchesPerTag > 0)
                                {
                                    if (matchRecord.Tags?.Any(x => _metaDataHelper.UniqueTags.TryGetValue(x, out int value) is bool foundValue && (!foundValue || foundValue && value < _options.MaxNumMatchesPerTag)) ?? false)
                                    {
                                        _metaDataHelper.AddMatchRecord(matchRecord);
                                    }
                                }
                                else
                                {
                                    _metaDataHelper.AddMatchRecord(matchRecord);
                                }
                            }
                        }
                    }
                }

                sw.Stop();

                fileRecord.ScanTime = sw.Elapsed;

                if (!_options.NoFileMetadata)
                {
                    _metaDataHelper.Files.Add(fileRecord);
                }
            }
        }

        /// <summary>
        /// Populate the records in the metadata asynchronously.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>Result code.</returns>
        public async Task<AnalyzeResult.ExitCode> PopulateRecordsAsync(CancellationToken? cancellationToken = null)
        {
            _logger.LogTrace("AnalyzeCommand::PopulateRecordsAsync");
            if (_metaDataHelper is null)
            {
                _logger.LogError("MetadataHelper is null");
                throw new NullReferenceException("_metaDataHelper");
            }
            if (_rulesProcessor is null)
            {
                return AnalyzeResult.ExitCode.CriticalError;
            }
            await foreach (var entry in GetFileEntriesAsync())
            {
                if (cancellationToken?.IsCancellationRequested is true) { return AnalyzeResult.ExitCode.Canceled; }
                await ProcessAndAddToMetadata(entry, cancellationToken);
            }

            return AnalyzeResult.ExitCode.Success;

            async Task ProcessAndAddToMetadata(FileEntry file, CancellationToken? cancellationToken = null)
            {
                var fileRecord = new FileRecord() { FileName = file.FullPath, ModifyTime = file.ModifyTime, CreateTime = file.CreateTime, AccessTime = file.AccessTime };

                var sw = new Stopwatch();
                sw.Start();

                if (_fileExclusionList.Any(x => x.IsMatch(file.FullPath)))
                {
                    _logger.LogDebug(MsgHelp.GetString(MsgHelp.ID.ANALYZE_EXCLUDED_TYPE_SKIPPED), fileRecord.FileName);
                    fileRecord.Status = ScanState.Skipped;
                }
                else
                {
                    if (IsBinary(file.Content))
                    {
                        _logger.LogDebug(MsgHelp.GetString(MsgHelp.ID.ANALYZE_EXCLUDED_BINARY), fileRecord.FileName);
                        fileRecord.Status = ScanState.Skipped;
                    }
                    else
                    {
                        _ = _metaDataHelper.FileExtensions.TryAdd(Path.GetExtension(file.FullPath).Replace('.', ' ').TrimStart(), 0);

                        LanguageInfo languageInfo = new();

                        if (_languages.FromFileName(file.FullPath, ref languageInfo))
                        {
                            _metaDataHelper.AddLanguage(languageInfo.Name);
                        }
                        else
                        {
                            _metaDataHelper.AddLanguage("Unknown");
                            languageInfo = new LanguageInfo() { Extensions = new string[] { Path.GetExtension(file.FullPath) }, Name = "Unknown" };
                            if (!_options.ScanUnknownTypes)
                            {
                                fileRecord.Status = ScanState.Skipped;
                            }
                        }

                        if (fileRecord.Status != ScanState.Skipped)
                        {
                            var contextLines = _options.TagsOnly ? -1 : _options.ContextLines;
                            var ignoredTags = _options.TagsOnly ? _metaDataHelper.UniqueTags.Keys : _options.MaxNumMatchesPerTag > 0 ? _metaDataHelper.UniqueTags.Where(x => x.Value < _options.MaxNumMatchesPerTag).Select(x => x.Key) : null;
                            var results = await _rulesProcessor.AnalyzeFileAsync(file, languageInfo, cancellationToken, ignoredTags, contextLines);
                            fileRecord.Status = ScanState.Analyzed;

                            if (results.Any())
                            {
                                fileRecord.Status = ScanState.Affected;
                                fileRecord.NumFindings = results.Count;
                            }
                            foreach (var matchRecord in results)
                            {
                                if (_options.TagsOnly)
                                {
                                    _metaDataHelper.AddTagsFromMatchRecord(matchRecord);
                                }
                                else if (_options.MaxNumMatchesPerTag > 0)
                                {
                                    if (matchRecord.Tags?.Any(x => _metaDataHelper.UniqueTags.TryGetValue(x, out int value) is bool foundValue && (!foundValue || foundValue && value < _options.MaxNumMatchesPerTag)) ?? true)
                                    {
                                        _metaDataHelper.AddMatchRecord(matchRecord);
                                    }
                                }
                                else
                                {
                                    _metaDataHelper.AddMatchRecord(matchRecord);
                                }
                            }
                        }
                    }
                }

                sw.Stop();

                fileRecord.ScanTime = sw.Elapsed;

                _metaDataHelper.Files.Add(fileRecord);
            }
        }

        /// <summary>
        /// Gets the FileEntries synchronously.
        /// </summary>
        /// <returns>An Enumerable of FileEntries.</returns>
        private IEnumerable<FileEntry> GetFileEntries()
        {
            _logger.LogTrace("AnalyzeCommand::GetFileEntries");

            Extractor extractor = new();
            // For every file, if the file isn't excluded return it, and if it is track the exclusion in the metadata
            foreach (var srcFile in _srcfileList)
            {
                if (_fileExclusionList.Any(x => x.IsMatch(srcFile)))
                {
                    _metaDataHelper?.Metadata.Files.Add(new FileRecord() { FileName = srcFile, Status = ScanState.Skipped });
                }
                else
                {
                    Stream? contents = null;
                    try
                    {
                        contents = File.OpenRead(srcFile);
                    }
                    catch(Exception ex)
                    {
                        _logger.LogError("Failed to open source file '{filename}' for reading. {type}:{message}.", srcFile, ex.GetType().Name, ex.Message);
                        _metaDataHelper?.Metadata.Files.Add(new FileRecord() { FileName = srcFile, Status = ScanState.Error });
                    }
                    if (contents != null)
                    {
                        // This call to extract will implicitly create a new FileEntry and copy the contents stream into a new backing stream used by the FileEntry
                        foreach (var entry in extractor.Extract(srcFile, contents, new ExtractorOptions() { Parallel = false, DenyFilters = _options.FilePathExclusions, MemoryStreamCutoff = 1 }))
                        {
                            yield return entry;
                        }
                    }

                    // Be sure to close the stream after we are done processing it.
                    contents?.Close();
                }
            }
        }

        /// <summary>
        /// Gets the FileEntries asynchronously.
        /// </summary>
        /// <returns>An enumeration of FileEntries</returns>
        private async IAsyncEnumerable<FileEntry> GetFileEntriesAsync()
        {
            _logger.LogTrace("AnalyzeCommand::GetFileEntriesAsync");

            Extractor extractor = new();
            foreach (var srcFile in _srcfileList ?? new List<string>())
            {
                if (_fileExclusionList.Any(x => x.IsMatch(srcFile)))
                {
                    _metaDataHelper?.Metadata.Files.Add(new FileRecord() { FileName = srcFile, Status = ScanState.Skipped });
                }
                else
                {
                    await foreach (var entry in extractor.ExtractAsync(srcFile, new ExtractorOptions() { Parallel = false, DenyFilters = _options.FilePathExclusions, MemoryStreamCutoff = 1 }))
                    {
                        yield return entry;
                    }
                }
            }
        }


        // Follows Perl's model, if there are NULs or too many non printable characters, this is probably a binary file
        private static bool IsBinary(Stream fileContents)
        {
            var numRead = 1;
            var span = new Span<byte>(new byte[8192]);
            var controlsEncountered = 0;
            var maxControlsEncountered = (int)(0.3 * fileContents.Length);
            while (numRead > 0)
            {
                numRead = fileContents.Read(span);
                for (var i = 0; i < numRead; i++)
                {
                    var ch = (char)span[i];
                    if (ch == '\0')
                    {
                        fileContents.Position = 0;
                        return true;
                    }
                    else if (char.IsControl(ch) && !char.IsWhiteSpace(ch))
                    {
                        if (++controlsEncountered > maxControlsEncountered)
                        {
                            fileContents.Position = 0;
                            return true;
                        }
                    }
                }

            }
            fileContents.Position = 0;
            return false;
        }

        /// <summary>
        /// Perform Analysis and get the result Asynchronously
        /// </summary>
        /// <param name="cancellationToken">Cancellation token to stop analysis and return results found so far.</param>
        /// <returns></returns>
        public async Task<AnalyzeResult> GetResultAsync(CancellationToken? cancellationToken = null)
        {
            _logger.LogTrace("AnalyzeCommand::GetResultAsync");
            _logger.LogInformation(MsgHelp.GetString(MsgHelp.ID.CMD_RUNNING), "Analyze");
            if (_metaDataHelper is null)
            {
                _logger.LogError("MetadataHelper is null");
                throw new NullReferenceException("_metaDataHelper");
            }
            AnalyzeResult analyzeResult = new()
            {
                AppVersion = Common.Utils.GetVersionString()
            };

            var exitCode = await PopulateRecordsAsync(cancellationToken);

            //wrapup result status
            if (!_options.NoFileMetadata && _metaDataHelper.Files.All(x => x.Status == ScanState.Skipped))
            {
                _logger.LogError(MsgHelp.GetString(MsgHelp.ID.ANALYZE_NOSUPPORTED_FILETYPES));
                analyzeResult.ResultCode = AnalyzeResult.ExitCode.NoMatches;
            }
            else if (!_metaDataHelper.HasFindings)
            {
                _logger.LogError(MsgHelp.GetString(MsgHelp.ID.ANALYZE_NOPATTERNS));
                analyzeResult.ResultCode = AnalyzeResult.ExitCode.NoMatches;
            }
            else if (_metaDataHelper != null && _metaDataHelper.Metadata != null)
            {
                _metaDataHelper.Metadata.DateScanned = DateScanned.ToString();
                _metaDataHelper.PrepareReport();
                analyzeResult.Metadata = _metaDataHelper.Metadata; //replace instance with metadatahelper processed one
                analyzeResult.ResultCode = AnalyzeResult.ExitCode.Success;
            }

            if (cancellationToken?.IsCancellationRequested is true)
            {
                analyzeResult.ResultCode = AnalyzeResult.ExitCode.Canceled;
            }

            return analyzeResult;
        }

        /// <summary>
        /// Main entry point to start analysis from CLI; handles setting up rules, directory enumeration
        /// file type detection and handoff
        /// Pre: All Configure Methods have been called already and we are ready to SCAN
        /// </summary>
        /// <returns></returns>
        public AnalyzeResult GetResult()
        {
            _logger.LogTrace("AnalyzeCommand::GetResultAsync");
            _logger.LogInformation(MsgHelp.GetString(MsgHelp.ID.CMD_RUNNING), "Analyze");
            if (_metaDataHelper is null)
            {
                _logger.LogError("MetadataHelper is null");
                throw new NullReferenceException("_metaDataHelper");
            }
            AnalyzeResult analyzeResult = new()
            {
                AppVersion = Utils.GetVersionString()
            };

            var timedOut = false;

            if (!_options.NoShowProgress)
            {
                var doneEnumerating = false;
                ConcurrentBag<FileEntry> fileQueue = new();

                _ = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        foreach (var entry in GetFileEntries())
                        {
                            fileQueue.Add(entry);
                        }
                    }
                    catch (OverflowException e)
                    {
                        _logger.LogError("Overflowed while extracting file entries. Check the input for quines or zip bombs. {message}", e.Message);
                    }
                    doneEnumerating = true;
                });

                var options = new ProgressBarOptions
                {
                    ForegroundColor = ConsoleColor.Yellow,
                    ForegroundColorDone = ConsoleColor.DarkGreen,
                    BackgroundColor = ConsoleColor.DarkGray,
                    BackgroundCharacter = '\u2593',
                    DisableBottomPercentage = true
                };

                using (var pbar = new IndeterminateProgressBar("Enumerating Files.", options))
                {
                    while (!doneEnumerating)
                    {
                        Thread.Sleep(_sleepDelay);
                        pbar.Message = $"Enumerating Files. {fileQueue.Count} Discovered.";
                    }
                    pbar.Message = $"Enumerating Files. {fileQueue.Count} Discovered.";

                    pbar.Finished();
                }
                Console.WriteLine();
                var doneProcessing = false;

                var options2 = new ProgressBarOptions
                {
                    ForegroundColor = ConsoleColor.Yellow,
                    ForegroundColorDone = ConsoleColor.DarkGreen,
                    BackgroundColor = ConsoleColor.DarkGray,
                    BackgroundCharacter = '\u2593',
                    DisableBottomPercentage = false,
                    ShowEstimatedDuration = true
                };
                using (var progressBar = new ProgressBar(fileQueue.Count, $"Analyzing Files.", options2))
                {
                    var sw = new Stopwatch();
                    sw.Start();
                    _ = Task.Factory.StartNew(() =>
                    {
                        DoProcessing(fileQueue);
                        doneProcessing = true;
                    });

                    while (!doneProcessing)
                    {
                        Thread.Sleep(_sleepDelay);
                        var current = _metaDataHelper.Files.Count;
                        var timePerRecord = sw.Elapsed.TotalMilliseconds / current;
                        var millisExpected = (int)(timePerRecord * (fileQueue.Count - current));
                        var timeExpected = new TimeSpan(0, 0, 0, 0, millisExpected);
                        progressBar.Tick(_metaDataHelper.Files.Count, timeExpected, $"Analyzing Files. {_metaDataHelper.Matches.Count} Matches. {_metaDataHelper.Files.Count(x => x.Status == ScanState.Skipped)} Files Skipped. {_metaDataHelper.Files.Count(x => x.Status == ScanState.TimedOut)} Timed Out. {_metaDataHelper.Files.Count(x => x.Status == ScanState.Affected)} Affected. {_metaDataHelper.Files.Count(x => x.Status == ScanState.Analyzed)} Not Affected.");
                    }

                    progressBar.Message = $"{_metaDataHelper.Matches.Count} Matches. {_metaDataHelper.Files.Count(x => x.Status == ScanState.Skipped)} Files Skipped. {_metaDataHelper.Files.Count(x => x.Status == ScanState.TimedOut)} Timed Out. {_metaDataHelper.Files.Count(x => x.Status == ScanState.Affected)} Affected. {_metaDataHelper.Files.Count(x => x.Status == ScanState.Analyzed)} Not Affected.";
                    progressBar.Tick(progressBar.MaxTicks);
                }
                Console.WriteLine();
            }
            else
            {
                DoProcessing(GetFileEntries());
            }

            //wrapup result status
            if (!_options.NoFileMetadata && _metaDataHelper.Files.All(x => x.Status == ScanState.Skipped))
            {
                _logger.LogError(MsgHelp.GetString(MsgHelp.ID.ANALYZE_NOSUPPORTED_FILETYPES));
                analyzeResult.ResultCode = AnalyzeResult.ExitCode.NoMatches;
            }
            else if (!_metaDataHelper.HasFindings)
            {
                _logger.LogError(MsgHelp.GetString(MsgHelp.ID.ANALYZE_NOPATTERNS));
                analyzeResult.ResultCode = AnalyzeResult.ExitCode.NoMatches;
            }
            else
            {
                analyzeResult.ResultCode = AnalyzeResult.ExitCode.Success;
            }

            if (_metaDataHelper != null && _metaDataHelper.Metadata != null)
            {
                _metaDataHelper.Metadata.DateScanned = DateScanned.ToString();
                _metaDataHelper.PrepareReport();
                analyzeResult.Metadata = _metaDataHelper.Metadata; //replace instance with metadatahelper processed one
            }

            if (timedOut)
            {
                _logger.LogError(MsgHelp.GetString(MsgHelp.ID.ANALYZE_PROCESSING_TIMED_OUT));
                analyzeResult.Metadata.TimedOut = true;
                analyzeResult.ResultCode = AnalyzeResult.ExitCode.TimedOut;
            }

            return analyzeResult;

            void DoProcessing(IEnumerable<FileEntry> fileEntries)
            {
                if (_options.ProcessingTimeOut > 0)
                {
                    using var cts = new CancellationTokenSource();
                    var t = Task.Run(() => PopulateRecords(cts.Token, fileEntries), cts.Token);
                    if (!t.Wait(new TimeSpan(0, 0, 0, 0, _options.ProcessingTimeOut)))
                    {
                        timedOut = true;
                        cts.Cancel();
                        if (_metaDataHelper is not null)
                        {
                            // Populate skips for all the entries we didn't process
                            foreach (var entry in fileEntries.Where(x => !_metaDataHelper.Files.Any(y => x.FullPath == y.FileName)))
                            {
                                _metaDataHelper.Files.Add(new FileRecord() { AccessTime = entry.AccessTime, CreateTime = entry.CreateTime, ModifyTime = entry.ModifyTime, FileName = entry.FullPath, Status = ScanState.TimeOutSkipped });
                            }
                        }
                    }
                }
                else
                {
                    PopulateRecords(new CancellationToken(), fileEntries);
                }
            }
        }
    }
}