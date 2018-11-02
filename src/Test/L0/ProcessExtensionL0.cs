using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.VisualStudio.Services.Agent.Tests
{
    public sealed class ProcessExtensionL0
    {
        [Fact]
        [Trait("Level", "L0")]
        [Trait("Category", "Common")]
        public async Task SuccessReadProcessEnv()
        {
            using (TestHostContext hc = new TestHostContext(this))
            {
                Tracing trace = hc.GetTrace();

                string envName = Guid.NewGuid().ToString();
                string envValue = Guid.NewGuid().ToString();

                CancellationTokenSource tokenSource = new CancellationTokenSource();
                var processInvoker = new ProcessInvokerWrapper();
                processInvoker.Initialize(hc);

                Task<int> sleep = null;
                try
                {
#if OS_WINDOWS
                    string node = Path.Combine(TestUtil.GetSrcPath(), @"..\_layout\externals\node\bin\node");
                    sleep = processInvoker.ExecuteAsync("", node, "-e \"setTimeout(function(){{}}, 30 * 1000);\"", new Dictionary<String, String>() { { envName, envValue } }, requireExitCodeZero: false, outputEncoding: null, killProcessOnCancel: false, cancellationToken: tokenSource.Token);
#else
                    string node = Path.Combine(TestUtil.GetSrcPath(), @"../_layout/externals/node/bin/node");
                    sleep = processInvoker.ExecuteAsync("", node, "-e \"setTimeout(function(){{}}, 30 * 1000);\"", new Dictionary<String, String>() { { envName, envValue } }, requireExitCodeZero: false, outputEncoding: null, killProcessOnCancel: false, cancellationToken: tokenSource.Token);
#endif

                    var timeouts = Process.GetProcessesByName("node");
                    while (timeouts.Length == 0)
                    {
                        await Task.Delay(100);
                        timeouts = Process.GetProcessesByName("node");
                    }

                    foreach (var timeout in timeouts)
                    {
                        try
                        {
                            Console.WriteLine($"Read env from {timeout.Id}");
                            var value = timeout.GetEnvironmentVariable(hc, envName);
                            if (string.Equals(value, envValue, StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"Find the env!");
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            trace.Error(ex);
                        }
                    }

                    Assert.True(false, "Fail to retrive process environment variable.");
                }
                finally
                {
                    tokenSource.Cancel();
                }
            }
        }
    }
}
