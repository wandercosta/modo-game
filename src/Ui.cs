using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ModoGame
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Modo de teste: só lista o que seria fechado, sem fechar nada
            if (args.Length > 0 && args[0] == "--listar")
            {
                var r = Engine.Scan();
                var sb = new StringBuilder();
                sb.AppendLine("Protegidos: " + r.ProtectedCount);
                foreach (var g in r.Groups)
                    sb.AppendLine(string.Format("{0,-32} {1,3}x {2,7} MB  restaurar={3} protegido={4}  {5}",
                        g.Name, g.Procs.Count, g.Bytes >> 20, string.Join(" | ", g.RestorePaths), g.UserProtected, g.Path));
                File.WriteAllText(args.Length > 1 ? args[1] : "listar.txt", sb.ToString(), Encoding.UTF8);
                return;
            }

            var app = new Application();
            Window w;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.xaml"))
                w = (Window)XamlReader.Load(s);
            var ui = new Ui(w);

            // Modo de teste: salva um print da janela (--foto arquivo.png [processos])
            if (args.Length > 1 && args[0] == "--foto")
            {
                if (args.Length > 2) ui.ShowProcesses();
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    var root = (FrameworkElement)w.Content;
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth * 2, (int)root.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
                    bmp.Render(root);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using (var f = File.Create(args[1])) enc.Save(f);
                    app.Shutdown();
                };
                t.Start();
            }
            app.Run(w);
        }
    }

    class Ui
    {
        public static readonly CultureInfo PtBr = new CultureInfo("pt-BR");

        static readonly Color Accent = Color.FromRgb(0x3D, 0xDC, 0x97);
        static readonly Color Idle = Color.FromRgb(0x8A, 0x91, 0xA3);
        static readonly Color Warn = Color.FromRgb(0xE0, 0xA3, 0x3D);

        readonly Window w;
        readonly Grid boostBtn, pageBoost, pageProc;
        readonly Ellipse halo, ring;
        readonly TextBlock btnGlyph, btnLabel, status, ramPct, ramText, statApps, statRam, statPower, pageTitle, protInfo;
        readonly Border ramFill;
        readonly StackPanel topList, procList;
        readonly Button navBoost, navProc;
        bool busy;

        T Get<T>(string name) where T : class { return (T)w.FindName(name); }

        public Ui(Window window)
        {
            w = window;
            boostBtn = Get<Grid>("BoostBtn"); pageBoost = Get<Grid>("PageBoost"); pageProc = Get<Grid>("PageProc");
            halo = Get<Ellipse>("Halo"); ring = Get<Ellipse>("Ring");
            btnGlyph = Get<TextBlock>("BtnGlyph"); btnLabel = Get<TextBlock>("BtnLabel"); status = Get<TextBlock>("Status");
            ramPct = Get<TextBlock>("RamPct"); ramText = Get<TextBlock>("RamText"); ramFill = Get<Border>("RamFill");
            statApps = Get<TextBlock>("StatApps"); statRam = Get<TextBlock>("StatRam"); statPower = Get<TextBlock>("StatPower");
            pageTitle = Get<TextBlock>("PageTitle"); protInfo = Get<TextBlock>("ProtInfo");
            topList = Get<StackPanel>("TopList"); procList = Get<StackPanel>("ProcList");
            navBoost = Get<Button>("NavBoost"); navProc = Get<Button>("NavProc");

            Get<Grid>("TitleBar").MouseLeftButtonDown += (s, e) => w.DragMove();
            Get<Button>("BtnClose").Click += (s, e) => w.Close();
            Get<Button>("BtnMin").Click += (s, e) => w.WindowState = WindowState.Minimized;
            Get<Button>("BtnSeeAll").Click += (s, e) => ShowPage(false);
            Get<Button>("BtnRefresh").Click += (s, e) => Refresh();
            navBoost.Click += (s, e) => ShowPage(true);
            navProc.Click += (s, e) => ShowPage(false);
            boostBtn.MouseLeftButtonUp += (s, e) => Toggle();
            boostBtn.MouseEnter += (s, e) => { if (!busy && !Engine.IsActive()) ring.Stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x50)); };
            boostBtn.MouseLeave += (s, e) => { if (!busy) Paint(Engine.IsActive(), false); };

            if (!Engine.IsAdmin()) Get<Border>("AdminBadge").Visibility = Visibility.Visible;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (s, e) => UpdateRam();
            timer.Start();

            ShowPage(true);
            Paint(Engine.IsActive(), false);
            UpdateRam();
            Refresh();
        }

        public void ShowProcesses() { ShowPage(false); }

        void ShowPage(bool boost)
        {
            pageBoost.Visibility = boost ? Visibility.Visible : Visibility.Collapsed;
            pageProc.Visibility = boost ? Visibility.Collapsed : Visibility.Visible;
            pageTitle.Text = boost ? "Boost" : "Processos";
            navBoost.Background = boost ? new SolidColorBrush(Color.FromRgb(0x10, 0x20, 0x1A)) : Brushes.Transparent;
            navProc.Background = boost ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x10, 0x20, 0x1A));
            navBoost.Foreground = new SolidColorBrush(boost ? Accent : Color.FromRgb(0x6B, 0x72, 0x84));
            navProc.Foreground = new SolidColorBrush(boost ? Color.FromRgb(0x6B, 0x72, 0x84) : Accent);
        }

        // ---------- Estado visual do botão ----------

        void Paint(bool active, bool working)
        {
            var c = active || working ? Accent : Idle;
            ring.Stroke = new SolidColorBrush(active || working ? Accent : Color.FromRgb(0x2A, 0x2F, 0x3A));
            ring.Fill = new SolidColorBrush(active ? Color.FromRgb(0x0F, 0x1F, 0x19) : Color.FromRgb(0x12, 0x15, 0x1C));
            btnGlyph.Foreground = new SolidColorBrush(c);
            btnLabel.Foreground = new SolidColorBrush(c);
            btnLabel.Text = working ? "AGUARDE" : active ? "ATIVO" : "ATIVAR";
            btnGlyph.Text = active && !working ? "" : "";

            var glow = (DropShadowEffect)ring.Effect;
            var dur = TimeSpan.FromMilliseconds(450);
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(active ? 0.7 : 0, dur));
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(active ? 46 : 0, dur));

            if (working)
            {
                var pulse = new DoubleAnimation(0.02, 0.16, TimeSpan.FromMilliseconds(700))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                halo.BeginAnimation(UIElement.OpacityProperty, pulse);
            }
            else halo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(active ? 0.06 : 0, dur));
        }

        void Toggle()
        {
            if (busy) return;
            busy = true;
            bool activating = !Engine.IsActive();
            Paint(false, true);
            Action<string> log = msg => w.Dispatcher.BeginInvoke(new Action(() => status.Text = msg));

            Task.Factory.StartNew(() =>
            {
                try { return activating ? Engine.Activate(log) : Engine.Restore(log); }
                catch (Exception ex) { return "Algo deu errado: " + ex.Message; }
            }).ContinueWith(t => w.Dispatcher.BeginInvoke(new Action(() =>
            {
                busy = false;
                Paint(Engine.IsActive(), false);
                status.Text = t.Result + (Engine.IsActive() ? "\nClique de novo para restaurar." : "");
                UpdateRam();
                Refresh(true);
            })));
        }

        // ---------- Dados ----------

        void UpdateRam()
        {
            ulong total, avail;
            Native.Memory(out total, out avail);
            double used = total - avail, pct = used / total;
            ramPct.Text = Math.Round(pct * 100) + "%";
            ramText.Text = string.Format(PtBr, "{0:N1} de {1:N1} GB", used / (1L << 30), (double)total / (1L << 30));
            var color = pct > 0.8 ? Warn : Accent;
            ramFill.Background = new SolidColorBrush(color);
            ramFill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(250 * pct, TimeSpan.FromMilliseconds(500)));
        }

        void Refresh(bool keepStatus = false)
        {
            Task.Factory.StartNew(() => Tuple.Create(Engine.Scan(), Engine.CurrentSchemeName()))
                .ContinueWith(t => w.Dispatcher.BeginInvoke(new Action(() => Render(t.Result.Item1, t.Result.Item2, keepStatus))));
        }

        void Render(ScanResult r, string power, bool keepStatus)
        {
            var closing = r.Groups.Where(g => !g.UserProtected).ToList();
            long bytes = closing.Sum(g => g.Bytes);
            statApps.Text = closing.Count.ToString();
            statRam.Text = string.Format(PtBr, "{0:N1} GB", bytes / (double)(1L << 30));
            statPower.Text = power;
            if (!keepStatus && !busy)
                status.Text = Engine.IsActive() ? "Modo game ativo.\nClique para restaurar seus apps." : "Pronto para jogar";

            topList.Children.Clear();
            long max = closing.Count > 0 ? closing[0].Bytes : 1;
            foreach (var g in closing.Take(7))
                topList.Children.Add(TopRow(g, max));
            if (closing.Count == 0)
                topList.Children.Add(new TextBlock { Text = "Nada pesado rodando. Tudo limpo.", Foreground = Brush(0x7D, 0x84, 0x96), FontSize = 13 });

            procList.Children.Clear();
            foreach (var g in r.Groups) procList.Children.Add(ProcRow(g));
            protInfo.Text = r.ProtectedCount + " processos do Windows, drivers, launchers e jogos protegidos automaticamente.";
        }

        static SolidColorBrush Brush(byte r, byte g, byte b) { return new SolidColorBrush(Color.FromRgb(r, g, b)); }

        static string Mb(long bytes)
        {
            return bytes >= (1L << 30)
                ? string.Format(PtBr, "{0:N1} GB", bytes / (double)(1L << 30))
                : (bytes >> 20) + " MB";
        }

        UIElement TopRow(Group g, long max)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

            var name = new TextBlock { Text = g.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var track = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
            track.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Brush(0x1C, 0x20, 0x29) });
            var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Brush(0x3D, 0xDC, 0x97), HorizontalAlignment = HorizontalAlignment.Left };
            track.Children.Add(bar);
            track.SizeChanged += (s, e) => bar.Width = Math.Max(4, e.NewSize.Width * g.Bytes / max);
            var size = new TextBlock { Text = Mb(g.Bytes), FontSize = 12, Foreground = Brush(0x7D, 0x84, 0x96), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

            Grid.SetColumn(track, 1); Grid.SetColumn(size, 2);
            grid.Children.Add(name); grid.Children.Add(track); grid.Children.Add(size);
            return grid;
        }

        UIElement ProcRow(Group g)
        {
            var row = new Border { BorderBrush = Brush(0x16, 0x1A, 0x22), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(4, 10, 4, 10) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });

            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = g.Name, FontSize = 13 });
            info.Children.Add(new TextBlock { Text = g.Path ?? "", FontSize = 11, Foreground = Brush(0x5A, 0x60, 0x70), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 16, 0) });

            var count = new TextBlock { Text = g.Procs.Count + (g.Procs.Count == 1 ? " proc." : " procs."), FontSize = 12, Foreground = Brush(0x7D, 0x84, 0x96), VerticalAlignment = VerticalAlignment.Center };
            var size = new TextBlock { Text = Mb(g.Bytes), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 16, 0) };
            var toggle = new CheckBox { IsChecked = !g.UserProtected, Style = (Style)w.FindResource("Toggle"), VerticalAlignment = VerticalAlignment.Center };
            toggle.Click += (s, e) =>
            {
                Engine.SetUserProtected(g.Name, toggle.IsChecked != true);
                Refresh(true);
            };

            Grid.SetColumn(count, 1); Grid.SetColumn(size, 2); Grid.SetColumn(toggle, 3);
            grid.Children.Add(info); grid.Children.Add(count); grid.Children.Add(size); grid.Children.Add(toggle);
            row.Child = grid;
            return row;
        }
    }
}
