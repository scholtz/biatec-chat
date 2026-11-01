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
  final gap = today.difference(day).inDays;
  if (gap == 0) return 'Today';
  if (gap == 1) return 'Yesterday';
  return '${_weekdays[day.weekday - 1]} ${day.day} '
      '${_months[day.month - 1]} ${day.year}';
}

/// `14:05` in the device's time zone.
String clockText(DateTime when) {
  final l = when.toLocal();
  String two(int n) => n.toString().padLeft(2, '0');
  return '${two(l.hour)}:${two(l.minute)}';
}

/// `Thu 23 Oct 2026, 14:05`.
String dayAndClock(DateTime when) =>
    '${_weekdays[_day(when).weekday - 1]} ${_day(when).day} '
    '${_months[_day(when).month - 1]} ${_day(when).year}, ${clockText(when)}';

/// The entries of one day, in the order they came.
class DayGroup {
  const DayGroup(this.label, this.entries);

  final String label;
  final List<HistoryEntry> entries;
}

/// Splits [entries] (newest first) into days, keeping their order, so each day is one heading with its decisions under it.
List<DayGroup> groupByDay(List<HistoryEntry> entries, DateTime now) {
  final groups = <DayGroup>[];
  DateTime? current;
  for (final e in entries) {
    final day = _day(e.decidedAt);
    if (current == null || day != current) {
      groups.add(DayGroup(dayLabel(e.decidedAt, now), []));
      current = day;
    }
    groups.last.entries.add(e);
  }
  return groups;
}
