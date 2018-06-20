﻿using IntelliTrader.Core;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace IntelliTrader.Backtesting
{
    internal class BacktestingService : ConfigrableServiceBase<BacktestingConfig>, IBacktestingService
    {
        public const string SNAPSHOT_FILE_EXTENSION = "bin";

        public override string ServiceName => Constants.ServiceNames.BacktestingService;

        IBacktestingConfig IBacktestingService.Config => Config;

        public object SyncRoot { get; private set; } = new object();

        private readonly ILoggingService loggingService;
        private readonly IHealthCheckService healthCheckService;
        private ISignalsService signalsService;
        private ITradingService tradingService;
        private BacktestingLoadSnapshotsTimedTask backtestingLoadSnapshotsTimedTask;
        private BacktestingSaveSnapshotsTimedTask backtestingSaveSnapshotsTimedTask;

        public BacktestingService(ILoggingService loggingService, IHealthCheckService healthCheckService)
        {
            this.loggingService = loggingService;
            this.healthCheckService = healthCheckService;
        }

        public void Start()
        {
            loggingService.Info($"Start Backtesting service... (Replay: {Config.Replay})");

            signalsService = Application.Resolve<ISignalsService>();
            tradingService = Application.Resolve<ITradingService>();

            if (Config.Replay)
            {
                backtestingLoadSnapshotsTimedTask = Application.Resolve<ITasksService>().AddTask(
                    name: nameof(BacktestingLoadSnapshotsTimedTask),
                    task: new BacktestingLoadSnapshotsTimedTask(loggingService, healthCheckService, tradingService, this),
                    interval: Config.SnapshotsInterval / Config.ReplaySpeed * 1000,
                    startDelay: Constants.TaskDelays.HighDelay,
                    startTask: false,
                    runNow: false);
            }

            backtestingSaveSnapshotsTimedTask = Application.Resolve<ITasksService>().AddTask(
                name: nameof(BacktestingSaveSnapshotsTimedTask),
                task: new BacktestingSaveSnapshotsTimedTask(loggingService, healthCheckService, tradingService, signalsService, this),
                interval: Config.SnapshotsInterval * 1000,
                startDelay: Constants.TaskDelays.HighDelay,
                startTask: false,
                runNow: false);

            if (Config.DeleteLogs)
            {
                loggingService.DeleteAllLogs();
            }

            string virtualAccountPath = Path.Combine(Directory.GetCurrentDirectory(), tradingService.Config.VirtualAccountFilePath);
            if (File.Exists(virtualAccountPath) && (Config.DeleteAccountData || !String.IsNullOrWhiteSpace(Config.CopyAccountDataPath)))
            {
                File.Delete(virtualAccountPath);
            }

            if (!String.IsNullOrWhiteSpace(Config.CopyAccountDataPath))
            {
                File.Copy(Path.Combine(Directory.GetCurrentDirectory(), Config.CopyAccountDataPath), virtualAccountPath, true);
            }

            if (Config.Replay)
            {
                Application.Speed = Config.ReplaySpeed;
            }

            loggingService.Info("Backtesting service started");
        }

        public void Stop()
        {
            loggingService.Info("Stop Backtesting service...");

            if (Config.Replay)
            {
                Application.Resolve<ITasksService>().RemoveTask(nameof(BacktestingLoadSnapshotsTimedTask), stopTask: true);
            }
            Application.Resolve<ITasksService>().RemoveTask(nameof(BacktestingSaveSnapshotsTimedTask), stopTask: true);

            healthCheckService.RemoveHealthCheck(Constants.HealthChecks.BacktestingSignalsSnapshotTaken);
            healthCheckService.RemoveHealthCheck(Constants.HealthChecks.BacktestingTickersSnapshotTaken);
            healthCheckService.RemoveHealthCheck(Constants.HealthChecks.BacktestingSignalsSnapshotLoaded);
            healthCheckService.RemoveHealthCheck(Constants.HealthChecks.BacktestingTickersSnapshotLoaded);

            loggingService.Info("Backtesting service stopped");
        }

        public void Complete(int skippedSignalSnapshots, int skippedTickerSnapshots)
        {
            loggingService.Info("Backtesting results:");

            double lagAmount = 0;
            foreach (var kvp in Application.Resolve<ITasksService>().GetAllTasks().OrderBy(t => t.Key))
            {
                string taskName = kvp.Key;
                ITimedTask task = kvp.Value;

                double averageWaitTime = Math.Round(task.TotalLagTime / task.RunCount, 3);
                if (averageWaitTime > 0) lagAmount += averageWaitTime;
                loggingService.Info($" [+] {taskName} Run times: {task.RunCount}, average wait time: " + averageWaitTime);
            }

            loggingService.Info($"Lag value: {lagAmount}. Lower the ReplaySpeed if lag value is positive.");
            loggingService.Info($"Skipped signal snapshots: {skippedSignalSnapshots}");
            loggingService.Info($"Skipped ticker snapshots: {skippedTickerSnapshots}");

            tradingService.SuspendTrading(forced: true);
            signalsService.ClearTrailing();
            signalsService.Stop();
        }

        public string GetSnapshotFilePath(string snapshotEntity)
        {
            var date = DateTimeOffset.UtcNow;
            return Path.Combine(
                Directory.GetCurrentDirectory(),
                Config.SnapshotsPath,
                snapshotEntity,
                date.ToString("yyyy-MM-dd"),
                date.ToString("HH"),
                date.ToString("mm-ss-fff")
            ) + "." + SNAPSHOT_FILE_EXTENSION;
        }

        public Dictionary<string, IEnumerable<ISignal>> GetCurrentSignals()
        {
            return backtestingLoadSnapshotsTimedTask.GetCurrentSignals() ?? new Dictionary<string, IEnumerable<ISignal>>();
        }

        public Dictionary<string, ITicker> GetCurrentTickers()
        {
            return backtestingLoadSnapshotsTimedTask.GetCurrentTickers() ?? new Dictionary<string, ITicker>();
        }

        public int GetTotalSnapshots()
        {
            return backtestingLoadSnapshotsTimedTask.GetTotalSnapshots();
        }
    }
}
