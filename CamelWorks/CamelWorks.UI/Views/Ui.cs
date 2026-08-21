using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace CamelWorks.UI.Views
{
    /// <summary>How a value reads: neutral, good, worth attention, or wrong.</summary>
    public enum Tone
    {
        /// <summary>No opinion.</summary>
        Plain = 0,

        /// <summary>Fine.</summary>
        Good = 1,

        /// <summary>Worth a look.</summary>
        Warn = 2,

        /// <summary>Wrong, or failed.</summary>
        Bad = 3,
    }

    /// <summary>
    /// One row of a table, plus the object it came from.
    ///
    /// The cells are indexed rather than named so a column can bind to <c>[0]</c> without a
    /// per-screen row class; <see cref="Item"/> keeps the original, so clicking a row can select
    /// elements, open a clash or edit a layer without the screen having to hold a parallel list.
    /// </summary>
    public sealed class TableRow
    {
        private readonly string?[] _cells;

        /// <summary>Create a row.</summary>
        /// <param name="item">What the row is about. Comes back on selection.</param>
        /// <param name="cells">The cell text, in column order.</param>
        public TableRow(object? item, params string?[] cells)
        {
            Item = item;
            _cells = cells ?? Array.Empty<string?>();
        }

        /// <summary>What the row is about.</summary>
        public object? Item { get; }

        /// <summary>How the row reads — used to tint it.</summary>
        public Tone Tone { get; set; }

        /// <summary>Cell text by column. Out-of-range is empty rather than an exception.</summary>
        /// <param name="index">Column index.</param>
        public string this[int index] =>
            index >= 0 && index < _cells.Length ? _cells[index] ?? string.Empty : string.Empty;

        /// <summary>How many cells the row has.</summary>
        public int Count => _cells.Length;

        /// <inheritdoc />
        public override string ToString() => string.Join(" | ", _cells);
    }

    /// <summary>
    /// A long-running piece of work, and the only way this product has of staying responsive
    /// during one.
    ///
    /// <b>This is not a background thread, and it cannot be.</b> The Navisworks API is
    /// main-thread-only: reading a property from another thread is undefined behaviour, not a
    /// slower version of the same thing. So the work runs on the UI thread and pumps the dispatcher
    /// between chunks, which is what lets the status line update and the Cancel button be clickable
    /// at all. Cancellation therefore happens between chunks, never inside one — stated plainly
    /// here because a Cancel button that takes twenty seconds to notice looks broken unless you
    /// know why.
    /// </summary>
    public sealed class Job
    {
        private readonly Dispatcher _dispatcher;
        private readonly TextBlock _status;
        private readonly System.Diagnostics.Stopwatch _sincePump = System.Diagnostics.Stopwatch.StartNew();

        internal Job(Dispatcher dispatcher, TextBlock status)
        {
            _dispatcher = dispatcher;
            _status = status;
        }

        /// <summary>Set when the user has asked to stop.</summary>
        public bool IsCancelled { get; internal set; }

        /// <summary>Say what is happening. Repaints at most a few times a second.</summary>
        /// <param name="message">One short line.</param>
        public void Say(string message)
        {
            _status.Text = message;

            // Pumping on every call would cost more than the work. A quarter second is fast enough
            // to read and slow enough not to dominate.
            if (_sincePump.ElapsedMilliseconds < 250) return;

            _sincePump.Restart();
            _dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Background);
        }

        /// <summary>
        /// Report progress and ask whether to keep going.
        /// </summary>
        /// <param name="done">How many items are finished.</param>
        /// <param name="what">What the items are, for the status line.</param>
        /// <returns>False once the user has cancelled.</returns>
        public bool Step(int done, string what)
        {
            Say(done.ToString("N0", CultureInfo.InvariantCulture) + " " + what);
            return !IsCancelled;
        }
    }

    /// <summary>
    /// The widget vocabulary every screen is built from.
    ///
    /// Written once and shared so that sixteen screens cannot each invent their own spacing, their
    /// own idea of an empty state, and their own way of saying something went wrong. Built in code
    /// rather than XAML because almost every screen's content is driven by data the shell holds,
    /// and templates for that would be more markup than the code they replace.
    /// </summary>
    public static class Ui
    {
        /// <summary>The gap used between stacked things, everywhere.</summary>
        public const double Gap = 8;

        // -----------------------------------------------------------------------------------
        // Type
        // -----------------------------------------------------------------------------------

        /// <summary>A screen or card title.</summary>
        /// <param name="text">The title.</param>
        public static TextBlock Heading(string text) => new TextBlock
        {
            Text = text,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 3),
        };

        /// <summary>The line under a title that says what the screen is for.</summary>
        /// <param name="text">The line.</param>
        public static TextBlock Sub(string text) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 2),
        };

        /// <summary>An ordinary line of text.</summary>
        /// <param name="text">The text.</param>
        /// <param name="opacity">How prominent it is.</param>
        /// <param name="bold">Whether to emphasise it.</param>
        public static TextBlock Line(string text, double opacity = 1, bool bold = false) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Opacity = opacity,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(0, bold ? 6 : 1, 0, 0),
        };

        /// <summary>A number and its label, for a scoreboard.</summary>
        /// <param name="value">The number, already formatted.</param>
        /// <param name="label">What it counts.</param>
        /// <param name="tone">How it reads.</param>
        public static UIElement Figure(string value, string label, Tone tone = Tone.Plain)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 22, 6) };

            panel.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = Ink(tone),
            });

            panel.Children.Add(new TextBlock { Text = label, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            return panel;
        }

        /// <summary>A small labelled pill, for status and counts.</summary>
        /// <param name="text">What it says.</param>
        /// <param name="tone">How it reads.</param>
        public static UIElement Pill(string text, Tone tone = Tone.Plain) => new Border
        {
            Background = Wash(tone),
            BorderBrush = Ink(tone),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(0, 0, 5, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = Ink(tone) },
        };

        // -----------------------------------------------------------------------------------
        // Layout
        // -----------------------------------------------------------------------------------

        /// <summary>Stack things vertically.</summary>
        /// <param name="children">What to stack.</param>
        public static StackPanel Stack(params UIElement[] children)
        {
            var panel = new StackPanel();
            foreach (var child in children) if (child != null) panel.Children.Add(child);
            return panel;
        }

        /// <summary>Lay things out left to right, wrapping when the pane is narrow.</summary>
        /// <param name="children">What to lay out.</param>
        public static Panel Across(params UIElement[] children)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal };

            foreach (var child in children)
            {
                if (child == null) continue;
                if (child is FrameworkElement element) element.Margin = new Thickness(0, 0, 6, 4);
                panel.Children.Add(child);
            }

            return panel;
        }

        /// <summary>Wrap content in a vertical scroller.</summary>
        /// <param name="content">What to scroll.</param>
        public static ScrollViewer Scroll(UIElement content) => new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(10),
            Content = content,
        };

        /// <summary>A titled block of related things.</summary>
        /// <param name="title">The block's title.</param>
        /// <param name="subtitle">One line saying what it is for, or null.</param>
        /// <param name="body">The contents.</param>
        public static Border Card(string title, string? subtitle, params UIElement[] body)
        {
            var panel = new StackPanel();
            panel.Children.Add(Heading(title));
            if (subtitle != null) panel.Children.Add(Sub(subtitle));

            foreach (var child in body) if (child != null) panel.Children.Add(child);

            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, Gap),
                Child = panel,
            };
        }

        // -----------------------------------------------------------------------------------
        // Controls
        // -----------------------------------------------------------------------------------

        /// <summary>A button.</summary>
        /// <param name="caption">What it says.</param>
        /// <param name="onClick">What it does.</param>
        public static System.Windows.Controls.Button Button(string caption, Action onClick)
        {
            var button = new System.Windows.Controls.Button
            {
                Content = caption,
                Padding = new Thickness(12, 4, 12, 4),
                MinWidth = 90,
            };

            if (onClick != null) button.Click += (s, e) => onClick();
            return button;
        }

        /// <summary>A text box that reports every change.</summary>
        /// <param name="value">Starting value.</param>
        /// <param name="onChanged">Called on each keystroke.</param>
        /// <param name="width">How wide.</param>
        public static TextBox Text(string? value, Action<string>? onChanged, double width = 180)
        {
            var box = new TextBox
            {
                Text = value ?? string.Empty,
                Width = width,
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (onChanged != null) box.TextChanged += (s, e) => onChanged(box.Text);
            return box;
        }

        /// <summary>A dropdown of fixed options.</summary>
        /// <param name="options">What can be picked.</param>
        /// <param name="selected">What is picked now, or null.</param>
        /// <param name="onChanged">Called with the new pick.</param>
        /// <param name="width">How wide.</param>
        public static ComboBox Choice(IEnumerable<string> options, string? selected,
                                      Action<string>? onChanged, double width = 170)
        {
            var box = new ComboBox { Width = width, VerticalAlignment = VerticalAlignment.Center };

            foreach (var option in options) box.Items.Add(option);

            if (selected != null && box.Items.Contains(selected)) box.SelectedItem = selected;

            if (onChanged != null)
                box.SelectionChanged += (s, e) =>
                {
                    if (box.SelectedItem is string pick) onChanged(pick);
                };

            return box;
        }

        /// <summary>A checkbox.</summary>
        /// <param name="caption">What it says.</param>
        /// <param name="value">Whether it starts ticked.</param>
        /// <param name="onChanged">Called with the new state.</param>
        public static CheckBox Check(string caption, bool value, Action<bool>? onChanged)
        {
            var box = new CheckBox
            {
                Content = caption,
                IsChecked = value,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 3),
            };

            if (onChanged != null)
            {
                box.Checked += (s, e) => onChanged(true);
                box.Unchecked += (s, e) => onChanged(false);
            }

            return box;
        }

        /// <summary>A control with a label in front of it.</summary>
        /// <param name="label">The label.</param>
        /// <param name="control">The control.</param>
        public static Panel Field(string label, UIElement control)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };

            panel.Children.Add(new TextBlock
            {
                Text = label,
                Opacity = 0.75,
                MinWidth = 120,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });

            panel.Children.Add(control);
            return panel;
        }

        // -----------------------------------------------------------------------------------
        // States
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// An empty result, said as a result.
        ///
        /// "No clashes matched these rules" is an answer; a blank panel is a bug report. Every
        /// screen that can come back with nothing uses this rather than showing nothing.
        /// </summary>
        /// <param name="what">What was looked for.</param>
        /// <param name="why">Why there is nothing, or what to try.</param>
        public static UIElement Empty(string what, string why) => Stack(
            Line(what, 1, true),
            Line(why, 0.7));

        /// <summary>Something failed, and this says what.</summary>
        /// <param name="message">The failure, in the user's terms.</param>
        public static UIElement Problem(string message) => new Border
        {
            Background = Wash(Tone.Bad),
            BorderBrush = Ink(Tone.Bad),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8),
            Margin = new Thickness(0, Gap, 0, 0),
            Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Ink(Tone.Bad) },
        };

        /// <summary>Something worth knowing before pressing the button.</summary>
        /// <param name="message">The note.</param>
        public static UIElement Note(string message) => new Border
        {
            Background = Wash(Tone.Warn),
            BorderThickness = new Thickness(0, 0, 0, 0),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 4),
            Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        };

        // -----------------------------------------------------------------------------------
        // Tables
        // -----------------------------------------------------------------------------------

        /// <summary>A table.</summary>
        /// <param name="rows">The rows.</param>
        /// <param name="columns">Header and width per column. A width of 0 sizes to content.</param>
        public static ListView Table(IEnumerable<TableRow> rows, params (string Header, double Width)[] columns)
        {
            var grid = new GridView { AllowsColumnReorder = false };

            for (var i = 0; i < columns.Length; i++)
            {
                var column = new GridViewColumn
                {
                    Header = columns[i].Header,
                    DisplayMemberBinding = new Binding("[" + i.ToString(CultureInfo.InvariantCulture) + "]"),
                };

                if (columns[i].Width > 0) column.Width = columns[i].Width;
                grid.Columns.Add(column);
            }

            return new ListView
            {
                View = grid,
                ItemsSource = rows.ToList(),
                MaxHeight = 420,
                Margin = new Thickness(0, Gap, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
        }

        /// <summary>Call back when a row is picked.</summary>
        /// <param name="table">The table.</param>
        /// <param name="onPick">Called with the row's item.</param>
        public static ListView OnPick(this ListView table, Action<object?> onPick)
        {
            table.SelectionChanged += (s, e) =>
            {
                if (table.SelectedItem is TableRow row) onPick(row.Item);
            };

            return table;
        }

        /// <summary>The item behind the selected row, or null.</summary>
        /// <param name="table">The table.</param>
        public static object? Picked(this ListView table) => (table.SelectedItem as TableRow)?.Item;

        // -----------------------------------------------------------------------------------
        // Running work
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// A button that runs something long, with a status line and a Cancel.
        ///
        /// Nothing in this product runs when a screen opens; everything runs from one of these.
        /// That is what keeps clicking a tab from freezing a federation of half a million elements,
        /// and it is also what makes the zero-setup rule honest — the button works on a raw model
        /// with nothing configured.
        /// </summary>
        /// <param name="caption">What the button says.</param>
        /// <param name="output">Where results are written. Cleared on each run.</param>
        /// <param name="work">The work. Check <see cref="Job.IsCancelled"/> in any loop.</param>
        public static Panel Runner(string caption, Panel output, Action<Job> work)
        {
            var status = new TextBlock { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
            var cancel = new System.Windows.Controls.Button
            {
                Content = "Stop",
                Padding = new Thickness(10, 4, 10, 4),
                Visibility = Visibility.Collapsed,
            };

            Job? running = null;
            cancel.Click += (s, e) => { if (running != null) running.IsCancelled = true; };

            var go = new System.Windows.Controls.Button { Content = caption, Padding = new Thickness(12, 4, 12, 4), MinWidth = 110 };

            go.Click += (s, e) =>
            {
                output.Children.Clear();

                go.IsEnabled = false;
                cancel.Visibility = Visibility.Visible;
                status.Text = "Working...";

                var job = new Job(go.Dispatcher, status);
                running = job;

                try
                {
                    work(job);
                    status.Text = job.IsCancelled ? "Stopped." : string.Empty;
                }
                catch (Exception ex)
                {
                    // Shown in the pane rather than thrown at the host. A plug-in exception that
                    // escapes takes the message loop with it in some versions, and the user would
                    // see a crash instead of a sentence about what failed.
                    output.Children.Clear();
                    output.Children.Add(Problem(Describe(ex)));
                    status.Text = string.Empty;
                }
                finally
                {
                    running = null;
                    go.IsEnabled = true;
                    cancel.Visibility = Visibility.Collapsed;
                }
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, Gap, 0, 4) };
            panel.Children.Add(go);
            panel.Children.Add(new Border { Width = 6 });
            panel.Children.Add(cancel);
            panel.Children.Add(new Border { Width = 10 });
            panel.Children.Add(status);
            return panel;
        }

        /// <summary>
        /// An exception in the words of somebody who did not write this code.
        ///
        /// The type name is dropped and the message kept, except where the type is the whole story —
        /// a stale element handle means the model changed underneath, which is worth saying in
        /// those words rather than as "StaleItemException".
        /// </summary>
        /// <param name="error">What went wrong.</param>
        public static string Describe(Exception error)
        {
            switch (error)
            {
                case Core.Abstractions.StaleItemException _:
                    return "The model changed while this was running, so some elements no longer exist. "
                           + "Run it again to work from the model as it is now.";

                case NotSupportedException _:
                    return error.Message + " (Navisworks does not offer this.)";

                case UnauthorizedAccessException _:
                    return "Windows refused access: " + error.Message;

                case System.IO.IOException _:
                    return "A file could not be read or written: " + error.Message;

                default:
                    return error.Message;
            }
        }

        // -----------------------------------------------------------------------------------

        /// <summary>The text colour for a tone.</summary>
        /// <param name="tone">The tone.</param>
        public static Brush Ink(Tone tone) => tone switch
        {
            Tone.Good => new SolidColorBrush(Color.FromRgb(0x1E, 0x7A, 0x46)),
            Tone.Warn => new SolidColorBrush(Color.FromRgb(0x8A, 0x5A, 0x00)),
            Tone.Bad => new SolidColorBrush(Color.FromRgb(0xA3, 0x2A, 0x2A)),
            _ => SystemColors.ControlTextBrush,
        };

        /// <summary>The background wash for a tone.</summary>
        /// <param name="tone">The tone.</param>
        public static Brush Wash(Tone tone) => tone switch
        {
            Tone.Good => new SolidColorBrush(Color.FromArgb(28, 0x1E, 0x7A, 0x46)),
            Tone.Warn => new SolidColorBrush(Color.FromArgb(30, 0xC8, 0x8A, 0x00)),
            Tone.Bad => new SolidColorBrush(Color.FromArgb(28, 0xA3, 0x2A, 0x2A)),
            _ => Brushes.Transparent,
        };

        /// <summary>A count with its unit, pluralised.</summary>
        /// <param name="count">How many.</param>
        /// <param name="unit">What of, singular.</param>
        public static string Count(int count, string unit) =>
            count.ToString("N0", CultureInfo.InvariantCulture) + " " + unit + (count == 1 ? string.Empty : "s");
    }
}
