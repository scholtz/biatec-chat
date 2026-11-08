import 'dart:async';

import 'package:biatec_authenticator/approvals/approval_screen.dart';
import 'package:biatec_authenticator/approvals/approval_services.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/enrollment/enrollment_screen.dart';
import 'package:biatec_authenticator/enrollment/enrollment_state.dart';
import 'package:biatec_authenticator/enrollment/enrollment_store.dart';
import 'package:biatec_authenticator/enrollment/observable_enrollment_store.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/shell/shell_controller.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import '../lock/support.dart';

import 'app_shell_test.dart' show ShellRig;

final _active = EnrollmentRecord(
    kid: 'dev-1',
    deviceToken: 'dev-token',
    activeAt: DateTime.utc(2020),
    attestation: 'att',
    recoveryCodeShown: true);

/// A store that cannot be read until [broken] is cleared.
class _BrokenStore extends MemoryEnrollmentStore {
  bool broken = true;

  @override
  Future<EnrollmentRecord?> read() async {
    if (broken) throw StateError('keystore locked');
    return super.read();
  }
}

/// Writes or clears fail while asked to.
class _FlakyWrites extends MemoryEnrollmentStore {
  bool failWrites = false;
  bool failClear = false;
  int writes = 0;

  @override
  Future<void> write(EnrollmentRecord record) async {
    writes++;
    if (failWrites) throw StateError('keystore busy');
    return super.write(record);
  }

  @override
  Future<void> clear() async {
    if (failClear) throw StateError('keystore busy');
    return super.clear();
  }
}

/// A read started while [hang] is set waits for [release], then fails when [failAfterHang] is set.
class _HangOnce extends MemoryEnrollmentStore {
  bool hang = false;
  bool failAfterHang = false;
  final release = Completer<void>();

  @override
  Future<EnrollmentRecord?> read() async {
    final hangs = hang;
    final record = await super.read();
    if (hangs) {
      await release.future;
      if (failAfterHang) throw StateError('keystore locked');
    }
    return record;
  }
}

class _CountingReads extends MemoryEnrollmentStore {
  int reads = 0;

  @override
  Future<EnrollmentRecord?> read() {
    reads++;
    return super.read();
  }
}

/// Holds each write until [gate] completes.
class _SlowWrites extends MemoryEnrollmentStore {
  final gate = Completer<void>();

  @override
  Future<void> write(EnrollmentRecord record) async {
    await gate.future;
    return super.write(record);
  }
}

ApprovalServices _servicesWith(ShellRig rig, EnrollmentStore store) =>
    ApprovalServices(
      client: rig.approvals.client,
      keys: rig.approvals.keys,
      enrollment: store is ObservableEnrollmentStore
          ? store
          : ObservableEnrollmentStore(store),
      gate: rig.approvals.gate,
      history: MemoryHistoryStore(),
      clock: rig.approvals.clock,
    );

Future<void> _pumpApp(
    WidgetTester tester, ShellRig rig, ApprovalServices approvals) async {
  tester.view.physicalSize = const Size(400, 900);
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(AuthenticatorApp(
    config: AppConfig.resolve(''),
    lock: await unlockedLock(),
    approvals: approvals,
    settings: rig.settings.services,
    shell: rig.shell,
  ));
  await tester.pump();
  await tester.pump();
  await tester.pump();
}

