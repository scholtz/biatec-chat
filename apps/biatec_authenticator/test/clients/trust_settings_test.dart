// T-245: the approval threshold, linked services and their detail, against a scripted Biatec OIDC that alone decides what loosens.
import 'package:biatec_authenticator/clients/client_detail_screen.dart';
import 'package:biatec_authenticator/clients/clients_screen.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:biatec_authenticator/settings/threshold_screen.dart';
import 'package:biatec_authenticator/shell/tab_reload.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support.dart';

Future<void> _open(WidgetTester tester, Widget screen,
    {Size size = const Size(400, 1800), Listenable? reload}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1.0;
  addTearDown(tester.view.reset);
  Widget home = screen;
  if (reload != null) home = TabReload(reload: reload, child: home);
  await tester.pumpWidget(MaterialApp(theme: buildTheme(Brightness.light), home: home));
  await tester.pumpAndSettle();
}

Future<void> _type(WidgetTester tester, String key, String text) async {
  await tester.enterText(find.byKey(Key(key)), text);
  await tester.pump();
}

Future<void> _tap(WidgetTester tester, String key) async {
  await tester.ensureVisible(find.byKey(Key(key)));
  await tester.pumpAndSettle();
  await tester.tap(find.byKey(Key(key)));
  await tester.pumpAndSettle();
}

