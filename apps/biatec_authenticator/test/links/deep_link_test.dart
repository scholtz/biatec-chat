// T-247: the `biatec-authenticator://link` deep link from another app is treated exactly like a scanned code: it waits for the signed-in app, is parsed strictly, shows the link screen (or why the code is
// refused) and never links anything by itself. And "Link a service" in Settings, Linked services opens the scanner and leads to the same screen.
import 'package:biatec_authenticator/approvals/approval_client.dart';
import 'package:biatec_authenticator/approvals/history_store.dart';
import 'package:biatec_authenticator/config.dart';
import 'package:biatec_authenticator/links/link_services.dart';
import 'package:biatec_authenticator/main.dart';
import 'package:biatec_authenticator/security/settings_controller.dart';
import 'package:biatec_authenticator/shell/shell_controller.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import '../codes/support.dart';
import '../shell/app_shell_test.dart' show ShellRig;
import 'link_flow_test.dart' show FakeLinkServer;

const _issuer = 'https://oidc.biatec.test';
const _linkId = 'lnk_6f24862262c182126431e3a6';
const _secret = 'c6cWUY11Jq3nvnZ0yLm821tPt4OJSKXr_j070m1UOL4';
final _uri =
    'biatec-authenticator://link?iss=${Uri.encodeQueryComponent(_issuer)}&id=$_linkId&s=$_secret';

DateTime _now() =>
    DateTime.fromMillisecondsSinceEpoch(1792425630 * 1000, isUtc: true);

class _Rig {
  _Rig({this.enabled = true}) {
    shellRig = ShellRig(pending: 0);
    server = FakeLinkServer();
    links = LinkServices(
      client: ApprovalClient(
          baseUrl: 'http://oidc', client: server.client, relyingParties: true),
      keys: shellRig.settings.keys,
      enrollment: shellRig.settings.enrollment,
      history: MemoryHistoryStore(),
      enrolledOrigin: _issuer,
      scanner: (context, onScanned) => TextButton(
          key: const Key('fake-scan'),
          onPressed: () => onScanned(scanned),
          child: const Text('scan')),
      clock: _now,
    );
    settings = SettingsServices(
      client: shellRig.settings.services.client,
      keys: shellRig.settings.keys,
      enrollment: shellRig.settings.enrollment,
      gate: shellRig.settings.gate,
      trustSettings: true,
      links: enabled ? links : null,
      clock: shellRig.settings.services.clock,
    );
  }

  final bool enabled;
  late final ShellRig shellRig;
  late final FakeLinkServer server;
  late final LinkServices links;
  late final SettingsServices settings;

  /// What the fake scanner reads.
  String scanned = _uri;

