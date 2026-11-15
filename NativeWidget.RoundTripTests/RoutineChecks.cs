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
            var legacy=new RoutineData{ReminderEnabled=true,ReminderTime="08:15",LastShownDate="2030-03-05"};RoutineStore.UpgradeReminders(legacy);var legacyRow=legacy.Reminders!.Single();Require(legacyRow.Enabled&&legacyRow.Time=="08:15"&&legacyRow.LastShownDate=="2030-03-05","Original reminder state survives migration");
            var data=new RoutineData();foreach(var day in RoutineStore.Days)data.Week[day]=new(){new RoutineBlock { Title="Example focus",Start="09:00",End="11:00",Tasks=new(){new RoutineTask{Text="Example task"}} }};
            data.Cards.Add(new RoutineCard { Text="Example note" });
            data.Cards.Add(new RoutineCard { Text="Another note" });
            RoutineStore.Save(data);var restored=RoutineStore.Load();Require(restored.Week.Count==7&&restored.Week[DayOfWeek.Monday][0].Tasks.Count==1,"Routine round trip");
            data.ReminderEnabled=true;var now=new DateTime(2030,3,5,7,0,0);Require(RoutineReminderService.IsDue(data,now),"Morning catch up");data.LastShownDate="2030-03-05";Require(!RoutineReminderService.IsDue(data,now),"At most once per date");data.LastShownDate="";Require(!RoutineReminderService.IsDue(data,now.Date.AddHours(19)),"No evening catch up");data.ReminderEnabled=false;
            var task=XDocument.Parse(RoutineReminderService.TaskXml("C:\\Example & test\\Widget.exe","06:45","S-1-5-21-example"));XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
            Require(task.Descendants(ns+"Command").Single().Value=="C:\\Example & test\\Widget.exe"&&task.Descendants(ns+"MultipleInstancesPolicy").Single().Value=="Parallel","Scheduler XML escapes paths and permits future daily launches");
            var originalReminder=new RoutineReminder { Time="06:45" };var laterReminder=new RoutineReminder { Time="12:00" };
            data.Reminders=new(){originalReminder,laterReminder};originalReminder.LastShownDate="2030-03-05";
            Require(RoutineReminderService.DueReminders(data,new DateTime(2030,3,5,12,5,0)).Single()==laterReminder,"An earlier reminder does not suppress a second reminder that day");
            laterReminder.LastShownDate="2030-03-05";Require(!RoutineReminderService.IsDue(data,new DateTime(2030,3,5,12,6,0)),"Each reminder fires once per date");
            var multi=XDocument.Parse(RoutineReminderService.TaskXml("C:\\Example\\Widget.exe",data.Reminders,"test-user"));Require(multi.Descendants(ns+"CalendarTrigger").Count()==2,"Multiple times create multiple scheduler triggers");
            var selectedTask=data.Week[DayOfWeek.Tuesday][0].Tasks[0];laterReminder.WholeDay=false;laterReminder.TaskIds.Add(selectedTask.Id);laterReminder.LastShownDate="";
            Require(RoutineReminderService.DueReminders(data,new DateTime(2030,3,6,12,5,0)).All(r=>r.Id!=laterReminder.Id),"Selected tasks are not shown on an unrelated weekday");
            Require(RoutineReminderService.DueReminders(data,new DateTime(2030,3,5,12,5,0)).Contains(laterReminder),"Selected tasks show on their own weekday");
            data.Reminders.Clear();RoutineStore.Save(data);Require(RoutineStore.Load().Reminders!.Count==0,"Removing every reminder persists without restoring the legacy slot");data.Reminders=new(){new RoutineReminder{Enabled=false}};
            var edited=RoutineStore.EditTaskLines(new(){selectedTask},"Renamed task");Require(edited[0].Id==selectedTask.Id,"Renaming a task preserves its reminder selection ID");
            foreach(var ratio in new[]{.25,1d,4d}){var photoBounds=RoutineCardLayout.FitPhoto(new Rect(10,20,200,180),ratio);var imageWidth=photoBounds.Width*.92;var imageHeight=photoBounds.Height-photoBounds.Width*.30;Require(Math.Abs(imageWidth/imageHeight-ratio)<.00001,"Polaroid image area preserves portrait and landscape ratios without side gaps");}
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
            var todayBlock=data.Week[DateTime.Today.DayOfWeek][0];todayBlock.Tasks.Add(new RoutineTask{Text="A different task"});var chosen=new RoutineReminder{WholeDay=false,TaskIds=new(){todayBlock.Tasks[0].Id}};
            window.ShowPlan(new[]{chosen});Require(window.VisibleBlocks.Count==1&&window.VisibleTasks(todayBlock).Count==1,"Selected-task view excludes other tasks and blocks");window.ShowPlan();Require(window.VisibleTasks(todayBlock).Count==2,"Manual opening restores the full routine");window.Width=1280;window.Height=850;window.Left=0;window.Top=0;window.UpdateLayout();
            Require(window.CardCountForTests==3,"Native text and Polaroid cards render");var width=window.BoardBoundsForTests.Width;window.SetTableScale(.4);window.UpdateLayout();Pump(100);Require(window.BoardBoundsForTests.Width<width,$"Table scale changes the whole board: before={width}, after={window.BoardBoundsForTests.Width}");window.SetTableScale(1);window.UpdateLayout();
            var render=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);render.Render((Visual)window.Content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(render));var screenshot=Path.Combine(folder,"native-routine.png");using(var stream=File.Create(screenshot))png.Save(stream);
            Require(window.CardsMeasuredForTests,"Background cards must have a rendered size: "+window.CardMetricsForTests);
            var settings=Find<Button>(window).First(b=>b.Content as string=="Settings");settings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();Require(Find<TabControl>(window).Single().Items.Count==3,"General, cards and routine controls available");
            var settingsImage=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);settingsImage.Render((Visual)window.Content);var settingsPng=new PngBitmapEncoder();settingsPng.Frames.Add(BitmapFrame.Create(settingsImage));using(var stream=File.Create(Path.Combine(folder,"native-routine-settings.png")))settingsPng.Save(stream);
            File.WriteAllText(Path.Combine(folder,"scheduler-fixture.xml"),RoutineReminderService.TaskXml(Environment.ProcessPath!,new[]{new RoutineReminder{Time="06:45"},new RoutineReminder{Time="12:00"}},System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value));
            window.Flush();var final=RoutineStore.Load();Require(final.Cards.Last().Caption=="Example caption"&&final.Cards.Last().CaptionFont=="Georgia"&&File.Exists(RoutineStore.ImagePath(final.Cards.Last().ImageFile)),"Photo caption, font, colors and owned image persist locally");
            var launcher=new MainWindow {CodexBackendEnabled=false};launcher.Show();((Button)launcher.FindName("BtnRoutine")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(450);Require(((System.Windows.Controls.Primitives.Popup)launcher.FindName("LauncherPopup")).IsOpen&&launcher.RoutineWindowForTests?.IsVisible==true,"Routine menu action must keep the launcher open and show the plan");launcher.RoutineWindowForTests!.Hide();launcher.Close();
            window.Dispose();app.Shutdown();Console.WriteLine("PASS routine: private storage, scheduler policy, no-overlap card layout, native UI, scale and Polaroid persistence.");Console.WriteLine("PREVIEW="+screenshot);
        }
        finally{Environment.SetEnvironmentVariable("NATIVEWIDGET_DATA_DIR",previous);Environment.SetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX",previousInstance);}
    }
    private static IEnumerable<T> Find<T>(DependencyObject root)where T:DependencyObject
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T item)yield return item;foreach(var found in Find<T>(child))yield return found;}}
    private static void Pump(int ms){var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
