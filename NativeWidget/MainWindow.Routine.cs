using System.Windows;
using NativeWidget.Services;

namespace NativeWidget;

public partial class MainWindow
{
    private RoutineWindow? _routineWindow;
    internal void ShowRoutine()
    {
        try{_routineWindow??=new RoutineWindow();_routineWindow.ShowPlan();}
        catch(Exception ex){MessageBox.Show(this,ex.Message,"Routine could not be opened");}
    }
    internal void CheckRoutineReminder(DateTime now)
    {
        try
        {
            var data=_routineWindow?.DataForTests??RoutineStore.Load();
            if(!RoutineReminderService.IsDue(data,now)||!RoutineReminderService.IsUnlocked())return;
            data.LastShownDate=now.ToString("yyyy-MM-dd");RoutineStore.Save(data);ShowRoutine();
        }
        catch(System.IO.IOException) { }
        catch(System.Text.Json.JsonException) { }
    }
    private void Routine_Click(object sender,RoutedEventArgs e){LauncherPopup.IsOpen=false;ShowRoutine();}
}
