using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using NativeWidget;
using NativeWidget.Models;
using NativeWidget.Services;

internal static class RoutineChecks
{
    internal static void Run()
    {
        var previous=Environment.GetEnvironmentVariable("NATIVEWIDGET_DATA_DIR");
        var previousInstance=Environment.GetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX");
        var folder=Path.Combine(Path.GetTempPath(),"nativewidget-routine-"+Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR",folder);
        Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX",Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(RoutineStore.Folder);File.WriteAllText(Path.Combine(RoutineStore.Folder,"import-source.json"),"{\"mon\":[{\"s\":\"09:00\",\"e\":\"10:00\",\"c\":\"work\",\"t\":\"Generic import fixture\"}]}");
            var migrated=RoutineStore.Load();Require(File.Exists(RoutineStore.FilePath)&&migrated.Week[DayOfWeek.Monday].Count==1,"Local import migrates once to persistent native storage");
            var data=new RoutineData();foreach(var day in RoutineStore.Days)data.Week[day]=new(){new RoutineBlock { Title="Example focus",Start="09:00",End="11:00",Tasks=new(){new RoutineTask{Text="Example task"}} }};
            data.Cards.Add(new RoutineCard { Text="Example note" });
            data.Cards.Add(new RoutineCard { Text="Another note" });
            RoutineStore.Save(data);var restored=RoutineStore.Load();Require(restored.Week.Count==7&&restored.Week[DayOfWeek.Monday][0].Tasks.Count==1,"Routine round trip");
            data.ReminderEnabled=true;var now=new DateTime(2030,3,5,7,0,0);Require(RoutineReminderService.IsDue(data,now),"Morning catch up");data.LastShownDate="2030-03-05";Require(!RoutineReminderService.IsDue(data,now),"At most once per date");data.LastShownDate="";Require(!RoutineReminderService.IsDue(data,now.Date.AddHours(19)),"No evening catch up");data.ReminderEnabled=false;
            var task=XDocument.Parse(RoutineReminderService.TaskXml("C:\\Example & test\\Widget.exe","06:45","S-1-5-21-example"));XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
            Require(task.Descendants(ns+"Command").Single().Value=="C:\\Example & test\\Widget.exe"&&task.Descendants(ns+"MultipleInstancesPolicy").Single().Value=="Parallel","Scheduler XML escapes paths and permits future daily launches");
            foreach(var size in new[]{new Size(1920,1080),new Size(1366,768),new Size(900,850)})foreach(var count in new[]{1,3,40,120})
            {
                var board=new Rect(size.Width*.2,size.Height*.13,size.Width*.6,size.Height*.74);var cards=RoutineCardLayout.Place(size,board,count,8);Require(cards.Count==count,"All cards placed");
                foreach(var card in cards){var r=card.Bounds;var t=Math.Abs(card.Angle*Math.PI/180);var w=r.Width*Math.Cos(t)+r.Height*Math.Sin(t);var h=r.Width*Math.Sin(t)+r.Height*Math.Cos(t);var outer=new Rect(r.Left+(r.Width-w)/2,r.Top+(r.Height-h)/2,w,h);Require(!outer.IntersectsWith(board)&&outer.Left>=0&&outer.Top>=0&&outer.Right<=size.Width&&outer.Bottom<=size.Height,"Rotated cards stay outside the board and on screen");}
            }
            var invalid=new RoutineData();invalid.Week[DayOfWeek.Monday]=new(){new RoutineBlock{Start="10:00",End="09:00"}};try{RoutineStore.Save(invalid);throw new Exception("Invalid routine accepted");}catch(InvalidDataException){}
            var app=new App();app.InitializeComponent();app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var image=Path.Combine(folder,"fixture.png");var bitmap=BitmapSource.Create(4,4,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)200,64).ToArray(),16);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(image))encoder.Save(stream);
            var photo=new RoutineCard{ImageFile=RoutineStore.CopyImage(image),Caption="Example caption",CaptionFont="Georgia",FrameColor="#F3F0E8",CaptionColor="#28301F"};data.Cards.Add(photo);
            var window=new RoutineWindow(data){Left=0,Top=0,Width=1280,Height=850,ShowActivated=false};window.Show();window.UpdateLayout();Pump(120);
            Require(window.CardCountForTests==3,"Native text and Polaroid cards render");var width=window.BoardBoundsForTests.Width;window.SetTableScale(.4);window.UpdateLayout();Pump(100);Require(window.BoardBoundsForTests.Width<width,$"Table scale changes the whole board: before={width}, after={window.BoardBoundsForTests.Width}");window.SetTableScale(1);window.UpdateLayout();
            var render=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);render.Render((Visual)window.Content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(render));var screenshot=Path.Combine(folder,"native-routine.png");using(var stream=File.Create(screenshot))png.Save(stream);
            Require(window.CardsMeasuredForTests,"Background cards must have a rendered size: "+window.CardMetricsForTests);
            var settings=Find<Button>(window).First(b=>b.Content as string=="Settings");settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();Require(Find<TabControl>(window).Single().Items.Count==3,"General, cards and routine controls available");
            var settingsImage=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);settingsImage.Render((Visual)window.Content);var settingsPng=new PngBitmapEncoder();settingsPng.Frames.Add(BitmapFrame.Create(settingsImage));using(var stream=File.Create(Path.Combine(folder,"native-routine-settings.png")))settingsPng.Save(stream);
            File.WriteAllText(Path.Combine(folder,"scheduler-fixture.xml"),RoutineReminderService.TaskXml(Environment.ProcessPath!,"06:45",System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value));
            window.Flush();var final=RoutineStore.Load();Require(final.Cards.Last().Caption=="Example caption"&&final.Cards.Last().CaptionFont=="Georgia"&&File.Exists(RoutineStore.ImagePath(final.Cards.Last().ImageFile)),"Photo caption, font, colors and owned image persist locally");
            window.Dispose();app.Shutdown();Console.WriteLine("PASS routine: private storage, scheduler policy, no-overlap card layout, native UI, scale and Polaroid persistence.");Console.WriteLine("PREVIEW="+screenshot);
        }
        finally{Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR",previous);Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX",previousInstance);}
    }
    private static IEnumerable<T> Find<T>(DependencyObject root)where T:DependencyObject
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var found in Find<T>(child))yield return found;}}
    private static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
