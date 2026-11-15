using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

internal sealed partial class RoutineWindow
{
    private UIElement ReminderSettings()
    {
        RoutineStore.UpgradeReminders(_data);
        var working=JsonSerializer.Deserialize<List<RoutineReminder>>(JsonSerializer.Serialize(_data.Reminders))!;
        var panel=new StackPanel { Margin=new Thickness(0,18,0,16) };panel.Children.Add(Label("Reminders",16));
        var rows=new StackPanel();panel.Children.Add(rows);
        void Render()
        {
            rows.Children.Clear();foreach(var reminder in working.ToArray())
            {
                var row=new StackPanel { Margin=new Thickness(0,12,0,12) };
                var enabled=new CheckBox { Content="Enabled",IsChecked=reminder.Enabled,Foreground=Text,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5) };enabled.Click+=(_,_)=>reminder.Enabled=enabled.IsChecked==true;
                var time=Input(reminder.Time,95);time.ToolTip="HH:mm";time.TextChanged+=(_,_)=>{reminder.Time=time.Text;reminder.LastShownDate="";};
                row.Children.Add(Horizontal(time,enabled,Button("Remove",()=>{working.Remove(reminder);Render();})));
                var full=new CheckBox { Content="Whole day",IsChecked=reminder.WholeDay,Foreground=Text,Margin=new Thickness(5,5,5,8) };row.Children.Add(full);
                var picker=new Expander { Header="Select blocks / tasks",Foreground=Text,Margin=new Thickness(5),Visibility=reminder.WholeDay?Visibility.Collapsed:Visibility.Visible };
                var choices=new StackPanel();
                foreach(var day in RoutineStore.Days)
                {
                    var dayItems=new StackPanel();foreach(var block in _data.Week[day])
                    {
                        var check=new CheckBox { Content=Label(block.Start+" · "+block.Title,12),IsChecked=reminder.BlockIds.Contains(block.Id),Margin=new Thickness(5,7,0,2),Foreground=Text };
                        check.Click+=(_,_)=>{if(check.IsChecked==true){if(!reminder.BlockIds.Contains(block.Id))reminder.BlockIds.Add(block.Id);}else reminder.BlockIds.Remove(block.Id);};dayItems.Children.Add(check);
                        foreach(var task in block.Tasks)
                        {
                            var taskCheck=new CheckBox { Content=Label(task.Text,12),IsChecked=reminder.TaskIds.Contains(task.Id),Margin=new Thickness(25,4,0,4),Foreground=Text };
                            taskCheck.Click+=(_,_)=>{if(taskCheck.IsChecked==true){if(!reminder.TaskIds.Contains(task.Id))reminder.TaskIds.Add(task.Id);}else reminder.TaskIds.Remove(task.Id);};dayItems.Children.Add(taskCheck);
                        }
                    }
                    choices.Children.Add(new Expander { Header=day.ToString(),Content=dayItems,Foreground=Text,Margin=new Thickness(5) });
                }
                picker.Content=choices;row.Children.Add(picker);full.Click+=(_,_)=>{reminder.WholeDay=full.IsChecked==true;picker.Visibility=reminder.WholeDay?Visibility.Collapsed:Visibility.Visible;};rows.Children.Add(row);
            }
            if(working.Count==0)rows.Children.Add(Label("No reminders. Add one to schedule a view.",12,Muted));
        }
        panel.Children.Add(Horizontal(Button("+ Add time",()=>{working.Add(new RoutineReminder { Time="12:00" });Render();}),Button("Save reminders",()=>
        {
            var previous=_data.Reminders;
            try
            {
                foreach(var row in working)
                {
                    var old=previous!.FirstOrDefault(r=>r.Id==row.Id);
                    if(old!=null&&old.Time==row.Time&&old.WholeDay==row.WholeDay&&old.BlockIds.SequenceEqual(row.BlockIds)&&old.TaskIds.SequenceEqual(row.TaskIds))row.LastShownDate=old.LastShownDate;
                }
                _data.Reminders=JsonSerializer.Deserialize<List<RoutineReminder>>(JsonSerializer.Serialize(working))!;RoutineStore.Validate(_data);RoutineReminderService.Configure(_data.Reminders);Flush();MessageBox.Show(this,"Reminders saved.","Routine");
            }
            catch(Exception ex){_data.Reminders=previous;MessageBox.Show(this,ex.Message,"Reminders could not be saved");}
        },true)));
        panel.Children.Add(Label("Each time can show the whole day or selected items. Selections only appear on their routine weekday.",11,Muted));Render();return panel;
    }
}
