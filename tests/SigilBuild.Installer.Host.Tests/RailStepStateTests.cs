// Copyright (c) 2026 Yevhen Khudoliiv. All rights reserved.
// Licensed under the Sigil License 1.0. See LICENSE in the repository root.

using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using SigilBuild.Installer.Host.Branding;
using SigilBuild.Installer.Host.ViewModels;
using SigilBuild.Installer.Host.Views;
using Xunit;

namespace SigilBuild.Installer.Host.Tests;

/// <summary>
/// The wizard rail marks what has been gone through: a check mark on a done step,
/// the bullet on the current and pending ones, every step done on Finish, and a
/// cross on the step the install failed in. (R109)
/// </summary>
public sealed class RailStepStateTests
{
    private static InstallerViewModel Vm() => new(new BrandTokens { AppName = "Acme Studio" });

    [Fact]
    public void Steps_before_the_current_one_are_done_and_the_rest_pending()
    {
        // Arrange
        var vm = Vm();

        // Act
        vm.CurrentStep = InstallerStep.InstallOptions;

        // Assert — the flow is Welcome → Location → Install.
        vm.RailSteps.Select(r => r.State).Should().Equal(
            RailStepState.Done, RailStepState.Current, RailStepState.Pending);
    }

    [Fact]
    public void The_first_screen_has_nothing_done()
    {
        // Arrange / Act
        var vm = Vm();

        // Assert
        vm.RailSteps.Select(r => r.State).Should().Equal(
            RailStepState.Current, RailStepState.Pending, RailStepState.Pending);
    }

    [Fact]
    public void On_Finish_every_step_is_done()
    {
        // Arrange
        var vm = Vm();
        vm.CurrentStep = InstallerStep.Installing;

        // Act
        vm.CurrentStep = InstallerStep.Finish;

        // Assert
        vm.RailSteps.Should().NotBeEmpty();
        vm.RailSteps.Should().OnlyContain(r => r.IsDone && !r.IsCurrent && !r.IsFailed);
    }

    [Fact]
    public void On_Failed_the_steps_before_the_install_are_done_and_the_install_failed()
    {
        // Arrange
        var vm = Vm();
        vm.CurrentStep = InstallerStep.Installing;

        // Act
        vm.CurrentStep = InstallerStep.Failed;

        // Assert
        vm.RailSteps.Select(r => r.State).Should().Equal(
            RailStepState.Done, RailStepState.Done, RailStepState.Failed);
        vm.RailSteps.Last().IsDone.Should().BeFalse("the step that failed did not go through");
    }

    [Theory]
    [InlineData(RailStepState.Pending, true, false, false, false)]
    [InlineData(RailStepState.Current, true, true, false, false)]
    [InlineData(RailStepState.Done, false, false, true, false)]
    [InlineData(RailStepState.Failed, false, false, false, true)]
    public void Each_state_projects_to_exactly_one_mark(
        RailStepState state, bool bullet, bool current, bool done, bool failed)
    {
        // Act
        var step = new RailStep("Step", state);

        // Assert
        step.ShowsBullet.Should().Be(bullet);
        step.IsCurrent.Should().Be(current);
        step.IsDone.Should().Be(done);
        step.IsFailed.Should().Be(failed);
    }

    [Fact]
    public void The_legacy_constructor_maps_its_flags_onto_a_state()
    {
        // Act / Assert
        new RailStep("a", isCurrent: true, isDone: false).State.Should().Be(RailStepState.Current);
        new RailStep("b", isCurrent: false, isDone: true).State.Should().Be(RailStepState.Done);
        new RailStep("c", isCurrent: false, isDone: false).State.Should().Be(RailStepState.Pending);
    }

    [AvaloniaFact]
    public void Rendered_rail_draws_a_check_mark_for_each_step_on_Finish()
    {
        // Arrange
        var vm = Vm();
        var window = new InstallerWindow { DataContext = vm };
        window.Show();

        // Act
        vm.CurrentStep = InstallerStep.Installing;
        vm.CurrentStep = InstallerStep.Finish;
        Dispatcher.UIThread.RunJobs();

        // Assert
        var rail = RailMarks(window);
        rail.VisibleChecks.Should().Be(vm.RailSteps.Count);
        rail.VisibleCrosses.Should().Be(0);
        rail.VisibleBullets.Should().Be(0);
    }

    [AvaloniaFact]
    public void Rendered_rail_draws_one_cross_on_Failed()
    {
        // Arrange
        var vm = Vm();
        var window = new InstallerWindow { DataContext = vm };
        window.Show();

        // Act
        vm.CurrentStep = InstallerStep.Installing;
        vm.CurrentStep = InstallerStep.Failed;
        Dispatcher.UIThread.RunJobs();

        // Assert
        var rail = RailMarks(window);
        rail.VisibleCrosses.Should().Be(1);
        rail.VisibleChecks.Should().Be(vm.RailSteps.Count - 1);
        rail.VisibleBullets.Should().Be(0);
    }

    [AvaloniaFact]
    public void Rendered_rail_keeps_bullets_before_anything_is_done()
    {
        // Arrange
        var vm = Vm();
        var window = new InstallerWindow { DataContext = vm };

        // Act
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Assert
        var rail = RailMarks(window);
        rail.VisibleBullets.Should().Be(vm.RailSteps.Count);
        rail.VisibleChecks.Should().Be(0);
        rail.VisibleCrosses.Should().Be(0);
    }

    private static (int VisibleBullets, int VisibleChecks, int VisibleCrosses) RailMarks(Window window)
    {
        var rail = window.GetVisualDescendants().OfType<ItemsControl>()
            .Single(i => ReferenceEquals(i.ItemsSource, ((InstallerViewModel)window.DataContext!).RailSteps));

        // Each entry's mark slot holds the bullet (a Border) and the two paths; a mark
        // counts only when it and every ancestor up to the rail is visible.
        var slots = rail.GetVisualDescendants().OfType<Grid>()
            .Where(g => g.Width == 12 && g.Height == 12)
            .ToList();

        int bullets = 0, checks = 0, crosses = 0;
        foreach (var slot in slots)
        {
            bullets += slot.Children.OfType<Border>().Count(b => b.IsVisible);
            var paths = slot.Children.OfType<Avalonia.Controls.Shapes.Path>().ToList();
            checks += paths.Count > 0 && paths[0].IsVisible ? 1 : 0;
            crosses += paths.Count > 1 && paths[1].IsVisible ? 1 : 0;
        }
        return (bullets, checks, crosses);
    }
}
