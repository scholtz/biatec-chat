import 'package:biatec_authenticator/approvals/history_screen.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/approvals/history_widgets/day_groups.dart';
import 'package:biatec_authenticator/approvals/history_widgets/history_detail.dart';
import 'package:biatec_authenticator/approvals/history_widgets/history_filters.dart';
import 'package:biatec_authenticator/approvals/history_widgets/history_status.dart';
import 'package:biatec_authenticator/approvals/history_widgets/history_tile.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:biatec_authenticator/ui/ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

/// Midday on the 24th, in the device's own time zone, so a day label does not depend on where the test runs.
final _now = DateTime(2026, 10, 24, 15);

HistoryEntry _entry(
  String id, {
  String decision = 'approve',
  String summary = '5000.000000 USDC (4500.00 EUR) on algorand-mainnet',
  String client = 'Example Bank',
  DateTime? at,
  Map<String, Object?>? receipt,
}) =>
    HistoryEntry(
      authReqId: id,
      decision: decision,
      summary: summary,
      clientLabel: client,
      decidedAt: at ?? DateTime(2026, 10, 24, 9, 5),
      receipt: receipt ??
          {
            'version': '1',
            'alg': 'ES256',
            'signature': 'sig',
            'issued_at': 1792425620,
            'signed': {'auth_req_id': id, 'decision': decision},
            'device': {'kid': 'device-key-1', 'attestation': 'h.p.s'},
          },
    );

class _FlakyHistory implements HistoryStore {
  _FlakyHistory(this.entries);
  final List<HistoryEntry> entries;
  bool broken = false;

  @override
  Future<List<HistoryEntry>> read() async {
    if (broken) throw StateError('unreadable');
    return entries;
  }

  @override
  Future<void> add(HistoryEntry entry) async => entries.insert(0, entry);
}

Future<MemoryHistoryStore> _store(List<HistoryEntry> entries) async {
  final store = MemoryHistoryStore();
  // Oldest first, so the newest ends up on top as the real store keeps it.
  for (final e in entries.reversed) {
    await store.add(e);
  }
  return store;
}

Future<void> _pump(WidgetTester tester, HistoryStore history,
    {Size size = const Size(400, 1400),
    Brightness brightness = Brightness.light,
    double scale = 1,
    DateTime Function()? clock}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  tester.platformDispatcher.textScaleFactorTestValue = scale;
  addTearDown(tester.platformDispatcher.clearAllTestValues);
  await tester.pumpWidget(MaterialApp(
    theme: buildTheme(brightness),
    builder: largeTypeBuilder,
    home: HistoryScreen(history: history, clock: clock ?? () => _now),
  ));
  await tester.pump();
  await tester.pump();
}

Future<void> _end(WidgetTester tester) => tester.pumpWidget(const SizedBox());

Future<void> _open(WidgetTester tester, String id) async {
  final tile = find.byKey(Key('history-$id'));
  await tester.ensureVisible(tile);
  // Near the top-left: at large text a row can be taller than the screen, and its centre then lies below it.
  await tester.tapAt(tester.getTopLeft(tile) + const Offset(24, 24));
  await tester.pump();
  await tester.pump(const Duration(seconds: 1));
}

