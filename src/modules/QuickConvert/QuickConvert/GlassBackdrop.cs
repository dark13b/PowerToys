// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace QuickConvert;

/// <summary>
/// A nearly clear, always-active acrylic: the blur stays but the tint is removed, so the
/// "Liquid Glass" wheel shows the colors behind it instead of a gray plate.
/// </summary>
public sealed partial class GlassBackdrop : SystemBackdrop
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, DesktopAcrylicController> _controllers = [];

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        bool dark = xamlRoot.Content is FrameworkElement { ActualTheme: ElementTheme.Dark };
        var controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
            TintColor = dark ? Colors.Black : Colors.White,
            TintOpacity = 0f,
            LuminosityOpacity = dark ? 0.12f : 0.2f,
        };

        // The wheel is never activated, so keep the material in its active look.
        controller.SetSystemBackdropConfiguration(new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
        });
        controller.AddSystemBackdropTarget(connectedTarget);
        _controllers[connectedTarget] = controller;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_controllers.Remove(disconnectedTarget, out var controller))
        {
            controller.RemoveSystemBackdropTarget(disconnectedTarget);
            controller.Dispose();
        }
    }
}
