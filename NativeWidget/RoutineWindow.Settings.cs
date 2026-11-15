using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

internal sealed partial class RoutineWindow
{
    private void BuildSettings(string selected = "General")
    {
        var grid=Shell("Settings",Button("Back",BuildDay));var tabs=new TabControl { Margin=new Thickness(22,0,22,18),Background=Bg,Foreground=Text,BorderBrush=Line };Grid.SetRow(tabs,1);grid.Children.Add(tabs);
        var tabStyle=new Style(typeof(TabItem));var border=new FrameworkElementFactory(typeof(Border));border.Name="TabChrome";border.SetValue(Border.BackgroundProperty,Bg);border.SetValue(Border.BorderBrushProperty,Line);border.SetValue(Border.BorderThicknessProperty,new Thickness(0,0,0,2));border.SetValue(Border.PaddingProperty,new Thickness(13,8,13,8));
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(ContentPresenter.ContentSourceProperty,"Header");border.AppendChild(presenter);var template=new ControlTemplate(typeof(TabItem)){VisualTree=border};var trigger=new Trigger{Property=TabItem.IsSelectedProperty,Value=true};trigger.Setters.Add(new Setter(Border.BorderBrushProperty,Accent,"TabChrome"));template.Triggers.Add(trigger);tabStyle.Setters.Add(new Setter(Control.TemplateProperty,template));tabs.Resources[typeof(TabItem)]=tabStyle;
        var general=new StackPanel { Margin=new Thickness(18) };
        general.Children.Add(Label("Table scale",14));var scale=new Slider { Minimum=.35,Maximum=1.4,Value=_data.Scale,TickFrequency=.05,IsSnapToTickEnabled=true,Margin=new Thickness(0,10,0,5) };var scaleLabel=Label($"{_data.Scale:P0}",12,Muted);
        scale.ValueChanged+=(_,_)=>{SetTableScale(scale.Value);scaleLabel.Text=$"{scale.Value:P0}";};general.Children.Add(scale);general.Children.Add(scaleLabel);general.Children.Add(Label("Fits to the display so the background cards stay visible.",11,Muted));
        general.Children.Add(Label("Background dimming",14));var dim=new Slider { Minimum=0,Maximum=.9,Value=_data.Dim,Margin=new Thickness(0,10,0,20) };dim.ValueChanged+=(_,_)=>{_data.Dim=dim.Value;LayoutBoard();Save();};general.Children.Add(dim);
        var enabled=new CheckBox { Content="Morning reminder",IsChecked=_data.ReminderEnabled,Foreground=Text,Margin=new Thickness(0,16,0,5) };var time=Input(_data.ReminderTime,100);general.Children.Add(enabled);
        general.Children.Add(Horizontal(time,Button("Save reminder",()=>
        {
            try
            {
                if(RoutineStore.Minute(time.Text)>=1440)throw new InvalidDataException("Use a time before midnight.");
                RoutineReminderService.Configure(enabled.IsChecked==true,time.Text);
                _data.ReminderEnabled=enabled.IsChecked==true;_data.ReminderTime=time.Text;Flush();MessageBox.Show(this,"Reminder saved.","Routine");
            }
            catch(Exception ex){MessageBox.Show(this,ex.Message,"Reminder could not be saved");}
        })));
        general.Children.Add(Label("Starts the widget at the chosen local time while you are signed in. Missed reminders appear after unlock within four hours. A powered-off PC cannot show a reminder.",12,Muted));
        general.Children.Add(Button("Import routine JSON",()=>
        {
            var picker=new OpenFileDialog { Filter="Routine JSON|*.json" };if(picker.ShowDialog(this)!=true)return;
            try{var imported=RoutineStore.Import(File.ReadAllText(picker.FileName));_data.Week=imported.Week;_data.Completed.Clear();Save();MessageBox.Show(this,"Routine imported locally. Images and settings were kept.","Routine");}
            catch(Exception ex){MessageBox.Show(this,ex.Message,"Import failed");}
        }));
        general.Children.Add(Label("Routine, progress, captions and images are stored only in your local NativeWidget data folder.",11,Muted));
        var cardPanel=CardsSettings();var routinePanel=RoutineEditor();
        foreach(var pair in new[]{("General",(UIElement)Scroll(general)),("Cards",(UIElement)cardPanel),("Routine",(UIElement)routinePanel)})
        {var tab=new TabItem { Header=pair.Item1,Content=pair.Item2,Foreground=Text,Background=Bg };tabs.Items.Add(tab);if(pair.Item1==selected)tabs.SelectedItem=tab;}
        _board.Child=grid;
    }
    private UIElement RoutineEditor()
    {
        var panel=new StackPanel { Margin=new Thickness(16) };var dayPicker=new ComboBox { ItemsSource=RoutineStore.Days,SelectedItem=_day,Margin=new Thickness(3,3,3,12) };
        var rows=new StackPanel();var working=new List<RoutineBlock>();
        void LoadDay(){working=JsonSerializer.Deserialize<List<RoutineBlock>>(JsonSerializer.Serialize(_data.Week[(DayOfWeek)dayPicker.SelectedItem]))!;Render();}
        void Render()
        {
            rows.Children.Clear();foreach(var block in working.ToArray())
            {
                var entry=new StackPanel { Margin=new Thickness(0,0,0,17) };var start=Input(block.Start,85);var end=Input(block.End,85);var title=Input(block.Title,365);
                start.TextChanged+=(_,_)=>block.Start=start.Text;end.TextChanged+=(_,_)=>block.End=end.Text;title.TextChanged+=(_,_)=>block.Title=title.Text;
                entry.Children.Add(Horizontal(start,end,title,Button("Remove",()=>{working.Remove(block);Render();})));
                var cat=new ComboBox { ItemsSource=new[]{"work","grow","play","frame","study"},SelectedItem=block.Category,Width=140,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(3) };cat.SelectionChanged+=(_,_)=>block.Category=(string)cat.SelectedItem;entry.Children.Add(cat);
                var tasks=Input(string.Join("\n",block.Tasks.Select(t=>t.Text)));tasks.AcceptsReturn=true;tasks.TextWrapping=TextWrapping.Wrap;tasks.MinHeight=62;tasks.ToolTip="One task per line";
                tasks.TextChanged+=(_,_)=>{var previous=block.Tasks;block.Tasks=tasks.Text.Split('\n').Select(t=>t.Trim()).Where(t=>t.Length>0).Select(text=>new RoutineTask { Id=previous.FirstOrDefault(t=>t.Text==text)?.Id??Guid.NewGuid().ToString("N"),Text=text }).ToList();};entry.Children.Add(tasks);rows.Children.Add(entry);
            }
        }
        dayPicker.SelectionChanged+=(_,_)=>LoadDay();panel.Children.Add(dayPicker);panel.Children.Add(Label("Start · End · Title. Checklist: one task per line. Save before switching days.",11,Muted));panel.Children.Add(rows);
        panel.Children.Add(Horizontal(Button("+ Add block",()=>{working.Add(new RoutineBlock());Render();}),Button("Save routine",()=>
        {
            var day=(DayOfWeek)dayPicker.SelectedItem;var old=_data.Week[day];_data.Week[day]=working;
            try{RoutineStore.Validate(_data);_data.Week[day]=working.OrderBy(b=>RoutineStore.Minute(b.Start)).ToList();Flush();MessageBox.Show(this,"Routine saved.","Routine");}
            catch(Exception ex){_data.Week[day]=old;MessageBox.Show(this,ex.Message,"Check routine");}
        },true)));
        LoadDay();return Scroll(panel);
    }
    private UIElement CardsSettings()
    {
        var root=new Grid { Margin=new Thickness(16) };root.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(230) });root.ColumnDefinitions.Add(new ColumnDefinition());
        var listPanel=new StackPanel();var list=new ListBox { Height=330,Background=Bg,Foreground=Text,BorderBrush=Line,Margin=new Thickness(0,10,10,10) };var details=new StackPanel { Margin=new Thickness(16,0,0,0) };
        void Refresh(RoutineCard? selected=null){list.Items.Clear();foreach(var c in _data.Cards)list.Items.Add(new ListBoxItem { Content=string.IsNullOrEmpty(c.ImageFile)?(c.Text.Length>25?c.Text[..25]+"…":c.Text):string.IsNullOrEmpty(c.Caption)?"Photo":c.Caption,Tag=c,Foreground=Text });if(selected!=null)list.SelectedIndex=_data.Cards.IndexOf(selected);else if(list.Items.Count>0)list.SelectedIndex=0;RenderCards();}
        listPanel.Children.Add(Button("+ Text card",()=>{var c=new RoutineCard { Text="New note" };_data.Cards.Add(c);Save();Refresh(c);}));
        listPanel.Children.Add(Button("+ Photo",()=>
        {
            var picker=new OpenFileDialog { Filter="Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff",Multiselect=true };if(picker.ShowDialog(this)!=true)return;
            foreach(var path in picker.FileNames)try
            {
                var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=1200;image.UriSource=new Uri(path);image.EndInit();
                _data.Cards.Add(new RoutineCard { ImageFile=RoutineStore.CopyImage(path),Caption="",CaptionFont="Segoe Print" });
            }catch(Exception ex){MessageBox.Show(this,ex.Message,"Image could not be added");}
            Save();Refresh(_data.Cards.LastOrDefault());
        }));listPanel.Children.Add(Button("Shuffle layout",()=>{_data.LayoutSeed++;Save();RenderCards();}));listPanel.Children.Add(list);listPanel.Children.Add(Label("Cards resize to fit outside the table.",11,Muted));
        list.SelectionChanged+=(_,_)=>
        {
            details.Children.Clear();if(list.SelectedItem is not ListBoxItem { Tag:RoutineCard card })return;
            details.Children.Add(Label(string.IsNullOrEmpty(card.ImageFile)?"Text":"Photo caption",16));
            var input=Input(string.IsNullOrEmpty(card.ImageFile)?card.Text:card.Caption);input.AcceptsReturn=true;input.TextWrapping=TextWrapping.Wrap;input.Height=90;
            input.TextChanged+=(_,_)=>{if(string.IsNullOrEmpty(card.ImageFile))card.Text=input.Text;else card.Caption=input.Text;if(list.SelectedItem is ListBoxItem item)item.Content=string.IsNullOrWhiteSpace(input.Text)?"Photo":input.Text.Length>25?input.Text[..25]+"…":input.Text;Save();RenderCards();};details.Children.Add(input);
            details.Children.Add(Label("Font",12,Muted));
            var fonts=new ComboBox { ItemsSource=new[]{"Georgia","Cambria","Times New Roman","Palatino Linotype","Segoe Print","Ink Free","Lucida Handwriting","Comic Sans MS"},SelectedItem=card.CaptionFont,Margin=new Thickness(3,5,3,14) };
            fonts.SelectionChanged+=(_,_)=>{if(fonts.SelectedItem is string font){card.CaptionFont=font;Save();RenderCards();}};details.Children.Add(fonts);
            var pickers=Horizontal();
            void Picker(string title,string color,Action<string> set)
            {
                var p=new StackPanel { Margin=new Thickness(0,0,15,0) };p.Children.Add(Label(title,12,Muted));var wheel=new ColorWheel { Width=140,Height=140 };wheel.SetColor(Brush(color).Color);var brightness=new Slider { Minimum=0,Maximum=1,Value=wheel.Brightness,Width=135 };
                wheel.ColorChanged+=c=>{set($"#{c.R:X2}{c.G:X2}{c.B:X2}");Save();RenderCards();};brightness.ValueChanged+=(_,_)=>wheel.Brightness=brightness.Value;p.Children.Add(wheel);p.Children.Add(brightness);pickers.Children.Add(p);
            }
            Picker("Frame",card.FrameColor,c=>card.FrameColor=c);Picker("Text",card.CaptionColor,c=>card.CaptionColor=c);details.Children.Add(pickers);
            details.Children.Add(Button("Remove card",()=>{_data.Cards.Remove(card);Save();Refresh();}));
        };
        root.Children.Add(listPanel);var scroll=Scroll(details);Grid.SetColumn(scroll,1);root.Children.Add(scroll);Refresh();return root;
    }
}
