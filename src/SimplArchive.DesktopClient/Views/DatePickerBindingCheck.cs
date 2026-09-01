using System;
using Avalonia.Controls;
using Avalonia.Data;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Headless check that a date field's binding round-trips, for <c>--datepicker-test</c>.
/// </summary>
/// <remarks>
/// The date fields are <see cref="CalendarDatePicker"/> — a text box with a calendar button — not the spinner
/// <c>DatePicker</c>. This exists because the obvious version of that change is broken and does not look it:
/// <c>SelectedDate</c> is <c>DateTime?</c>, every property behind one of these fields used to be
/// <c>DateTimeOffset?</c>, and a binding across that gap fails SILENTLY. Measured, not assumed — before the
/// properties were converted, view-model→control and control→view-model were BOTH false, so a swapped field
/// would render, accept a date, and never write it back. A screenshot cannot show that.
/// </remarks>
internal static class DatePickerBindingCheck
{
    internal static void Run()
    {
        var vm = new MainWindowViewModel();
        var picker = new Avalonia.Controls.CalendarDatePicker { DataContext = vm };
        picker.Bind(Avalonia.Controls.CalendarDatePicker.SelectedDateProperty,
            new Avalonia.Data.Binding(nameof(MainWindowViewModel.SysDocumentDate))
            {
                Mode = Avalonia.Data.BindingMode.TwoWay,
            });

        vm.SysDocumentDate = new DateTime(2026, 3, 31);
        var toControl = picker.SelectedDate?.Date == new DateTime(2026, 3, 31);

        picker.SelectedDate = new DateTime(2026, 5, 17);
        var toViewModel = vm.SysDocumentDate?.Date == new DateTime(2026, 5, 17);

        var cleared = true;
        picker.SelectedDate = null;
        cleared = vm.SysDocumentDate is null;

        // The Intray's own field, which is the shape this pattern was copied FROM: DateOnly? behind the
        // same control, and DateTime? is what CalendarDatePicker.SelectedDate actually is.
        var intray = new MainWindowViewModel();
        var ip = new Avalonia.Controls.CalendarDatePicker { DataContext = intray.Intray };
        ip.Bind(Avalonia.Controls.CalendarDatePicker.SelectedDateProperty,
            new Avalonia.Data.Binding(nameof(IntrayTabViewModel.DocumentDate)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        intray.Intray.DocumentDate = new DateTime(2026, 3, 31);
        var sameTypeToControl = ip.SelectedDate?.Date == new DateTime(2026, 3, 31);
        ip.SelectedDate = new DateTime(2026, 5, 17);
        var sameTypeToVm = intray.Intray.DocumentDate == new DateTime(2026, 5, 17);

        Console.WriteLine($"SysDocumentDate      vm->control={toControl} control->vm={toViewModel} clear={cleared}");
        Console.WriteLine($"Intray.DocumentDate  vm->control={sameTypeToControl} control->vm={sameTypeToVm}");
        Console.WriteLine(toControl && toViewModel && cleared && sameTypeToControl && sameTypeToVm ? "OK" : "FAILED");
    }
}
