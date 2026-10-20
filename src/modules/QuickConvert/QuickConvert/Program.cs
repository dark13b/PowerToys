// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;

using ManagedCommon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace QuickConvert;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Logger.InitializeLogger("\\QuickConvert\\Logs");
        WinRT.ComWrappersSupport.InitializeComWrappers();

        using var mutex = new Mutex(true, @"Local\PowerToys_QuickConvert_InstanceMutex", out bool createdNew);
        if (!createdNew)
        {
            Logger.LogWarning("Another running QuickConvert instance was detected. Exiting QuickConvert");
            return;
        }

        if (args.Length > 0 && int.TryParse(args[0], out int runnerPid))
        {
            RunnerHelper.WaitForPowerToysRunner(runnerPid, () => Environment.Exit(0));
        }

        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
    }
}
