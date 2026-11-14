import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

import '../approvals/approval_client.dart';
import '../config.dart';
import '../enrollment/enrollment_store.dart';
import '../keys/device_key_store.dart';
import '../push/device_event_controller.dart';
import '../security/device_proof.dart';
import '../security/pinning/pinned_http.dart';
import '../security/pinning/pins.dart';
import 'channel_wiring.dart';
import 'local_notifier.dart';

/// What the Android foreground service (`StayConnectedService`) runs in its own Flutter engine, with no UI, while the user has "Stay connected for approvals" on.
///
/// It builds the same pieces as the app: the pinned HTTP client, the keystore (the native service registers `KeystorePlugin` on this engine, so the ticket request is signed with the device key exactly as in the app) and the enrollment in the secure storage. A request that arrives while the app is not in front shows a notification with fixed text; while it is, the app shows it itself.
@pragma('vm:entry-point')
Future<void> stayConnectedMain() async {
  WidgetsFlutterBinding.ensureInitialized();
  const service = MethodChannel('io.biatec.authenticator/stay_connected_service');
  Future<void> stopSelf() async {
    try {
      await service.invokeMethod<void>('stopSelf');
    } on Object {
      // The service is going away anyway.
    }
  }

  final AppConfig config;
  try {
    config = AppConfig.fromEnvironment();
    PinnedHttp.configure(pinPolicyFor(config));
  } on ConfigException {
    // An unsafe URL: the app does not start either, so there is nothing to stay connected to.
    await stopSelf();
    return;
  }
  const keys = PlatformDeviceKeyStore();
  final enrollment = SecureEnrollmentStore();
  // The same proof of possession as every other device call (BCHAT-030): no proof, no ticket, no channel.
  final proof = KeystoreDeviceProofSigner(keys: keys, enrollment: enrollment);
  final client = ApprovalClient(baseUrl: config.oidcBaseUrl, proof: proof);
  final notifier = LocalDeviceNotifier();
  final channel = buildDeviceChannel(
    config: config,
    client: client,
    enrollment: enrollment,
    onNotEnrolled: stopSelf,
  );
  final controller = DeviceEventController(
    enrollment: enrollment,
    client: client,
    notifier: notifier,
    watchLifecycle: false,
    isForeground: () async {
      try {
        return await service.invokeMethod<bool>('uiVisible') ?? false;
      } on Object {
        return false;
      }
    },
  );
  await controller.start(channel);
}
