using System.Windows;
using System.Windows.Controls;
using System.Globalization;

namespace FolderSentinel.Desktop;

public partial class ScheduleWindow : Window
{
    private readonly NotificationSchedule original;
    public NotificationSchedule? Result { get; private set; }
    public ScheduleWindow(NotificationSchedule schedule)
    {
        original = schedule; InitializeComponent();
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)((i + 1) % 7);
            DayChoices.Children.Add(new CheckBox { Content = "日月火水木金土"[(int)day] + "", Tag = day, IsChecked = schedule.Days.Contains(day), Margin = new Thickness(0, 7, 18, 7) });
        }
        FrequencyInput.SelectedIndex = (int)schedule.Frequency;
        HourInput.ItemsSource = Enumerable.Range(0, 24).Select(x => x.ToString("00", CultureInfo.InvariantCulture));
        MinuteInput.ItemsSource = Enumerable.Range(0, 60).Select(x => x.ToString("00", CultureInfo.InvariantCulture));
        var time = TimeOnly.ParseExact(schedule.Time, "HH:mm", CultureInfo.InvariantCulture);
        HourInput.SelectedIndex = time.Hour; MinuteInput.SelectedIndex = time.Minute;
        MonthDayInput.Text = schedule.MonthDay.ToString(); LastDayBox.IsChecked = schedule.LastDay;
        ZoneLabel.Text = "タイムゾーン: " + TimeZoneInfo.Local.DisplayName;
        RefreshFields();
    }
    private void RefreshFields()
    {
        if (WeeklyPanel is null || MonthlyPanel is null) return;
        WeeklyPanel.Visibility = FrequencyInput.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        MonthlyPanel.Visibility = FrequencyInput.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        MonthDayInput.IsEnabled = LastDayBox.IsChecked != true;
    }
    private void FrequencyChanged(object sender, SelectionChangedEventArgs e) => RefreshFields();
    private void LastDayChanged(object sender, RoutedEventArgs e) => RefreshFields();
    private void PresetTimeClick(object sender, RoutedEventArgs e)
    {
        HourInput.SelectedIndex = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
        MinuteInput.SelectedIndex = 0;
    }
    private void SaveSchedule(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(MonthDayInput.Text, out int day) || day is < 1 or > 31)
            { if (FrequencyInput.SelectedIndex == 2 && LastDayBox.IsChecked != true) throw new ArgumentException("日にちは1〜31を入力してください。"); day = 1; }
            var schedule = new NotificationSchedule
            {
                Id = original.Id, Frequency = (ScheduleFrequency)FrequencyInput.SelectedIndex, Time = $"{HourInput.SelectedItem}:{MinuteInput.SelectedItem}", MonthDay = day,
                LastDay = LastDayBox.IsChecked == true, Days = DayChoices.Children.OfType<CheckBox>().Where(x => x.IsChecked == true).Select(x => (DayOfWeek)x.Tag).ToList(), TimeZoneId = TimeZoneInfo.Local.Id
            };
            ScheduleCalculator.Validate(schedule); Result = schedule; DialogResult = true;
        }
        catch (Exception ex) { ErrorLabel.Text = ex.Message; }
    }
}
