using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Quartz;
using Quartz.Impl;
using Shoko.Server.Scheduling.Jobs;

namespace Shoko.Server.Scheduling;

public class QueueHandler
{
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly QueueStateEventHandler _queueStateEventHandler;
    private readonly JobFactory _jobFactory;
    private readonly ThreadPooledJobStore _jobStore;
    private readonly Dictionary<string, QueueItem> _executingJobs = new();

    public QueueHandler(ISchedulerFactory schedulerFactory, QueueStateEventHandler queueStateEventHandler, JobFactory jobFactory, ThreadPooledJobStore jobStore)
    {
        _schedulerFactory = schedulerFactory;
        _queueStateEventHandler = queueStateEventHandler;
        _jobFactory = jobFactory;
        _jobStore = jobStore;
        _queueStateEventHandler.ExecutingJobsChanged += ExecutingJobsStateEventHandlerOnExecutingJobsChanged;
    }

    ~QueueHandler()
    {
        _queueStateEventHandler.ExecutingJobsChanged -= ExecutingJobsStateEventHandlerOnExecutingJobsChanged;
    }

    private void ExecutingJobsStateEventHandlerOnExecutingJobsChanged(object sender, QueueChangedEventArgs e)
    {
        lock (_executingJobs)
        {
            foreach (var item in e.AddedItems)
            {
                _executingJobs[item.Key] = item;
            }

            foreach (var item in e.RemovedItems)
            {
                if (!_executingJobs.ContainsKey(item.Key)) continue;
                _executingJobs.Remove(item.Key);
            }
        }

        WaitingCount = e.WaitingJobsCount;
        BlockedCount = e.BlockedJobsCount;
    }

    public async Task Pause()
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        if (scheduler.IsShutdown || !scheduler.IsStarted || scheduler.InStandbyMode) return;
        await scheduler.Standby();
    }

    public async Task Resume()
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        if (scheduler.IsShutdown || !scheduler.IsStarted || !scheduler.InStandbyMode) return;
        await scheduler.Start();
    }

    public async Task Clear()
    {
        var scheduler = await _schedulerFactory.GetScheduler();
        if (scheduler.IsShutdown || !scheduler.IsStarted) return;
        await scheduler.Clear();
        await QuartzStartup.ScheduleRecurringJobs(false);
    }

    public bool Paused
    {
        get
        {
            var scheduler = _schedulerFactory.GetScheduler().Result;
            return scheduler.IsStarted && scheduler.InStandbyMode;
        }
    }

    public int Count => WaitingCount + BlockedCount;

    public int WaitingCount { get; private set; }

    public int BlockedCount { get; private set; }

    private int _threadCount = -1;

    public int ThreadCount
    {
        get
        {
            if (_threadCount > -1) return _threadCount;
            var scheduler = _schedulerFactory.GetScheduler().Result;
            var metaData = scheduler.GetMetaData().Result;
            _threadCount = metaData.ThreadPoolSize;
            return _threadCount;
        }
    }

    public QueueItem[] GetExecutingJobs()
    {
        lock (_executingJobs)
        {
            return _executingJobs.Values.ToArray();
        }
    }

    public Task<int> GetTotalWaitingJobCount()
    {
        return _jobStore.GetTotalWaitingTriggersCount();
    }

    public async Task<Dictionary<string, int>> GetJobCounts()
    {
        var jobs = await _jobStore.GetJobCounts();
        return jobs.Where(a => typeof(BaseJob).IsAssignableFrom(a.Key))
            .ToDictionary(a => _jobFactory.CreateJob(new JobDetailImpl(Guid.NewGuid().ToString(), a.Key))?.Name, a => a.Value);
    }

    public Task<List<QueueItem>> GetJobs(int maxCount, int offset)
    {
        return _jobStore.GetJobs(maxCount, offset);
    }
}