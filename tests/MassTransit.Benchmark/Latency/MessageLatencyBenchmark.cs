﻿namespace MassTransitBenchmark.Latency
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Threading.Tasks;
    using MassTransit;


    /// <summary>
    /// Benchmark that determines the latency of messages between the time the message is published
    /// to the broker until it is acked by RabbitMQ. And then consumed by the message consumer.
    /// </summary>
    public class MessageLatencyBenchmark
    {
        readonly string _payload;
        readonly IMessageLatencySettings _settings;
        readonly IMessageLatencyTransport _transport;
        MessageMetricCapture _capture;
        TimeSpan _consumeDuration;
        TimeSpan _sendDuration;

        public MessageLatencyBenchmark(IMessageLatencyTransport transport, IMessageLatencySettings settings)
        {
            _transport = transport;
            _settings = settings;

            if (settings.MessageCount / settings.Clients * settings.Clients != settings.MessageCount)
                throw new ArgumentException("The clients must be a factor of message count");

            _payload = _settings.PayloadSize > 0 ? new string('*', _settings.PayloadSize) : null;
        }

        public async Task Run()
        {
            _capture = new MessageMetricCapture(_settings.MessageCount);

            IReportConsumerMetric report = _capture;

            await _transport.Start(ConfigureReceiveEndpoint, report);
            // wait for 3 seconds - add debug output
            Console.WriteLine("Waiting for 5 seconds");
            await Task.Delay(5000);
            try
            {
                Console.WriteLine("Running Message Latency Benchmark");

                await RunBenchmark();

                Console.WriteLine("Message Count: {0}", _settings.MessageCount);
                Console.WriteLine("Clients: {0}", _settings.Clients);
                Console.WriteLine("Durable: {0}", _settings.Durable);
                Console.WriteLine("Payload Length: {0}", _payload?.Length ?? 0);
                Console.WriteLine("Prefetch Count: {0}", _settings.PrefetchCount);
                Console.WriteLine("Concurrency Limit: {0}", _settings.ConcurrencyLimit);

                Console.WriteLine("Total send duration: {0:g}", _sendDuration);
                Console.WriteLine("Send message rate: {0:F2} (msg/s)",
                    _settings.MessageCount * 1000 / _sendDuration.TotalMilliseconds);
                Console.WriteLine("Total consume duration: {0:g}", _consumeDuration);
                Console.WriteLine("Consume message rate: {0:F2} (msg/s)",
                    _settings.MessageCount * 1000 / _consumeDuration.TotalMilliseconds);
                Console.WriteLine("Concurrent Consumer Count: {0}", MessageLatencyConsumer.MaxConsumerCount);

                MessageMetric[] messageMetrics = _capture.GetMessageMetrics();

                Console.WriteLine("Avg Ack Time: {0:F0}ms",
                    messageMetrics.Average(x => x.AckLatency) * 1000 / Stopwatch.Frequency);
                Console.WriteLine("Min Ack Time: {0:F0}ms",
                    messageMetrics.Min(x => x.AckLatency) * 1000 / Stopwatch.Frequency);
                Console.WriteLine("Max Ack Time: {0:F0}ms",
                    messageMetrics.Max(x => x.AckLatency) * 1000 / Stopwatch.Frequency);
                Console.WriteLine("Med Ack Time: {0:F0}ms",
                    messageMetrics.Median(x => x.AckLatency) * 1000 / Stopwatch.Frequency);
                Console.WriteLine("95t Ack Time: {0:F0}ms",
                    messageMetrics.Percentile(x => x.AckLatency) * 1000 / Stopwatch.Frequency);

                Console.WriteLine("Avg Consume Time: {0:F0}ms",
                    messageMetrics.Average(x => x.ConsumeLatency) * 1000 / Stopwatch.Frequency);
                Console.WriteLine("Min Consume Time: {0:F0}ms",
                    messageMetrics.Min(x => x.ConsumeLatency) * 1000 / Stopwatch.Frequency);
                Console.WriteLine("Max Consume Time: {0:F0}ms",
                    messageMetrics.Max(x => x.ConsumeLatency) * 1000 / Stopwatch.Frequency);
                Console.WriteLine("Med Consume Time: {0:F0}ms",
                    messageMetrics.Median(x => x.ConsumeLatency) * 1000 / Stopwatch.Frequency);
                Console.WriteLine("95t Consume Time: {0:F0}ms",
                    messageMetrics.Percentile(x => x.ConsumeLatency) * 1000 / Stopwatch.Frequency);

                Console.WriteLine();
                DrawResponseTimeGraph(messageMetrics, x => x.ConsumeLatency);
            }
            finally
            {
                await _transport.DisposeAsync();
            }
        }

        void DrawResponseTimeGraph(MessageMetric[] metrics, Func<MessageMetric, long> selector)
        {
            var maxTime = metrics.Max(selector);
            var minTime = metrics.Min(selector);

            const int segments = 10;

            var span = maxTime - minTime;
            var increment = span / segments;

            var histogram = (from x in metrics.Select(selector)
                let key = (x - minTime) * segments / span
                where key >= 0 && key < segments
                let groupKey = key
                group x by groupKey
                into segment
                orderby segment.Key
                select new
                {
                    Value = segment.Key,
                    Count = segment.Count()
                }).ToList();

            var maxCount = histogram.Max(x => x.Count);

            foreach (var item in histogram)
            {
                var barLength = item.Count * 60 / maxCount;
                Console.WriteLine("{0,5}ms {2,-60} ({1,7})", (minTime + increment * item.Value) * 1000 / Stopwatch.Frequency, item.Count,
                    new string('*', barLength));
            }
        }

        async Task RunBenchmark()
        {
            await Task.Yield();

            var stripes = new Task[_settings.Clients];

            var messageCount = _settings.MessageCount / _settings.Clients;
            if (messageCount > int.MaxValue)
                throw new IndexOutOfRangeException("Too many messages");

            for (var i = 0; i < _settings.Clients; i++)
            {
                stripes[i] = Task.Run(() => RunStripe((int)messageCount));
            }

            await Task.WhenAll(stripes).ConfigureAwait(false);

            _sendDuration = await _capture.SendCompleted.ConfigureAwait(false);
            _consumeDuration = await _capture.ConsumeCompleted.ConfigureAwait(false);
        }

        async Task RunStripe(int messageCount)
        {
            await Task.Yield();

            NewId[] ids = NewId.Next(messageCount);

            for (long i = 0; i < messageCount; i++)
            {
                var messageId = ids[i].ToGuid();
                var task = _transport.Send(new LatencyTestMessage(messageId, _payload));

                await _capture.Sent(messageId, task).ConfigureAwait(false);
            }
        }

        void ConfigureReceiveEndpoint(IReceiveEndpointConfigurator configurator)
        {
            if (_settings.ConcurrencyLimit > 0)
                configurator.UseConcurrencyLimit(_settings.ConcurrencyLimit);

            configurator.Consumer(() => new MessageLatencyConsumer(_capture));
        }
    }
}
