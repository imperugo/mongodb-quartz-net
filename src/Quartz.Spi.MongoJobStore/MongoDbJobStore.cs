using System.Diagnostics;
using System.Globalization;

using JetBrains.Annotations;

using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using Quartz.Impl.AdoJobStore;
using Quartz.Impl.Matchers;
using Quartz.Simpl;
using Quartz.Spi.MongoJobStore.Cluster;
using Quartz.Spi.MongoJobStore.Database;
using Quartz.Spi.MongoJobStore.Models;
using Quartz.Spi.MongoJobStore.Models.Id;
using Quartz.Spi.MongoJobStore.Repositories;
using Quartz.Spi.MongoJobStore.Util;

using Calendar = Quartz.Spi.MongoJobStore.Models.Calendar;

namespace Quartz.Spi.MongoJobStore;

[PublicAPI]
public class MongoDbJobStore : IJobStore
{
    internal static readonly JsonObjectSerializer ObjectSerializer = new();

    private const string KeySignalChangeForTxCompletion = "sigChangeForTxCompletion";
    private const string AllGroupsPaused = "_$_ALL_GROUPS_PAUSED_$_";

    internal static readonly DateTimeOffset SchedulingSignalDateTime =
        new DateTimeOffset(1982, 6, 28, 0, 0, 0, TimeSpan.FromSeconds(0));

    private static long _fireTriggerRecordCounter = DateTime.UtcNow.Ticks;

    private readonly ILogger _logger = LogProvider.CreateLogger<MongoDbJobStore>();

    private readonly IMongoDbJobStoreConnectionFactory _connectionFactory;
    private readonly IMongoDatabase _database;

    private ISchedulerSignaler _schedulerSignaler = null!;

    private LockRepository _lockRepository = null!;
    private CalendarRepository _calendarRepository = null!;
    private FiredTriggerRepository _firedTriggerRepository = null!;
    private JobDetailRepository _jobDetailRepository = null!;
    private PausedTriggerGroupRepository _pausedTriggerGroupRepository = null!;
    private SchedulerRepository _schedulerRepository = null!;
    private TriggerRepository _triggerRepository = null!;

    private LockManager _lockManager = null!;

    private MisfireHandler? _misfireHandler;
    private TimeSpan _misfireThreshold = TimeSpan.FromMinutes(1);
    private ClusterManager? clusterManager;


    private SchedulerId _schedulerId;
    private bool _schedulerRunning;


    public string ConnectionString { get; set; }
    public string CollectionPrefix { get; set; }

    /// <summary>
    ///     Get or set the maximum number of misfired triggers that the misfire handling
    ///     thread will try to recover at one time (within one transaction).  The
    ///     default is 20.
    /// </summary>
    public int MaxMisfiresToHandleAtATime { get; set; } = 20;

    /// <summary>
    ///     Gets or sets the database retry interval.
    /// </summary>
    /// <value>The db retry interval.</value>
    [TimeSpanParseRule(TimeSpanParseRule.Milliseconds)]
    public TimeSpan DbRetryInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     The time span by which a trigger must have missed its
    ///     next-fire-time, in order for it to be considered "misfired" and thus
    ///     have its misfire instruction applied.
    /// </summary>
    [TimeSpanParseRule(TimeSpanParseRule.Milliseconds)]
    public TimeSpan MisfireThreshold
    {
        get => _misfireThreshold;
        set
        {
            if (value.TotalMilliseconds < 1)
            {
                throw new ArgumentException("MisfireThreshold must be larger than 0");
            }

            _misfireThreshold = value;
        }
    }

    /// <summary>
    ///     Gets or sets the number of retries before an error is logged for recovery operations.
    /// </summary>
    public int RetryableActionErrorLogThreshold { get; set; } = 4;

    /// <summary>
    /// Get whether the threads spawned by this JobStore should be
    /// marked as daemon.  Possible threads include the <see cref="MisfireHandler" />
    /// and the <see cref="Impl.AdoJobStore.ClusterManager"/>.
    /// </summary>
    /// <returns></returns>
    public bool MakeThreadsDaemons { get; set; }

    /// <summary>
    /// Get or set the frequency at which this instance "checks-in"
    /// with the other instances of the cluster. -- Affects the rate of
    /// detecting failed instances.
    /// </summary>
    [TimeSpanParseRule(TimeSpanParseRule.Milliseconds)]
    public TimeSpan ClusterCheckinInterval { get; set; }

    /// <summary>
    /// The time span by which a check-in must have missed its
    /// next-fire-time, in order for it to be considered "misfired" and thus
    /// other scheduler instances in a cluster can consider a "misfired" scheduler
    /// instance as failed or dead.
    /// </summary>
    [TimeSpanParseRule(TimeSpanParseRule.Milliseconds)]
    public TimeSpan ClusterCheckinMisfireThreshold { get; set; }


    protected internal DateTimeOffset LastCheckin { get; set; } = SystemTime.UtcNow();

    protected bool firstCheckIn = true;


    protected DateTimeOffset MisfireTime
    {
        get
        {
            var misfireTime = SystemTime.UtcNow();
            if (MisfireThreshold > TimeSpan.Zero)
            {
                misfireTime = misfireTime.AddMilliseconds(-1 * MisfireThreshold.TotalMilliseconds);
            }

            return misfireTime;
        }
    }

    public bool SupportsPersistence => true;
    public long EstimatedTimeToReleaseAndAcquireTrigger => 200;
    public bool Clustered { get; set; }
    public string InstanceId { get; set; } = null!;
    public string InstanceName { get; set; } = null!;
    public int ThreadPoolSize { get; set; }


    public MongoDbJobStore(ILoggerFactory loggerFactory, IMongoDbJobStoreConnectionFactory connectionFactory)
    {
        JobStoreClassMap.RegisterClassMaps();

        ObjectSerializer.Initialize();

        _connectionFactory = connectionFactory;
        _database = _connectionFactory.GetDatabase();

        LogProvider.SetLogProvider(loggerFactory);
    }


    public async Task Initialize(
        ITypeLoadHelper loadHelper,
        ISchedulerSignaler signaler,
        CancellationToken token = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(InstanceName);

        _schedulerSignaler = signaler;
        _schedulerId = new SchedulerId
        {
            InstanceName = InstanceName,
            Id = InstanceId,
        };
        _logger.LogTrace("Scheduler {SchedulerId} initialize", _schedulerId);

        _calendarRepository = new CalendarRepository(_database, InstanceName, CollectionPrefix);
        _firedTriggerRepository = new FiredTriggerRepository(_database, InstanceName, CollectionPrefix);
        _jobDetailRepository = new JobDetailRepository(_database, InstanceName, CollectionPrefix);
        _lockRepository = new LockRepository(_database, InstanceName, CollectionPrefix);
        _pausedTriggerGroupRepository = new PausedTriggerGroupRepository(_database, InstanceName, CollectionPrefix);
        _schedulerRepository = new SchedulerRepository(_database, InstanceName, CollectionPrefix);
        _triggerRepository = new TriggerRepository(_database, InstanceName, CollectionPrefix);

        _lockManager = new LockManager(_lockRepository);


        _logger.LogTrace("Validating indices...");
        var repositories = new List<IRepository>
        {
            _schedulerRepository,
            _jobDetailRepository,
            _triggerRepository,
            _pausedTriggerGroupRepository,
            _firedTriggerRepository,
            _calendarRepository,
            _lockRepository,
        };

        foreach (var repository in repositories)
        {
            await repository.EnsureIndex();
        }
    }

