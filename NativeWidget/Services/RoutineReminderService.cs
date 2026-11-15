using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using NativeWidget.Models;

namespace NativeWidget.Services;

internal static class RoutineReminderService
{
    private const string TaskName = "NativeWidget Morning Routine";
    private static DispatcherTimer? _timer;
    private static CancellationTokenSource? _stop;
    private static string PipeName => "NativeWidget-Routine-" + WindowsIdentity.GetCurrent().User!.Value + Environment.GetEnvironmentVariable("NATIVEWIDGET_INSTANCE_SUFFIX");
    internal static void Start()
    {
        _stop = new CancellationTokenSource(); var token=_stop.Token;
        _ = Task.Run(async () =>
        {
            while(!token.IsCancellationRequested)
            {
                try
                {
                    using var server=new NamedPipeServerStream(PipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);using var reader=new StreamReader(server);
                    var command=await reader.ReadLineAsync(token);
                    if(command is "show" or "due")_ = Application.Current.Dispatcher.BeginInvoke(new Action(()=>
                    {if(Application.Current.MainWindow is MainWindow main){if(command=="show")main.ShowRoutine();else main.CheckRoutineReminder(DateTime.Now);}}));
                }
                catch(OperationCanceledException){break;}
                catch(IOException){await Task.Delay(300,token).ConfigureAwait(false);}
            }
        },token);
        _timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(30) };
        _timer.Tick+=(_,_)=>{if(Application.Current.MainWindow is MainWindow main)main.CheckRoutineReminder(DateTime.Now);};_timer.Start();
    }
    internal static bool Send(string command)
    {
        try{using var client=new NamedPipeClientStream(".",PipeName,PipeDirection.Out,PipeOptions.CurrentUserOnly);client.Connect(2000);using var writer=new StreamWriter(client){AutoFlush=true};writer.WriteLine(command);return true;}
        catch(IOException){return false;}catch(TimeoutException){return false;}
    }
    internal static void Stop(){_timer?.Stop();_stop?.Cancel();_stop?.Dispose();_stop=null;}
    internal static bool IsDue(RoutineData data,DateTime now)
    {
        if(data.Reminders!=null)return DueReminders(data,now).Count>0;
        return Due(new RoutineReminder { Enabled=data.ReminderEnabled,Time=data.ReminderTime,LastShownDate=data.LastShownDate },now);
    }
    private static bool Due(RoutineReminder row,DateTime now)
    {var due=now.Date.AddMinutes(RoutineStore.Minute(row.Time));return row.Enabled&&row.LastShownDate!=now.ToString("yyyy-MM-dd")&&now>=due&&now<due.AddHours(4);}
    internal static List<RoutineReminder> DueReminders(RoutineData data,DateTime now)
    {
        RoutineStore.UpgradeReminders(data);
        return data.Reminders!.Where(r=>Due(r,now)&&(r.WholeDay||data.Week.GetValueOrDefault(now.DayOfWeek,new()).Any(b=>r.BlockIds.Contains(b.Id)||b.Tasks.Any(t=>r.TaskIds.Contains(t.Id))))).ToList();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    internal static bool IsUnlocked()
    {var desktop=OpenInputDesktop(0,false,0x0100);if(desktop==IntPtr.Zero)return false;CloseDesktop(desktop);return true;}
    internal static string TaskXml(string exe,string time,string user)
        =>TaskXml(exe,new[]{new RoutineReminder{Time=time}},user);
    internal static string TaskXml(string exe,IEnumerable<RoutineReminder> reminders,string user)
    {
        var times=reminders.Where(r=>r.Enabled).Select(r=>RoutineStore.Minute(r.Time)).Distinct().Order().ToArray();if(times.Any(m=>m>=1440))throw new InvalidDataException("Use a time before midnight.");
        XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name,object? content)=>new(ns+name,content);
        return new XDocument(new XElement(ns+"Task",new XAttribute("version","1.2"),
            E("Triggers",times.Select(minutes=>E("CalendarTrigger",new object[]{E("StartBoundary",DateTime.Today.AddMinutes(minutes).ToString("yyyy-MM-ddTHH:mm:ss")),E("Enabled","true"),E("ScheduleByDay",E("DaysInterval",1))}))),
            E("Principals",new XElement(ns+"Principal",new XAttribute("id","User"),E("UserId",user),E("LogonType","InteractiveToken"),E("RunLevel","LeastPrivilege"))),
            E("Settings",new object[]{E("MultipleInstancesPolicy","Parallel"),E("DisallowStartIfOnBatteries","false"),E("StopIfGoingOnBatteries","false"),E("StartWhenAvailable","true"),E("ExecutionTimeLimit","PT0S")}),
            new XElement(ns+"Actions",new XAttribute("Context","User"),E("Exec",new object[]{E("Command",exe),E("Arguments","--daily-brief"),E("WorkingDirectory",Path.GetDirectoryName(exe))})))).ToString();
    }
    internal static void Configure(bool enabled,string time)
        =>Configure(new[]{new RoutineReminder{Enabled=enabled,Time=time}});
    internal static void Configure(IEnumerable<RoutineReminder> reminders)
    {
        var rows=reminders.ToArray();
        var type=Type.GetTypeFromProgID("Schedule.Service")??throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
        dynamic service=Activator.CreateInstance(type)!;service.Connect();dynamic folder=service.GetFolder("\\");
        if(!rows.Any(r=>r.Enabled)){try{folder.DeleteTask(TaskName,0);}catch(System.Runtime.InteropServices.COMException ex)when((uint)ex.HResult==0x80070002){}return;}
        var xml=TaskXml(Environment.ProcessPath!,rows,WindowsIdentity.GetCurrent().User!.Value);
        folder.RegisterTask(TaskName,xml,6,null,null,3,null);
    }
}