void main() {
  group('approval threshold', () {
    testWidgets('lowering sends no signature and asks for no prompt', (tester) async {
      final rig = TrustRig();
      await _open(tester, ThresholdScreen(services: rig.services));
      await _type(tester, 'threshold-field', '250.50');
      expect(find.text('This lowers it. It applies at once.'), findsOneWidget);
      await _tap(tester, 'threshold-save');
      final put = rig.oidc.puts.single;
      expect(put['change'], {'threshold_eur': '250.50'});
      expect(put.containsKey('consent'), isFalse);
      expect(rig.keys.signed, isEmpty);
      expect(rig.keys.prompts, isEmpty);
      expect(rig.gate.asked, 1, reason: 'rule 4: a fresh biometric for any security change');
      expect(rig.oidc.settings['threshold_eur'], '250.50');
      expect(find.text('Now: 250.50 EUR'), findsOneWidget);
    });

    testWidgets('raising signs with the approval key exactly once', (tester) async {
      final rig = TrustRig();
      await _open(tester, ThresholdScreen(services: rig.services));
      await _tap(tester, 'threshold-choice-5000');
      expect(find.text('Requests under this amount will be signed without asking you.'),
          findsOneWidget);
      await _tap(tester, 'threshold-save');
      // First the plain change (refused by the server, which alone classifies), then the same change signed.
      expect(rig.oidc.puts, hasLength(2));
      expect(rig.oidc.puts[0].containsKey('consent'), isFalse);
      final consent = (rig.oidc.puts[1]['consent'] as Map).cast<String, Object?>();
      final signed = (consent['signed'] as Map).cast<String, Object?>();
      expect(signed['type'], 'biatec_settings_change');
      expect(signed['change'], {'threshold_eur': '5000'});
      expect((consent['device'] as Map)['key_use'], 'approve');
      expect(rig.keys.signed, hasLength(1));
      expect(rig.keys.prompts, hasLength(1));
      expect(rig.gate.asked, 1, reason: 'the gate asks first; the signature itself is made once');
      expect(rig.keys.presenceSigned, isEmpty);
      expect(rig.oidc.settings['threshold_eur'], '5000.00');
    });

    testWidgets('0 shows Always ask; amounts stay strings', (tester) async {
      final rig = TrustRig();
      rig.oidc.settings['threshold_eur'] = '0.00';
      await _open(tester, ThresholdScreen(services: rig.services));
      expect(find.text('Now: Always ask'), findsOneWidget);
      expect(
          tester.widget<ChoiceChip>(find.byKey(const Key('threshold-choice-0'))).selected,
          isTrue);
      // A value no double holds exactly goes to the wire as typed.
      await _type(tester, 'threshold-field', '0.10');
      await _tap(tester, 'threshold-save');
      expect(rig.oidc.puts.last['change'], {'threshold_eur': '0.10'});
    });

    testWidgets('a value that is not an amount is refused before anything is sent',
        (tester) async {
      final rig = TrustRig();
      await _open(tester, ThresholdScreen(services: rig.services));
      await _type(tester, 'threshold-field', '1e3');
      await _tap(tester, 'threshold-save');
      expect(find.text('Enter an amount like 250 or 250.50.'), findsOneWidget);
      expect(rig.oidc.puts, isEmpty);
    });

    testWidgets('a value above an operator maximum shows the server\'s refusal',
        (tester) async {
      final rig = TrustRig();
      rig.oidc.maxThresholdEur = '5000.00';
      await _open(tester, ThresholdScreen(services: rig.services));
      await _type(tester, 'threshold-field', '9000');
      await _tap(tester, 'threshold-save');
      expect(find.textContaining('threshold_eur must be at most 5000.00'), findsOneWidget);
      expect(rig.keys.signed, isEmpty, reason: 'a 400 is not retried with a signature');
      expect(rig.oidc.settings['threshold_eur'], '1000.00');
    });

    testWidgets('Ask on test networks and Block services: the server decides', (tester) async {
      final rig = TrustRig();
      await _open(tester, ThresholdScreen(services: rig.services));
      // Turning "ask" off is loosening: signed once.
      await _tap(tester, 'ask-test-networks');
      expect(rig.oidc.settings['ask_on_test_networks'], isFalse);
      expect(rig.keys.signed, hasLength(1));
      // Turning "block" on is tightening: no signature.
      await _tap(tester, 'block-unproven');
      expect(rig.oidc.settings['block_unproven_clients'], isTrue);
      expect(rig.keys.signed, hasLength(1));
    });

    testWidgets('an inactive device reads everything and changes nothing, with the reason',
        (tester) async {
      final rig = TrustRig(activeAt: DateTime.utc(2030));
      await _open(tester, ThresholdScreen(services: rig.services));
      expect(find.byKey(const Key('device-inactive')), findsOneWidget);
      expect(find.text('This device is not active yet'), findsOneWidget);
      expect(find.text('Now: 1000.00 EUR'), findsOneWidget);
      expect(tester.widget<FilledButton>(find.byKey(const Key('threshold-save'))).onPressed,
          isNull);
      expect(tester.widget<SwitchListTile>(find.byKey(const Key('block-unproven'))).onChanged,
          isNull);
      expect(rig.oidc.puts, isEmpty);
    });

    testWidgets('the screen reloads when the tab is reloaded (settings_changed)',
        (tester) async {
      final rig = TrustRig();
      final reload = ValueNotifier<int>(0);
      await _open(tester, ThresholdScreen(services: rig.services), reload: reload);
      expect(find.text('Now: 1000.00 EUR'), findsOneWidget);
      rig.oidc.settings['threshold_eur'] = '300.00';
      reload.value++;
      await tester.pumpAndSettle();
      expect(find.text('Now: 300.00 EUR'), findsOneWidget);
    });
  });

  group('linked services', () {
    testWidgets('each service is drawn by the identity widget with the date linked',
        (tester) async {
      final rig = TrustRig();
      await _open(tester, ClientsScreen(services: rig.services));
      expect(find.text('chat.biatec.io'), findsOneWidget);
      expect(find.textContaining('You call it: “My chat”'), findsOneWidget);
      expect(find.textContaining('Linked 2026-'), findsWidgets);
      // The fixture's display_name / verified / entity_id are not drawn.
      expect(find.textContaining('Totally Your Bank'), findsNothing);
      expect(find.textContaining('bank-entity'), findsNothing);
      expect(find.textContaining('erified'), findsNothing);
      expect(find.textContaining('Unproven application'), findsOneWidget);
    });

    testWidgets('no linked service is explained', (tester) async {
      final rig = TrustRig();
      rig.oidc.clients.clear();
      await _open(tester, ClientsScreen(services: rig.services));
      expect(find.byKey(const Key('no-clients')), findsOneWidget);
    });

    Future<TrustRig> detail(WidgetTester tester, String id,
        {TrustRig? rig}) async {
      rig ??= TrustRig();
      await _open(tester,
          ClientDetailScreen(services: rig.services, clientId: id),
          size: const Size(400, 3000));
      return rig;
    }

    testWidgets('label edit applies the server\'s text rules before sending',
        (tester) async {
      final rig = await detail(tester, 'bank-example');
      await _tap(tester, 'label-edit');
      await _type(tester, 'label-field', 'x' * 41);
      expect(find.text('At most 40 characters.'), findsOneWidget);
      expect(tester.widget<FilledButton>(find.byKey(const Key('label-save'))).onPressed, isNull);
      await _type(tester, 'label-field', 'Bank\u202E');
      expect(find.textContaining('no line breaks, invisible'), findsOneWidget);
      expect(tester.widget<FilledButton>(find.byKey(const Key('label-save'))).onPressed, isNull);
      await _type(tester, 'label-field', 'My bank');
      await tester.tap(find.byKey(const Key('label-save')));
      await tester.pumpAndSettle();
      expect(rig.oidc.puts.single['change'], {
        'clients': {
          'bank-example': {'label': 'My bank'}
        }
      });
      expect(rig.keys.signed, isEmpty, reason: 'a label is tightening');
      expect(find.textContaining('You call it: “My bank”'), findsOneWidget);
    });

    testWidgets('always ask: on without a signature, off with one', (tester) async {
      final rig = await detail(tester, 'bank-example');
      await _tap(tester, 'always-ask');
      expect(rig.oidc.clientById('bank-example')['always_ask'], isTrue);
      expect(rig.keys.signed, isEmpty);
      await _tap(tester, 'always-ask');
      expect(rig.oidc.clientById('bank-example')['always_ask'], isFalse);
      expect(rig.keys.signed, hasLength(1));
    });

    testWidgets('a threshold of its own, lowered at once and reset by a signature',
        (tester) async {
      final rig = await detail(tester, 'bank-example');
      await _tap(tester, 'threshold-edit');
      await _type(tester, 'amount-field', '100');
      await tester.tap(find.byKey(const Key('amount-save')));
      await tester.pumpAndSettle();
      expect(rig.oidc.clientById('bank-example')['per_client_threshold_eur'], '100.00');
      expect(rig.keys.signed, isEmpty);
      // Following the account threshold again (1000) is a raise: signed.
      await _tap(tester, 'threshold-reset');
      expect(rig.oidc.clientById('bank-example')['per_client_threshold_eur'], isNull);
      expect(rig.keys.signed, hasLength(1));
    });

    testWidgets('a trusted application is removed without a signature', (tester) async {
      final rig = await detail(tester, 'bank-example');
      expect(find.text('Application 1234567 on algorand-mainnet'), findsOneWidget);
      await _tap(tester, 'remove-app-algorand-mainnet-1234567');
      expect(rig.oidc.puts.single['change'], {
        'clients': {
          'bank-example': {
            'trusted_applications_remove': [
              {'network': 'algorand-mainnet', 'app_id': 1234567}
            ]
          }
        }
      });
      expect(rig.keys.signed, isEmpty);
      expect(find.text('Application 1234567 on algorand-mainnet'), findsNothing);
      expect(find.text('Application 7654321 on algorand-testnet'), findsOneWidget);
    });

    testWidgets('lifting a pause is signed, with the pause\'s origin', (tester) async {
      final oidc = FakeTrustOidc();
      final until = oidc.nowSeconds + 3600;
      oidc.clients[1]
        ..['paused_until'] = until
        ..['pauses'] = [
          {'origin': 'https://bank.example', 'paused_until': until}
        ];
      final rig = await detail(tester, 'bank-example', rig: TrustRig(oidc: oidc));
      expect(find.byKey(const Key('paused-bank-example-https://bank.example')),
          findsOneWidget);
      await _tap(tester, 'lift-bank-example-https://bank.example');
      expect(find.text('Your phone will ask for you'), findsOneWidget);
      await _tap(tester, 'confirm-change');
      expect(rig.keys.signed, hasLength(1));
      expect(oidc.clientById('bank-example')['paused_until'], isNull);
      final signed = ((oidc.puts.last['consent'] as Map)['signed'] as Map)['change'];
      expect(signed, {
        'clients': {
          'bank-example': {'paused': false, 'origin': 'https://bank.example'}
        }
      });
    });

    testWidgets('unlink asks first, applies without a signature and the service is gone',
        (tester) async {
      final rig = await detail(tester, 'bank-example');
      await _tap(tester, 'unlink');
      expect(find.text('Unlink this service?'), findsOneWidget);
      await tester.tap(find.byKey(const Key('keep')));
      await tester.pumpAndSettle();
      expect(rig.oidc.puts, isEmpty);
      await _tap(tester, 'unlink');
      await _tap(tester, 'confirm-unlink');
      expect(rig.oidc.puts.single['change'], {
        'clients': {
          'bank-example': {'unlink': true}
        }
      });
      expect(rig.keys.signed, isEmpty);
      expect(rig.oidc.clients.map((c) => c['client_id']), isNot(contains('bank-example')));
    });

    testWidgets('an inactive device sees the detail read-only', (tester) async {
      final rig = await detail(tester, 'bank-example',
          rig: TrustRig(activeAt: DateTime.utc(2030)));
      expect(find.byKey(const Key('device-inactive')), findsOneWidget);
      for (final key in ['label-edit', 'threshold-edit', 'unlink']) {
        await tester.ensureVisible(find.byKey(Key(key)));
        final w = tester.widget(find.byKey(Key(key)));
        expect((w as dynamic).onPressed, isNull, reason: key);
      }
      expect(rig.oidc.puts, isEmpty);
    });
  });

  group('controller', () {
    test('a refusal that is not consent_required is shown and not retried', () async {
      final rig = TrustRig();
      final c = rig.controller();
      await c.load();
      rig.oidc.deviceInactive = true;
      expect(await c.apply({'threshold_eur': '10'}), isFalse);
      expect(c.error, contains('this device is not active yet'));
      expect(rig.oidc.puts, hasLength(1));
      expect(c.phase, SettingsPhase.ready);
    });
  });
}
