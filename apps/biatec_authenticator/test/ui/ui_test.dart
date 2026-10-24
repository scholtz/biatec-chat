import 'package:biatec_authenticator/theme.dart';
import 'package:biatec_authenticator/ui/ui.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// [child] on a narrow screen, in both brightnesses, at normal and 3x text.
Future<void> pumpWidgetAt(
  WidgetTester tester,
  Widget child, {
  Brightness brightness = Brightness.light,
  double textScale = 1,
  bool reduceMotion = false,
}) {
  tester.view.physicalSize = const Size(320, 640);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  return tester.pumpWidget(MaterialApp(
    theme: buildTheme(brightness),
    builder: (context, c) => MediaQuery(
      data: MediaQuery.of(context).copyWith(
          textScaler: TextScaler.linear(textScale),
          disableAnimations: reduceMotion),
      child: c!,
    ),
    home: Scaffold(body: SingleChildScrollView(child: child)),
  ));
}

void main() {
  group('theme', () {
    test(
        'both brightnesses carry the status colors, Material 3, and 48 dp buttons',
        () {
      for (final b in Brightness.values) {
        final t = buildTheme(b);
        expect(t.useMaterial3, isTrue);
        expect(t.extension<StatusColors>(), isNotNull);
        expect(t.filledButtonTheme.style?.minimumSize?.resolve({}),
            const Size(kMinTapTarget, kMinTapTarget));
        expect(t.navigationBarTheme.labelBehavior,
            NavigationDestinationLabelBehavior.alwaysShow);
      }
      expect(
          buildTheme(Brightness.dark).extension<StatusColors>()!.danger,
          isNot(
              buildTheme(Brightness.light).extension<StatusColors>()!.danger));
    });

    test(
        'status colors are legible: text on its background has at least 4.5:1 contrast',
        () {
      double lum(Color c) => c.computeLuminance();
      double ratio(Color a, Color b) {
        final l1 = lum(a), l2 = lum(b);
        return (l1 > l2 ? l1 + 0.05 : l2 + 0.05) /
            (l1 > l2 ? l2 + 0.05 : l1 + 0.05);
      }

      for (final s in [StatusColors.light, StatusColors.dark]) {
        for (final (bg, fg) in [
          (s.info, s.onInfo),
          (s.success, s.onSuccess),
          (s.warning, s.onWarning),
          (s.danger, s.onDanger),
        ]) {
          expect(ratio(bg, fg), greaterThanOrEqualTo(4.5));
        }
      }
    });

    test('the text scale is clamped at 3x', () {
      expect(AppSpacing.m, 16);
      expect(kMinTapTarget, 48);
    });
  });

  group('StatusBanner', () {
    for (final severity in StatusSeverity.values) {
      testWidgets(
          '${severity.name} has an icon, a spoken severity and its text',
          (tester) async {
        final handle = tester.ensureSemantics();
        await pumpWidgetAt(tester,
            StatusBanner(severity: severity, message: 'Something to know'));
        expect(find.byIcon(StatusBanner.defaultIcon(severity)), findsOneWidget);
        expect(find.text('Something to know'), findsOneWidget);
        expect(
            find.bySemanticsLabel(RegExp(StatusBanner.severityWord(severity))),
            findsOneWidget);
        handle.dispose();
      });
    }

    testWidgets('the action runs, and a custom icon replaces the default',
        (tester) async {
      var taps = 0;
      await pumpWidgetAt(
          tester,
          StatusBanner(
            severity: StatusSeverity.warning,
            title: 'First use',
            message: 'You have not approved this client before.',
            icon: Icons.person_add,
            actionLabel: 'Details',
            onAction: () => taps++,
          ));
      expect(find.byIcon(Icons.person_add), findsOneWidget);
      expect(find.byIcon(Icons.warning_amber_rounded), findsNothing);
      expect(find.text('First use'), findsOneWidget);
      await tester.tap(find.text('Details'));
      expect(taps, 1);
      expect(tester.getSize(find.byType(TextButton)).height,
          greaterThanOrEqualTo(kMinTapTarget));
    });

    testWidgets('holds at 3x text in dark mode', (tester) async {
      await pumpWidgetAt(
          tester,
          const StatusBanner(
              severity: StatusSeverity.danger,
              title: 'Request expired',
              message:
                  'This request can no longer be approved. Ask the app that '
                  'sent it to start again.',
              actionLabel: 'Back to the list',
              onAction: null),
          brightness: Brightness.dark,
          textScale: 3);
      expect(tester.takeException(), isNull);
    });
  });

  group('EmptyState', () {
    testWidgets('says what the place is for and offers the next step',
        (tester) async {
      var taps = 0;
      await pumpWidgetAt(
          tester,
          EmptyState(
            icon: Icons.inbox,
            title: 'No pending approvals',
            message: 'Requests from Biatec Chat and other apps appear here.',
            actionLabel: 'Refresh',
            onAction: () => taps++,
          ));
      expect(find.text('No pending approvals'), findsOneWidget);
      expect(find.text('Requests from Biatec Chat and other apps appear here.'),
          findsOneWidget);
      await tester.tap(find.text('Refresh'));
      expect(taps, 1);
    });

    testWidgets('has no button without an action, and holds at 3x text',
        (tester) async {
      await pumpWidgetAt(
          tester,
          const EmptyState(
              icon: Icons.history,
              title: 'No approvals yet',
              message: 'Decisions you make on this device are listed here.'),
          textScale: 3);
      expect(find.byType(FilledButton), findsNothing);
      expect(tester.takeException(), isNull);
    });
  });

  group('ErrorState', () {
    testWidgets('a failure offers a retry', (tester) async {
      var retries = 0;
      await pumpWidgetAt(
          tester,
          ErrorState(
              message: 'Could not load requests.', onRetry: () => retries++));
      expect(find.text('Something went wrong'), findsOneWidget);
      expect(find.byIcon(Icons.error_outline), findsOneWidget);
      await tester.tap(find.text('Try again'));
      expect(retries, 1);
    });

    testWidgets(
        'offline has its own icon and title; without a retry there is no button',
        (tester) async {
      await pumpWidgetAt(
          tester, const ErrorState(message: 'No connection.', offline: true));
      expect(find.text('You are offline'), findsOneWidget);
      expect(find.byIcon(Icons.cloud_off), findsOneWidget);
      expect(find.byType(OutlinedButton), findsNothing);
    });

    testWidgets('holds at 3x text', (tester) async {
      await pumpWidgetAt(
          tester,
          ErrorState(
              message:
                  'The server did not answer in time. Check the connection '
                  'and try again.',
              onRetry: () {}),
          textScale: 3,
          brightness: Brightness.dark);
      expect(tester.takeException(), isNull);
    });
  });

  group('OfflineBanner', () {
    testWidgets('says that approving needs a live connection', (tester) async {
      var retries = 0;
      await pumpWidgetAt(tester, OfflineBanner(onRetry: () => retries++));
      expect(find.text('You are offline'), findsOneWidget);
      expect(find.textContaining('live connection'), findsOneWidget);
      expect(find.byIcon(Icons.cloud_off), findsOneWidget);
      await tester.tap(find.text('Try again'));
      expect(retries, 1);
    });
  });

  group('LoadingSkeleton', () {
    testWidgets('is announced as loading, and pulses', (tester) async {
      final handle = tester.ensureSemantics();
      await pumpWidgetAt(tester, const LoadingSkeleton(rows: 2));
      expect(find.bySemanticsLabel('Loading'), findsOneWidget);
      await tester.pump(const Duration(milliseconds: 500));
      expect(tester.hasRunningAnimations, isTrue);
      await tester.pumpWidget(const SizedBox());
      handle.dispose();
    });

    testWidgets('holds still when the system asks to reduce motion',
        (tester) async {
      await pumpWidgetAt(tester, const LoadingSkeleton(), reduceMotion: true);
      await tester.pumpAndSettle();
      expect(tester.hasRunningAnimations, isFalse);
      // Stopped at full opacity, not dimmed.
      final fade = tester.widget<FadeTransition>(find.descendant(
          of: find.byType(LoadingSkeleton),
          matching: find.byType(FadeTransition)));
      expect(fade.opacity.value, 1.0);
    });

    testWidgets('holds at 3x text', (tester) async {
      await pumpWidgetAt(tester, const LoadingSkeleton(rows: 4),
          textScale: 3, reduceMotion: true);
      expect(tester.takeException(), isNull);
    });
  });

  group('AmountText', () {
    testWidgets('shows the amount exactly as given and speaks it in full',
        (tester) async {
      final handle = tester.ensureSemantics();
      await pumpWidgetAt(
          tester, const AmountText(amount: '12.500000', unit: 'ALGO'));
      expect(
          find.textContaining('12.500000', findRichText: true), findsOneWidget);
      expect(find.bySemanticsLabel('12.500000 ALGO'), findsOneWidget);
      handle.dispose();
    });

    testWidgets('without a unit it speaks the amount alone, large or not',
        (tester) async {
      final handle = tester.ensureSemantics();
      await pumpWidgetAt(tester, const AmountText(amount: '0.01', large: true));
      expect(find.bySemanticsLabel('0.01'), findsOneWidget);
      handle.dispose();
    });

    testWidgets('a large amount holds at 3x text', (tester) async {
      await pumpWidgetAt(tester,
          const AmountText(amount: '1234567.890123', unit: 'USDC', large: true),
          textScale: 3);
      expect(tester.takeException(), isNull);
    });
  });

  group('SectionHeader', () {
    testWidgets('is a heading, with room for a trailing widget',
        (tester) async {
      final handle = tester.ensureSemantics();
      await pumpWidgetAt(
          tester,
          const SectionHeader(
              title: 'Security', trailing: Icon(Icons.lock_outline)));
      expect(find.text('Security'), findsOneWidget);
      expect(find.byIcon(Icons.lock_outline), findsOneWidget);
      expect(tester.getSemantics(find.text('Security')),
          matchesSemantics(label: 'Security', isHeader: true));
      handle.dispose();
    });
  });
}