    public async Task Shutdown(CancellationToken token = default)
    {
        _logger.LogTrace("Scheduler {SchedulerId} shutdown", _schedulerId);

        if (_misfireHandler != null)
        {
            await _misfireHandler.Shutdown().ConfigureAwait(false);
        }

        if (clusterManager != null)
        {
            await clusterManager.Shutdown().ConfigureAwait(false);
        }
    }


    #region Scheduler

    public async Task SchedulerStarted(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace("Scheduler {SchedulerId} started", _schedulerId);

        if (Clustered)
        {
            clusterManager = new ClusterManager(this);
            await clusterManager.Initialize();
        }
        else
        {
            try
            {
                await RecoverJobs().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failure occurred during job recovery: {Message}", ex.Message);

                throw new SchedulerConfigException("Failure occurred during job recovery", ex);
            }
        }

        _misfireHandler = new MisfireHandler(this);
        await _misfireHandler.Initialize(cancellationToken);
        _schedulerRunning = true;
    }

    public Task SchedulerPaused(CancellationToken token = default)
    {
        _logger.LogTrace("Scheduler {SchedulerId} paused", _schedulerId);
        _schedulerRunning = false;

        return Task.CompletedTask;
    }

    public Task SchedulerResumed(CancellationToken token = default)
    {
        _logger.LogTrace("Scheduler {SchedulerId} resumed", _schedulerId);
        _schedulerRunning = true;

        return Task.CompletedTask;
    }

    #endregion


