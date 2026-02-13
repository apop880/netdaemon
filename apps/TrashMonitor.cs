using System.Collections.Generic;
using System.Linq;
using NetDaemon.Extensions.Scheduler;

namespace HomeAssistantApps
{
    public class TrashMonitorConfig
    {
        public required InputTextEntity InputTextEntity { get; set; }
        public required InputBooleanEntity VacationModeEntity { get; set; }
        public DateTime RecyclingReferenceDate { get; set; } // A past date where recycling definitely occurred
    }

    [NetDaemonApp]
    public class TrashMonitor
    {
        private readonly IScheduler _scheduler;
        private readonly TrashMonitorConfig _config;
        private readonly Telegram _telegram;

        public TrashMonitor(IScheduler scheduler, Entities entities, Telegram telegram)
        {
            _scheduler = scheduler;
            _telegram = telegram;
            _config = new TrashMonitorConfig
            {
                InputTextEntity = entities.InputText.TrashStatus,
                VacationModeEntity = entities.InputBoolean.VacationMode,
                RecyclingReferenceDate = new DateTime(2026, 1, 30)
            };

            // Run every day at 6:00 AM and 8:00 PM
            _scheduler.ScheduleCron("0 6 * * *", () => CheckTrashSchedule());
            _scheduler.ScheduleCron("0 20 * * *", () => CheckTrashSchedule(true));
        }

        private void CheckTrashSchedule(bool notifyOnly = false)    
        {
            var currentStatus = _config.InputTextEntity.State;

            // If status is not empty/unknown, the user hasn't cleared it yet
            if (!string.IsNullOrEmpty(currentStatus) && !currentStatus.Equals("unknown", StringComparison.CurrentCultureIgnoreCase))
            {
                _telegram.All($"Reminder: Take out {currentStatus}!");
                return;
            }
            // If on vacation mode or notify only, skip
            if (_config.VacationModeEntity.IsOn() || notifyOnly)
            {
                return;
            }
            var today = DateTime.Today;
            
            // 1. Calculate the actual pickup date for this week
            var pickupDate = GetPickupDateForWeek(today);
            
            // 2. Calculate if it is a Recycling week
            var isRecycling = IsRecyclingWeek(pickupDate);
            var statusMessage = isRecycling ? "Trash & Recycling" : "Trash";

            // LOGIC A: Is Today the day BEFORE pickup? (Set the Alert)
            if (today == pickupDate.AddDays(-1))
            {
                _config.InputTextEntity.SetValue(statusMessage);
            }
        }

        private static DateTime GetPickupDateForWeek(DateTime dateInWeek)
        {
            // 1. Find the Friday of the current week
            // (Assumes Sunday is start of week)
            var dayOfWeek = dateInWeek.DayOfWeek;
            var daysUntilFriday = DayOfWeek.Friday - dayOfWeek;
            
            // If we are calculating this on a Saturday, look backwards to yesterday's Friday
            // If we are calculating on Sunday, look forward
            // NetDaemon cron runs this check daily, so relative to 'today' works fine.
            var fridayOfThisWeek = dateInWeek.AddDays(daysUntilFriday);

            // 2. Check for Holidays
            if (IsHolidayWeek(fridayOfThisWeek))
            {
                // Shift to Saturday
                return fridayOfThisWeek.AddDays(1);
            }

            return fridayOfThisWeek;
        }

        private bool IsRecyclingWeek(DateTime pickupDate)
        {
            // Calculate weeks between reference date and current pickup date
            var diff = pickupDate - _config.RecyclingReferenceDate;
            var weeks = (int)Math.Floor(diff.TotalDays / 7);

            // If weeks is even, schedule matches. If odd, it's the other week.
            return weeks % 2 == 0;
        }

        private static bool IsHolidayWeek(DateTime fridayOfThisWeek)
        {
            var year = fridayOfThisWeek.Year;
            
            // Get the list of holidays for this year
            var holidays = GetHolidays(year);

            // The Monday of this week
            var mondayOfThisWeek = fridayOfThisWeek.AddDays(-4);

            // Check if any holiday falls between Monday and Friday (inclusive) of this week
            return holidays.Any(h => h >= mondayOfThisWeek && h <= fridayOfThisWeek);
        }

        private static List<DateTime> GetHolidays(int year)
        {
            var holidays = new List<DateTime>
            {
                // New Year's Day
                new(year, 1, 1),

                // Independence Day
                new(year, 7, 4),

                // Christmas Day
                new(year, 12, 25),

                // Memorial Day (Last Monday in May)
                FindDay(year, 5, DayOfWeek.Monday, false),

                // Labor Day (First Monday in September)
                FindDay(year, 9, DayOfWeek.Monday, true),

                // Thanksgiving Day (4th Thursday in November)
                // Find first Thursday, add 3 weeks
                FindDay(year, 11, DayOfWeek.Thursday, true).AddDays(21)
            };

            return holidays;
        }

        // Helper to find "First Monday" or "Last Monday" of a month
        private static DateTime FindDay(int year, int month, DayOfWeek day, bool first)
        {
            if (first)
            {
                var date = new DateTime(year, month, 1);
                while (date.DayOfWeek != day) date = date.AddDays(1);
                return date;
            }
            else
            {
                var daysInMonth = DateTime.DaysInMonth(year, month);
                var date = new DateTime(year, month, daysInMonth);
                while (date.DayOfWeek != day) date = date.AddDays(-1);
                return date;
            }
        }
    }
}