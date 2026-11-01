import '../history_store.dart';

const _weekdays = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const _months = [
  'Jan',
  'Feb',
  'Mar',
  'Apr',
  'May',
  'Jun',
  'Jul',
  'Aug',
  'Sep',
  'Oct',
  'Nov',
  'Dec'
];

DateTime _day(DateTime t) {
  final l = t.toLocal();
  return DateTime(l.year, l.month, l.day);
}

/// "Today", "Yesterday", or "Thu 23 Oct 2026", for the day of [when] seen from [now], both in the device's time zone.
String dayLabel(DateTime when, DateTime now) {
  final day = _day(when);
  final today = _day(now);
  // Calendar days, counted in UTC: local midnights are 23 or 25 hours apart across a clock change, and would count a day short or long.
  final gap = DateTime.utc(today.year, today.month, today.day)
      .difference(DateTime.utc(day.year, day.month, day.day))
      .inDays;
  if (gap == 0) return 'Today';
  if (gap == 1) return 'Yesterday';
  return _dateText(day);
}

/// `14:05` in the device's time zone.
String clockText(DateTime when) {
  final l = when.toLocal();
  String two(int n) => n.toString().padLeft(2, '0');
  return '${two(l.hour)}:${two(l.minute)}';
}

/// `Thu 23 Oct 2026, 14:05`.
String dayAndClock(DateTime when) =>
    '${_dateText(_day(when))}, ${clockText(when)}';

String _dateText(DateTime day) => '${_weekdays[day.weekday - 1]} ${day.day} '
    '${_months[day.month - 1]} ${day.year}';

/// The entries of one day, in the order they came.
class DayGroup {
  const DayGroup(this.label, this.entries);

  final String label;
  final List<HistoryEntry> entries;
}

/// Splits [entries] (newest first) into days, keeping their order, so each day is one heading with its decisions under it. A day is one group however the entries come: out of order, a day is not split in two.
List<DayGroup> groupByDay(List<HistoryEntry> entries, DateTime now) {
  final byDay = <DateTime, DayGroup>{};
  for (final e in entries) {
    byDay
        .putIfAbsent(
            _day(e.decidedAt), () => DayGroup(dayLabel(e.decidedAt, now), []))
        .entries
        .add(e);
  }
  return byDay.values.toList();
}

/// How long until the next midnight in the device's time zone, so a screen can redraw its "Today" then.
Duration untilMidnight(DateTime now) {
  final l = now.toLocal();
  final next = DateTime(l.year, l.month, l.day + 1);
  return next.difference(l);
}
