using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(Worker))]
    public interface IWorker : IAgentService
    {
        Task<int> RunAsync(string pipeIn, string pipeOut, CancellationTokenSource hostTokenSource);
    }

    public sealed class Worker : AgentService, IWorker
    {
        private readonly TimeSpan _workerStartTimeout = TimeSpan.FromSeconds(30);

        public async Task<int> RunAsync(string pipeIn, string pipeOut, CancellationTokenSource hostTokenSource)
        {
            // Validate args.
            ArgUtil.NotNullOrEmpty(pipeIn, nameof(pipeIn));
            ArgUtil.NotNullOrEmpty(pipeOut, nameof(pipeOut));
            ArgUtil.NotNull(hostTokenSource, nameof(hostTokenSource));
            var jobRunner = HostContext.GetService<IJobRunner>();

            using (var channel = HostContext.CreateService<IProcessChannel>())
            {
                // Start the channel.
                channel.StartClient(pipeIn, pipeOut);

                // Wait for up to 30 seconds for a message from the channel.
                Trace.Info("Waiting to receive the job message from the channel.");
                WorkerMessage channelMessage = await channel.ReceiveAsync(new CancellationTokenSource(_workerStartTimeout).Token);

                // Deserialize the job message.
                Trace.Info("Message received.");
                ArgUtil.Equal(MessageType.NewJobRequest, channelMessage.MessageType, nameof(channelMessage.MessageType));
                ArgUtil.NotNullOrEmpty(channelMessage.Body, nameof(channelMessage.Body));
                var jobMessage = JsonUtility.FromString<JobRequestMessage>(channelMessage.Body);
                ArgUtil.NotNull(jobMessage, nameof(jobMessage));

                // Initialize the secret masker and set the thread culture.
                InitializeSecretMasker(jobMessage);
                SetCulture(jobMessage);

                // Start the job.
                Trace.Verbose($"JobMessage: {channelMessage.Body}");
                Task<TaskResult> jobRunnerTask = jobRunner.RunAsync(jobMessage);

                // Start listening for a cancel message from the channel.
                Trace.Info("Listening for cancel message from the channel.");
                CancellationTokenSource channelTokenSource = new CancellationTokenSource();
                Task<WorkerMessage> channelTask = channel.ReceiveAsync(channelTokenSource.Token);

                // Wait for one of the tasks to complete.
                Trace.Info("Waiting for the job to complete or for a cancel message from the channel.");
                Task.WaitAny(jobRunnerTask, channelTask);

                // Handle if the job completed.
                if (jobRunnerTask.IsCompleted)
                {
                    Trace.Info("Job completed.");
                    channelTokenSource.Cancel(); // Cancel waiting for a message from the channel.
                    return TaskResultUtil.TranslateToReturnCode(await jobRunnerTask);
                }

                // Otherwise a cancel message was received from the channel.
                Trace.Info("Cancellation message received.");
                channelMessage = await channelTask;
                ArgUtil.Equal(MessageType.CancelRequest, channelMessage.MessageType, nameof(channelMessage.MessageType));
                hostTokenSource.Cancel();   // Expire the host cancellation token.
                // Await the job.
                return TaskResultUtil.TranslateToReturnCode(await jobRunnerTask);
            }
        }

        private void InitializeSecretMasker(JobRequestMessage message)
        {
            Trace.Entering();
            var secretMasker = HostContext.GetService<ISecretMasker>();

            // Add mask hints
            var variables = message?.Environment?.Variables ?? new Dictionary<string, string>();
            foreach (MaskHint maskHint in (message.Environment.MaskHints ?? new List<MaskHint>()))
            {
                if (maskHint.Type == MaskType.Regex)
                {
                    secretMasker.AddRegex(maskHint.Value);
                }
                else if (maskHint.Type == MaskType.Variable)
                {
                    string value;
                    if (variables.TryGetValue(maskHint.Value, out value) &&
                        !string.IsNullOrEmpty(value))
                    {
                        secretMasker.AddVariable(maskHint.Value, value);
                    }
                } // TODO: Else? Fail? Warn?
            }

            // Add masks for service endpoints
            foreach (ServiceEndpoint endpoint in message.Environment.Endpoints ?? new List<ServiceEndpoint>())
            {
                foreach (string value in endpoint.Authorization.Parameters?.Values ?? new string[0])
                {
                    secretMasker.AddValue(value);
                    // TODO: Add a comment here explaining this.
                    if (!Uri.EscapeDataString(value).Equals(value, StringComparison.OrdinalIgnoreCase))
                    {
                        secretMasker.AddValue(Uri.EscapeDataString(value));
                    }
                }
            }
        }

        private void SetCulture(JobRequestMessage message)
        {
            // Extract the culture name from the job's variable dictionary.
            string culture;
            ArgUtil.NotNull(message.Environment, nameof(message.Environment));
            ArgUtil.NotNull(message.Environment.Variables, nameof(message.Environment.Variables));
            if (!message.Environment.Variables.TryGetValue(Constants.Variables.System.Culture, out culture))
            {
                culture = null;
            }

            // Set the default thread culture.
            ArgUtil.NotNullOrEmpty(culture, nameof(culture));
            HostContext.SetDefaultCulture(culture);
        }
    }
}