void main() {
  group('signing out and storage failures', () {
    testWidgets(
        'a store that announces its change sends a signed-out device to enrollment at once',
        (tester) async {
      final rig = ShellRig();
      final store = rig.settings.enrollment;
      await _pumpApp(tester, rig, _servicesWith(rig, store));
      expect(find.byType(NavigationBar), findsOneWidget);

      // Devices cancelling this device clears the store; no tab change follows.
      await store.clear();
      expect(store.clearCount, 1);
      await tester.pump();
      await tester.pump();
      await tester.pump();
      expect(find.byType(EnrollmentScreen), findsOneWidget);
      expect(find.byType(NavigationBar), findsNothing);
      expect(await store.read(), isNull);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets(
        'a sign-out holds even when the store cannot be read afterwards',
        (tester) async {
      final rig = ShellRig();
      final inner = _BrokenStore()..broken = false;
      await inner.write(_active);
      final store = ObservableEnrollmentStore(inner);
      await _pumpApp(tester, rig, _servicesWith(rig, store));
      expect(find.byType(NavigationBar), findsOneWidget);

      await store.clear();
      inner.broken = true;
      await tester.pump();
      await tester.pump();
      await tester.pump();
      expect(find.byType(NavigationBar), findsNothing);
      expect(find.byType(EnrollmentScreen), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets(
        'a store that cannot be read gives a retry, not the enrollment flow',
        (tester) async {
      final rig = ShellRig();
      final broken = _BrokenStore();
      await broken.write(EnrollmentRecord(
          kid: 'dev-1',
          deviceToken: 'dev-token',
          activeAt: DateTime.utc(2020),
          attestation: 'att',
          recoveryCodeShown: true));
      await _pumpApp(tester, rig, _servicesWith(rig, broken));
      expect(find.text('Could not read this device'), findsOneWidget);
      expect(find.byKey(const Key('mock-notice')), findsOneWidget,
          reason: 'a mock build says so before the shell exists too');
      expect(find.byType(EnrollmentScreen), findsNothing);

      broken.broken = false;
      await tester.tap(find.text('Try again'));
      await tester.pump();
      await tester.pump();
      await tester.pump();
      expect(find.byType(NavigationBar), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });
  });

  testWidgets('the enrollment screen of a mock build says so too',
      (tester) async {
    final rig = ShellRig(enrolled: false);
    await _pumpApp(tester, rig, rig.approvals);
    expect(find.byType(EnrollmentScreen), findsOneWidget);
    expect(find.byKey(const Key('mock-notice')), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets(
      'a resume that reads the store and finds no enrollment leaves the "could not read" screen',
      (tester) async {
    final rig = ShellRig(enrolled: false);
    final broken = _BrokenStore();
    await _pumpApp(tester, rig, _servicesWith(rig, broken));
    expect(find.text('Could not read this device'), findsOneWidget);

    // The keystore opens, and the device turns out never to have been enrolled.
    broken.broken = false;
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await tester.pump();
    await tester.pump();
    await tester.pump();
    expect(find.text('Could not read this device'), findsNothing);
    expect(find.byType(EnrollmentScreen), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets(
      'an enrollment kept in memory after a failed write is not mistaken for a sign-out',
      (tester) async {
    final rig = ShellRig();
    final inner = _FlakyWrites()..failWrites = true;
    final store = ObservableEnrollmentStore(inner);
    // Enrolled, but the write never landed: the store keeps it in memory.
    await store.write(_active);
    await _pumpApp(tester, rig, _servicesWith(rig, store));
    expect(find.byType(NavigationBar), findsOneWidget);

    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await tester.pump();
    await tester.pump();
    await tester.pump();
    expect(find.byType(NavigationBar), findsOneWidget);
    expect(find.byType(EnrollmentScreen), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });

  group('ObservableEnrollmentStore', () {
    test('tells listeners about writes and clears, and passes the rest through',
        () async {
      final store = ObservableEnrollmentStore(MemoryEnrollmentStore());
      var n = 0;
      store.addListener(() => n++);
      final record = EnrollmentRecord(
          kid: 'k', deviceToken: 't', activeAt: DateTime.utc(2020));
      await store.write(record);
      expect(n, 1);
      expect((await store.read())?.kid, 'k');
      await store.writeAlias('alias');
      expect(await store.readAlias(), 'alias');
      await store.clearAlias();
      expect(await store.readAlias(), isNull);
      expect(n, 1, reason: 'aliases are not the enrollment');
      await store.clear();
      expect(n, 2);
      expect(store.clearCount, 1);
      expect(await store.read(), isNull);
      store.dispose();
    });

    test(
        'an enrollment whose write failed is served from memory and saved later',
        () async {
      var now = DateTime.utc(2026, 10, 29, 12);
      final inner = _FlakyWrites();
      final store = ObservableEnrollmentStore(inner, clock: () => now);
      inner.failWrites = true;
      await store.write(_active); // completes against memory, never throws
      await store.flush();
      expect(store.unsaved, isTrue);
      expect((await store.read())?.kid, 'dev-1',
          reason: 'the pending list and settings see the device enrolled');
      expect(await inner.read(), isNull);
      inner.failWrites = false;
      now = now.add(ObservableEnrollmentStore.saveRetryEvery);
      expect((await store.read())?.kid, 'dev-1');
      await store.flush();
      expect((await inner.read())?.kid, 'dev-1', reason: 'saved by the read');
      expect(store.unsaved, isFalse);
      store.dispose();
    });

    test('an unsaved newer record wins over an older one on disk', () async {
      final inner = _FlakyWrites();
      final store = ObservableEnrollmentStore(inner);
      await store.write(_active);
      await store.flush();
      inner.failWrites = true;
      final shown = _active.withCodeShown();
      await store.write(shown);
      expect((await store.read())?.recoveryCodeShown, isTrue);
      store.dispose();
    });

    test('the disk is read once; after that memory answers', () async {
      final inner = _CountingReads();
      await inner.write(_active);
      final store = ObservableEnrollmentStore(inner);
      expect((await store.read())?.kid, 'dev-1');
      expect((await store.read())?.kid, 'dev-1');
      expect(inner.reads, 1);
      store.dispose();
    });

    test(
        'a read that hangs does not hold up the next, and a sign-out during a read wins',
        () async {
      final inner = _HangOnce();
      await inner.write(_active);
      final store = ObservableEnrollmentStore(inner);
      inner.hang = true;
      final first = store.read();
      inner.hang = false;
      await store.clear();
      expect(await store.read(), isNull,
          reason: 'known at once, without waiting for the disk');
      inner.release.complete();
      expect(await first, isNull, reason: 'it ran across the sign-out');
      store.dispose();
    });

    test('a failed first read does not escape when a write happened meanwhile',
        () async {
      final inner = _HangOnce()..failAfterHang = true;
      await inner.write(_active);
      final store = ObservableEnrollmentStore(inner);
      inner.hang = true;
      final first = store.read();
      inner.hang = false;
      await store.write(_active.withCodeShown());
      inner.release.complete();
      expect((await first)?.recoveryCodeShown, isTrue,
          reason: 'the write is the answer');
      store.dispose();
    });

    test('a failed first read with nothing known is the caller\'s error',
        () async {
      final inner = _HangOnce()..failAfterHang = true;
      inner.release.complete();
      final store = ObservableEnrollmentStore(inner);
      inner.hang = true;
      await expectLater(store.read(), throwsA(anything));
      inner.failAfterHang = false;
      inner.hang = false;
      expect(await store.read(), isNull, reason: 'the next read tries again');
      store.dispose();
    });

    test('a clear that fails still signs the device out, for every reader',
        () async {
      var now = DateTime.utc(2026, 10, 29, 12);
      final inner = _FlakyWrites();
      final store = ObservableEnrollmentStore(inner, clock: () => now);
      await store.write(_active);
      await store.flush();
      var told = 0;
      store.addListener(() => told++);
      inner.failClear = true;
      await store.clear(); // never throws
      expect(store.clearCount, 1);
      expect(told, 1);
      expect(await store.read(), isNull,
          reason: 'the record left on disk is not handed out');
      inner.failClear = false;
      now = now.add(ObservableEnrollmentStore.saveRetryEvery);
      expect(await store.read(), isNull);
      await store.flush();
      expect(await inner.read(), isNull, reason: 'the clear was tried again');
      store.dispose();
    });

    test('a write that hangs does not hold up a later write or a sign-out',
        () async {
      final inner = _SlowWrites();
      final store = ObservableEnrollmentStore(inner);
      await store.write(_active); // the disk step hangs; this still completes
      final shown = _active.withCodeShown();
      await store.write(shown);
      expect((await store.read())?.recoveryCodeShown, isTrue);
      await store.clear();
      expect(await store.read(), isNull);
      inner.gate.complete();
      await store.flush();
      expect(await inner.read(), isNull,
          reason: 'the newest state is what ends up on disk');
      store.dispose();
    });

    test('a write that lands after a sign-out is taken out again', () async {
      final inner = _SlowWrites();
      final store = ObservableEnrollmentStore(inner);
      final writing = store.write(_active);
      final clearing = store.clear();
      expect(store.clearCount, 1, reason: 'announced at once');
      inner.gate.complete();
      await writing;
      await clearing;
      await store.flush();
      expect(await inner.read(), isNull);
      expect(await store.read(), isNull);
      store.dispose();
    });

    test('an unsaved enrollment is saved at most every so often', () async {
      var now = DateTime.utc(2026, 10, 29, 12);
      final inner = _FlakyWrites()..failWrites = true;
      final store = ObservableEnrollmentStore(inner, clock: () => now);
      await store.write(_active);
      await store.flush();
      final tries = inner.writes;
      for (var i = 0; i < 5; i++) {
        expect((await store.read())?.kid, 'dev-1');
      }
      await store.flush();
      expect(inner.writes, tries, reason: 'no write on every read');
      now = now.add(ObservableEnrollmentStore.saveRetryEvery);
      await store.read();
      await store.flush();
      expect(inner.writes, tries + 1);
      store.dispose();
    });
  });

  group('queued notification', () {
    testWidgets('one that waited too long is not opened', (tester) async {
      final rig = ShellRig();
      var now = DateTime.utc(2026, 10, 24, 12);
      rig.shell.clock = () => now;
      rig.shell.openApproval(rig.approvals, rig.requestId);
      now = now.add(ShellController.queuedOpenTtl + const Duration(seconds: 1));
      await rig.pump(tester);
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsNothing);
      await rig.dispose(tester);
    });

    testWidgets('one that was dropped is not opened', (tester) async {
      final rig = ShellRig();
      rig.shell.openApproval(rig.approvals, rig.requestId);
      rig.shell.dropQueuedOpen();
      await rig.pump(tester);
      await tester.pump(const Duration(seconds: 1));
      expect(find.byType(ApprovalScreen), findsNothing);
      await rig.dispose(tester);
    });

    testWidgets('a launch that ends in enrollment forgets it', (tester) async {
      final rig = ShellRig(enrolled: false);
      rig.shell.openApproval(rig.approvals, rig.requestId);
      await rig.pump(tester);
      expect(find.byType(EnrollmentScreen), findsOneWidget);
      // Were it still queued, the shell would open it as soon as it is shown.
      rig.shell.openQueued();
      expect(rig.shell.navigators[AppTab.approvals]!.currentState, isNull);
      await rig.dispose(tester);
    });
  });

  group('layout', () {
    testWidgets('a phone on its side keeps the bar: the rail needs height',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester, size: const Size(900, 400));
      expect(find.byType(NavigationBar), findsOneWidget);
      expect(find.byType(NavigationRail), findsNothing);
      await rig.dispose(tester);
    });

    testWidgets('the bar steps aside while the keyboard is open',
        (tester) async {
      final rig = ShellRig();
      await rig.pump(tester);
      expect(find.byType(NavigationBar), findsOneWidget);
      tester.view.viewInsets = const FakeViewPadding(bottom: 300);
      addTearDown(tester.view.resetViewInsets);
      await tester.pump();
      expect(find.byType(NavigationBar), findsNothing);
      tester.view.resetViewInsets();
      await tester.pump();
      expect(find.byType(NavigationBar), findsOneWidget);
      await rig.dispose(tester);
    });
  });

  testWidgets(
      'status colors follow the brightness when the theme has no extension',
      (tester) async {
    late StatusColors seen;
    await tester.pumpWidget(MaterialApp(
        theme: ThemeData(brightness: Brightness.dark),
        home: Builder(builder: (context) {
          seen = context.statusColors;
          return const SizedBox();
        })));
    expect(seen.danger, StatusColors.dark.danger);
  });
}
