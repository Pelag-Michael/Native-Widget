using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

internal sealed partial class RoutineWindow : Window
{
    private readonly RoutineData _data;
    private readonly Canvas _root = new(), _cards = new() { IsHitTestVisible = false };
    private readonly Border _dim = new(), _board = new() { Width = 940, Height = 720, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
    private readonly Viewbox _view = new() { Stretch = Stretch.Uniform };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _snoozeTimer = new();
    private DayOfWeek _day = DateTime.Today.DayOfWeek;
    private IReadOnlyList<RoutineReminder>? _reminderView;
    internal bool IsEditingSettings { get; private set; }
    internal List<RoutineBlock> VisibleBlocks => _data.Week[_day].Where(b=>_reminderView==null||_reminderView.Any(r=>r.WholeDay||r.BlockIds.Contains(b.Id)||b.Tasks.Any(t=>r.TaskIds.Contains(t.Id)))).ToList();
    internal List<RoutineTask> VisibleTasks(RoutineBlock block) => block.Tasks.Where(t=>_reminderView==null||_reminderView.Any(r=>r.WholeDay||r.BlockIds.Contains(block.Id)||r.TaskIds.Contains(t.Id))).ToList();
    private readonly Dictionary<string, BitmapImage> _imageCache = new();
    private bool _disposing;
    internal RoutineData DataForTests => _data;
    internal Rect BoardBoundsForTests { get; private set; }
    internal int CardCountForTests => _cards.Children.Count;
    internal bool CardsMeasuredForTests => _cards.Children.OfType<Border>().All(b=>b.ActualWidth>0&&b.ActualHeight>0);
    internal string CardMetricsForTests => $"window={IsVisible},root={_root.IsVisible},canvas={_cards.ActualWidth}x{_cards.ActualHeight},visible={_cards.IsVisible},visibility={_cards.Visibility}; "+string.Join(";",_cards.Children.OfType<Border>().Select(b=>$"{b.Width}x{b.Height}/{b.ActualWidth}x{b.ActualHeight},visible={b.IsVisible}"));
    internal void SetTableScale(double scale) { _data.Scale=Math.Clamp(scale,.35,1.4);LayoutBoard();Save(); }
    private static readonly Brush Bg = Brush("#171A19"), Text = Brush("#EEF1EA"), Muted = Brush("#949D94"), Line = Brush("#30362F"), Accent = Brush("#C6D8AE");
    internal RoutineWindow() : this(RoutineStore.Load()) { }
    internal RoutineWindow(RoutineData data)
    {
        foreach(var day in RoutineStore.Days)data.Week.TryAdd(day,new());
        RoutineStore.UpgradeReminders(data);
        _data = data; Title = "Daily plan"; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Segoe UI"); Foreground = Text;
        _board.Background = Bg; _board.BorderBrush = Line; _view.Child = _board;
        _root.Children.Add(_dim); _root.Children.Add(_cards); _root.Children.Add(_view); Content = _root;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Flush(); };
        _snoozeTimer.Tick += (_, _) => { _snoozeTimer.Stop(); ShowPlan(_reminderView); };
        Loaded += (_, _) => LayoutBoard(); SizeChanged += (_, _) => LayoutBoard();
        Closing += (_, e) => { Flush(); if (!_disposing) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => { _saveTimer.Stop(); _snoozeTimer.Stop(); _imageCache.Clear(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        WindowInterop.HideFromAltTab(this); BuildDay();
    }
    internal void ShowPlan(IReadOnlyList<RoutineReminder>? reminders=null)
    {
        _reminderView=reminders;
        _snoozeTimer.Stop(); _day = DateTime.Today.DayOfWeek;
        Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        BuildDay(); Show(); LayoutBoard(); WindowInterop.EnsureTopmost(this); Activate();
    }
    internal void Dispose() { _disposing = true; Close(); }
    internal void Flush() { _saveTimer.Stop(); RoutineStore.Save(_data); }
    private void Save() { _saveTimer.Stop(); _saveTimer.Start(); }
    private static SolidColorBrush Brush(string hex)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Colors.White); }
    }
    private static TextBlock Label(string text, double size = 13, Brush? color = null) => new() { Text = text, FontSize = size, Foreground = color ?? Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static Button Button(string text, Action action, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(11,7,11,7), Margin = new Thickness(3), Background = primary ? Accent : Bg, Foreground = primary ? Bg : Text, BorderBrush = Line, BorderThickness = new Thickness(1), FontSize = 12, Cursor = Cursors.Arrow };
        b.Click += (_, _) => action(); return b;
    }
    private static TextBox Input(string text = "", double width = double.NaN) => new() { Text = text, Width = width, Background = Brush("#20251F"), Foreground = Text, CaretBrush = Text, BorderBrush = Line, Padding = new Thickness(7), Margin = new Thickness(3), FontSize = 13 };
    private static ScrollViewer Scroll(UIElement child) => new() { Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static StackPanel Horizontal(params UIElement[] children) { var panel = new StackPanel { Orientation = Orientation.Horizontal }; foreach(var c in children)panel.Children.Add(c);return panel; }
    private static string ColorFor(string cat) => cat switch { "work" => "#9FB4D2", "grow" => "#D4BD8E", "play" => "#9FBDA9", "study" => "#D3A09A", _ => "#A8B1A5" };
    private DateTime SelectedDate => DateTime.Today.AddDays((Array.IndexOf(RoutineStore.Days,_day)-Array.IndexOf(RoutineStore.Days,DateTime.Today.DayOfWeek)));
    private string TaskKey(string id) => SelectedDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+":"+id;
    private Grid Shell(string heading, params UIElement[] buttons)
    {
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new DockPanel { Margin = new Thickness(25,18,25,14) };
        var actions = Horizontal(buttons); DockPanel.SetDock(actions,Dock.Right); top.Children.Add(actions);
        var title = Label(heading,28);title.FontFamily=new FontFamily("Bahnschrift");top.Children.Add(title);root.Children.Add(top);return root;
    }
    private void BuildDay()
    {
        IsEditingSettings=false;
        var fullDay=Button("Full day",()=>{_reminderView=null;BuildDay();});fullDay.Visibility=_reminderView==null?Visibility.Collapsed:Visibility.Visible;
        var grid = Shell(_reminderView!=null?"Selected tasks":_day == DateTime.Today.DayOfWeek ? "Today" : _day.ToString(),fullDay, Button("Week ↗",OpenWeek),Button("Settings",()=>BuildSettings()),Button("×",Hide));
        var body = new Grid(); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });body.RowDefinitions.Add(new RowDefinition());Grid.SetRow(body,1);grid.Children.Add(body);
        var daybar = new DockPanel { Margin = new Thickness(25,0,25,14) };
        var date=Label(SelectedDate.ToString("dddd, d MMMM",CultureInfo.GetCultureInfo("en-GB")),12,Muted);DockPanel.SetDock(date,Dock.Right);daybar.Children.Add(date);
        var days=Horizontal();foreach(var day in RoutineStore.Days){var selected=day;var b=Button(day.ToString()[..3],()=>{_day=selected;BuildDay();});if(day==_day)b.Foreground=Accent;days.Children.Add(b);}daybar.Children.Add(days);body.Children.Add(daybar);
        var columns=new Grid();columns.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(47,GridUnitType.Star) });columns.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(53,GridUnitType.Star) });Grid.SetRow(columns,1);body.Children.Add(columns);
        var blocks=VisibleBlocks;
        var timeline=Timeline(blocks,390,40);var schedule=Scroll(timeline);schedule.Margin=new Thickness(23,8,16,10);columns.Children.Add(schedule);
        var tasks=new StackPanel { Margin=new Thickness(20,8,25,12) };var total=blocks.Sum(b=>VisibleTasks(b).Count);var done=blocks.SelectMany(VisibleTasks).Count(t=>_data.Completed.GetValueOrDefault(TaskKey(t.Id)));
        var progress=Label($"{done} / {total}",11,Muted);var head=new DockPanel { Margin=new Thickness(0,0,0,20) };var edit=Button("Edit",()=>BuildSettings("Routine"));DockPanel.SetDock(edit,Dock.Right);head.Children.Add(edit);DockPanel.SetDock(progress,Dock.Right);head.Children.Add(progress);head.Children.Add(Label("To do",16));tasks.Children.Add(head);
        if(blocks.Count==0)tasks.Children.Add(Label(_reminderView==null?"Add your routine in Settings → Routine.":"No selected tasks for this day.",14,Muted));
        foreach(var block in blocks)
        {
            var group=new StackPanel { Margin=new Thickness(0,0,0,22) };group.Children.Add(Label(block.Start+"   "+block.Title,11,Brush(ColorFor(block.Category))));
            foreach(var task in VisibleTasks(block))
            {
                var check=new CheckBox { Content=Label(task.Text,13),IsChecked=_data.Completed.GetValueOrDefault(TaskKey(task.Id)),Margin=new Thickness(0,9,0,0),Foreground=Text };
                check.Click+=(_,_)=>{_data.Completed[TaskKey(task.Id)]=check.IsChecked==true;Save();var checks=blocks.SelectMany(VisibleTasks);progress.Text=$"{checks.Count(t=>_data.Completed.GetValueOrDefault(TaskKey(t.Id)))} / {total}";};group.Children.Add(check);
            }
            var input=Input();input.Width=285;input.ToolTip="Add task";
            void Add(){if(string.IsNullOrWhiteSpace(input.Text))return;block.Tasks.Add(new RoutineTask { Text=input.Text.Trim() });Save();BuildDay();}
            input.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){Add();e.Handled=true;}};if(_reminderView==null)group.Children.Add(Horizontal(input,Button("+",Add)));tasks.Children.Add(group);
        }
        var taskScroll=Scroll(tasks);Grid.SetColumn(taskScroll,1);columns.Children.Add(taskScroll);
        var footer=new DockPanel { Margin=new Thickness(22,12,22,15) };var actions=Horizontal(Button("Dismiss",Hide),Button("Snooze 10m",()=>{Hide();_snoozeTimer.Interval=TimeSpan.FromMinutes(10);_snoozeTimer.Start();}),Button("Start day ↗",Hide,true));DockPanel.SetDock(actions,Dock.Right);footer.Children.Add(actions);footer.Children.Add(Label(string.Join(" · ",_data.Reminders!.Where(r=>r.Enabled).Select(r=>r.Time)),12,Muted));Grid.SetRow(footer,2);grid.Children.Add(footer);_board.Child=grid;
    }
    private Canvas Timeline(List<RoutineBlock> blocks,double width,double hour,bool week=false)
    {
        var all=_data.Week.Values.SelectMany(x=>x).ToArray();var start=Math.Min(360,all.Length==0?360:all.Min(b=>RoutineStore.Minute(b.Start)))/60*60;
        var end=(int)Math.Ceiling(Math.Max(1380,all.Length==0?1380:all.Max(b=>RoutineStore.Minute(b.End)))/60d)*60;
        var canvas=new Canvas { Width=width,Height=(end-start)/60d*hour+16,Background=Bg };
        for(var m=start;m<=end;m+=60){var y=(m-start)/60d*hour;var line=new Rectangle { Width=width-42,Height=1,Fill=Line };Canvas.SetLeft(line,40);Canvas.SetTop(line,y);canvas.Children.Add(line);var label=Label($"{m/60:00}:00",9,Muted);Canvas.SetTop(label,Math.Max(0,y-7));canvas.Children.Add(label);}
        foreach(var b in blocks)
        {
            var height=Math.Max(5,(RoutineStore.Minute(b.End)-RoutineStore.Minute(b.Start))/60d*hour-2);
            var text=new StackPanel { Margin=new Thickness(week?5:9,3,4,2) };text.Children.Add(Label(b.Start+"–"+b.End,week?8:10,Brush(ColorFor(b.Category))));var title=Label(b.Title,week?10:12);title.FontWeight=FontWeights.SemiBold;text.Children.Add(title);
            var card=new Border { Width=width-46,Height=height,Background=new SolidColorBrush(Color.FromArgb(24,((SolidColorBrush)Brush(ColorFor(b.Category))).Color.R,((SolidColorBrush)Brush(ColorFor(b.Category))).Color.G,((SolidColorBrush)Brush(ColorFor(b.Category))).Color.B)),BorderBrush=Brush(ColorFor(b.Category)),BorderThickness=new Thickness(2,0,0,0),Child=text,ClipToBounds=true,ToolTip=b.Start+"–"+b.End+" · "+b.Title };
            Canvas.SetLeft(card,43);Canvas.SetTop(card,(RoutineStore.Minute(b.Start)-start)/60d*hour);canvas.Children.Add(card);
        }
        return canvas;
    }
    private void OpenWeek()
    {
        var window=new Window { Title="Weekly routine",Owner=this,WindowStartupLocation=WindowStartupLocation.CenterScreen,Width=Math.Min(1440,SystemParameters.WorkArea.Width-40),Height=Math.Min(900,SystemParameters.WorkArea.Height-40),Background=Bg,Foreground=Text,Topmost=true };
        var grid=new Grid { Margin=new Thickness(18) };grid.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });grid.RowDefinitions.Add(new RowDefinition());var top=new DockPanel();var close=Button("Close",window.Close);DockPanel.SetDock(close,Dock.Right);top.Children.Add(close);top.Children.Add(Label("Weekly routine",24));grid.Children.Add(top);
        var columns=Horizontal();foreach(var d in RoutineStore.Days){var col=new StackPanel { Margin=new Thickness(0,15,8,0) };col.Children.Add(Label(d.ToString(),13,d==DateTime.Today.DayOfWeek?Accent:Text));col.Children.Add(Timeline(_data.Week[d],170,35,true));columns.Children.Add(col);}var scroll=new ScrollViewer { Content=columns,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto };Grid.SetRow(scroll,1);grid.Children.Add(scroll);window.Content=grid;window.ShowDialog();
    }
    private void LayoutBoard()
    {
        if(ActualWidth<1||ActualHeight<1)return;_dim.Width=ActualWidth;_dim.Height=ActualHeight;_dim.Background=new SolidColorBrush(Color.FromArgb((byte)(_data.Dim*255),0,0,0));
        _cards.Width=ActualWidth;_cards.Height=ActualHeight;
        // Reserve bands even at large scales so background cards always have room.
        var area=Rect.Intersect(new Rect(Left,Top,ActualWidth,ActualHeight),SystemParameters.WorkArea);
        if(area.IsEmpty)area=new Rect(Left,Top,ActualWidth,ActualHeight);
        var scale=Math.Min(_data.Scale,Math.Min((area.Width-100)/940,(area.Height-160)/720));scale=Math.Max(.15,scale);
        var w=940*scale;var h=720*scale;var left=area.Left-Left+(area.Width-w)/2;var top=area.Top-Top+(area.Height-h)/2;
        _view.Width=w;_view.Height=h;Canvas.SetLeft(_view,left);Canvas.SetTop(_view,top);BoardBoundsForTests=new Rect(left,top,w,h);RenderCards();
    }
    private BitmapImage Image(string file)
    {
        if(_imageCache.TryGetValue(file,out var image))return image;
        image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=640;image.UriSource=new Uri(RoutineStore.ImagePath(file));image.EndInit();image.Freeze();_imageCache[file]=image;return image;
    }
    private void RenderCards()
    {
        _cards.Children.Clear();
        var random=new Random(_data.LayoutSeed);var ordered=_data.Cards.OrderBy(_=>random.Next()).ToArray();
        var aspects=ordered.Select(card=>{try{if(!string.IsNullOrEmpty(card.ImageFile)){var image=Image(card.ImageFile);return 1/(.92/(image.PixelWidth/(double)image.PixelHeight)+.30);}}catch{}return 1.1;}).ToArray();
        var placements=RoutineCardLayout.Place(new Size(Math.Max(1,ActualWidth),Math.Max(1,ActualHeight)),BoardBoundsForTests,_data.Cards.Count,_data.LayoutSeed,aspects);
        for(var i=0;i<placements.Count;i++)
        {
            var card=ordered[i];var p=placements[i];var bounds=p.Bounds;BitmapImage? photo=null;
            if(!string.IsNullOrEmpty(card.ImageFile))try{photo=Image(card.ImageFile);bounds=RoutineCardLayout.FitPhoto(bounds,photo.PixelWidth/(double)photo.PixelHeight);}catch{}
            var content=new Grid();content.RowDefinitions.Add(new RowDefinition());
            var captionHeight=string.IsNullOrEmpty(card.ImageFile)?0:bounds.Width*.22;content.RowDefinitions.Add(new RowDefinition { Height=new GridLength(captionHeight) });
            if(!string.IsNullOrEmpty(card.ImageFile))
            {
                if(photo!=null)content.Children.Add(new System.Windows.Controls.Image { Source=photo,Stretch=Stretch.Uniform });
                else content.Children.Add(Label("Image unavailable",10,Bg));
                var caption=Label(card.Caption,18,Brush(card.CaptionColor));caption.FontFamily=new FontFamily(card.CaptionFont);caption.TextAlignment=TextAlignment.Center;caption.Margin=new Thickness(2,3,2,0);var fit=new Viewbox { Child=caption,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly };Grid.SetRow(fit,1);content.Children.Add(fit);
            }
            else
            {
                var text=Label(card.Text,18,Brush(card.CaptionColor));text.Width=210;text.FontFamily=new FontFamily(card.CaptionFont);content.Children.Add(new Viewbox { Child=text,Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly });
            }
            var border=new Border { Width=bounds.Width,Height=bounds.Height,Background=Brush(card.FrameColor),Padding=new Thickness(bounds.Width*.04),Child=content,RenderTransformOrigin=new Point(.5,.5),RenderTransform=new RotateTransform(p.Angle),ClipToBounds=true };
            Canvas.SetLeft(border,bounds.Left);Canvas.SetTop(border,bounds.Top);_cards.Children.Add(border);
        }
        _cards.Measure(new Size(ActualWidth,ActualHeight));
        _cards.Arrange(new Rect(0,0,ActualWidth,ActualHeight));
        _cards.UpdateLayout();
    }
}
