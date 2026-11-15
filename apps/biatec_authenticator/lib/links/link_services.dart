import 'package:flutter/foundation.dart';

import '../approvals/approval_client.dart';
import '../approvals/approval_services.dart';
import '../approvals/history_store.dart';
import '../codes/add/qr_scan_screen.dart';
import '../config.dart';
import '../enrollment/enrollment_store.dart';
import '../keys/device_key_store.dart';
import '../security/touch_guard.dart';
import 'link_code.dart';
import 'link_controller.dart';

/// A link code another app opened this app with (`biatec-authenticator://link?...`), waiting for the link screen. Opening the app with such a link never links anything: the text only waits here until
/// the signed-in app can show the link screen for it, which is where the user decides. It is treated exactly like a scanned code.
class LinkIntake extends ChangeNotifier {
  String? _pending;

  /// Whether a code is waiting.
  bool get hasPending => _pending != null;

  /// The waiting text, once: it is not kept after it was taken.
  String? take() {
    final p = _pending;
    _pending = null;
    return p;
  }

  /// Another app (or a route name the platform delivered) opened the app with [raw]. The latest one wins.
  void offer(String raw) {
    _pending = raw;
    notifyListeners();
  }

  /// The link code text of a route name the platform delivers for a deep link, or null when the route is something else. The platform may give the full URI or only its path and query, so both are
  /// read; only a text of the app's own scheme or of the `link` route reaches the parser, which then checks it strictly.
  static String? linkTextFromRoute(String route) {
    final r = route.trim();
    if (r.toLowerCase().startsWith('${LinkCode.scheme}:')) return r;
    final m = RegExp(r'^/?link(\?.*)$', dotAll: true).firstMatch(r);
    return m == null ? null : '${LinkCode.scheme}://${LinkCode.host}${m[1]}';
  }
}

/// What the link flow needs, built once from [AppConfig] and replaced in tests. Null in [SettingsServices.links] when `AppConfig.relyingPartiesEnabled` is off: there is then no "Link a service" and
/// no link code is acted on.
class LinkServices {
  LinkServices({
    required this.client,
    required this.keys,
    required this.enrollment,
    required this.history,
    required this.enrolledOrigin,
    this.extraIssuers = const {},
    this.scanner = QrScanScreen.cameraScanner,
    this.touchGuard = const NoTouchGuard(),
    LinkIntake? intake,
    DateTime Function()? clock,
  })  : intake = intake ?? LinkIntake(),
        clock = clock ?? DateTime.now;

  /// The issuer the development mock puts in its link codes (a fixed name, not the URL it is served from). Accepted only by a build against the mock or against a plain `http` server, which only a
  /// debug build accepts at all and only for the developer's own machine (`AppConfig.validateUrl`); a real Biatec OIDC is always `https`, so a real build never has this exception.
  static const mockIssuer = 'https://oidc.biatec.test';

  /// Null when the relying-party protocol is off for this build.
  static LinkServices? fromConfig(AppConfig config, ApprovalServices approvals) =>
      config.relyingPartiesEnabled
          ? LinkServices(
              client: approvals.client,
              keys: approvals.keys,
              enrollment: approvals.enrollment,
              history: approvals.history,
              enrolledOrigin: originOf(config.oidcBaseUrl),
              extraIssuers: config.isMock ||
                      originOf(config.oidcBaseUrl).startsWith('http://')
                  ? const {mockIssuer}
                  : const {},
              touchGuard: approvals.touchGuard,
              clock: approvals.clock,
            )
          : null;

  final ApprovalClient client;
  final DeviceKeyStore keys;
  final EnrollmentStore enrollment;
  final HistoryStore history;

  /// The origin of the Biatec OIDC this app is enrolled with: the only issuer a link code may name.
  final String enrolledOrigin;
  final Set<String> extraIssuers;

  /// The camera view of the scanner; replaced in tests.
  final QrScannerBuilder scanner;
  final TouchGuard touchGuard;
  final LinkIntake intake;
  final DateTime Function() clock;

  /// Parses [raw] strictly. Throws [LinkCodeException].
  LinkCode parse(String raw) => LinkCode.parse(raw,
      enrolledOrigin: enrolledOrigin, extraIssuers: extraIssuers);

  LinkController controllerFor(LinkCode code, {VoidCallback? onLinked}) =>
      LinkController(
        code: code,
        client: client,
        keys: keys,
        enrollment: enrollment,
        history: history,
        onLinked: onLinked,
        clock: clock,
      );
}
