using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Data;
using AutoOrganize.Model;
using Emby.Naming.Common;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace AutoOrganize.Core
{
    /// <inheritdoc/>
    public class FileOrganizationService : IFileOrganizationService
    {
        private readonly ITaskManager _taskManager;
        private readonly IFileOrganizationRepository _repo;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<FileOrganizationService> _logger;
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly ILibraryManager _libraryManager;
        private readonly IServerConfigurationManager _config;
        private readonly IFileSystem _fileSystem;
        private readonly IProviderManager _providerManager;
        private readonly ConcurrentDictionary<string, bool> _inProgressItemIds = new ConcurrentDictionary<string, bool>();
        private readonly NamingOptions _namingOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileOrganizationService"/> class.
        /// </summary>
        [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1611:Element parameters should be documented", Justification = "Parameter names/types are self-documenting.")]
        public FileOrganizationService(
            ITaskManager taskManager,
            IFileOrganizationRepository repo,
            ILoggerFactory loggerFactory,
            ILibraryMonitor libraryMonitor,
            ILibraryManager libraryManager,
            IServerConfigurationManager config,
            IFileSystem fileSystem,
            IProviderManager providerManager)
        {
            _taskManager = taskManager;
            _repo = repo;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<FileOrganizationService>();
            _libraryMonitor = libraryMonitor;
            _libraryManager = libraryManager;
            _config = config;
            _fileSystem = fileSystem;
            _providerManager = providerManager;
            _namingOptions = new NamingOptions();
        }

        /// <inheritdoc/>
        public void BeginProcessNewFiles()
        {
            _taskManager.CancelIfRunningAndQueue<OrganizerScheduledTask>();
        }

        /// <inheritdoc/>
        public void SaveResult(FileOrganizationResult result, CancellationToken cancellationToken)
        {
            if (result == null || string.IsNullOrEmpty(result.OriginalPath))
            {
                throw new ArgumentNullException(nameof(result));
            }

            result.Id = result.OriginalPath.GetMD5().ToString("N", CultureInfo.InvariantCulture);

            _repo.SaveResult(result, cancellationToken);
        }

        /// <inheritdoc/>
        public void SaveResult(SmartMatchResult result, CancellationToken cancellationToken)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            _repo.SaveResult(result, cancellationToken);
        }

        /// <inheritdoc/>
        public QueryResult<FileOrganizationResult> GetResults(FileOrganizationResultQuery query)
        {
            var results = _repo.GetResults(query);

            foreach (var result in results.Items)
            {
                result.IsInProgress = _inProgressItemIds.ContainsKey(result.Id);
            }

            return results;
        }

        /// <inheritdoc/>
        public FileOrganizationResult GetResult(string id)
        {
            var result = _repo.GetResult(id);

            if (result != null)
            {
                result.IsInProgress = _inProgressItemIds.ContainsKey(result.Id);
            }

            return result;
        }

        /// <inheritdoc/>
        public FileOrganizationResult GetResultBySourcePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            var id = path.GetMD5().ToString("N", CultureInfo.InvariantCulture);

            return GetResult(id);
        }

        /// <inheritdoc/>
        public async Task DeleteOriginalFile(string resultId)
        {
            var result = _repo.GetResult(resultId);

            _logger.LogInformation("Requested to delete {0}", result.OriginalPath);

            if (!AddToInProgressList(result, false))
            {
                throw new OrganizationException("Path is currently processed otherwise. Please try again later.");
            }

            try
            {
                _fileSystem.DeleteFile(result.OriginalPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting {0}", result.OriginalPath);
            }
            finally
            {
                RemoveFromInprogressList(result);
            }

            await _repo.Delete(resultId).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task PerformOrganization(string resultId)
        {
            var result = _repo.GetResult(resultId);

            var options = _config.GetAutoOrganizeOptions();

            if (string.IsNullOrEmpty(result.TargetPath))
            {
                throw new ArgumentException("No target path available.");
            }

            FileOrganizationResult organizeResult;
            switch (result.Type)
            {
                case FileOrganizerType.Episode:
                    var episodeOrganizer = new EpisodeFileOrganizer(
                        this,
                        _fileSystem,
                        _loggerFactory.CreateLogger<EpisodeFileOrganizer>(),
                        _libraryManager,
                        _libraryMonitor,
                        _providerManager,
                        _namingOptions);
                    organizeResult = await episodeOrganizer.OrganizeEpisodeFile(result.OriginalPath, options.TvOptions, CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                case FileOrganizerType.Movie:
                    var movieOrganizer = new MovieFileOrganizer(
                        this,
                        _fileSystem,
                        _loggerFactory.CreateLogger<MovieFileOrganizer>(),
                        _libraryManager,
                        _libraryMonitor,
                        _providerManager,
                        _namingOptions);
                    organizeResult = await movieOrganizer.OrganizeMovieFile(result.OriginalPath, options.MovieOptions, true, CancellationToken.None)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new OrganizationException("No organizer exist for the type " + result.Type);
            }

            if (organizeResult.Status != FileSortingStatus.Success)
            {
                throw new OrganizationException(result.StatusMessage);
            }
        }

        /// <inheritdoc/>
        public async Task ClearLog()
        {
            await _repo.DeleteAll().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task ClearCompleted()
        {
            await _repo.DeleteCompleted().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task PerformOrganization(EpisodeFileOrganizationRequest request)
        {
            var organizer = new EpisodeFileOrganizer(
                this,
                _fileSystem,
                _loggerFactory.CreateLogger<EpisodeFileOrganizer>(),
                _libraryManager,
                _libraryMonitor,
                _providerManager,
                _namingOptions);

            var options = _config.GetAutoOrganizeOptions();
            var result = await organizer.OrganizeWithCorrection(request, options.TvOptions, CancellationToken.None).ConfigureAwait(false);

            if (result.Status != FileSortingStatus.Success)
            {
                throw new Exception(result.StatusMessage);
            }
        }

        /// <inheritdoc/>
        public async Task PerformOrganization(MovieFileOrganizationRequest request)
        {
            var organizer = new MovieFileOrganizer(
                this,
                _fileSystem,
                _loggerFactory.CreateLogger<MovieFileOrganizer>(),
                _libraryManager,
                _libraryMonitor,
                _providerManager,
                _namingOptions);

            var options = _config.GetAutoOrganizeOptions();
            var result = await organizer.OrganizeWithCorrection(request, options.MovieOptions, CancellationToken.None).ConfigureAwait(false);

            if (result.Status != FileSortingStatus.Success)
            {
                throw new Exception(result.StatusMessage);
            }
        }

        /// <inheritdoc/>
        public QueryResult<SmartMatchResult> GetSmartMatchInfos(FileOrganizationResultQuery query)
        {
            return _repo.GetSmartMatch(query);
        }

        /// <inheritdoc/>
        public QueryResult<SmartMatchResult> GetSmartMatchInfos()
        {
            return _repo.GetSmartMatch(new FileOrganizationResultQuery());
        }

        /// <inheritdoc/>
        public void DeleteSmartMatchEntry(string id, string matchString)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentNullException(nameof(id));
            }

            if (string.IsNullOrEmpty(matchString))
            {
                throw new ArgumentNullException(nameof(matchString));
            }

            _repo.DeleteSmartMatch(id, matchString);
        }

        /// <inheritdoc/>
        public bool AddToInProgressList(FileOrganizationResult result, bool fullClientRefresh)
        {
            if (string.IsNullOrWhiteSpace(result.Id))
            {
                result.Id = result.OriginalPath.GetMD5().ToString("N", CultureInfo.InvariantCulture);
            }

            if (!_inProgressItemIds.TryAdd(result.Id, false))
            {
                return false;
            }

            result.IsInProgress = true;
            return true;
        }

        /// <inheritdoc/>
        public bool RemoveFromInprogressList(FileOrganizationResult result)
        {
            bool itemValue;
            var retval = _inProgressItemIds.TryRemove(result.Id, out itemValue);

            result.IsInProgress = false;
            return retval;
        }
    }
}
