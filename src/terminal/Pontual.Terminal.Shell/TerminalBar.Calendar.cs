using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pontual.Terminal.Shell;

// Calendário da barra: clicar no relógio abre o mês, com o dia de hoje marcado e os feriados nacionais.
// É só para consulta: não guarda nada e não fala com a Central.
public partial class TerminalBar
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly string[] Weekdays = ["D", "S", "T", "Q", "Q", "S", "S"];

    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    private void ShowDate()
    {
        var today = DateTime.Today;
        DateText.Text = today.ToString("dd/MM/yyyy", PtBr);
        ClockButton.ToolTip = Capitalize(today.ToString("dddd, d 'de' MMMM 'de' yyyy", PtBr));
    }

    private void OnClock(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        _calendarMonth = new DateTime(today.Year, today.Month, 1);
        ShowCalendar();
        CalendarPopup.IsOpen = true;
    }

    private void OnCalendarPrevious(object sender, RoutedEventArgs e)
    {
        _calendarMonth = _calendarMonth.AddMonths(-1);
        ShowCalendar();
    }

    private void OnCalendarNext(object sender, RoutedEventArgs e)
    {
        _calendarMonth = _calendarMonth.AddMonths(1);
        ShowCalendar();
    }

    private void OnCalendarToday(object sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        _calendarMonth = new DateTime(today.Year, today.Month, 1);
        ShowCalendar();
    }

    private void ShowCalendar()
    {
        var today = DateTime.Today;
        CalendarTodayText.Text = Capitalize(today.ToString("dddd, d 'de' MMMM 'de' yyyy", PtBr));
        CalendarMonth.Text = Capitalize(_calendarMonth.ToString("MMMM 'de' yyyy", PtBr));

        if (CalendarWeekdays.Children.Count == 0)
        {
            foreach (var name in Weekdays)
            {
                CalendarWeekdays.Children.Add(new TextBlock
                {
                    Text = name,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("Apagado"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4),
                });
            }
        }

        var holidays = Holidays(_calendarMonth.Year);
        CalendarDays.Children.Clear();

        // Sempre 6 semanas, começando no domingo: o quadro não muda de altura entre os meses.
        var first = _calendarMonth.AddDays(-(int)_calendarMonth.DayOfWeek);
        for (var i = 0; i < 42; i++)
        {
            var day = first.AddDays(i);
            var inMonth = day.Month == _calendarMonth.Month;
            var isToday = day == today;
            var holiday = holidays.TryGetValue(day, out var holidayName) ? holidayName : null;

            var label = new TextBlock
            {
                Text = day.Day.ToString(PtBr),
                FontSize = 14,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isToday ? Brushes.White
                    : !inMonth ? Brush("LinhaForte")
                    : holiday is not null || day.DayOfWeek == DayOfWeek.Sunday ? Brush("Rubi700")
                    : Brush("Tinta"),
            };

            var cell = new Border
            {
                Width = 38,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = isToday ? Brush("Azul700")
                    : holiday is not null && inMonth ? Brush("Rubi100")
                    : Brushes.Transparent,
                Child = label,
                ToolTip = holiday is not null && inMonth ? holiday : null,
            };
            System.Windows.Automation.AutomationProperties.SetName(cell,
                day.ToString("d 'de' MMMM", PtBr) + (holiday is not null ? $", feriado: {holiday}" : "") + (isToday ? ", hoje" : ""));
            CalendarDays.Children.Add(cell);
        }

        var monthHolidays = holidays
            .Where(h => h.Key.Month == _calendarMonth.Month)
            .OrderBy(h => h.Key)
            .Select(h => $"{h.Key.Day:00}/{h.Key.Month:00} · {h.Value}")
            .ToList();
        CalendarHolidays.Text = "Feriados do mês\n" + string.Join("\n", monthHolidays);
        CalendarHolidays.Visibility = monthHolidays.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Feriados nacionais e pontos facultativos mais comuns (Carnaval, Corpus Christi). Feriados da cidade não entram.</summary>
    internal static Dictionary<DateTime, string> Holidays(int year)
    {
        var easter = Easter(year);
        var list = new Dictionary<DateTime, string>
        {
            [new DateTime(year, 1, 1)] = "Confraternização Universal",
            [easter.AddDays(-48)] = "Carnaval (ponto facultativo)",
            [easter.AddDays(-47)] = "Carnaval (ponto facultativo)",
            [easter.AddDays(-2)] = "Sexta-feira Santa",
            [new DateTime(year, 4, 21)] = "Tiradentes",
            [new DateTime(year, 5, 1)] = "Dia do Trabalho",
            [easter.AddDays(60)] = "Corpus Christi (ponto facultativo)",
            [new DateTime(year, 9, 7)] = "Independência",
            [new DateTime(year, 10, 12)] = "Nossa Senhora Aparecida",
            [new DateTime(year, 11, 2)] = "Finados",
            [new DateTime(year, 11, 15)] = "Proclamação da República",
            [new DateTime(year, 12, 25)] = "Natal",
        };

        // Consciência Negra virou feriado nacional em 2024 (Lei 14.759/2023).
        if (year >= 2024)
        {
            list[new DateTime(year, 11, 20)] = "Consciência Negra";
        }

        return list;
    }

    /// <summary>Domingo de Páscoa (cálculo gregoriano de Meeus/Jones/Butcher).</summary>
    internal static DateTime Easter(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateTime(year, month, day);
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private static string Capitalize(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], PtBr) + text[1..];
}
