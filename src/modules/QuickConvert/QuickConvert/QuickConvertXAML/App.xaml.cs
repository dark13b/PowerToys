// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using ManagedCommon;
using Microsoft.UI.Xaml;

namespace QuickConvert;

public partial class App : Application, IDisposable
{
    private WheelWindow? _wheel;

    public App()
    {
        InitializeComponent();
        UnhandledException += (s, e) => Logger.LogError("Unhandled exception", e.Exception);
    }

    // No main window: the wheel appears only when Shift is pressed during a file drag.
    protected override void OnLaunched(LaunchActivatedEventArgs args) => _wheel = new WheelWindow();

    public void Dispose()
    {
        _wheel?.Dispose();
        GC.SuppressFinalize(this);
    }
}
