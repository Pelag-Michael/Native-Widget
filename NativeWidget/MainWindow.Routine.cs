using System.Windows;
using NativeWidget.Services;

namespace NativeWidget;

public partial class MainWindow
{
    private RoutineWindow? _routineWindow;
    private bool _routineMenuPinned;
    internal RoutineWindow? RoutineWindowForTests => _routineWindow;
    private RoutineWindow EnsureRoutineWindow(NativeWidget.Models.RoutineData? data=null)
    {
        if(_routineWindow!=null)return _routineWindow;
        _routineWindow=data==null?new RoutineWindow():new RoutineWindow(data);
        _routineWindow.IsVisibleChanged+=(_,_)=>{if(!_routineWindow.IsVisible&&_routineMenuPinned){_routineMenuPinned=false;_launcherCloseTimer.Start();}};
        return _routineWindow;
    }
    internal void ShowRoutine()
    {
        try
        {
            EnsureRoutineWindow().ShowPlan();
        }
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Routine could not be opened");}
    }
    internal void CheckRoutineReminder(DateTime now)
    {
        if(_routineWindow?.IsVisible==true&&_routineWindow.IsEditingSettings)return;
        try
        {
            var data=_routineWindow?.DataForTests??RoutineStore.Load();
            var due=RoutineReminderService.DueReminders(data,now);
            if(due.Count==0||!RoutineReminderService.IsUnlocked())return;
            foreach(var reminder in due)reminder.LastShownDate=now.ToString("yyyy-MM-dd");RoutineStore.Save(data);
            EnsureRoutineWindow(data).ShowPlan(due);
        }
        catch(System.IO.IOException) { }
        catch(System.Text.Json.JsonException) { }
    }
    private void Routine_Click(object sender,RoutedEventArgs e)
    {
        _routineMenuPinned=true;_launcherCloseTimer.Stop();ShowLauncher();e.Handled=true;
        Dispatcher.BeginInvoke(new Action(()=>{ShowRoutine();EnsureLauncherTopmost();}));
    }
}
