import 'package:material_ui/material_ui.dart';

import '../../l10n/texts.dart';
import '../history_store.dart';
import 'history_texts.dart';

/// How a date is written: in the language of [locale] (the device's when null). Slovak dates come from [material], the Material texts of the widget tree
/// (`GlobalMaterialLocalizations` has the Slovak names of the days and months); English keeps the app's own short form, `Thu 23 Oct 2026`.
class DayFormat {
  const DayFormat({this.locale, this.material});

  /// The language of the widget tree at [context] and its Material texts.
  factory DayFormat.of(BuildContext context) => DayFormat(
      locale: Localizations.maybeLocaleOf(context),
      material: Localizations.of<MaterialLocalizations>(
          context, MaterialLocalizations));

  final Locale? locale;
  final MaterialLocalizations? material;

  bool get _slovak => appLanguageIsSlovak(locale);

  String _date(DateTime day) {
    if (!_slovak) return _englishDate(day);
    final names = material;
    // Without the Material texts the date is written in numbers, which reads the same in Slovak.
    return names == null
        ? '${day.day}. ${day.month}. ${day.year}'
        : names.formatFullDate(day);
  }
}

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

/// "Today", "Yesterday", or "Thu 23 Oct 2026", for the day of [when] seen from [now], both in the device's time zone, written as [format] says.
String dayLabel(DateTime when, DateTime now,
    [DayFormat format = const DayFormat()]) {
  final day = _day(when);
  final today = _day(now);
  // Calendar days, counted in UTC: local midnights are 23 or 25 hours apart across a clock change, and would count a day short or long.
  final gap = DateTime.utc(today.year, today.month, today.day)
      .difference(DateTime.utc(day.year, day.month, day.day))
      .inDays;
  final texts = HistoryTexts.of(format.locale);
  if (gap == 0) return texts.text('today');
  if (gap == 1) return texts.text('yesterday');
  return format._date(day);
}

/// `14:05` in the device's time zone.
String clockText(DateTime when) {
  final l = when.toLocal();
  String two(int n) => n.toString().padLeft(2, '0');
  return '${two(l.hour)}:${two(l.minute)}';
}

/// `Thu 23 Oct 2026, 14:05`, written as [format] says.
String dayAndClock(DateTime when, [DayFormat format = const DayFormat()]) =>
    '${format._date(_day(when))}, ${clockText(when)}';

/// The English short form. Slovak dates are never written from these names (see [DayFormat]).
String _englishDate(DateTime day) =>
    '${_weekdays[day.weekday - 1]} ${day.day} '
    '${_months[day.month - 1]} ${day.year}';

/// The entries of one day, in the order they came.
class DayGroup {
  const DayGroup(this.label, this.entries);

  final String label;
  final List<HistoryEntry> entries;
}

/// Splits [entries] (newest first) into days, keeping their order, so each day is one heading with its decisions under it. A day is one group however the entries come: out of order, a day is not split in two.
List<DayGroup> groupByDay(List<HistoryEntry> entries, DateTime now,
    [DayFormat format = const DayFormat()]) {
  final byDay = <DateTime, DayGroup>{};
  for (final e in entries) {
    byDay
        .putIfAbsent(_day(e.decidedAt),
            () => DayGroup(dayLabel(e.decidedAt, now, format), []))
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
