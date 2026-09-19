// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace SigilBuild.Installer.Host.Views;

/// <summary>
/// Puts the resolved brand logo in a window's title bar and taskbar button.
/// </summary>
/// <remarks>
/// <c>Window.Icon</c> is a <see cref="WindowIcon"/>, not an <c>IImage</c>, so it
/// cannot be bound in XAML the way the rail's <c>Image.Source</c> is. Three windows
/// need the same two lines, and none of them set an icon at all before this —
/// the title bar showed the toolkit's default while the rail showed the product's
/// mark, which is what a user notices first and trusts least.
/// </remarks>
internal static class BrandWindowIcon
{
    public static void Apply(Window window, Bitmap? logo)
    {
        if (logo is null)
        {
            // No brand logo and no default asset. Leaving the toolkit default is
            // the right answer — an installer that refuses to open because of a
            // title-bar icon would be a far worse trade.
            return;
        }

        window.Icon = new WindowIcon(logo);
    }
}