    public async Task StoreJobAndTrigger(
        IJobDetail newJob,
        IOperableTrigger newTrigger,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                await StoreJobInternal(newJob, false).ConfigureAwait(false);

                await StoreTriggerInternal(
                        newTrigger,
                        newJob,
                        false,
                        Models.TriggerState.Waiting,
                        false,
                        false,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
        }
        catch (AggregateException ex)
        {
            throw new JobPersistenceException(ex.InnerExceptions[0].Message, ex.InnerExceptions[0]);
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    // Checked
    public Task<bool> IsJobGroupPaused(string groupName, CancellationToken token = default)
    {
        // This is not implemented in the core ADO stuff, so we won't implement it here either
        throw new NotImplementedException();
    }

    // Checked
    public Task<bool> IsTriggerGroupPaused(string groupName, CancellationToken token = default)
    {
        // This is not implemented in the core ADO stuff, so we won't implement it here either
        throw new NotImplementedException();
    }

    // Checked
    public async Task StoreJob(IJobDetail newJob, bool replaceExisting, CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                await StoreJobInternal(newJob, replaceExisting).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    // Checked
    public async Task StoreJobsAndTriggers(
        IReadOnlyDictionary<IJobDetail, IReadOnlyCollection<ITrigger>> triggersAndJobs,
        bool replace,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                foreach (var (job, triggers) in triggersAndJobs)
                {
                    await StoreJobInternal(job, replace).ConfigureAwait(false);

                    foreach (var trigger in triggers)
                    {
                        await StoreTriggerInternal(
                                (IOperableTrigger)trigger,
                                job,
                                replace,
                                Models.TriggerState.Waiting,
                                false,
                                false,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        catch (AggregateException ex)
        {
            throw new JobPersistenceException(ex.InnerExceptions[0].Message, ex.InnerExceptions[0]);
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task<bool> RemoveJob(JobKey jobKey, CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                return await RemoveJobInternal(jobKey).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    // Checked
    public async Task<bool> RemoveJobs(IReadOnlyCollection<JobKey> jobKeys, CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                var result = true;

                foreach (var jobKey in jobKeys)
                {
                    result = result && await RemoveJobInternal(jobKey).ConfigureAwait(false);
                }

                return result;
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    // Checked
    public async Task<IJobDetail?> RetrieveJob(JobKey jobKey, CancellationToken token = default)
    {
        var result = await _jobDetailRepository.GetJob(jobKey).ConfigureAwait(false);

        return result?.GetJobDetail();
    }

    // Checked
    public async Task StoreTrigger(
        IOperableTrigger newTrigger,
        bool replaceExisting,
        CancellationToken cancellationToken = default
    )
    {
        await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
        {
            await StoreTriggerInternal(
                newTrigger,
                null,
                replaceExisting,
                Models.TriggerState.Waiting,
                false,
                false,
                cancellationToken
            );
        }
    }

    public async Task<bool> RemoveTrigger(TriggerKey triggerKey, CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                return await RemoveTriggerInternal(triggerKey).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task<bool> RemoveTriggers(
        IReadOnlyCollection<TriggerKey> triggerKeys,
        CancellationToken token = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                return triggerKeys.Aggregate(
                    true,
                    (current, triggerKey) => current && RemoveTriggerInternal(triggerKey).Result
                );
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    // Checked
    public async Task<bool> ReplaceTrigger(
        TriggerKey triggerKey,
        IOperableTrigger newTrigger,
        CancellationToken token = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                return await ReplaceTriggerInternal(triggerKey, newTrigger).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task<IOperableTrigger?> RetrieveTrigger(TriggerKey triggerKey, CancellationToken token = default)
    {
        var result = await _triggerRepository.GetTrigger(triggerKey).ConfigureAwait(false);
        return result?.GetTrigger() as IOperableTrigger;
    }

    public async Task<bool> CalendarExists(string calName, CancellationToken token = default)
    {
        return await _calendarRepository.CalendarExists(calName).ConfigureAwait(false);
    }

    public async Task<bool> CheckExists(JobKey jobKey, CancellationToken token = default)
    {
        return await _jobDetailRepository.JobExists(jobKey).ConfigureAwait(false);
    }

    public async Task<bool> CheckExists(TriggerKey triggerKey, CancellationToken token = default)
    {
        return await _triggerRepository.TriggerExists(triggerKey).ConfigureAwait(false);
    }

    public async Task ClearAllSchedulingData(CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                await _calendarRepository.DeleteAll().ConfigureAwait(false);
                await _firedTriggerRepository.DeleteAll().ConfigureAwait(false);
                await _jobDetailRepository.DeleteAll().ConfigureAwait(false);
                await _pausedTriggerGroupRepository.DeleteAll().ConfigureAwait(false);
                await _schedulerRepository.DeleteAll().ConfigureAwait(false);
                await _triggerRepository.DeleteAll().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task StoreCalendar(
        string name,
        ICalendar calendar,
        bool replaceExisting,
        bool updateTriggers,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                await StoreCalendarInternal(name, calendar, replaceExisting, updateTriggers, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task<bool> RemoveCalendar(string calName, CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                return await RemoveCalendarInternal(calName).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task<ICalendar?> RetrieveCalendar(string calName, CancellationToken token = default)
    {
        return await _calendarRepository.GetCalendar(calName).ConfigureAwait(false);
    }

    public async Task<int> GetNumberOfJobs(CancellationToken token = default)
    {
        return (int)await _jobDetailRepository.GetCount().ConfigureAwait(false);
    }

    public async Task<int> GetNumberOfTriggers(CancellationToken token = default)
    {
        return (int)await _triggerRepository.GetCount().ConfigureAwait(false);
    }

    public async Task<int> GetNumberOfCalendars(CancellationToken token = default)
    {
        return (int)await _calendarRepository.GetCount().ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<JobKey>> GetJobKeys(
        GroupMatcher<JobKey> matcher,
        CancellationToken token = default
    )
    {
        return new HashSet<JobKey>(await _jobDetailRepository.GetJobsKeys(matcher).ConfigureAwait(false));
    }

    public async Task<IReadOnlyCollection<TriggerKey>> GetTriggerKeys(
        GroupMatcher<TriggerKey> matcher,
        CancellationToken token = default
    )
    {
        return new HashSet<TriggerKey>(await _triggerRepository.GetTriggerKeys(matcher).ConfigureAwait(false));
    }

    public async Task<IReadOnlyCollection<string>> GetJobGroupNames(CancellationToken token = default)
    {
        return await _jobDetailRepository.GetJobGroupNames().ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<string>> GetTriggerGroupNames(CancellationToken token = default)
    {
        return await _triggerRepository.GetTriggerGroupNames().ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<string>> GetCalendarNames(CancellationToken token = default)
    {
        return await _calendarRepository.GetCalendarNames().ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<IOperableTrigger>> GetTriggersForJob(
        JobKey jobKey,
        CancellationToken token = default
    )
    {
        var result = await _triggerRepository.GetTriggers(jobKey).ConfigureAwait(false);
        return result.Select(trigger => trigger.GetTrigger()).Cast<IOperableTrigger>().ToList();
    }

    public async Task<TriggerState> GetTriggerState(TriggerKey triggerKey, CancellationToken token = default)
    {
        var trigger = await _triggerRepository.GetTrigger(triggerKey).ConfigureAwait(false);

        if (trigger == null)
        {
            return TriggerState.None;
        }

        return trigger.State switch
        {
            Models.TriggerState.Deleted => TriggerState.None,
            Models.TriggerState.Complete => TriggerState.Complete,
            Models.TriggerState.Paused => TriggerState.Paused,
            Models.TriggerState.PausedBlocked => TriggerState.Paused,
            Models.TriggerState.Error => TriggerState.Error,
            Models.TriggerState.Blocked => TriggerState.Blocked,
            _ => TriggerState.Normal,
        };
    }

    public Task ResetTriggerFromErrorState(TriggerKey triggerKey, CancellationToken cancellationToken = new())
    {
        throw new NotImplementedException();
    }

    public async Task PauseTrigger(TriggerKey triggerKey, CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                await PauseTriggerInternal(triggerKey).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyCollection<string>> PauseTriggers(
        GroupMatcher<TriggerKey> matcher,
        CancellationToken token = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                return await PauseTriggerGroupInternal(matcher, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task PauseJob(JobKey jobKey, CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                var triggers = await GetTriggersForJob(jobKey, token).ConfigureAwait(false);
                foreach (var operableTrigger in triggers)
                {
                    await PauseTriggerInternal(operableTrigger.Key).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyCollection<string>> PauseJobs(
        GroupMatcher<JobKey> matcher,
        CancellationToken token = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                var jobKeys = await _jobDetailRepository.GetJobsKeys(matcher).ConfigureAwait(false);
                foreach (var jobKey in jobKeys)
                {
                    var triggers = await _triggerRepository.GetTriggers(jobKey).ConfigureAwait(false);
                    foreach (var trigger in triggers)
                    {
                        await PauseTriggerInternal(trigger.GetTrigger().Key).ConfigureAwait(false);
                    }
                }

                return jobKeys.Select(key => key.Group).Distinct().ToList();
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task ResumeTrigger(TriggerKey triggerKey, CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                await ResumeTriggerInternal(triggerKey, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyCollection<string>> ResumeTriggers(
        GroupMatcher<TriggerKey> matcher,
        CancellationToken token = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                return await ResumeTriggersInternal(matcher, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyCollection<string>> GetPausedTriggerGroups(CancellationToken cancellationToken = default)
    {
        var groups = await _pausedTriggerGroupRepository.GetPausedTriggerGroups().ConfigureAwait(false);

        return new HashSet<string>(groups);
    }

    public async Task ResumeJob(JobKey jobKey, CancellationToken cancellationToken = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                var triggers = await _triggerRepository.GetTriggers(jobKey).ConfigureAwait(false);
                await Task.WhenAll(
                        triggers.Select(trigger => ResumeTriggerInternal(trigger.GetTrigger().Key, cancellationToken))
                    )
                    .ConfigureAwait(false);
            }
        }
        catch (AggregateException ex)
        {
            throw new JobPersistenceException(ex.InnerExceptions[0].Message, ex.InnerExceptions[0]);
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyCollection<string>> ResumeJobs(
        GroupMatcher<JobKey> matcher,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                var jobKeys = await _jobDetailRepository.GetJobsKeys(matcher).ConfigureAwait(false);
                foreach (var jobKey in jobKeys)
                {
                    var triggers = await _triggerRepository.GetTriggers(jobKey).ConfigureAwait(false);
                    await Task.WhenAll(
                            triggers.Select(
                                trigger => ResumeTriggerInternal(trigger.GetTrigger().Key, cancellationToken)
                            )
                        )
                        .ConfigureAwait(false);
                }

                return new HashSet<string>(jobKeys.Select(key => key.Group));
            }
        }
        catch (AggregateException ex)
        {
            throw new JobPersistenceException(ex.InnerExceptions[0].Message, ex.InnerExceptions[0]);
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task PauseAll(CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                await PauseAllInternal().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task ResumeAll(CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                await ResumeAllInternal().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyCollection<IOperableTrigger>> AcquireNextTriggers(
        DateTimeOffset noLaterThan,
        int maxCount,
        TimeSpan timeWindow,
        CancellationToken token = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                return await AcquireNextTriggersInternal(noLaterThan, maxCount, timeWindow).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task ReleaseAcquiredTrigger(IOperableTrigger trigger, CancellationToken token = default)
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                await _triggerRepository.UpdateTriggerState(
                        trigger.Key,
                        Models.TriggerState.Waiting,
                        Models.TriggerState.Acquired
                    )
                    .ConfigureAwait(false);
                await _triggerRepository.UpdateTriggerState(
                        trigger.Key,
                        Models.TriggerState.Waiting,
                        Models.TriggerState.Blocked
                    )
                    .ConfigureAwait(false);

                await _firedTriggerRepository.DeleteFiredTrigger(trigger.FireInstanceId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException($"Couldn't release acquired trigger: {ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyCollection<TriggerFiredResult>> TriggersFired(
        IReadOnlyCollection<IOperableTrigger> triggers,
        CancellationToken token = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                var results = new List<TriggerFiredResult>();

                foreach (var operableTrigger in triggers)
                {
                    TriggerFiredResult result;
                    try
                    {
                        var bundle = await TriggerFiredInternal(operableTrigger).ConfigureAwait(false);
                        result = new TriggerFiredResult(bundle);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Caught exception: {Message}", ex.Message);
                        result = new TriggerFiredResult(ex);
                    }

                    results.Add(result);
                }

                return results;
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    public async Task TriggeredJobComplete(
        IOperableTrigger trigger,
        IJobDetail jobDetail,
        SchedulerInstruction triggerInstCode,
        CancellationToken token = default
    )
    {
        try
        {
            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                await TriggeredJobCompleteInternal(trigger, jobDetail, triggerInstCode, token).ConfigureAwait(false);
            }

            var sigTime = ClearAndGetSignalSchedulingChangeOnTxCompletion();
            if (sigTime != null)
            {
                SignalSchedulingChangeImmediately(sigTime);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    internal async Task<RecoverMisfiredJobsResult> DoRecoverMisfires()
    {
        try
        {
            var misfireCount = await _triggerRepository.GetMisfireCount(MisfireTime.UtcDateTime).ConfigureAwait(false);
            if (misfireCount == 0)
            {
                _logger.LogDebug("Found 0 triggers that missed their scheduled fire-time.");
                return RecoverMisfiredJobsResult.NoOp;
            }

            await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
            {
                return await RecoverMisfiredJobsInternal(false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    private async Task RecoverJobs()
    {
        await using (await _lockManager.AcquireLock(LockType.TriggerAccess, InstanceId).ConfigureAwait(false))
        {
            await RecoverJobsInternal().ConfigureAwait(false);
        }
    }

    private async Task PauseTriggerInternal(TriggerKey triggerKey)
    {
        var trigger = await _triggerRepository.GetTrigger(triggerKey).ConfigureAwait(false);
        switch (trigger.State)
        {
            case Models.TriggerState.Waiting:
            case Models.TriggerState.Acquired:
            {
                await _triggerRepository.UpdateTriggerState(triggerKey, Models.TriggerState.Paused)
                    .ConfigureAwait(false);
                break;
            }
            case Models.TriggerState.Blocked:
            {
                await _triggerRepository.UpdateTriggerState(triggerKey, Models.TriggerState.PausedBlocked)
                    .ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task<IReadOnlyCollection<string>> PauseTriggerGroupInternal(
        GroupMatcher<TriggerKey> matcher,
        CancellationToken token = default
    )
    {
        await _triggerRepository.UpdateTriggersStates(
                matcher,
                Models.TriggerState.Paused,
                Models.TriggerState.Acquired,
                Models.TriggerState.Waiting
            )
            .ConfigureAwait(false);
        await _triggerRepository.UpdateTriggersStates(
                matcher,
                Models.TriggerState.PausedBlocked,
                Models.TriggerState.Blocked
            )
            .ConfigureAwait(false);

        var triggerGroups = await _triggerRepository.GetTriggerGroupNames(matcher).ConfigureAwait(false);

        // make sure to account for an exact group match for a group that doesn't yet exist
        var op = matcher.CompareWithOperator;
        if (op.Equals(StringOperator.Equality) && !triggerGroups.Contains(matcher.CompareToValue))
        {
            triggerGroups.Add(matcher.CompareToValue);
        }

        foreach (var triggerGroup in triggerGroups)
        {
            if (!await _pausedTriggerGroupRepository.IsTriggerGroupPaused(triggerGroup).ConfigureAwait(false))
            {
                await _pausedTriggerGroupRepository.AddPausedTriggerGroup(triggerGroup).ConfigureAwait(false);
            }
        }

        return new HashSet<string>(triggerGroups);
    }

    private async Task PauseAllInternal()
    {
        var groupNames = await _triggerRepository.GetTriggerGroupNames().ConfigureAwait(false);

        await Task.WhenAll(
                groupNames.Select(
                    groupName => PauseTriggerGroupInternal(GroupMatcher<TriggerKey>.GroupEquals(groupName))
                )
            )
            .ConfigureAwait(false);

        if (!await _pausedTriggerGroupRepository.IsTriggerGroupPaused(AllGroupsPaused).ConfigureAwait(false))
        {
            await _pausedTriggerGroupRepository.AddPausedTriggerGroup(AllGroupsPaused).ConfigureAwait(false);
        }
    }

    private async Task<bool> ReplaceTriggerInternal(TriggerKey triggerKey, IOperableTrigger newTrigger)
    {
        // SELECT
        //  J.JOB_NAME,
        //  J.JOB_GROUP,
        //  J.IS_DURABLE,
        //  J.JOB_CLASS_NAME,
        //  J.REQUESTS_RECOVERY
        // FROM
        //  TRIGGERS T,
        //  JOB_DETAILS J
        // WHERE
        //  T.SCHED_NAME = @schedulerName AND
        //  T.SCHED_NAME = J.SCHED_NAME AND
        //  T.TRIGGER_NAME = @triggerName AND
        //  T.TRIGGER_GROUP = @triggerGroup AND
        //  T.JOB_NAME = J.JOB_NAME AND
        //  T.JOB_GROUP = J.JOB_GROUP";


        var trigger = await _triggerRepository.GetTrigger(triggerKey).ConfigureAwait(false);


        var result = await _jobDetailRepository.GetJob(trigger.JobKey).ConfigureAwait(false);
        var job = result?.GetJobDetail();

        if (job == null)
        {
            return false;
        }

        if (!newTrigger.JobKey.Equals(job.Key))
        {
            throw new JobPersistenceException("New trigger is not related to the same job as the old trigger.");
        }

        var removedTrigger = await _triggerRepository.DeleteTrigger(triggerKey).ConfigureAwait(false);
        await StoreTriggerInternal(newTrigger, job, false, Models.TriggerState.Waiting, false, false)
            .ConfigureAwait(false);
        return removedTrigger > 0;
    }


    private async Task<bool> RemoveJobInternal(JobKey jobKey)
    {
        await _triggerRepository.DeleteTriggers(jobKey).ConfigureAwait(false);

        var result = await _jobDetailRepository.DeleteJob(jobKey).ConfigureAwait(false);
        return result > 0;
    }

    private async Task<bool> RemoveTriggerInternal(TriggerKey key, IJobDetail? job = null)
    {
        var trigger = await _triggerRepository.GetTrigger(key);
        if (trigger == null)
        {
            return false;
        }

        if (job == null)
        {
            var result = await _jobDetailRepository.GetJob(trigger.JobKey).ConfigureAwait(false);
            job = result?.GetJobDetail();
        }

        var removedTrigger = await _triggerRepository.DeleteTrigger(key).ConfigureAwait(false) > 0;

        if (job != null && !job.Durable)
        {
            if (await _triggerRepository.GetCount(job.Key).ConfigureAwait(false) == 0)
            {
                if (await RemoveJobInternal(job.Key).ConfigureAwait(false))
                {
                    await _schedulerSignaler.NotifySchedulerListenersJobDeleted(job.Key).ConfigureAwait(false);
                }
            }
        }

        return removedTrigger;
    }

    private async Task<bool> RemoveCalendarInternal(string calendarName)
    {
        if (await _triggerRepository.TriggersExists(calendarName).ConfigureAwait(false))
        {
            throw new JobPersistenceException("Calender cannot be removed if it referenced by a trigger!");
        }

        return await _calendarRepository.DeleteCalendar(calendarName).ConfigureAwait(false) > 0;
    }

    private async Task ResumeTriggerInternal(TriggerKey triggerKey, CancellationToken cancellationToken = default)
    {
        var trigger = await _triggerRepository.GetTrigger(triggerKey).ConfigureAwait(false);
        if (trigger?.NextFireTime == null || trigger.NextFireTime == DateTime.MinValue)
        {
            return;
        }

        var blocked = trigger.State == Models.TriggerState.PausedBlocked;
        var newState = await CheckBlockedState(trigger.JobKey, Models.TriggerState.Waiting).ConfigureAwait(false);
        var misfired = false;

        if (_schedulerRunning && trigger.NextFireTime < DateTime.UtcNow)
        {
            misfired = await UpdateMisfiredTrigger(triggerKey, newState, true).ConfigureAwait(false);
        }

        if (!misfired)
        {
            await _triggerRepository.UpdateTriggerState(
                    triggerKey,
                    newState,
                    blocked ? Models.TriggerState.PausedBlocked : Models.TriggerState.Paused
                )
                .ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyCollection<string>> ResumeTriggersInternal(
        GroupMatcher<TriggerKey> matcher,
        CancellationToken token = default
    )
    {
        await _pausedTriggerGroupRepository.DeletePausedTriggerGroup(matcher).ConfigureAwait(false);
        var groups = new HashSet<string>();

        var keys = await _triggerRepository.GetTriggerKeys(matcher).ConfigureAwait(false);
        foreach (var triggerKey in keys)
        {
            await ResumeTriggerInternal(triggerKey, token).ConfigureAwait(false);
            groups.Add(triggerKey.Group);
        }

        return groups.ToList();
    }

    private async Task ResumeAllInternal()
    {
        var groupNames = await _triggerRepository.GetTriggerGroupNames().ConfigureAwait(false);
        await Task.WhenAll(
                groupNames.Select(groupName => ResumeTriggersInternal(GroupMatcher<TriggerKey>.GroupEquals(groupName)))
            )
            .ConfigureAwait(false);
        await _pausedTriggerGroupRepository.DeletePausedTriggerGroup(AllGroupsPaused).ConfigureAwait(false);
    }

    private async Task StoreCalendarInternal(
        string calName,
        ICalendar calendar,
        bool replaceExisting,
        bool updateTriggers,
        CancellationToken token = default
    )
    {
        var existingCal = await CalendarExists(calName, token).ConfigureAwait(false);
        if (existingCal && !replaceExisting)
        {
            throw new ObjectAlreadyExistsException("Calendar with name '" + calName + "' already exists.");
        }

        if (existingCal)
        {
            if (await _calendarRepository.UpdateCalendar(new Calendar(calName, calendar, InstanceName))
                    .ConfigureAwait(false) ==
                0)
            {
                throw new JobPersistenceException("Couldn't store calendar.  Update failed.");
            }

            if (updateTriggers)
            {
                var triggers = await _triggerRepository.GetTriggers(calName).ConfigureAwait(false);
                foreach (var trigger in triggers)
                {
                    var quartzTrigger = (IOperableTrigger)trigger.GetTrigger();
                    quartzTrigger.UpdateWithNewCalendar(calendar, MisfireThreshold);
                    await StoreTriggerInternal(
                            quartzTrigger,
                            null,
                            true,
                            Models.TriggerState.Waiting,
                            false,
                            false,
                            token
                        )
                        .ConfigureAwait(false);
                }
            }
        }
        else
        {
            await _calendarRepository.AddCalendar(new Calendar(calName, calendar, InstanceName)).ConfigureAwait(false);
        }
    }

    // Checked
    private async Task StoreJobInternal(IJobDetail newJob, bool replaceExisting)
    {
        var existingJob = await _jobDetailRepository.JobExists(newJob.Key).ConfigureAwait(false);

        var jobDetail = new JobDetail(newJob, InstanceName);

        if (existingJob)
        {
            if (!replaceExisting)
            {
                throw new ObjectAlreadyExistsException(newJob);
            }

            await _jobDetailRepository.UpdateJob(jobDetail).ConfigureAwait(false);
        }
        else
        {
            await _jobDetailRepository.AddJob(jobDetail).ConfigureAwait(false);
        }
    }

    // Checked
    private async Task StoreTriggerInternal(
        IOperableTrigger newTrigger,
        IJobDetail? job,
        bool replaceExisting,
        Models.TriggerState state,
        bool forceState,
        bool recovering,
        CancellationToken token = default
    )
    {
        var existingTrigger = await _triggerRepository.TriggerExists(newTrigger.Key).ConfigureAwait(false);

        if (existingTrigger && !replaceExisting)
        {
            throw new ObjectAlreadyExistsException(newTrigger);
        }

        if (!forceState)
        {
            var shouldBePaused = await _pausedTriggerGroupRepository.IsTriggerGroupPaused(newTrigger.Key.Group)
                .ConfigureAwait(false);

            if (!shouldBePaused)
            {
                shouldBePaused = await _pausedTriggerGroupRepository.IsTriggerGroupPaused(AllGroupsPaused)
                    .ConfigureAwait(false);
                if (shouldBePaused)
                {
                    await _pausedTriggerGroupRepository.AddPausedTriggerGroup(newTrigger.Key.Group)
                        .ConfigureAwait(false);
                }
            }

            if (shouldBePaused && (state == Models.TriggerState.Waiting || state == Models.TriggerState.Acquired))
            {
                state = Models.TriggerState.Paused;
            }
        }

        if (job == null)
        {
            var jobDetail = await _jobDetailRepository.GetJob(newTrigger.JobKey).ConfigureAwait(false);
            job = jobDetail?.GetJobDetail();
        }

        if (job == null)
        {
            throw new JobPersistenceException(
                $"The job ({newTrigger.JobKey}) referenced by the trigger does not exist."
            );
        }

        if (job.ConcurrentExecutionDisallowed && !recovering)
        {
            state = await CheckBlockedState(job.Key, state).ConfigureAwait(false);
        }


        var trigger = TriggerFactory.CreateTrigger(newTrigger, state, InstanceName);

        if (existingTrigger)
        {
            await _triggerRepository.UpdateTrigger(trigger).ConfigureAwait(false);
        }
        else
        {
            await _triggerRepository.AddTrigger(trigger).ConfigureAwait(false);
        }
    }

    // Checked
    private async Task<Models.TriggerState> CheckBlockedState(JobKey jobKey, Models.TriggerState currentState)
    {
        // State can only transition to BLOCKED from PAUSED or WAITING.
        if (currentState != Models.TriggerState.Waiting && currentState != Models.TriggerState.Paused)
        {
            return currentState;
        }

        var firedTriggers = await _firedTriggerRepository.GetFiredTriggers(jobKey).ConfigureAwait(false);
        
        var firedTrigger = firedTriggers.FirstOrDefault();
        if (firedTrigger != null)
        {
            if (firedTrigger.ConcurrentExecutionDisallowed) // TODO: worry about failed/recovering/volatile job  states?
            {
                return currentState == Models.TriggerState.Paused ? Models.TriggerState.PausedBlocked
                    : Models.TriggerState.Blocked;
            }
        }

        return currentState;
    }

    private async Task<TriggerFiredBundle?> TriggerFiredInternal(IOperableTrigger trigger)
    {
        // Make sure trigger wasn't deleted, paused, or completed...
        var state = await _triggerRepository.GetTriggerState(trigger.Key).ConfigureAwait(false);
        if (state != Models.TriggerState.Acquired)
        {
            return null;
        }

        JobDetail? job;
        try
        {
            job = await _jobDetailRepository.GetJob(trigger.JobKey).ConfigureAwait(false);
            if (job == null)
            {
                return null;
            }
        }
        catch (JobPersistenceException ex)
        {
            _logger.LogError(ex, "Error retrieving job, setting trigger state to ERROR.");

            await _triggerRepository.UpdateTriggerState(trigger.Key, Models.TriggerState.Error);
            throw;
        }

        ICalendar? calendar = null;
        if (trigger.CalendarName != null)
        {
            calendar = await _calendarRepository.GetCalendar(trigger.CalendarName).ConfigureAwait(false);
            if (calendar == null)
            {
                return null;
            }
        }

        await _firedTriggerRepository.UpdateFiredTrigger(
                new FiredTrigger(
                    trigger.FireInstanceId,
                    TriggerFactory.CreateTrigger(trigger, Models.TriggerState.Executing, InstanceName),
                    job
                )
                {
                    InstanceId = InstanceId,
                    State = Models.TriggerState.Executing,
                }
            )
            .ConfigureAwait(false);

        var prevFireTime = trigger.GetPreviousFireTimeUtc();
        trigger.Triggered(calendar);

        state = Models.TriggerState.Waiting;
        var force = true;

        if (job.ConcurrentExecutionDisallowed)
        {
            state = Models.TriggerState.Blocked;
            force = false;
            await _triggerRepository.UpdateTriggersStates(
                    trigger.JobKey,
                    Models.TriggerState.Blocked,
                    Models.TriggerState.Waiting
                )
                .ConfigureAwait(false);
            await _triggerRepository.UpdateTriggersStates(
                    trigger.JobKey,
                    Models.TriggerState.Blocked,
                    Models.TriggerState.Acquired
                )
                .ConfigureAwait(false);
            await _triggerRepository.UpdateTriggersStates(
                    trigger.JobKey,
                    Models.TriggerState.PausedBlocked,
                    Models.TriggerState.Paused
                )
                .ConfigureAwait(false);
        }

        if (!trigger.GetNextFireTimeUtc().HasValue)
        {
            state = Models.TriggerState.Complete;
            force = true;
        }

        var jobDetail = job.GetJobDetail();
        await StoreTriggerInternal(trigger, jobDetail, true, state, force, force).ConfigureAwait(false);

        jobDetail.JobDataMap.ClearDirtyFlag();

        return new TriggerFiredBundle(
            jobDetail,
            trigger,
            calendar,
            trigger.Key.Group.Equals(SchedulerConstants.DefaultRecoveryGroup),
            DateTimeOffset.UtcNow,
            trigger.GetPreviousFireTimeUtc(),
            prevFireTime,
            trigger.GetNextFireTimeUtc()
        );
    }

    private async Task<bool> UpdateMisfiredTrigger(
        TriggerKey triggerKey,
        Models.TriggerState newStateIfNotComplete,
        bool forceState
    )
    {
        var trigger = await _triggerRepository.GetTrigger(triggerKey).ConfigureAwait(false);
        var misfireTime = DateTime.Now;
        if (MisfireThreshold > TimeSpan.Zero)
        {
            misfireTime = misfireTime.AddMilliseconds(-1 * MisfireThreshold.TotalMilliseconds);
        }

        if (trigger.NextFireTime > misfireTime)
        {
            return false;
        }

        await DoUpdateOfMisfiredTrigger(trigger, forceState, newStateIfNotComplete, false).ConfigureAwait(false);

        return true;
    }

    private async Task DoUpdateOfMisfiredTrigger(
        Trigger trigger,
        bool forceState,
        Models.TriggerState newStateIfNotComplete,
        bool recovering
    )
    {
        var operableTrigger = (IOperableTrigger)trigger.GetTrigger();

        ICalendar? cal = null;
        if (trigger.CalendarName != null)
        {
            cal = await _calendarRepository.GetCalendar(trigger.CalendarName).ConfigureAwait(false);
        }

        await _schedulerSignaler.NotifyTriggerListenersMisfired(operableTrigger).ConfigureAwait(false);
        operableTrigger.UpdateAfterMisfire(cal);

        if (!operableTrigger.GetNextFireTimeUtc().HasValue)
        {
            await StoreTriggerInternal(
                    operableTrigger,
                    null,
                    true,
                    Models.TriggerState.Complete,
                    forceState,
                    recovering
                )
                .ConfigureAwait(false);
            await _schedulerSignaler.NotifySchedulerListenersFinalized(operableTrigger).ConfigureAwait(false);
        }
        else
        {
            await StoreTriggerInternal(operableTrigger, null, true, newStateIfNotComplete, forceState, false)
                .ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyCollection<IOperableTrigger>> AcquireNextTriggersInternal(
        DateTimeOffset noLaterThan,
        int maxCount,
        TimeSpan timeWindow
    )
    {
        if (timeWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeWindow));
        }

        var acquiredTriggers = new List<IOperableTrigger>();
        var acquiredJobKeysForNoConcurrentExec = new HashSet<JobKey>();

        const int maxDoLoopRetry = 3;
        var currentLoopCount = 0;

        do
        {
            currentLoopCount++;
            var keys = await _triggerRepository.GetTriggersToAcquire(noLaterThan + timeWindow, MisfireTime, maxCount)
                .ConfigureAwait(false);

            if (keys.Count == 0)
            {
                return acquiredTriggers;
            }

            foreach (var triggerKey in keys)
            {
                // If our trigger is no longer available, try a new one.
                var nextTrigger = await _triggerRepository.GetTrigger(triggerKey).ConfigureAwait(false);
                if (nextTrigger == null)
                {
                    continue; // next trigger
                }

                var jobKey = nextTrigger.JobKey;
                JobDetail? jobDetail;
                try
                {
                    jobDetail = await _jobDetailRepository.GetJob(jobKey).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    await _triggerRepository.UpdateTriggerState(triggerKey, Models.TriggerState.Error)
                        .ConfigureAwait(false);
                    continue;
                }

                if (jobDetail.ConcurrentExecutionDisallowed)
                {
                    if (!acquiredJobKeysForNoConcurrentExec.Add(jobKey))
                    {
                        continue;
                    }
                }

                var result = await _triggerRepository.UpdateTriggerState(
                        triggerKey,
                        Models.TriggerState.Acquired,
                        Models.TriggerState.Waiting
                    )
                    .ConfigureAwait(false);
                if (result <= 0)
                {
                    continue;
                }

                var operableTrigger = (IOperableTrigger)nextTrigger.GetTrigger();
                operableTrigger.FireInstanceId = GetFiredTriggerRecordId();

                var firedTrigger = new FiredTrigger(operableTrigger.FireInstanceId, nextTrigger, null)
                {
                    State = Models.TriggerState.Acquired,
                    InstanceId = InstanceId,
                };
                await _firedTriggerRepository.AddFiredTrigger(firedTrigger).ConfigureAwait(false);

                acquiredTriggers.Add(operableTrigger);
            }

            if (acquiredTriggers.Count == 0 && currentLoopCount < maxDoLoopRetry)
            {
                continue;
            }

            break;
        } while (true);

        return acquiredTriggers;
    }

    private string GetFiredTriggerRecordId()
    {
        Interlocked.Increment(ref _fireTriggerRecordCounter);
        return InstanceId + _fireTriggerRecordCounter;
    }

    private async Task TriggeredJobCompleteInternal(
        IOperableTrigger trigger,
        IJobDetail jobDetail,
        SchedulerInstruction triggerInstCode,
        CancellationToken token = default
    )
    {
        try
        {
            switch (triggerInstCode)
            {
                case SchedulerInstruction.DeleteTrigger:
                {
                    if (!trigger.GetNextFireTimeUtc().HasValue)
                    {
                        var trig = await _triggerRepository.GetTrigger(trigger.Key).ConfigureAwait(false);
                        if (trig != null && !trig.NextFireTime.HasValue)
                        {
                            await RemoveTriggerInternal(trigger.Key, jobDetail).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await RemoveTriggerInternal(trigger.Key, jobDetail).ConfigureAwait(false);
                        SignalSchedulingChangeOnTxCompletion(SchedulingSignalDateTime);
                    }

                    break;
                }
                case SchedulerInstruction.SetTriggerComplete:
                {
                    await _triggerRepository.UpdateTriggerState(trigger.Key, Models.TriggerState.Complete)
                        .ConfigureAwait(false);
                    SignalSchedulingChangeOnTxCompletion(SchedulingSignalDateTime);
                    break;
                }
                case SchedulerInstruction.SetTriggerError:
                {
                    _logger.LogInformation("Trigger {Key} set to ERROR state.", trigger.Key);
                    await _triggerRepository.UpdateTriggerState(trigger.Key, Models.TriggerState.Error)
                        .ConfigureAwait(false);
                    SignalSchedulingChangeOnTxCompletion(SchedulingSignalDateTime);
                    break;
                }
                case SchedulerInstruction.SetAllJobTriggersComplete:
                {
                    await _triggerRepository.UpdateTriggersStates(trigger.JobKey, Models.TriggerState.Complete)
                        .ConfigureAwait(false);
                    SignalSchedulingChangeOnTxCompletion(SchedulingSignalDateTime);
                    break;
                }
                case SchedulerInstruction.SetAllJobTriggersError:
                {
                    _logger.LogInformation("All triggers of Job {JobKey} set to ERROR state.", trigger.JobKey);
                    await _triggerRepository.UpdateTriggersStates(trigger.JobKey, Models.TriggerState.Error)
                        .ConfigureAwait(false);
                    SignalSchedulingChangeOnTxCompletion(SchedulingSignalDateTime);
                    break;
                }
            }

            if (jobDetail.ConcurrentExecutionDisallowed)
            {
                await _triggerRepository.UpdateTriggersStates(
                        jobDetail.Key,
                        Models.TriggerState.Waiting,
                        Models.TriggerState.Blocked
                    )
                    .ConfigureAwait(false);

                await _triggerRepository.UpdateTriggersStates(
                        jobDetail.Key,
                        Models.TriggerState.Paused,
                        Models.TriggerState.PausedBlocked
                    )
                    .ConfigureAwait(false);
                SignalSchedulingChangeOnTxCompletion(SchedulingSignalDateTime);
            }

            if (jobDetail.PersistJobDataAfterExecution && jobDetail.JobDataMap.Dirty)
            {
                await _jobDetailRepository.UpdateJobData(jobDetail.Key, jobDetail.JobDataMap).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }

        try
        {
            await _firedTriggerRepository.DeleteFiredTrigger(trigger.FireInstanceId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new JobPersistenceException(ex.Message, ex);
        }
    }

    protected virtual void SignalSchedulingChangeOnTxCompletion(DateTimeOffset candidateNewNextFireTime)
    {
        var sigTime = LogicalThreadContext.GetData<DateTimeOffset?>(KeySignalChangeForTxCompletion);
        if (sigTime == null)
        {
            LogicalThreadContext.SetData(KeySignalChangeForTxCompletion, candidateNewNextFireTime);
        }
        else
        {
            if (candidateNewNextFireTime < sigTime)
            {
                LogicalThreadContext.SetData(KeySignalChangeForTxCompletion, candidateNewNextFireTime);
            }
        }
    }

    protected virtual DateTimeOffset? ClearAndGetSignalSchedulingChangeOnTxCompletion()
    {
        var t = LogicalThreadContext.GetData<DateTimeOffset?>(KeySignalChangeForTxCompletion);
        LogicalThreadContext.FreeNamedDataSlot(KeySignalChangeForTxCompletion);
        return t;
    }

    internal virtual void SignalSchedulingChangeImmediately(DateTimeOffset? candidateNewNextFireTime)
    {
        _schedulerSignaler.SignalSchedulingChange(candidateNewNextFireTime);
    }

    private async Task RecoverJobsInternal()
    {
        var result = await _triggerRepository.UpdateTriggersStates(
                Models.TriggerState.Waiting,
                Models.TriggerState.Acquired,
                Models.TriggerState.Blocked
            )
            .ConfigureAwait(false);

        result += await _triggerRepository
            .UpdateTriggersStates(Models.TriggerState.Paused, Models.TriggerState.PausedBlocked)
            .ConfigureAwait(false);

        _logger.LogInformation("Freed {Count} triggers from 'acquired' / 'blocked' state.", result);

        await RecoverMisfiredJobsInternal(true).ConfigureAwait(false);

        var results =
            (await _firedTriggerRepository.GetRecoverableFiredTriggers(InstanceId).ConfigureAwait(false)).Select(
                async trigger => trigger.GetRecoveryTrigger(
                    await _triggerRepository.GetTriggerJobDataMap(trigger.TriggerKey).ConfigureAwait(false)
                )
            );
        var recoveringJobTriggers = (await Task.WhenAll(results).ConfigureAwait(false)).ToList();

        _logger.LogInformation(
            "Recovering {Count} jobs that were in-progress at the time of the last shut-down.",
            recoveringJobTriggers.Count
        );

        foreach (var recoveringJobTrigger in recoveringJobTriggers)
        {
            if (await _jobDetailRepository.JobExists(recoveringJobTrigger.JobKey).ConfigureAwait(false))
            {
                recoveringJobTrigger.ComputeFirstFireTimeUtc(null);

                await StoreTriggerInternal(recoveringJobTrigger, null, false, Models.TriggerState.Waiting, false, true)
                    .ConfigureAwait(false);
            }
        }

        _logger.LogInformation("Recovery complete");

        var completedTriggers =
            await _triggerRepository.GetTriggerKeys(Models.TriggerState.Complete).ConfigureAwait(false);

        foreach (var completedTrigger in completedTriggers)
        {
            await RemoveTriggerInternal(completedTrigger).ConfigureAwait(false);
        }

        _logger.LogInformation("Removed {Count} 'complete' triggers.", completedTriggers.Count);

        result = await _firedTriggerRepository.DeleteFiredTriggersByInstanceId(InstanceId).ConfigureAwait(false);
        _logger.LogInformation("Removed {Count} stale fired job entries.", result);
    }

    private async Task<RecoverMisfiredJobsResult> RecoverMisfiredJobsInternal(bool recovering)
    {
        var maxMisfiresToHandleAtTime = recovering ? -1 : MaxMisfiresToHandleAtATime;
        var earliestNewTime = DateTime.MaxValue;

        var hasMoreMisfiredTriggers = _triggerRepository.HasMisfiredTriggers(
            MisfireTime.UtcDateTime,
            maxMisfiresToHandleAtTime,
            out var misfiredTriggers
        );

        if (hasMoreMisfiredTriggers)
        {
            _logger.LogInformation(
                "Handling the first {Count} triggers that missed their scheduled fire-time.  " +
                "More misfired triggers remain to be processed.",
                misfiredTriggers.Count
            );
        }
        else if (misfiredTriggers.Count > 0)
        {
            _logger.LogInformation(
                "Handling {Count} trigger(s) that missed their scheduled fire-time.",
                misfiredTriggers.Count
            );
        }
        else
        {
            _logger.LogInformation("Found 0 triggers that missed their scheduled fire-time.");
            return RecoverMisfiredJobsResult.NoOp;
        }

        foreach (var misfiredTrigger in misfiredTriggers)
        {
            var trigger = await _triggerRepository.GetTrigger(misfiredTrigger).ConfigureAwait(false);

            if (trigger == null)
            {
                continue;
            }

            await DoUpdateOfMisfiredTrigger(trigger, false, Models.TriggerState.Waiting, recovering)
                .ConfigureAwait(false);

            var nextTime = trigger.NextFireTime;
            if (nextTime.HasValue && nextTime.Value < earliestNewTime)
            {
                earliestNewTime = nextTime.Value;
            }
        }

        return new RecoverMisfiredJobsResult(hasMoreMisfiredTriggers, misfiredTriggers.Count, earliestNewTime);
    }


    #region Cluster

    protected internal virtual async Task<bool> DoCheckin(
        Guid requestorId,
        CancellationToken cancellationToken = default
    )
    {
        // TODO
        return true;
    }

    /// <summary>
    /// Get a list of all scheduler instances in the cluster that may have failed.
    /// This includes this scheduler if it is checking in for the first time.
    /// </summary>
    protected virtual async Task<IReadOnlyList<SchedulerStateRecord>> FindFailedInstances(
        ConnectionAndTransactionHolder conn,
        CancellationToken cancellationToken = default
    )
    {
    }

    /// <summary>
    /// Create dummy <see cref="SchedulerStateRecord" /> objects for fired triggers
    /// that have no scheduler state record.  Checkin timestamp and interval are
    /// left as zero on these dummy <see cref="SchedulerStateRecord" /> objects.
    /// </summary>
    /// <param name="conn"></param>
    /// <param name="schedulerStateRecords">List of all current <see cref="SchedulerStateRecord" />s</param>
    /// <param name="cancellationToken">The cancellation instruction.</param>
    private async Task<IReadOnlyList<SchedulerStateRecord>> FindOrphanedFailedInstances(
        ConnectionAndTransactionHolder conn,
        IReadOnlyCollection<SchedulerStateRecord> schedulerStateRecords,
        CancellationToken cancellationToken
    )
    {
        var orphanedInstances = new List<Scheduler>();

        var names = _firedTriggerRepository.GetFiredTriggers()
    }

    protected DateTimeOffset CalcFailedIfAfter(SchedulerStateRecord rec)
    {
        TimeSpan passed = SystemTime.UtcNow() - LastCheckin;
        TimeSpan ts = rec.CheckinInterval > passed ? rec.CheckinInterval : passed;
        return rec.CheckinTimestamp.Add(ts).Add(ClusterCheckinMisfireThreshold);
    }

    protected virtual async Task<IReadOnlyList<SchedulerStateRecord>> ClusterCheckIn(
        ConnectionAndTransactionHolder conn,
        CancellationToken cancellationToken = default
    )
    {
    }

    protected virtual async Task ClusterRecover(
        ConnectionAndTransactionHolder conn,
        IReadOnlyList<SchedulerStateRecord> failedInstances,
        CancellationToken cancellationToken = default
    )
    {
    }

    #endregion
}
