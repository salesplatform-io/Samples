﻿using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using App.Metrics;
using App.Metrics.Core;
using App.Metrics.Extensions.Reporting.Console;
using App.Metrics.Extensions.Reporting.InfluxDB;
using App.Metrics.Extensions.Reporting.InfluxDB.Client;
using App.Metrics.Extensions.Reporting.TextFile;
using App.Metrics.Filtering;
using App.Metrics.Health;
using App.Metrics.Reporting.Abstractions;
using App.Metrics.Reporting.Interfaces;
using App.Metrics.Scheduling;
using HealthCheck.Samples;
using Metrics.Samples;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.PlatformAbstractions;
using Serilog;

namespace App.Sample
{
    public class Host
    {
        public static void Main()
        {
            var cpuUsage = new CpuUsage();
            cpuUsage.Start();

            IServiceCollection serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            ConfigureMetrics(serviceCollection);

            var process = Process.GetCurrentProcess();

            var provider = serviceCollection.BuildServiceProvider();

            var application = new Application(provider);
            var scheduler = new DefaultTaskScheduler();

            var simpleMetrics = new SampleMetrics(application.Metrics);
            var setCounterSample = new SetCounterSample(application.Metrics);
            var setMeterSample = new SetMeterSample(application.Metrics);
            var userValueHistogramSample = new UserValueHistogramSample(application.Metrics);
            var userValueTimerSample = new UserValueTimerSample(application.Metrics);

            var cancellationTokenSource = new CancellationTokenSource();

            //cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(10));

            var task = scheduler.Interval(
                TimeSpan.FromMilliseconds(300), TaskCreationOptions.LongRunning,() =>
                {
                    using (application.Metrics.Measure.Apdex.Track(AppMetricsRegistry.ApdexScores.AppApdex))
                    {
                        setCounterSample.RunSomeRequests();
                        setMeterSample.RunSomeRequests();
                        userValueHistogramSample.RunSomeRequests();
                        userValueTimerSample.RunSomeRequests();
                        simpleMetrics.RunSomeRequests();
                    }

                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.Gauges.Errors, () => 1);
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.Gauges.PercentGauge, () => 1);
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.Gauges.ApmGauge, () => 1);
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.Gauges.ParenthesisGauge, () => 1);
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.Gauges.GaugeWithNoValue, () => double.NaN);

                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.ProcessMetrics.CpuUsageTotal, () =>
                    {
                        cpuUsage.CallCpu();
                        return cpuUsage.CpuUsageTotal;
                    });
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.ProcessMetrics.ProcessPagedMemorySizeGauge, () => process.PagedMemorySize64);
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.ProcessMetrics.ProcessPeekPagedMemorySizeGauge, () => process.PeakPagedMemorySize64);
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.ProcessMetrics.ProcessPeekVirtualMemorySizeGauge,
                        () => process.PeakVirtualMemorySize64);
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.ProcessMetrics.ProcessPeekWorkingSetSizeGauge, () => process.WorkingSet64);
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.ProcessMetrics.ProcessPrivateMemorySizeGauge, () => process.PrivateMemorySize64);
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.ProcessMetrics.ProcessVirtualMemorySizeGauge, () => process.VirtualMemorySize64);
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.ProcessMetrics.SystemNonPagedMemoryGauge, () => process.NonpagedSystemMemorySize64);
                    application.Metrics.Measure.Gauge.SetValue(AppMetricsRegistry.ProcessMetrics.SystemPagedMemorySizeGauge, () => process.PagedSystemMemorySize64);
                }, cancellationTokenSource.Token);

            application.Reporter.RunReports(application.Metrics, cancellationTokenSource.Token);

            Console.WriteLine("Report Cancelled...");

            Console.ReadKey();
        }

        private static void ConfigureMetrics(IServiceCollection services)
        {
            services
                .AddMetrics(options =>
                {
                    options.ReportingEnabled = true;
                    options.GlobalTags.Add("env", "stage");
                })
                .AddHealthChecks(factory =>
                {
                    factory.RegisterProcessPrivateMemorySizeHealthCheck("Private Memory Size", 200);
                    factory.RegisterProcessVirtualMemorySizeHealthCheck("Virtual Memory Size", 200);
                    factory.RegisterProcessPhysicalMemoryHealthCheck("Working Set", 200);

                    factory.Register("DatabaseConnected", () => Task.FromResult("Database Connection OK"));
                    factory.Register("DiskSpace", () =>
                    {
                        var freeDiskSpace = GetFreeDiskSpace();

                        return Task.FromResult(freeDiskSpace <= 512
                            ? HealthCheckResult.Unhealthy("Not enough disk space: {0}", freeDiskSpace)
                            : HealthCheckResult.Unhealthy("Disk space ok: {0}", freeDiskSpace));
                    });
                })
                .AddReporting(factory =>
                {
                    factory.AddConsole(new ConsoleReporterSettings
                    {
                        ReportInterval = TimeSpan.FromSeconds(5),
                    });

                    factory.AddTextFile(new TextFileReporterSettings
                    {
                        ReportInterval = TimeSpan.FromSeconds(30),
                        FileName = @"C:\metrics\sample.txt"
                    });

                    var influxFilter = new DefaultMetricsFilter()
                        .WhereMetricTaggedWithKeyValue(new TagKeyValueFilter { { "reporter", "influxdb" } })
                        .WithHealthChecks(true)
                        .WithEnvironmentInfo(true);

                    factory.AddInfluxDb(new InfluxDBReporterSettings
                    {
                        HttpPolicy = new HttpPolicy
                        {
                            FailuresBeforeBackoff = 3,
                            BackoffPeriod = TimeSpan.FromSeconds(30),
                            Timeout = TimeSpan.FromSeconds(3)
                        },
                        InfluxDbSettings = new InfluxDBSettings("appmetrics", new Uri("http://127.0.0.1:8086")),
                        ReportInterval = TimeSpan.FromSeconds(5)
                    }, influxFilter);
                });
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            var env = PlatformServices.Default.Application;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.RollingFile(Path.Combine($@"C:\logs\{env.ApplicationName}", "log-{Date}.txt"))
                .CreateLogger();

            var loggerFactory = new LoggerFactory();
            loggerFactory.AddConsole((l, s) => s == LogLevel.Trace);
            loggerFactory.AddSerilog(Log.Logger);

            services.AddSingleton<ILoggerFactory, LoggerFactory>();
            services.AddLogging();
            services.AddTransient<IDatabase, Database>();
        }

        private static int GetFreeDiskSpace()
        {
            return 1024;
        }
    }

    public class Application
    {
        public Application(IServiceProvider provider)
        {
            Metrics = provider.GetRequiredService<IMetrics>();

            var reporterFactory = provider.GetRequiredService<IReportFactory>();
            Reporter = reporterFactory.CreateReporter();

            Token = new CancellationToken();
        }

        public IMetrics Metrics { get; set; }

        public IReporter Reporter { get; set; }

        public CancellationToken Token { get; set; }
    }
}