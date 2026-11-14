import 'dart:async';

import 'package:flutter/services.dart';

import 'device_event_source.dart';

/// Whether this build has the optional Apple wake: `--dart-define=APNS_ENABLED=true`. Off by default; with it off no registration code runs and the iOS project asks for no push entitlement.
const bool apnsEnabled = bool.fromEnvironment('APNS_ENABLED');

/// The Swift side (`ApplePushChannel`), over `io.biatec.authenticator/apns`: `register` asks for permission, registers for remote notifications and returns the device token as hex (null when declined); `tokenRefreshed` comes back when the system issues a new one.
class MethodChannelApplePush implements ApplePushRegistration {
  MethodChannelApplePush() {
    _channel.setMethodCallHandler((call) async {
      if (call.method == 'tokenRefreshed' && call.arguments is String) {
        _refreshes.add(call.arguments as String);
      }
    });
  }

  static const _channel = MethodChannel('io.biatec.authenticator/apns');
  final _refreshes = StreamController<String>.broadcast();

  @override
  Future<String?> token() async {
    try {
      return await _channel.invokeMethod<String>('register');
    } on Object {
      return null;
    }
  }

  @override
  Stream<String> get tokenRefreshes => _refreshes.stream;
}