void main() {
  group('days', () {
    test('a day is Today, Yesterday, or its date', () {
      expect(dayLabel(DateTime(2026, 10, 24, 0, 1), _now), 'Today');
      expect(dayLabel(DateTime(2026, 10, 23, 23, 59), _now), 'Yesterday');
      expect(dayLabel(DateTime(2026, 10, 22, 12), _now), 'Thu 22 Oct 2026');
      expect(dayLabel(DateTime(2025, 1, 5, 12), _now), 'Sun 5 Jan 2025');
      // Across a month and a year end.
      expect(dayLabel(DateTime(2026, 12, 31, 12), DateTime(2027, 1, 1, 8)),
          'Yesterday');
    });

    test('entries are split by day and keep their order', () {
      final groups = groupByDay([
        _entry('a', at: DateTime(2026, 10, 24, 14)),
        _entry('b', at: DateTime(2026, 10, 24, 9)),
        _entry('c', at: DateTime(2026, 10, 23, 18)),
        _entry('d', at: DateTime(2026, 10, 20, 8)),
      ], _now);
      expect(groups.map((g) => g.label),
          ['Today', 'Yesterday', 'Tue 20 Oct 2026']);
      expect(groups[0].entries.map((e) => e.authReqId), ['a', 'b']);
      expect(groupByDay(const [], _now), isEmpty);
    });

    test('the clock is two digits each', () {
      expect(clockText(DateTime(2026, 10, 24, 9, 5)), '09:05');
      expect(
          dayAndClock(DateTime(2026, 10, 24, 9, 5)), 'Sat 24 Oct 2026, 09:05');
    });

    test('a day stays one group even when the entries come out of order', () {
      final groups = groupByDay([
        _entry('a', at: DateTime(2026, 10, 24, 14)),
        _entry('c', at: DateTime(2026, 10, 23, 18)),
        _entry('b', at: DateTime(2026, 10, 24, 9)),
      ], _now);
      expect(groups.map((g) => g.label), ['Today', 'Yesterday']);
      expect(groups[0].entries.map((e) => e.authReqId), ['a', 'b']);
    });

    test('a spoken sentence ends in exactly one stop', () {
      expect(sentence('Send 5 ALGO'), 'Send 5 ALGO.');
      expect(sentence('Send 5 ALGO.'), 'Send 5 ALGO.');
      expect(sentence('Send 5 ALGO?'), 'Send 5 ALGO?');
      expect(sentence('Done!'), 'Done!');
      expect(sentence('  padded  '), 'padded.');
      expect(sentence('   '), '');
      expect(sentence(''), '');
    });
  });

  group('the list', () {
    testWidgets('is grouped by day, newest first, under headings',
        (tester) async {
      final history = await _store([
        _entry('today1', at: DateTime(2026, 10, 24, 14)),
        _entry('today2', at: DateTime(2026, 10, 24, 9)),
        _entry('yesterday', at: DateTime(2026, 10, 23, 18)),
        _entry('older', at: DateTime(2026, 10, 20, 8)),
      ]);
      await _pump(tester, history);
      expect(find.byKey(const Key('history-day-Today')), findsOneWidget);
      expect(find.byKey(const Key('history-day-Yesterday')), findsOneWidget);
      expect(
          find.byKey(const Key('history-day-Tue 20 Oct 2026')), findsOneWidget);
      double y(String key) => tester.getTopLeft(find.byKey(Key(key))).dy;
      expect(y('history-day-Today'), lessThan(y('history-today1')));
      expect(y('history-today1'), lessThan(y('history-today2')));
      expect(y('history-today2'), lessThan(y('history-day-Yesterday')));
      expect(y('history-day-Yesterday'), lessThan(y('history-yesterday')));
      expect(
          y('history-yesterday'), lessThan(y('history-day-Tue 20 Oct 2026')));
      expect(y('history-day-Tue 20 Oct 2026'), lessThan(y('history-older')));
      await _end(tester);
    });

    testWidgets(
        'each row says its status in an icon and a word, and who and when',
        (tester) async {
      final history = await _store([
        _entry('a', decision: 'approve', at: DateTime(2026, 10, 24, 14, 30)),
        _entry('d', decision: 'deny', at: DateTime(2026, 10, 24, 13)),
        _entry('r', decision: 'code_failed', at: DateTime(2026, 10, 24, 12)),
      ]);
      await _pump(tester, history);
      for (final (key, word, icon) in [
        ('a', 'Approved', Icons.check_circle),
        ('d', 'Denied', Icons.block),
        ('r', 'Refused', Icons.gpp_bad),
      ]) {
        final row = find.byKey(Key('history-$key'));
        expect(
            find.descendant(of: row, matching: find.text(word)), findsOneWidget,
            reason: key);
        expect(find.descendant(of: row, matching: find.byIcon(icon)),
            findsOneWidget,
            reason: key);
      }
      expect(find.textContaining('Example Bank · 14:30'), findsOneWidget);
      await _end(tester);
    });

    testWidgets('an unknown decision is still listed, in its own words',
        (tester) async {
      final history = await _store([_entry('x', decision: 'maybe')]);
      await _pump(tester, history);
      expect(find.byKey(const Key('history-x')), findsOneWidget);
      expect(
          find.descendant(
              of: find.byKey(const Key('history-x')),
              matching: find.text('maybe')),
          findsOneWidget);
      expect(find.byIcon(Icons.help_outline), findsOneWidget);
      // Only All shows it.
      await tester.tap(find.byKey(const Key('history-filter-approved')));
      await tester.pump();
      expect(find.byKey(const Key('history-x')), findsNothing);
      await _end(tester);
    });

    testWidgets(
        'a row is one button for a screen reader, with everything in full',
        (tester) async {
      final handle = tester.ensureSemantics();
      final history = await _store([
        _entry('a', at: DateTime(2026, 10, 24, 14, 30)),
      ]);
      await _pump(tester, history);
      expect(
          find.bySemanticsLabel(
              'Approved. 5000.000000 USDC (4500.00 EUR) on algorand-mainnet. From Example Bank, Sat 24 Oct 2026, 14:30. Double tap to open the receipt.'),
          findsOneWidget);
      handle.dispose();
      await _end(tester);
    });

    testWidgets('an empty client label is not read as "From ,"',
        (tester) async {
      final handle = tester.ensureSemantics();
      final history = await _store([_entry('a', client: '  ')]);
      await _pump(tester, history);
      expect(find.bySemanticsLabel(RegExp(r'From ,')), findsNothing);
      expect(
          find.bySemanticsLabel(
              RegExp(r'\. At Sat 24 Oct 2026, 09:05\. Double')),
          findsOneWidget);
      handle.dispose();
      await _end(tester);
    });

    testWidgets('a long unknown status cannot make a row tall', (tester) async {
      final history = await _store([
        _entry('x', decision: 'a_status_a_newer_version_wrote ' * 6),
      ]);
      await _pump(tester, history, size: const Size(320, 568), scale: 3);
      expect(tester.takeException(), isNull);
      final chip = tester.getSize(find.byType(StatusChip));
      // Two lines of the chip's text at most, whatever the value.
      expect(chip.height, lessThan(300));
      await _end(tester);
    });

    testWidgets('a text that ends in a full stop is not read with two',
        (tester) async {
      final handle = tester.ensureSemantics();
      final history = await _store([
        _entry('a', summary: 'Send 5 ALGO.', client: 'Biatec Chat.'),
      ]);
      await _pump(tester, history);
      expect(
          find.bySemanticsLabel(
              RegExp(r'Approved\. Send 5 ALGO\. From Biatec Chat, ')),
          findsOneWidget);
      expect(find.bySemanticsLabel(RegExp(r'\.\.')), findsNothing);
      handle.dispose();
      await _end(tester);
    });

    testWidgets(
        'Today becomes Yesterday when the day turns over while it is open',
        (tester) async {
      var now = DateTime(2026, 10, 24, 23, 59, 30);
      final history = await _store([
        _entry('a', at: DateTime(2026, 10, 24, 9)),
      ]);
      await _pump(tester, history, clock: () => now);
      expect(find.byKey(const Key('history-day-Today')), findsOneWidget);
      now = DateTime(2026, 10, 25, 0, 0, 5);
      await tester.pump(const Duration(seconds: 40));
      expect(find.byKey(const Key('history-day-Yesterday')), findsOneWidget);
      expect(find.byKey(const Key('history-day-Today')), findsNothing);
      await _end(tester);
    });

    test('midnight is counted to the next calendar day', () {
      expect(untilMidnight(DateTime(2026, 10, 24, 23, 0)),
          const Duration(hours: 1));
      expect(untilMidnight(DateTime(2026, 10, 24, 0, 0)),
          const Duration(hours: 24));
      expect(
          untilMidnight(DateTime(2026, 12, 31, 12)), const Duration(hours: 12));
    });

    test('calendar days, not 24-hour spans, decide Today and Yesterday', () {
      // A late evening and an early morning: a day apart on the calendar, an hour apart on the clock.
      final late = DateTime(2026, 10, 23, 23, 30);
      final early = DateTime(2026, 10, 24, 0, 30);
      expect(dayLabel(late, early), 'Yesterday');
      expect(dayLabel(early, early), 'Today');
      expect(
          dayLabel(
              DateTime(2026, 10, 22, 23, 30), DateTime(2026, 10, 24, 0, 30)),
          'Thu 22 Oct 2026');
    });
  });

  group('filters', () {
    testWidgets('keep only the chosen status, and show it with a check',
        (tester) async {
      final history = await _store([
        _entry('a1', decision: 'approve', at: DateTime(2026, 10, 24, 14)),
        _entry('d1', decision: 'deny', at: DateTime(2026, 10, 24, 13)),
        _entry('r1', decision: 'code_failed', at: DateTime(2026, 10, 24, 12)),
      ]);
      await _pump(tester, history);
      FilterChip chip(String name) =>
          tester.widget<FilterChip>(find.byKey(Key('history-filter-$name')));
      expect(chip('all').selected, isTrue);
      expect(chip('all').showCheckmark, isTrue);
      for (final id in ['a1', 'd1', 'r1']) {
        expect(find.byKey(Key('history-$id')), findsOneWidget);
      }

      await tester.tap(find.byKey(const Key('history-filter-denied')));
      await tester.pump();
      expect(chip('denied').selected, isTrue);
      expect(chip('all').selected, isFalse);
      expect(find.byKey(const Key('history-d1')), findsOneWidget);
      expect(find.byKey(const Key('history-a1')), findsNothing);
      expect(find.byKey(const Key('history-r1')), findsNothing);

      await tester.tap(find.byKey(const Key('history-filter-refused')));
      await tester.pump();
      expect(find.byKey(const Key('history-r1')), findsOneWidget);
      expect(find.byKey(const Key('history-d1')), findsNothing);
      await _end(tester);
    });

    testWidgets('a filter with nothing in it says so and offers Show all',
        (tester) async {
      final history = await _store([_entry('a1')]);
      await _pump(tester, history);
      await tester.tap(find.byKey(const Key('history-filter-denied')));
      await tester.pump();
      expect(find.byKey(const Key('history-filter-empty')), findsOneWidget);
      expect(find.text('Nothing denied yet'), findsOneWidget);
      expect(find.byKey(const Key('history-a1')), findsNothing);
      await tester.tap(find.byKey(const Key('history-show-all')));
      await tester.pump();
      expect(find.byKey(const Key('history-a1')), findsOneWidget);
      expect(find.byKey(const Key('history-filter-empty')), findsNothing);
      await _end(tester);
    });

    test('every status can be filtered, and each filter says what is empty',
        () {
      for (final s in HistoryStatus.values) {
        expect(HistoryFilter.values.where((f) => f.status == s), hasLength(1),
            reason: '${s.name} has a filter');
      }
      expect(HistoryFilter.values.where((f) => f.status == null), hasLength(1),
          reason: 'one All');
      for (final f in HistoryFilter.values) {
        expect(f.emptyText, isNotEmpty);
      }
      expect(HistoryFilter.denied.emptyText, 'Nothing denied yet');
    });

    test('each filter matches only its own decision', () {
      final a = _entry('a');
      final d = _entry('d', decision: 'deny');
      final r = _entry('r', decision: 'code_failed');
      expect([a, d, r].where(HistoryFilter.all.matches), hasLength(3));
      expect([a, d, r].where(HistoryFilter.approved.matches), [a]);
      expect([a, d, r].where(HistoryFilter.denied.matches), [d]);
      expect([a, d, r].where(HistoryFilter.refused.matches), [r]);
      expect(HistoryStatus.of('approve'), HistoryStatus.approved);
      expect(HistoryStatus.of('nope'), isNull);
    });
  });

  group('the receipt', () {
    late List<String> copied;

    setUp(() {
      copied = [];
    });

    void mockClipboard(WidgetTester tester) {
      tester.binding.defaultBinaryMessenger
          .setMockMethodCallHandler(SystemChannels.platform, (call) async {
        if (call.method == 'Clipboard.setData') {
          copied.add((call.arguments as Map)['text'] as String);
        }
        return null;
      });
      addTearDown(() => tester.binding.defaultBinaryMessenger
          .setMockMethodCallHandler(SystemChannels.platform, null));
    }

    testWidgets('opens from a row and shows what was decided, by whom and when',
        (tester) async {
      final history = await _store([
        _entry('ciba_42', at: DateTime(2026, 10, 24, 9, 5)),
      ]);
      await _pump(tester, history);
      await _open(tester, 'ciba_42');
      expect(find.byType(HistoryDetailScreen), findsOneWidget);
      expect(find.text('5000.000000 USDC (4500.00 EUR) on algorand-mainnet'),
          findsOneWidget);
      String factText(String key) => tester
          .widget<SelectableText>(find.descendant(
              of: find.byKey(Key(key)), matching: find.byType(SelectableText)))
          .data!;
      expect(factText('receipt-decision'), 'Approved');
      expect(factText('receipt-client'), 'Example Bank');
      expect(factText('receipt-decided'), 'Sat 24 Oct 2026, 09:05');
      expect(factText('receipt-id'), 'ciba_42');
      expect(factText('receipt-kid'), 'device-key-1');
      expect(factText('receipt-alg'), 'ES256');
      await _end(tester);
    });

    testWidgets('copies the receipt id', (tester) async {
      mockClipboard(tester);
      final history = await _store([_entry('ciba_42')]);
      await _pump(tester, history);
      await _open(tester, 'ciba_42');
      await tester.tap(find.byKey(const Key('receipt-copy')));
      await tester.pump();
      expect(copied, ['ciba_42']);
      expect(find.text('Receipt id copied'), findsOneWidget);
      await _end(tester);
    });

    testWidgets(
        'shows no one-time code, even for an entry the store did not build',
        (tester) async {
      // Built directly, so it was never through the store's redaction.
      final raw = _entry('ciba_1', receipt: {
        'version': '1',
        'alg': 'ES256',
        'signature': 'sig',
        'signed': {
          'auth_req_id': 'ciba_1',
          'binding_code': '483920',
          'decision': 'approve'
        },
        'device': {'kid': 'device-key-1'},
      });
      tester.view.physicalSize = const Size(400, 2000);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
          theme: buildTheme(Brightness.light),
          home: HistoryDetailScreen(entry: raw)));
      await tester.tap(find.byKey(const Key('receipt-json-tile')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('receipt-json')), findsOneWidget);
      expect(find.textContaining('483920'), findsNothing);
      expect(find.textContaining('"binding_code"'), findsNothing);
      expect(find.textContaining('signed.binding_code'), findsOneWidget,
          reason: 'the receipt says what was left out');
      expect(find.textContaining('"auth_req_id": "ciba_1"'), findsOneWidget);
    });

    testWidgets('the stored receipt is collapsed until asked for',
        (tester) async {
      final history = await _store([_entry('ciba_42')]);
      await _pump(tester, history);
      await _open(tester, 'ciba_42');
      expect(find.byKey(const Key('receipt-json')), findsNothing);
      await tester.tap(find.byKey(const Key('receipt-json-tile')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('receipt-json')), findsOneWidget);
      expect(find.textContaining('"signature": "sig"'), findsOneWidget);
      await _end(tester);
    });

    testWidgets('a refused request says three wrong codes ended it',
        (tester) async {
      final history = await _store([_entry('r1', decision: 'code_failed')]);
      await _pump(tester, history);
      await _open(tester, 'r1');
      expect(find.text('The request was refused'), findsOneWidget);
      expect(find.textContaining('Three wrong codes ended it'), findsOneWidget);
      expect(find.text('Refused after three wrong codes'), findsOneWidget);
      await _end(tester);
    });

    testWidgets('a receipt with little in it still shows', (tester) async {
      final history = await _store([
        _entry('bare', receipt: const {'signed': {}})
      ]);
      await _pump(tester, history);
      await _open(tester, 'bare');
      expect(find.byKey(const Key('receipt-id')), findsOneWidget);
      expect(find.byKey(const Key('receipt-kid')), findsNothing);
      expect(find.byKey(const Key('receipt-alg')), findsNothing);
      expect(tester.takeException(), isNull);
      await _end(tester);
    });

    test('the device key id is read only from a device that names one', () {
      expect(
          HistoryDetailScreen.deviceKeyId({
            'device': {'kid': 'k1'}
          }),
          'k1');
      expect(
          HistoryDetailScreen.deviceKeyId({
            'device': {'kid': ''}
          }),
          isNull);
      expect(HistoryDetailScreen.deviceKeyId({'device': 'x'}), isNull);
      expect(HistoryDetailScreen.deviceKeyId(const {}), isNull);
    });

    testWidgets('the receipt id is copyable text and an item with its label',
        (tester) async {
      final handle = tester.ensureSemantics();
      final history = await _store([_entry('ciba_42')]);
      await _pump(tester, history);
      await _open(tester, 'ciba_42');
      final node = tester.getSemantics(find.byKey(const Key('receipt-id')));
      expect('${node.label} ${node.value}', contains('Receipt id'));
      expect('${node.label} ${node.value}', contains('ciba_42'));
      handle.dispose();
      await _end(tester);
    });
  });

  group('states', () {
    testWidgets('loading shows the shared skeleton', (tester) async {
      tester.view.physicalSize = const Size(400, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      await tester.pumpWidget(MaterialApp(
          home:
              HistoryScreen(history: MemoryHistoryStore(), clock: () => _now)));
      expect(find.byType(LoadingSkeleton), findsOneWidget);
      await tester.pump();
      await tester.pump();
      await _end(tester);
    });

    testWidgets('empty shows the shared empty state', (tester) async {
      await _pump(tester, MemoryHistoryStore());
      expect(find.byType(EmptyState), findsOneWidget);
      expect(find.text('No approvals yet'), findsOneWidget);
      expect(find.byType(HistoryFilterBar), findsNothing,
          reason: 'nothing to filter');
      await _end(tester);
    });

    testWidgets('an unreadable history shows the shared error with a retry',
        (tester) async {
      final history = _FlakyHistory([])..broken = true;
      await _pump(tester, history);
      expect(find.byType(ErrorState), findsOneWidget);
      expect(find.text('History could not be read'), findsOneWidget);
      history.broken = false;
      history.entries.add(_entry('late'));
      await tester.tap(find.text('Try again'));
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      expect(find.byKey(const Key('history-late')), findsOneWidget);
      await _end(tester);
    });

    testWidgets('a refresh that fails keeps the list and the chosen filter',
        (tester) async {
      final history = _FlakyHistory([
        _entry('a1'),
        _entry('d1', decision: 'deny'),
      ]);
      await _pump(tester, history);
      await tester.tap(find.byKey(const Key('history-filter-denied')));
      await tester.pump();
      history.broken = true;
      await tester.fling(find.byType(ListView), const Offset(0, 400), 1000);
      await tester.pump();
      await tester.pump(const Duration(seconds: 1));
      await tester.pump(const Duration(seconds: 1));
      expect(find.byKey(const Key('history-stale')), findsOneWidget);
      expect(find.byKey(const Key('history-d1')), findsOneWidget);
      expect(find.byKey(const Key('history-a1')), findsNothing,
          reason: 'the filter is kept');
      await _end(tester);
    });
  });

  group('large text and guidelines', () {
    Future<MemoryHistoryStore> many() => _store([
          _entry('a1', at: DateTime(2026, 10, 24, 14)),
          _entry('d1',
              decision: 'deny',
              at: DateTime(2026, 10, 24, 10),
              client: 'A client with a rather long display name indeed'),
          _entry('r1', decision: 'code_failed', at: DateTime(2026, 10, 23, 12)),
        ]);

    for (final brightness in Brightness.values) {
      testWidgets(
          'the list and the receipt hold at 3x text (${brightness.name})',
          (tester) async {
        await _pump(tester, await many(),
            size: const Size(320, 568), brightness: brightness, scale: 3);
        expect(tester.takeException(), isNull, reason: 'list');
        await tester
            .ensureVisible(find.byKey(const Key('history-filter-denied')));
        await tester.pump();
        await tester.tap(find.byKey(const Key('history-filter-denied')));
        await tester.pump();
        expect(tester.takeException(), isNull, reason: 'filtered');
        await tester.ensureVisible(find.byKey(const Key('history-filter-all')));
        await tester.pump();
        await tester.tap(find.byKey(const Key('history-filter-all')));
        await tester.pump();
        await _open(tester, 'a1');
        expect(tester.takeException(), isNull, reason: 'receipt');
        await tester.scrollUntilVisible(
            find.byKey(const Key('receipt-json-tile')), 200,
            scrollable: find
                .descendant(
                    of: find.byType(HistoryDetailScreen),
                    matching: find.byType(Scrollable))
                .first);
        await tester.tapAt(
            tester.getTopLeft(find.byKey(const Key('receipt-json-tile'))) +
                const Offset(24, 24));
        await tester.pumpAndSettle();
        expect(find.byKey(const Key('receipt-json')), findsOneWidget);
        expect(tester.takeException(), isNull, reason: 'receipt expanded');
        await _end(tester);
      });
    }

    testWidgets(
        'every control is at least 48 dp and labelled, on the list and the receipt',
        (tester) async {
      final handle = tester.ensureSemantics();
      await _pump(tester, await many());
      await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
      await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
      await _open(tester, 'a1');
      await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
      await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
      handle.dispose();
      await _end(tester);
    });

    testWidgets('a status is a word as well as a colour', (tester) async {
      await tester.pumpWidget(MaterialApp(
          theme: buildTheme(Brightness.light),
          home: const Scaffold(
              body: Column(children: [
            StatusChip(decision: 'approve'),
            StatusChip(decision: 'deny'),
            StatusChip(decision: 'code_failed'),
          ]))));
      for (final word in ['Approved', 'Denied', 'Refused']) {
        expect(find.text(word), findsOneWidget);
      }
      for (final icon in [Icons.check_circle, Icons.block, Icons.gpp_bad]) {
        expect(find.byIcon(icon), findsOneWidget);
      }
      // The tile is used by the screen only; built alone it still works.
      await tester.pumpWidget(MaterialApp(
          home:
              Scaffold(body: HistoryTile(entry: _entry('t'), onOpen: () {}))));
      expect(find.byKey(const Key('history-t')), findsOneWidget);
    });
  });
}
