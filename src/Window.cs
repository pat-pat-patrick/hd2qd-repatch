using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Hd2Qd.Repatch
{
    public static partial class Plugin
    {
        static Window open;

        /// <summary>The plugin's window: the library's mods whose models need a repair, the ones
        /// repaired (which can be undone), and a repair for each or for all. One window at a time:
        /// opening again brings it forward and reads again.</summary>
        public static void Open(Window owner, Dictionary<string, object> host)
        {
            var lang = Words.Lang(host);
            if (open != null) { open.Activate(); ((View)open.Tag).Read(); return; }
            var v = new View(host, lang);
            Window w;
            if (host.TryGetValue("window", out var mk) && mk is Func<string, FrameworkElement, Window> make) w = make(Title(lang), v.Root);
            else w = new Window
            {
                Title = Title(lang) + " · HD2 Mod Quick Deploy", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = v.B("Ground900"), Foreground = v.B("Ink"), Content = v.Root, Icon = owner?.Icon,
                FontFamily = owner?.FontFamily ?? SystemFonts.MessageFontFamily,
            };
            w.Width = 980; w.Height = 680; w.MinWidth = 700; w.MinHeight = 420;
            w.Tag = v;
            w.KeyDown += (s, e) => { if (e.Key == Key.Escape) w.Close(); };
            w.Closed += (s, e) => open = null;
            open = w;
            w.Show();
            v.Read();
        }

        sealed class View
        {
            readonly Dictionary<string, object> host;
            readonly string lang;
            public readonly DockPanel Root = new DockPanel { Margin = new Thickness(28, 24, 28, 24) };
            readonly StackPanel head = new StackPanel();
            readonly StackPanel list = new StackPanel();
            bool busy;

            public View(Dictionary<string, object> host, string lang)
            {
                this.host = host; this.lang = lang;
                DockPanel.SetDock(head, Dock.Top);
                Root.Children.Add(head);
                var credits = Credits();
                DockPanel.SetDock(credits, Dock.Bottom);
                Root.Children.Add(credits);
                Root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) });
            }

            public Brush B(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
            Style S(string key) => Application.Current?.TryFindResource(key) as Style;
            /// <summary>The text of a key in the window's language (Words, Lang\*.json).</summary>
            string T(string key) => Words.Get(lang, key);

            /// <summary>A text with arguments; a first argument of 1 picks key_1 where the language has it.</summary>
            string T(string key, params object[] args) => Words.F(lang, key, args);

            public async void Read()
            {
                if (busy) return;
                busy = true;
                Head(T("reading"), null);
                list.Children.Clear();
                List<Repairs.Mod> mods;
                try
                {
                    var data = Data(host) ?? throw new InvalidOperationException(T("no_game_folder"));
                    string home = Home(host);
                    mods = await Task.Run(() =>
                    {
                        var game = GameUnits.Load(home, data);
                        return Repairs.ScanAll(home, Library(host), game, data);
                    });
                }
                catch (Exception e)
                {
                    Head(T("check_failed", e.Message), null);
                    busy = false;
                    return;
                }
                busy = false;
                Show(mods);
            }

            void Head(string line, Button action)
            {
                head.Children.Clear();
                var top = new DockPanel();
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
                if (action != null) buttons.Children.Add(action);
                var again = new Button { Content = T("check_again"), Style = S("Ghost"), MinHeight = 36, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(8, 0, 0, 0) };
                again.Click += (s, e) => Read();
                buttons.Children.Add(again);
                DockPanel.SetDock(buttons, Dock.Right);
                top.Children.Add(buttons);
                top.Children.Add(Small(T("intro"), new Thickness(0, 4, 16, 0)));
                head.Children.Add(top);
                head.Children.Add(new TextBlock { Text = line, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });
            }

            /// <summary>Where the sound bank repair method comes from: 40Hour's Bilibili page, linked
            /// from the credits line (the user, 2026-11-11).</summary>
            const string AuthorPage = "https://space.bilibili.com/157681854";

            /// <summary>The credits at the window's foot: who contributed and whose methods the repair
            /// follows, in the window's language; {0} in the text is 40Hour, a link to the author's page.</summary>
            TextBlock Credits()
            {
                var t = Small("", new Thickness(0, 12, 0, 0));
                var parts = T("credits").Split(new[] { "{0}" }, 2, StringSplitOptions.None);
                t.Inlines.Add(new Run(parts[0]));
                if (parts.Length == 2)
                {
                    var link = new Hyperlink(new Run("40Hour")) { NavigateUri = new Uri(AuthorPage), Foreground = B("Muted") };
                    link.RequestNavigate += (s, e) =>
                    {
                        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                        catch (Exception x) when (x is System.ComponentModel.Win32Exception || x is InvalidOperationException) { }
                        e.Handled = true;
                    };
                    t.Inlines.Add(link);
                    t.Inlines.Add(new Run(parts[1]));
                }
                return t;
            }

            TextBlock Small(string text, Thickness margin) =>
                new TextBlock { Text = text, FontSize = 12, LineHeight = 16, Foreground = B("Muted"), TextWrapping = TextWrapping.Wrap, Margin = margin };

            void Show(List<Repairs.Mod> mods)
            {
                var shown = mods.Where(m => Stale(m) + Newer(m) > 0 || m.Undoable || m.Errors.Count > 0)
                    .OrderBy(m => Stale(m) > 0 ? 0 : m.Errors.Count > 0 ? 1 : Newer(m) > 0 ? 2 : 3).ThenBy(m => m.Name).ToList();
                int outdated = mods.Count(m => Stale(m) > 0);
                int current = mods.Count(m => m.Models + m.Banks + m.Tables > m.Added && Stale(m) + Newer(m) == 0 && m.Errors.Count == 0 && !m.Undoable);
                Button all = null;
                if (outdated > 1)
                {
                    all = new Button { Content = T("repair_all"), Style = S("Primary"), MinHeight = 36, Padding = new Thickness(16, 0, 16, 0) };
                    all.Click += (s, e) => Repair(mods.Where(m => Stale(m) > 0).ToList());
                }
                Head(outdated > 0 ? T("mods_outdated", outdated) : T("mods_current"), all);
                list.Children.Clear();
                if (shown.Count > 0) list.Children.Add(Row(null));
                foreach (var m in shown) list.Children.Add(Row(m));
                if (current > 0)
                    list.Children.Add(Small(T("others_current", current), new Thickness(0, 12, 0, 0)));
            }

            /// <summary>How many of the mod's resources an earlier version of the game was made
            /// for, over every kind.</summary>
            static int Stale(Repairs.Mod m) => m.Update + m.BankUpdate + m.TableUpdate;

            static int Newer(Repairs.Mod m) => m.Newer + m.BankNewer + m.TableNewer;

            /// <summary>The row's details: what the mod has of each kind, or that it is newer.</summary>
            string Said(Repairs.Mod m)
            {
                if (Newer(m) > 0) return T("row_newer", Newer(m));
                var said = new List<string>();
                if (m.Update > 0) said.Add(T("row_outdated", m.Update));
                if (m.BankUpdate > 0) said.Add(T("row_banks_outdated", m.BankUpdate));
                if (m.TableUpdate > 0) said.Add(T("row_tables_outdated", m.TableUpdate));
                return said.Count > 0 ? string.Join(" ", said) : T("row_repaired");
            }

            static readonly GridLength[] Columns = { new GridLength(1, GridUnitType.Star), new GridLength(120), new GridLength(1.3, GridUnitType.Star), new GridLength(110) };

            FrameworkElement Row(Repairs.Mod m)
            {
                bool header = m == null;
                var g = new Grid { MinHeight = header ? 28 : 48 };
                foreach (var c in Columns) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
                void Cell(int col, UIElement e) { Grid.SetColumn(e, col); g.Children.Add(e); }
                TextBlock Text(string s, int col) => new TextBlock
                {
                    Text = s, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = header ? 12 : 14, Foreground = header ? B("Muted") : B("Ink"), Margin = new Thickness(col == 0 ? 12 : 0, 0, 12, 0),
                };
                if (header)
                {
                    Cell(0, Text(T("column_mod"), 0)); Cell(1, Text(T("column_status"), 1)); Cell(2, Text(T("column_details"), 2));
                }
                else
                {
                    var name = Text(m.Name, 0);
                    if (!m.Enabled) name.Foreground = B("Muted");
                    Cell(0, name);
                    var (bt, bc) = Stale(m) > 0 ? (T("badge_outdated"), "Yellow") : m.Errors.Count > 0 ? (T("badge_unreadable"), "Red")
                                 : Newer(m) > 0 ? (T("badge_newer"), "Muted") : (T("badge_repaired"), "Blue");
                    Cell(1, new Border
                    {
                        BorderBrush = B(bc), BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = bt, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(bc) },
                    });
                    string said = m.Errors.Count > 0 && Stale(m) == 0 ? m.Errors[0] : Said(m);
                    var details = Text(said, 2);
                    details.ToolTip = m.Errors.Count > 0 ? string.Join("\n", m.Errors) : said;
                    Cell(2, details);
                    Button act = null;
                    if (Stale(m) > 0) { act = new Button { Content = T("repair"), Style = S("Primary") }; act.Click += (s, e) => Repair(new List<Repairs.Mod> { m }); }
                    else if (m.Undoable) { act = new Button { Content = T("undo"), Style = S("Ghost") }; act.Click += (s, e) => Undo(m); }
                    if (act != null)
                    {
                        act.MinHeight = 32; act.MinWidth = 88; act.Padding = new Thickness(12, 0, 12, 0);
                        act.HorizontalAlignment = HorizontalAlignment.Right; act.VerticalAlignment = VerticalAlignment.Center;
                        Cell(3, act);
                    }
                }
                return new Border { Child = g, BorderBrush = B("Grid"), BorderThickness = new Thickness(0, 0, 0, 1) };
            }

            void Tell(string text)
            {
                if (host.TryGetValue("tell", out var t) && t is Action<string> tell) tell(text);
                else MessageBox.Show(text, Title(lang));
            }

            void Changed(string text)
            {
                if (host.TryGetValue("foot", out var f) && f is Action<string, string> foot) foot(text, "Blue");
                if (host.TryGetValue("refresh", out var r) && r is Action refresh) refresh();
            }

            async void Repair(List<Repairs.Mod> mods)
            {
                if (busy) return;
                busy = true;
                Head(T("repairing"), null);
                string home = Home(host), data = Data(host);
                List<(string Name, Repairs.Done Done, string Error)> results;
                try
                {
                    results = await Task.Run(() =>
                    {
                        var game = GameUnits.Load(home, data);
                        return mods.Select(m =>
                        {
                            try { return (m.Name, Repairs.Repair(home, m.Path, game, data), (string)null); }
                            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { return (m.Name, (Repairs.Done)null, e.Message); }
                        }).ToList();
                    });
                }
                finally { busy = false; }
                int fixed_ = results.Where(r => r.Done != null).Sum(r => r.Done.Models + r.Done.Banks + r.Done.Tables);
                var failed = results.Where(r => r.Error != null || r.Done.Refused.Count > 0).ToList();
                var notes = results.Where(r => r.Done != null && r.Done.Notes.Count > 0)
                    .SelectMany(r => r.Done.Notes.Select(n => r.Name + ": " + n)).ToList();
                Changed(T("repaired", fixed_));
                if (notes.Count > 0)
                    Tell(string.Join("\n", notes));                 // a bank that fell back to the game's own
                if (failed.Count > 0)
                    Tell(T("repair_failed") + string.Join("\n", failed.Select(r =>
                        r.Name + ": " + (r.Error ?? string.Join("; ", r.Done.Refused)) +
                        (r.Error != null ? T("close_game_retry") : ""))));
                Read();
            }

            async void Undo(Repairs.Mod m)
            {
                if (busy) return;
                if (host.TryGetValue("ask", out var a) && a is Func<string, string, bool> ask
                    && !ask(T("undo_ask", m.Name), T("undo"))) return;
                busy = true;
                string home = Home(host);
                (int Restored, List<string> Changed) r;
                try { r = await Task.Run(() => Repairs.Undo(home, m.Path)); }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
                {
                    busy = false;
                    Tell(T("undo_failed", e.Message));
                    return;
                }
                busy = false;
                Changed(T("undone", m.Name));
                if (r.Changed.Count > 0)
                    Tell(T("undo_left") + string.Join("\n", r.Changed));
                Read();
            }
        }
    }
}
