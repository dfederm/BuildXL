﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.Monitor.App;
using BuildXL.Cache.Monitor.App.Rules;
using BuildXL.Cache.Monitor.App.Scheduling;
using Kusto.Data.Common;
using static BuildXL.Cache.Monitor.App.Analysis.Utilities;

namespace BuildXL.Cache.Monitor.Library.Rules.Kusto
{
    internal class LongCopyRule : KustoRuleBase
    {
        public class Configuration : KustoRuleConfiguration
        {
            public Configuration(KustoRuleConfiguration other) : base(other)
            {
            }

            public TimeSpan LookbackPeriod { get; set; } = TimeSpan.FromHours(1);

            public TimeSpan LongCopyDurationThreshold { get; set; } = TimeSpan.FromHours(1);

            public Thresholds<long> LongCopiesAmountThresholds = new Thresholds<long>()
            {
                Info = 1,
            };

            public Thresholds<double> LongCopiesPercentThresholds = new Thresholds<double>()
            {
                Warning =   0.00001,
                Error =     0.0001,
                Fatal =     0.001,
            };
        }

        public LongCopyRule(Configuration configuration) : base(configuration) => _configuration = configuration;

        public override string Identifier => $"{nameof(LongCopyRule)}:{_configuration.StampId}";

        private readonly Configuration _configuration;

#pragma warning disable CS0649
        private class Result
        {
            public long TotalCopies;
            public long LongCopies;
        }
#pragma warning restore CS0649

        public override async Task Run(RuleContext context)
        {
            var now = _configuration.Clock.UtcNow;
            var query =
                $@"
                let end = now();
                let start = end - {CslTimeSpanLiteral.AsCslString(_configuration.LookbackPeriod)};
                table(""{_configuration.CacheTableName}"")
                | where PreciseTimeStamp between (start .. end)
                | where Stamp == ""{_configuration.Stamp}""
                | where Service == ""{Constants.ServiceName}""
                | where Operation == ""RemoteCopyFile""
                | summarize TotalCopies = count(), LongCopies = countif(Duration > {CslTimeSpanLiteral.AsCslString(_configuration.LongCopyDurationThreshold)})
                | where isnotempty(TotalCopies)";

            var result = (await QueryKustoAsync<Result>(context, query)).FirstOrDefault();

            if (result == null)
            {
                return;
            }

            var percentage = result.TotalCopies > 0 ? 100.0 * result.LongCopies / result.TotalCopies : 0;

            _configuration.LongCopiesPercentThresholds.Check(percentage, (severity, pct) =>
            {
                Emit(context, "LongCopyPercent", severity,
                    $"{percentage}% of copies ({result.LongCopies}/{result.TotalCopies}) had a duration longer than {_configuration.LongCopyDurationThreshold}.",
                    eventTimeUtc: now);
            });

            _configuration.LongCopiesAmountThresholds.Check(result.LongCopies, (severity, amount) =>
            {
                Emit(
                    context, "LongCopyDetected", severity,
                    $"Detected {result.LongCopies} copies that had a duration longer than {_configuration.LongCopyDurationThreshold}.",
                    eventTimeUtc: now);
            });
        }
    }
}