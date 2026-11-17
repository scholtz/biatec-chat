import 'package:biatec_authenticator/clients/label_rules.dart';
import 'package:biatec_authenticator/clients/service_identity.dart';
import 'package:biatec_authenticator/clients/trust_strings.dart';
import 'package:biatec_authenticator/security/settings_client.dart';
import 'package:biatec_authenticator/theme.dart';
import 'package:material_ui/material_ui.dart';
import 'package:flutter_test/flutter_test.dart';

Future<void> _show(WidgetTester tester, Widget w) => tester.pumpWidget(
    MaterialApp(theme: buildTheme(Brightness.light), home: Scaffold(body: w)));

void main() {
  testWidgets('a proven domain is shown in ASCII form with the fingerprint',
      (tester) async {
    await _show(
        tester,
        const ServiceIdentity(
            domainProven: true,
            domain: 'xn--bcher-kva.example',
            fingerprint: '68dcf7ce89a943b0'));
    expect(find.text('xn--bcher-kva.example'), findsOneWidget);
    expect(find.textContaining('68dc-f7ce-89a9-43b0'), findsOneWidget);
    expect(find.byKey(const Key('client-unverified')), findsNothing);
    expect(find.byKey(const Key('service-label')), findsNothing);
  });

  testWidgets('a domain that is not plain ASCII is never drawn as proven',
      (tester) async {
    await _show(
        tester,
        const ServiceIdentity(
            domainProven: true,
            domain: 'bücher.example',
            fingerprint: '68dcf7ce89a943b0'));
    expect(find.text('bücher.example'), findsNothing);
    expect(find.text('Unproven application 68dc-f7ce-89a9-43b0'), findsOneWidget);
  });

  testWidgets('an unproven application is the warning style with the fingerprint',
      (tester) async {
    await _show(
        tester,
        const ServiceIdentity(
            domainProven: false, fingerprint: '68dcf7ce89a943b0'));
    final banner = find.byKey(const Key('client-unverified'));
    expect(banner, findsOneWidget);
    expect(find.text('Unproven application 68dc-f7ce-89a9-43b0'), findsOneWidget);
    expect(find.descendant(of: banner, matching: find.byIcon(Icons.warning_amber_rounded)),
        findsOneWidget);
  });

  testWidgets('the user label is in another style, beside the domain, never instead',
      (tester) async {
    await _show(
        tester,
        const ServiceIdentity(
            domainProven: true,
            domain: 'chat.biatec.io',
            fingerprint: '68dcf7ce89a943b0',
            label: 'My chat'));
    expect(find.text('chat.biatec.io'), findsOneWidget);
    final label = find.descendant(
        of: find.byKey(const Key('service-label')),
        matching: find.byType(Text));
    final text = tester.widget<Text>(label);
    expect(text.data, 'You call it: “My chat”');
    expect(text.style!.fontStyle, FontStyle.italic);
    final domain = tester.widget<Text>(find.byKey(const Key('service-domain')));
    expect(domain.style!.fontStyle, isNot(FontStyle.italic));
  });

  testWidgets('a label that breaks the rules is not drawn', (tester) async {
    await _show(
        tester,
        const ServiceIdentity(
            domainProven: true,
            domain: 'chat.biatec.io',
            fingerprint: '68dcf7ce89a943b0',
            label: 'Bank\u202E'));
    expect(find.byKey(const Key('service-label')), findsNothing);
  });

  testWidgets('a settings fixture with display_name, verified and entity_id renders none of them',
      (tester) async {
    final client = ClientSetting.fromJson({
      'client_id': 'bank-example',
      'fingerprint': '0123456789abcdef',
      'domain_proven': false,
      'domain': 'evil.example',
      'display_name': 'Your Trusted Bank',
      'verified': true,
      'entity_id': 'https://trusted.bank',
      'name': 'Official Bank',
      'always_ask': false,
      'per_client_threshold_eur': null,
    });
    // A domain is taken only from a service that proved it.
    expect(client.domain, isNull);
    await _show(tester, ServiceIdentity.setting(client));
    for (final leaked in [
      'Your Trusted Bank',
      'https://trusted.bank',
      'Official Bank',
      'evil.example',
      'erified',
    ]) {
      expect(find.textContaining(leaked), findsNothing, reason: leaked);
    }
    expect(find.text('Unproven application 0123-4567-89ab-cdef'), findsOneWidget);
  });

  test('a server fingerprint is 16 lowercase hex characters or it is replaced by the local one', () {
    for (final bad in ['Totally Your Bank', '68DCF7CE89A943B0', '68dcf7ce', '68dcf7ce89a943b0ff', '']) {
      final c = ClientSetting.fromJson({
        'client_id': 'bank-example',
        'fingerprint': bad,
        'always_ask': false,
      });
      expect(c.fingerprint, fingerprintOfClientId('bank-example'), reason: bad);
    }
    final ok = ClientSetting.fromJson({
      'client_id': 'x',
      'fingerprint': '68dcf7ce89a943b0',
      'always_ask': false,
    });
    expect(ok.fingerprint, '68dcf7ce89a943b0');
    expect(ok.linked, isTrue);
    expect(ClientSetting.fromJson({'client_id': 'x', 'linked': false, 'always_ask': false}).linked,
        isFalse);
  });

  testWidgets('a request fingerprint that is not hex is not drawn as an identity',
      (tester) async {
    await _show(
        tester,
        const ServiceIdentity(domainProven: false, fingerprint: 'Your Trusted Bank'));
    expect(find.text('Unproven application'), findsOneWidget);
    expect(find.textContaining('Trusted'), findsNothing);
  });

  test('a pause with an origin that is not canonical is dropped', () {
    final c = ClientSetting.fromJson({
      'client_id': 'x',
      'always_ask': false,
      'pauses': [
        {'origin': 'https://ok.example', 'paused_until': 1792429200},
        {'origin': 'https://EVIL.example', 'paused_until': 1792429200},
        {'origin': 'https://ok.example:443', 'paused_until': 1792429200},
        {'origin': 'javascript:alert(1)', 'paused_until': 1792429200},
        {'origin': 'https://a.example/path', 'paused_until': 1792429200},
      ],
    });
    expect(c.pauses.map((p) => p.origin), ['https://ok.example']);
  });

  test('placeholders are substituted in one pass', () {
    expect(TrustStrings.en.t('pausedOrigin', {'origin': 'https://{when}.example', 'when': 'X'}),
        'https://{when}.example is paused until X');
  });

  group('label rules (the server\'s isDisplaySafe)', () {
    test('accepts ordinary text, Slovak letters, emoji', () {
      for (final ok in ['My chat', 'Banka čistá', 'Work \u{1F4BC}', 'a' * 40]) {
        expect(labelProblem(ok), isNull, reason: ok);
      }
    });
    test('refuses what the server refuses', () {
      expect(labelProblem(''), LabelProblem.empty);
      expect(labelProblem('   '), LabelProblem.empty);
      expect(labelProblem('a' * 41), LabelProblem.tooLong);
      expect(labelProblem(' x'), LabelProblem.padded);
      for (final bad in [
        'a\nb', // line break
        'a\u202Eb', // right-to-left override
        'a​b', // zero width space
        'a\u0000b', // control
        'a  b', // run of spaces
        'é', // a mark after a letter that has a precomposed form
        'a­b', // soft hyphen
        'a﻿b',
        '\uD800',
      ]) {
        expect(labelProblem(bad), isNotNull, reason: bad.runes.toList().toString());
      }
    });
  });

  test('English and Slovak have the same keys', () {
    expect(TrustStrings.sk.keys.toSet(), TrustStrings.en.keys.toSet());
    expect(TrustStrings.sk.t('newServiceTitle'), 'Nová služba');
    expect(TrustStrings.of(const Locale('sk')).t('alwaysAsk'), 'Vždy sa pýtať');
  });
}