  Future<void> pump(WidgetTester tester) async {
    final codes = await CodesRig.create();
    tester.view.physicalSize = const Size(400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(AuthenticatorApp(
      config: AppConfig.resolve(''),
      lock: codes.lock,
      codes: codes.services,
      approvals: shellRig.approvals,
      settings: settings,
      shell: shellRig.shell,
    ));
    await tester.pump();
    await tester.pump();
  }

  Future<void> deepLink(WidgetTester tester, String uri) async {
    // What the engine does when the operating system opens the app with a link: a `pushRouteInformation` call on the navigation channel.
    await tester.binding.defaultBinaryMessenger.handlePlatformMessage(
        'flutter/navigation',
        const JSONMethodCodec().encodeMethodCall(
            MethodCall('pushRouteInformation', {'location': uri})),
        (_) {});
    await tester.pump();
    await tester.pump();
    await tester.pump();
  }
}

void main() {
  testWidgets('a deep link opens the link screen, and nothing is sent until the user acts', (tester) async {
    final rig = _Rig();
    await rig.pump(tester);
    await rig.deepLink(tester, _uri);
    expect(find.byKey(const Key('link-what')), findsOneWidget);
    expect(find.text('bank.example'), findsOneWidget);
    // Fetched for display; nothing posted, no prompt.
    expect(rig.server.fetches, 1);
    expect(rig.server.posted, isEmpty);
    expect(rig.shellRig.settings.keys.prompts, isEmpty);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a deep link that arrived before the app was up waits and is shown once the signed-in app is', (tester) async {
    final rig = _Rig();
    rig.links.intake.offer(_uri);
    await rig.pump(tester);
    await tester.pump();
    expect(find.byKey(const Key('link-what')), findsOneWidget);
    expect(rig.links.intake.hasPending, isFalse);
    expect(rig.server.posted, isEmpty);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('the same link delivered twice (the launch route and a pushed route) opens one link screen', (tester) async {
    final rig = _Rig();
    await rig.pump(tester);
    await rig.deepLink(tester, _uri);
    await rig.deepLink(tester, _uri);
    expect(find.byKey(const Key('link-what')), findsOneWidget);
    expect(rig.server.fetches, 1);
    await tester.pumpWidget(const SizedBox());
  });

  test('the same text twice in a moment is one delivery; after that the same link can be opened again', () {
    var now = DateTime.utc(2026, 11, 15, 12);
    final intake = LinkIntake(clock: () => now);
    var heard = 0;
    intake.addListener(() => heard++);
    intake.offer('a'); // the launch route
    expect(intake.take(), 'a');
    now = now.add(const Duration(seconds: 1));
    intake.offer('a'); // the route pushed right after
    expect(intake.hasPending, isFalse);
    expect(heard, 1);
    // Later (the link screen was closed): the same QR code opens again.
    now = now.add(const Duration(minutes: 1));
    intake.offer('a');
    expect(intake.take(), 'a');
    expect(heard, 2);
  });

  testWidgets('a deep link for another authenticator service is refused on a screen and nothing is fetched',
      (tester) async {
    final rig = _Rig();
    await rig.pump(tester);
    await rig.deepLink(
        tester,
        'biatec-authenticator://link?iss=https%3A%2F%2Foidc.evil.example&id=$_linkId&s=$_secret');
    expect(find.text('This code is for a different authenticator service'),
        findsOneWidget);
    expect(rig.server.fetches, 0, reason: 'the secret is sent nowhere');
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a deep link with an extra parameter is not a link code', (tester) async {
    final rig = _Rig();
    await rig.pump(tester);
    await rig.deepLink(tester, '$_uri&x=1');
    expect(find.text('This is not a code for linking a service.'),
        findsOneWidget);
    expect(rig.server.fetches, 0);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('the path form of the route (the platform may drop the scheme) leads to the same screen', (tester) async {
    final rig = _Rig();
    await rig.pump(tester);
    await rig.deepLink(tester, '/link${_uri.substring(_uri.indexOf('?'))}');
    expect(find.byKey(const Key('link-what')), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('with the relying-party protocol off a link code is dropped: no screen, no call', (tester) async {
    final rig = _Rig(enabled: false);
    await rig.pump(tester);
    await rig.deepLink(tester, _uri);
    expect(find.byKey(const Key('link-what')), findsNothing);
    expect(find.text('Link a service'), findsNothing);
    expect(rig.server.fetches, 0);
    // Dropped with feedback, not silently.
    expect(find.text('This build cannot link services.'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('Settings, Linked services: "Link a service" scans, and a scanned code leads to the link screen', (tester) async {
    final rig = _Rig();
    await rig.pump(tester);
    rig.shellRig.shell.select(AppTab.settings);
    await tester.pump();
    await tester.pump();
    await tester.tap(find.byKey(const Key('linked-services')));
    await tester.pump();
    await tester.pump();
    await tester.pump();
    expect(find.byKey(const Key('link-a-service')), findsOneWidget);
    await tester.tap(find.byKey(const Key('link-a-service')));
    await tester.pump();
    await tester.pump();
    await tester.tap(find.byKey(const Key('fake-scan')));
    await tester.pump();
    await tester.pump();
    await tester.pump();
    await tester.pump();
    expect(find.byKey(const Key('link-what')), findsOneWidget);
    expect(rig.server.posted, isEmpty);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('a scanned code for another service says so and sends nothing', (tester) async {
    final rig = _Rig()..scanned = 'biatec-authenticator://link?iss=https%3A%2F%2Fother.example&id=$_linkId&s=$_secret';
    await rig.pump(tester);
    rig.shellRig.shell.select(AppTab.settings);
    await tester.pump();
    await tester.pump();
    await tester.tap(find.byKey(const Key('linked-services')));
    await tester.pump();
    await tester.pump();
    await tester.pump();
    await tester.tap(find.byKey(const Key('link-a-service')));
    await tester.pump();
    await tester.pump();
    await tester.tap(find.byKey(const Key('fake-scan')));
    await tester.pump();
    await tester.pump();
    await tester.pump();
    await tester.pump();
    expect(find.text('This code is for a different authenticator service'),
        findsOneWidget);
    expect(rig.server.fetches, 0);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('without the protocol, Linked services has no "Link a service"', (tester) async {
    final rig = _Rig(enabled: false);
    await rig.pump(tester);
    rig.shellRig.shell.select(AppTab.settings);
    await tester.pump();
    await tester.pump();
    await tester.tap(find.byKey(const Key('linked-services')));
    await tester.pump();
    await tester.pump();
    await tester.pump();
    expect(find.byKey(const Key('link-a-service')), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });
}
